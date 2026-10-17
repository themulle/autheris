namespace Autheris.Application.Governance.Services;

using System.Collections.Concurrent;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Services;
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
    private const int MaxTrackedPrincipals = 10_000;

    // Used for the ownership lookup when the identifier does not parse or resolve, so every non-admin call costs the same.
    private static readonly TableIdentifier ProbePlaceholder = new("catalog", "probe", "placeholder");

    private readonly ITableMetadataRepository _tables;
    private readonly IDataOwnershipRepository? _owners;
    private readonly IAuditWriter _audit;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly ICatalogVisibilityMetrics? _metrics;
    private readonly ConcurrentDictionary<string, ProbeWindowState> _probes = new(StringComparer.Ordinal);

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

    private sealed class ProbeWindowState(DateTimeOffset start)
    {
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
                : new DatasetCatalogResult(DatasetCatalogOutcome.Unchanged, gate.Table, settings, IsTierEffective(gate.Table));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DatasetCatalogResult(DatasetCatalogOutcome.Unavailable);
        }
    }

    public async Task<DatasetCatalogResult> ApplyAsync(ClaimsPrincipal? caller, string datasetId, DatasetCatalogEdit edit, string source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var gate = await AuthorizeAsync(caller, datasetId, ct).ConfigureAwait(false);
        if (gate.Failure is { } failure)
        {
            if (failure.Outcome == DatasetCatalogOutcome.NotFound && gate.Caller is { } probe)
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
            await AuditDenyFullAsync(actor, table, source, ct).ConfigureAwait(false);
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
        if (tagsChanged)
        {
            await AuditChangeAsync(AuditEventTypes.CatalogTagsChanged, actor, table, source, tagChange!.Before, tagChange.After).ConfigureAwait(false);
        }

        if (visibilityChanged)
        {
            await AuditChangeAsync(AuditEventTypes.CatalogVisibilityChanged, actor, table, source, visibilityChange!.Before, visibilityChange.After).ConfigureAwait(false);
            _metrics?.RequestRefresh();
        }

        return new DatasetCatalogResult(
            tagsChanged || visibilityChanged ? DatasetCatalogOutcome.Applied : DatasetCatalogOutcome.Unchanged,
            table, after, IsTierEffective(table));
    }

    private readonly record struct Gate(DatasetCatalogResult? Failure, Caller? Caller, TableIdentifier Table);

    /// <summary>Steps 1-5 of the mandatory order (A-5): returns a failure (identical NotFound for every non-manageable case) or the manageable table.</summary>
    private async Task<Gate> AuthorizeAsync(ClaimsPrincipal? principal, string? datasetId, CancellationToken ct)
    {
        var notFound = new DatasetCatalogResult(DatasetCatalogOutcome.NotFound);
        var mode = _options.CurrentValue.Tenancy.EffectiveMode;
        var scope = CallerScopeResolver.Resolve(principal, mode);
        if (scope == CallerScope.None || principal is null)
        {
            return new Gate(notFound, null, default);
        }

        var sid = principal.GetUserSid() ?? new Sid(principal.Identity?.Name ?? "ANONYMOUS");
        var caller = new Caller(sid, scope is CallerScope.Global or CallerScope.Unrestricted, TenantOf(principal), principal.Identity?.AuthenticationType ?? string.Empty);

        var parsed = await ParseAsync(datasetId, ct).ConfigureAwait(false);
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
            resolved = await _tables.GetActiveTableIdentifierAsync(id, ct).ConfigureAwait(false);
        }

        if (resolved is not { } table || (!caller.IsAdmin && !isOwner))
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

    private static TenantId TenantOf(ClaimsPrincipal principal)
    {
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

    /// <summary>Total tags across both sources after the edit, computed from the strict before-state (the repository does not enforce the total).</summary>
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

    private bool IsTierEffective(TableIdentifier table)
    {
        var options = _options.CurrentValue;
        if (options.Tenancy.EffectiveMode == Autheris.Domain.Options.TenancyMode.Single)
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
    /// aggregated entry when the next window of that principal starts.
    /// </summary>
    private async Task AuditProbeAsync(Caller probe, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var key = probe.Sid.Value;
        int? flushed = null;
        var write = false;
        lock (_probes)
        {
            if (_probes.Count > MaxTrackedPrincipals)
            {
                foreach (var stale in _probes.Where(kv => now - kv.Value.Start >= ProbeWindow && kv.Value.Suppressed == 0).Select(kv => kv.Key).ToList())
                {
                    _probes.TryRemove(stale, out _);
                }
            }

            var state = _probes.GetOrAdd(key, _ => new ProbeWindowState(now));
            if (now - state.Start >= ProbeWindow)
            {
                if (state.Suppressed > 0)
                {
                    flushed = state.Suppressed;
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

        if (flushed is { } count)
        {
            await _audit.RecordBestEffortAsync(new AuditLogEntry
            {
                TenantId = probe.Tenant,
                EventType = AuditEventTypes.CatalogProbeSuppressed,
                ActorSid = probe.Sid,
                TargetTable = string.Empty,
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new { count, windowSeconds = (int)ProbeWindow.TotalSeconds })
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
                DetailsJson = JsonSerializer.Serialize(new { reason = "not_manageable", authType = probe.AuthType, tenant = probe.Tenant.Value })
            }, ct).ConfigureAwait(false);
        }
    }
}
