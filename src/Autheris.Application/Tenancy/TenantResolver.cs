namespace Autheris.Application.Tenancy;

using System.Diagnostics;
using System.Security.Claims;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Logging;

/// <summary>
/// DT-A-2 / DT-A-5: claim (or realm binding) wins; the configured default tenant only fills principals without a tenant statement
/// and never overrides or rewrites an invalid one. An unusable default yields <see cref="TenantResolutionOutcome.Invalid"/>, never a
/// silent fall back (DT-SC-01). Messages and logs carry no claim values (DT-SC-02).
/// </summary>
public sealed class TenantResolver : ITenantResolver
{
    private const string RealmClaimType = "autheris:realm";
    private const long WarningIntervalTicks = 60L * TimeSpan.TicksPerSecond;

    private readonly Func<TenancyOptions> _tenancy;
    private readonly IIdentityRealmRegistry? _realms;
    private readonly ILogger<TenantResolver> _logger;
    private DefaultState _state;
    private long _lastWarningTicks;

    /// <param name="tenancy">Current <c>Gateway:Tenancy</c> options (a delegate keeps the type off the options-wrapper consumption ratchet).</param>
    public TenantResolver(Func<TenancyOptions> tenancy, IIdentityRealmRegistry? realms, ILogger<TenantResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(tenancy);
        ArgumentNullException.ThrowIfNull(logger);
        _tenancy = tenancy;
        _realms = realms;
        _logger = logger;
        _state = DefaultState.From(tenancy());
    }

    public TenantId? DefaultTenant => Current().Effective;

    public TenantId? ConfiguredDefaultTenant => Current().Configured;

    public bool IsDefaultTenant(TenantId tenantId) =>
        tenantId.Value is not null && Current().Effective is { } d && string.Equals(d.Value, tenantId.Value, StringComparison.Ordinal);

    /// <summary>Pure decision over the claim statement and the default tenant (S1-S6 of the semantics table).</summary>
    /// <summary>
    /// The effective default tenant (the catalog owner, DT-A-7) of <paramref name="tenancy"/> for services that hold the options but no
    /// resolver; <c>null</c> when none is configured or the configured value is unusable (fail closed). Same rule as <see cref="DefaultTenant"/>.
    /// </summary>
    // Allocation-free (called per request by services that hold only the options): no DefaultState is built.
    public static TenantId? EffectiveDefaultOf(TenancyOptions? tenancy)
    {
        if (tenancy is null)
        {
            return null;
        }

        if (tenancy.DefaultTenantId is { } raw)
        {
            return TenantId.TryParse(raw, out var parsed) ? (TenantId?)parsed : null;
        }

        // Transitional shim (removed in WP-DT-6): see DefaultState.From.
        return tenancy.EffectiveMode == TenancyMode.Single ? (TenantId?)TenantId.LegacySingleTenant : null;
    }

    public static TenantResolution Decide(ClaimTenant claim, TenantId? defaultTenant) => claim.Kind switch
    {
        ClaimTenantKind.Valid => new TenantResolution(TenantResolutionOutcome.FromClaim, claim.Tenant),
        ClaimTenantKind.Invalid => new TenantResolution(TenantResolutionOutcome.Invalid, null),
        _ => defaultTenant is { } d && d.Value is not null
            ? new TenantResolution(TenantResolutionOutcome.DefaultAssigned, d)
            : new TenantResolution(TenantResolutionOutcome.Unresolved, null)
    };

    public TenantResolution Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return new TenantResolution(TenantResolutionOutcome.Unauthenticated, null);
        }

        var state = Current();
        var fromRealm = false;
        ClaimTenant claim;
        var realmId = principal.FindFirst(RealmClaimType)?.Value;
        if (!string.IsNullOrWhiteSpace(realmId))
        {
            if (_realms is not null)
            {
                // A non-blank realm claim names an authoritative realm: an unregistered one is Invalid (fail closed), never a claim-chain fallback.
                if (_realms.GetRealm(realmId) is not { } realm)
                {
                    Record(TenantResolutionOutcome.Invalid);
                    return new TenantResolution(TenantResolutionOutcome.Invalid, null);
                }

                claim = realm.TenantBinding.ResolveClaim(principal);
                fromRealm = true;
            }
            else
            {
                claim = TenantClaimReader.Read(principal);
            }
        }
        else
        {
            claim = TenantClaimReader.Read(principal);
        }

        var result = claim.Kind == ClaimTenantKind.Absent && state.Invalid
            ? new TenantResolution(TenantResolutionOutcome.Invalid, null)
            : Decide(claim, state.Effective);
        if (fromRealm && result.Outcome == TenantResolutionOutcome.FromClaim)
        {
            result = result with { Outcome = TenantResolutionOutcome.FromRealm };
        }

        Record(result.Outcome);
        return result;
    }

    private void Record(TenantResolutionOutcome outcome)
    {
        var source = outcome switch
        {
            TenantResolutionOutcome.FromClaim => "claim",
            TenantResolutionOutcome.FromRealm => "realm",
            TenantResolutionOutcome.DefaultAssigned => "default",
            _ => null
        };
        if (source is not null)
        {
            Activity.Current?.SetTag("autheris.tenant.source", source);
        }

        if (outcome == TenantResolutionOutcome.DefaultAssigned)
        {
            GatewayDiagnostics.TenantDefaultAssignmentsCounter.Add(1);
        }
        else if (outcome == TenantResolutionOutcome.Invalid)
        {
            WarnInvalidRateLimited();
        }
    }

    // DT-SC-11: bounded, rate-limited (one per minute), no claim values.
    private void WarnInvalidRateLimited()
    {
        var now = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
        var last = Interlocked.Read(ref _lastWarningTicks);
        if ((last == 0 || now - last >= WarningIntervalTicks) &&
            Interlocked.CompareExchange(ref _lastWarningTicks, now == 0 ? 1 : now, last) == last)
        {
            _logger.LogWarning("A principal with an invalid or reserved tenant claim was rejected (rate limited to one entry per minute).");
        }
    }

    // The options monitor hands out one TenancyOptions instance per configuration reload (the Func returns the current GatewayOptions.Tenancy
    // reference), so ReferenceEquals on it is a stable change detector: same reference = same values. Reads and writes are published
    // through Volatile so a reader never observes a half-built state on weak memory models.
    private DefaultState Current()
    {
        var tenancy = _tenancy();
        var state = Volatile.Read(ref _state);
        if (!ReferenceEquals(state.Source, tenancy))
        {
            state = DefaultState.From(tenancy);
            Volatile.Write(ref _state, state);
        }

        return state;
    }

    private sealed class DefaultState
    {
        public required TenancyOptions Source { get; init; }

        public TenantId? Configured { get; init; }

        public TenantId? Effective { get; init; }

        /// <summary>A default is configured but unusable (empty, malformed, reserved): claim-less principals are Invalid.</summary>
        public bool Invalid { get; init; }

        public static DefaultState From(TenancyOptions tenancy)
        {
            if (tenancy.DefaultTenantId is { } raw)
            {
                if (TenantId.TryParse(raw, out var parsed))
                {
                    return new DefaultState { Source = tenancy, Configured = parsed, Effective = parsed };
                }

                return new DefaultState { Source = tenancy, Invalid = true };
            }

            // Transitional shim (WP-DT-1..5, removed in WP-DT-6): Mode=Single keeps the legacy tenant as the default.
            TenantId? shim = null;
            if (tenancy.EffectiveMode == TenancyMode.Single)
            {
                shim = TenantId.LegacySingleTenant;
            }

            return new DefaultState { Source = tenancy, Effective = shim };
        }
    }
}
