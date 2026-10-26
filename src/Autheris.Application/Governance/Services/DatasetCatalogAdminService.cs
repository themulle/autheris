namespace Autheris.Application.Governance.Services;

using Autheris.Application.Tenancy;
using Autheris.Domain.Tenancy;
using System.Collections.Concurrent;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #111 (PLAN-111 A-5, A-8, A-9): the only writer of dataset tags and visibility tiers. Authorization comes from the authenticated principal
/// (<see cref="CallerScopeResolver"/> plus the ownership store), never from the request. The mandatory order is: caller class, ownership lookup
/// on the parsed identifier for every non-admin caller (also for unknown datasets, so both take the same path), dataset resolution, manageable check
/// (identical NotFound), body validation, the <c>Full</c> rule, then one strict before-read and the writes. The before-state is read strictly, so a
/// store failure is <see cref="DatasetCatalogOutcome.Unavailable"/> and never a fake Hidden.
/// </summary>
public sealed class DatasetCatalogAdminService : IDatasetCatalogAdminService
{
    internal const int ProbeAuditBudget = 20;
    internal static readonly TimeSpan ProbeWindow = TimeSpan.FromMinutes(5);
    internal const int MaxTrackedPrincipals = 10_000;

    // Used for the ownership lookup when the identifier does not parse or resolve, so every non-admin call costs the same.
    private static readonly TableIdentifier ProbePlaceholder = new("catalog", "probe", "placeholder");

    private readonly ITableMetadataRepository _tables;
    private readonly IDataOwnershipRepository? _owners;
    private readonly IAuditWriter _audit;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly ICatalogVisibilityMetrics? _metrics;
    // Probe throttle state: bounded (hard cap, then one shared overflow bucket), swept at most once per window under the lock.
    private readonly Dictionary<string, ProbeWindowState> _probes = new(StringComparer.Ordinal);
    private readonly object _probeLock = new();
    private ProbeWindowState? _overflow;
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    internal int TrackedProbePrincipalsForTesting
    {
        get { lock (_probeLock) { return _probes.Count; } }
    }

    public DatasetCatalogAdminService(
        ITableMetadataRepository tables,
        IDataOwnershipRepository? owners,
        IAuditWriter audit,
        IOptionsMonitor<GatewayOptions> options,
        TimeProvider? time = null,
        ICatalogVisibilityMetrics? metrics = null)
    {
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _owners = owners;
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
        _metrics = metrics;
    }

    private sealed class ProbeWindowState(DateTimeOffset start, Caller owner, bool overflow = false)
    {
        public Caller Owner { get; } = owner;
        public bool Overflow { get; } = overflow;
        public DateTimeOffset Start { get; set; } = start;
        public int Written { get; set; }
        public int Suppressed { get; set; }
    }

    private readonly record struct Caller(Sid Sid, bool IsAdmin, TenantId Tenant, string AuthType);

    public async Task<DatasetCatalogResult> GetAsync(ClaimsPrincipal? caller, string datasetId, CancellationToken ct)
    {
        var gate = await AuthorizeAsync(caller, datasetId, ct).ConfigureAwait(false);
        if (gate.Failure is { } failure)
        {
            return failure;
        }

        try
        {
            var settings = await _tables.GetDatasetCatalogSettingsAsync(gate.Table, strict: true, ct).ConfigureAwait(false);
            return settings is null
                ? new DatasetCatalogResult(DatasetCatalogOutcome.NotFound)
                : new DatasetCatalogResult(DatasetCatalogOutcome.Unchanged, gate.Table, settings, IsTierEffective(gate.Table, gate.Caller!.Value.Tenant));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DatasetCatalogResult(DatasetCatalogOutcome.Unavailable);
        }
    }

    public async Task<DatasetCatalogResult> ApplyAsync(ClaimsPrincipal? caller, string datasetId, DatasetCatalogEdit edit, string source, CancellationToken ct, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var gate = await AuthorizeAsync(caller, datasetId, ct).ConfigureAwait(false);
        if (gate.Failure is { } failure)
        {
            if (!dryRun && failure.Outcome == DatasetCatalogOutcome.NotFound && gate.Caller is { } probe)
            {
                await AuditProbeAsync(probe, ct).ConfigureAwait(false);
            }

            return failure;
        }

        var actor = gate.Caller!.Value;
        var table = gate.Table;

        // Validation of the whole body, only after the caller is known to manage the dataset.
        if (edit.Malformed)
        {
            return new DatasetCatalogResult(DatasetCatalogOutcome.InvalidRequest);
        }

        DatasetVisibility? requested = null;
        if (edit.Visibility is not null)
        {
            if (!DatasetTagRules.TryParseVisibility(edit.Visibility, out var parsed))
            {
                return new DatasetCatalogResult(DatasetCatalogOutcome.InvalidVisibility);
            }

            requested = parsed;
        }

        var max = _options.CurrentValue.Catalog.MaxTagsPerDataset;
        if (!TryNormalizeList(edit.ReplaceTags, out var replace) || !TryNormalizeList(edit.AddTags, out var add) || !TryNormalizeList(edit.RemoveTags, out var remove))
        {
            return new DatasetCatalogResult(DatasetCatalogOutcome.InvalidTag);
        }

        // The Full rule: only the administrator classes may set Full; the owner who asks is told so after ownership is established.
        if (requested == DatasetVisibility.Full && !actor.IsAdmin)
        {
            if (!dryRun)
            {
                await AuditDenyFullAsync(actor, table, source, ct).ConfigureAwait(false);
            }

            return new DatasetCatalogResult(DatasetCatalogOutcome.Forbidden);
        }

        DatasetCatalogSettings before;
        try
        {
            var read = await _tables.GetDatasetCatalogSettingsAsync(table, strict: true, ct).ConfigureAwait(false);
            if (read is null)
            {
                return new DatasetCatalogResult(DatasetCatalogOutcome.NotFound);
            }

            before = read;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DatasetCatalogResult(DatasetCatalogOutcome.Unavailable);
        }

        if (replace is not null || add is not null || remove is not null)
        {
            if (ExceedsCap(before, replace, add, remove, max))
            {
                return new DatasetCatalogResult(DatasetCatalogOutcome.TagLimitExceeded);
            }
        }

        if (dryRun)
        {
            return Simulate(table, actor.Tenant, before, replace, add, remove, requested);
        }

        DatasetCatalogChange? tagChange = null;
        DatasetCatalogChange? visibilityChange = null;
        var actorSid = actor.Sid;
        try
        {
            if (replace is not null)
            {
                tagChange = await _tables.ReplaceDatasetTagsAsync(table, replace, DatasetTagSource.Admin, actorSid, ct).ConfigureAwait(false);
                if (tagChange is null)
                {
                    return new DatasetCatalogResult(DatasetCatalogOutcome.NotFound);
                }
            }

            if (add is not null || remove is not null)
            {
                var change = await _tables.ChangeDatasetTagsAsync(table, add ?? [], remove ?? [], actorSid, ct).ConfigureAwait(false);
                if (change is null)
                {
                    return new DatasetCatalogResult(DatasetCatalogOutcome.NotFound);
                }

                tagChange = tagChange is null ? change : new DatasetCatalogChange(table, tagChange.Before, change.After);
            }

            // The tag change is committed: audit it before the visibility write, so a failing visibility write cannot lose the record.
            if (tagChange?.Changed == true)
            {
                await AuditChangeAsync(AuditEventTypes.CatalogTagsChanged, actor, table, source, tagChange.Before, tagChange.After).ConfigureAwait(false);
            }

            if (requested is { } visibility)
            {
                visibilityChange = await _tables.SetDatasetVisibilityAsync(table, visibility, actorSid, ct).ConfigureAwait(false);
                if (visibilityChange is null)
                {
                    return new DatasetCatalogResult(DatasetCatalogOutcome.NotFound);
                }
            }
        }
        catch (ArgumentException)
        {
            // The repository re-validates the tag form; a rejection there is the same class as the service validation.
            return new DatasetCatalogResult(DatasetCatalogOutcome.InvalidTag);
        }

        var after = visibilityChange?.After ?? tagChange?.After ?? before;
        if (tagChange is not null && visibilityChange is not null)
        {
            after = new DatasetCatalogSettings(tagChange.After.Tags, visibilityChange.After.Visibility);
        }

        var tagsChanged = tagChange?.Changed == true;
        var visibilityChanged = visibilityChange?.Changed == true;
        if (visibilityChanged)
        {
            await AuditChangeAsync(AuditEventTypes.CatalogVisibilityChanged, actor, table, source, visibilityChange!.Before, visibilityChange.After).ConfigureAwait(false);
            _metrics?.RequestRefresh();
        }

        return new DatasetCatalogResult(
            tagsChanged || visibilityChanged ? DatasetCatalogOutcome.Applied : DatasetCatalogOutcome.Unchanged,
            table, after, IsTierEffective(table, actor.Tenant));
    }

    /// <summary>Dry run (same non-atomic cap semantics as <see cref="ExceedsCap"/>): the state the edit would produce, computed from the strict before-state like the repositories do (admin rows only are touched).</summary>
    private DatasetCatalogResult Simulate(
        TableIdentifier table, TenantId tenant, DatasetCatalogSettings before, IReadOnlyList<string>? replace, IReadOnlyList<string>? add, IReadOnlyList<string>? remove, DatasetVisibility? requested)
    {
        var admin = new HashSet<string>(before.Tags.Where(t => t.Source == DatasetTagSource.Admin).Select(t => t.Tag), StringComparer.Ordinal);
        if (replace is not null)
        {
            admin = new HashSet<string>(replace, StringComparer.Ordinal);
        }

        if (remove is not null)
        {
            admin.ExceptWith(remove);
        }

        if (add is not null)
        {
            admin.UnionWith(add);
        }

        var tags = admin.Select(t => new DatasetTag(t, DatasetTagSource.Admin))
            .Concat(before.Tags.Where(t => t.Source == DatasetTagSource.Dbt && !admin.Contains(t.Tag))).ToList();
        var after = new DatasetCatalogSettings(tags, requested ?? before.Visibility);
        var changed = new DatasetCatalogChange(table, before, after).Changed;
        return new DatasetCatalogResult(changed ? DatasetCatalogOutcome.Applied : DatasetCatalogOutcome.Unchanged, table, after, IsTierEffective(table, tenant));
    }

    private readonly record struct Gate(DatasetCatalogResult? Failure, Caller? Caller, TableIdentifier Table);

    /// <summary>Steps 1-5 of the mandatory order (A-5): returns a failure (identical NotFound for every non-manageable case) or the manageable table.</summary>
    private async Task<Gate> AuthorizeAsync(ClaimsPrincipal? principal, string? datasetId, CancellationToken ct)
    {
        var notFound = new DatasetCatalogResult(DatasetCatalogOutcome.NotFound);
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_options.CurrentValue.Tenancy);
        var resolution = CallerScopeResolver.ResolutionOf(principal, catalogOwner);
        var scope = CallerScopeResolver.Resolve(principal, resolution, catalogOwner);
        if (scope == CallerScope.None || principal is null)
        {
            return new Gate(notFound, null, default);
        }

        var sid = principal.GetUserSid() ?? new Sid(principal.Identity?.Name ?? "ANONYMOUS");
        var caller = new Caller(sid, scope is CallerScope.Global or CallerScope.CatalogOwner, TenantOf(resolution, principal), principal.Identity?.AuthenticationType ?? string.Empty);

        TableIdentifier? parsed;
        try
        {
            parsed = await ParseAsync(datasetId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Gate(new DatasetCatalogResult(DatasetCatalogOutcome.Unavailable), caller.IsAdmin ? null : caller, default);
        }

        var isOwner = false;
        if (!caller.IsAdmin)
        {
            if (_owners is null)
            {
                return new Gate(new DatasetCatalogResult(DatasetCatalogOutcome.Unavailable), caller, default);
            }

            try
            {
                isOwner = await _owners.IsAuthorizedApproverForTableAsync(parsed ?? ProbePlaceholder, caller.Sid, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new Gate(new DatasetCatalogResult(DatasetCatalogOutcome.Unavailable), caller, default);
            }
        }

        TableIdentifier? resolved = null;
        if (parsed is { } id)
        {
            try
            {
                resolved = await _tables.GetActiveTableIdentifierAsync(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new Gate(new DatasetCatalogResult(DatasetCatalogOutcome.Unavailable), caller.IsAdmin ? null : caller, default);
            }
        }

        if (resolved is not { } table || (!caller.IsAdmin && !isOwner))
        {
            return new Gate(notFound, caller.IsAdmin ? null : caller, default);
        }

        // DT-SC-08: virtual tables / semantic models are tenant-scoped objects; the catalog-owner widening is physical metadata only. A caller
        // that is not the global administrator reaches only the semantic tables of its own tenant schema (identical NotFound otherwise).
        if (scope != CallerScope.Global &&
            SemanticVirtualTables.IsInSemanticDomain(table) &&
            !string.Equals(table.Schema, SemanticVirtualTables.TenantSchema(caller.Tenant), StringComparison.Ordinal))
        {
            return new Gate(notFound, caller.IsAdmin ? null : caller, default);
        }

        return new Gate(null, caller, table);
    }

    private async Task<TableIdentifier?> ParseAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > DatasetIdResolver.MaxDatasetIdLength)
        {
            return null;
        }

        if (Guid.TryParseExact(id, "D", out var guid))
        {
            return await _tables.GetActiveTableIdentifierByIdAsync(guid, ct).ConfigureAwait(false);
        }

        return TableIdentifier.TryParse(id.Replace('/', '.'), out var identifier) ? identifier : null;
    }

    // The scope is not None here, so the resolution carries the tenant; a ClusterAdmin without tenant is audited under its claim, else unresolved.
    private static TenantId TenantOf(TenantResolution resolution, ClaimsPrincipal principal)
    {
        if (resolution.Tenant is { } resolved && resolved.Value is not null)
        {
            return resolved;
        }

        try
        {
            return principal.GetTenantId();
        }
        catch (SecurityException)
        {
            return TenantId.LegacySingleTenant;
        }
    }

    private static bool TryNormalizeList(IReadOnlyList<string>? raw, out IReadOnlyList<string>? tags)
    {
        tags = null;
        if (raw is null)
        {
            return true;
        }

        if (raw.Count > DatasetTagRules.HardMaxTagsPerDataset * 2)
        {
            return false;
        }

        if (!DatasetTagRules.TryNormalizeSet(raw, DatasetTagRules.HardMaxTagsPerDataset, out var normalized, out _))
        {
            return false;
        }

        tags = normalized;
        return true;
    }

    /// <summary>
    /// Total tags across both sources after the edit, computed from the strict before-state (the repository does not enforce the total). The check is not
    /// atomic with a concurrent <c>ChangeDatasetTagsAsync</c> on the same dataset (admin path): two racing edits can together exceed the cap by their
    /// own size; accepted, the cap is a hygiene limit and the next edit is checked against the then-current state.
    /// </summary>
    private static bool ExceedsCap(
        DatasetCatalogSettings before, IReadOnlyList<string>? replace, IReadOnlyList<string>? add, IReadOnlyList<string>? remove, int max)
    {
        var admin = new HashSet<string>(before.Tags.Where(t => t.Source == DatasetTagSource.Admin).Select(t => t.Tag), StringComparer.Ordinal);
        var dbt = new HashSet<string>(before.Tags.Where(t => t.Source == DatasetTagSource.Dbt).Select(t => t.Tag), StringComparer.Ordinal);
        if (replace is not null)
        {
            admin = new HashSet<string>(replace, StringComparer.Ordinal);
        }

        if (remove is not null)
        {
            admin.ExceptWith(remove);
        }

        if (add is not null)
        {
            admin.UnionWith(add);
        }

        // A tag present in both sources is stored once as admin (A-2), so the union counts it once.
        admin.UnionWith(dbt);
        return admin.Count > max;
    }

    private bool IsTierEffective(TableIdentifier table, TenantId tenant)
    {
        var options = _options.CurrentValue;
        if (CallerScopeResolver.IsOwner(tenant, TenantResolver.EffectiveDefaultOf(options.Tenancy)))
        {
            return true;
        }

        foreach (var entry in options.Catalog.SharedTables ?? [])
        {
            if (TableIdentifier.TryParse(entry, out var shared) && shared.Equals(table))
            {
                return true;
            }
        }

        return false;
    }

    private static object View(DatasetCatalogSettings s) => new { visibility = s.Visibility.ToString(), tags = s.Tags.Select(t => t.Tag).OrderBy(t => t, StringComparer.Ordinal).ToArray() };

    private Task AuditChangeAsync(string eventType, Caller actor, TableIdentifier table, string source, DatasetCatalogSettings before, DatasetCatalogSettings after)
        => _audit.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = actor.Tenant,
            EventType = eventType,
            ActorSid = actor.Sid,
            TargetTable = table.ToString(),
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new { before = View(before), after = View(after), source, authType = actor.AuthType, tenant = actor.Tenant.Value })
        }, CancellationToken.None);

    private Task AuditDenyFullAsync(Caller actor, TableIdentifier table, string source, CancellationToken ct)
        => _audit.RecordBestEffortAsync(new AuditLogEntry
        {
            TenantId = actor.Tenant,
            EventType = AuditEventTypes.CatalogVisibilityChanged,
            ActorSid = actor.Sid,
            TargetTable = table.ToString(),
            Decision = "DENY",
            DetailsJson = JsonSerializer.Serialize(new { requested = "Full", reason = "owner_may_not_set_full", source, authType = actor.AuthType, tenant = actor.Tenant.Value })
        }, ct);

    /// <summary>
    /// Probe signal for a caller who cannot manage the dataset (unknown, inactive and foreign datasets all end here, so the response and the work
    /// are identical): at most <see cref="ProbeAuditBudget"/> DENY entries per principal and window; later attempts are counted and flushed as one
    /// aggregated entry when the window of that principal ends. The state is bounded: expired entries (suppressed counts included, written as the
    /// aggregate on eviction) are swept at most once per window, and beyond <see cref="MaxTrackedPrincipals"/> principals a shared overflow bucket
    /// with its own budget and aggregate is used.
    /// </summary>
    private async Task AuditProbeAsync(Caller probe, CancellationToken ct, int attempts = 1, string? source = null)
    {
        var now = _time.GetUtcNow();
        var key = probe.Sid.Value;
        List<(Caller Owner, bool Overflow, int Count)>? flushes = null;
        var write = false;
        lock (_probeLock)
        {
            if (now >= _nextSweep)
            {
                foreach (var (k, v) in _probes)
                {
                    if (now - v.Start >= ProbeWindow)
                    {
                        if (v.Suppressed > 0)
                        {
                            (flushes ??= []).Add((v.Owner, false, v.Suppressed));
                        }

                        _probes.Remove(k);
                    }
                }

                if (_overflow is { } o && now - o.Start >= ProbeWindow)
                {
                    if (o.Suppressed > 0)
                    {
                        (flushes ??= []).Add((o.Owner, true, o.Suppressed));
                    }

                    _overflow = null;
                }

                _nextSweep = now + ProbeWindow;
            }

            ProbeWindowState state;
            if (_probes.TryGetValue(key, out var existing))
            {
                state = existing;
            }
            else if (_probes.Count < MaxTrackedPrincipals)
            {
                state = _probes[key] = new ProbeWindowState(now, probe);
            }
            else
            {
                state = _overflow ??= new ProbeWindowState(now, probe with { Sid = new Sid("overflow") }, overflow: true);
            }

            if (now - state.Start >= ProbeWindow)
            {
                // The principal's own window ended between sweeps.
                if (state.Suppressed > 0)
                {
                    (flushes ??= []).Add((state.Owner, state.Overflow, state.Suppressed));
                }

                state.Start = now;
                state.Written = 0;
                state.Suppressed = 0;
            }

            if (state.Written < ProbeAuditBudget)
            {
                state.Written++;
                write = true;
            }
            else
            {
                state.Suppressed++;
            }
        }

        foreach (var (owner, overflow, count) in flushes ?? [])
        {
            await _audit.RecordBestEffortAsync(new AuditLogEntry
            {
                TenantId = owner.Tenant,
                EventType = AuditEventTypes.CatalogProbeSuppressed,
                ActorSid = owner.Sid,
                TargetTable = string.Empty,
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new { count, overflow, windowSeconds = (int)ProbeWindow.TotalSeconds })
            }, ct).ConfigureAwait(false);
        }

        if (write)
        {
            await _audit.RecordBestEffortAsync(new AuditLogEntry
            {
                TenantId = probe.Tenant,
                EventType = AuditEventTypes.CatalogVisibilityChanged,
                ActorSid = probe.Sid,
                TargetTable = string.Empty,
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new { reason = "not_manageable", attempts, source, authType = probe.AuthType, tenant = probe.Tenant.Value })
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A manifest skipped <paramref name="attempts"/> entries as unknown or not manageable: one aggregated probe entry per manifest, throttled like
    /// the other probes. Administrators are no probes (an unknown dataset is simply unknown to them).
    /// </summary>
    public async Task RecordManifestProbesAsync(ClaimsPrincipal? caller, int attempts, CancellationToken ct)
    {
        if (attempts <= 0)
        {
            return;
        }

        var catalogOwner = TenantResolver.EffectiveDefaultOf(_options.CurrentValue.Tenancy);
        var resolution = CallerScopeResolver.ResolutionOf(caller, catalogOwner);
        var scope = CallerScopeResolver.Resolve(caller, resolution, catalogOwner);
        if (scope == CallerScope.None || caller is null || scope is CallerScope.Global or CallerScope.CatalogOwner)
        {
            return;
        }

        var sid = caller.GetUserSid() ?? new Sid(caller.Identity?.Name ?? "ANONYMOUS");
        await AuditProbeAsync(new Caller(sid, false, TenantOf(resolution, caller), caller.Identity?.AuthenticationType ?? string.Empty), ct, attempts, "manifest").ConfigureAwait(false);
    }
}
