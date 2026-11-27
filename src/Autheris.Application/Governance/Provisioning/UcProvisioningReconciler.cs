namespace Autheris.Application.Governance.Provisioning;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Runtime.Hosting;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Metrics of the Unity Catalog reconciler.</summary>
public static class UcProvisioningMetrics
{
    private static long _overdueRevokes;

    public static readonly Counter<long> Operations = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_uc_provisioning_operations_total",
        description: "Unity Catalog provisioning operations by operation and result");

    public static readonly Counter<long> DriftFindings = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_uc_drift_findings_total",
        description: "Unity Catalog drift findings by kind");

    private static readonly ObservableGauge<long> OverdueGauge = GatewayDiagnostics.Meter.CreateObservableGauge(
        "autheris_uc_revokes_overdue",
        () => Interlocked.Read(ref _overdueRevokes),
        description: "Autheris-applied Unity Catalog privileges whose revocation is overdue");

    public static long OverdueRevokes => Interlocked.Read(ref _overdueRevokes);

    public static void SetOverdueRevokes(long value)
    {
        _ = OverdueGauge;
        Interlocked.Exchange(ref _overdueRevokes, value);
    }

    public static void Count(string operation, string result, long count = 1)
    {
        if (count > 0)
        {
            Operations.Add(count, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("result", result));
        }
    }
}

/// <summary>
/// Cluster-singleton host of the Unity Catalog reconciler (lease <c>governance:uc-provisioning</c>). It runs while at least one
/// Databricks data source has a provisioning section, at the shortest configured interval.
/// </summary>
public sealed class UcProvisioningService(
    IServiceScopeFactory scopeFactory,
    IDistributedClusterStateProvider clusterState,
    IOptionsMonitor<GatewayOptions> options,
    ILogger<UcProvisioningService> logger,
    TimeProvider? timeProvider = null)
    : ClusterScheduledService<UcProvisioningReconciler>(scopeFactory, clusterState, logger, timeProvider)
{
    protected override string LeaseKey => "governance:uc-provisioning";

    protected override ClusterSchedule GetSchedule()
    {
        var intervals = UcProvisioningReconciler.ProvisionedSources(options.CurrentValue)
            .Select(s => s.Provisioning.ReconcileIntervalSeconds)
            .ToList();
        var interval = TimeSpan.FromSeconds(intervals.Count == 0 ? 60 : Math.Clamp(intervals.Min(), 10, 300));
        return new ClusterSchedule(
            Enabled: intervals.Count > 0,
            Interval: interval,
            InitialDelay: TimeSpan.FromSeconds(30),
            LeaseTtl: interval * 5 > TimeSpan.FromMinutes(5) ? interval * 5 : TimeSpan.FromMinutes(5),
            MaxJitter: TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// One reconcile cycle over every provisioning-managed Databricks data source. Per source the desired privileges are derived from
/// the active consents of the provisioning tenant and compared with the ledger: revokes run first, then grants, then the
/// per-consent feedback is updated. A failure while planning ends that source's cycle without any change; Unity Catalog failures
/// are recorded per privilege and retried with backoff. Gateway enforcement never depends on any of this.
/// </summary>
public sealed partial class UcProvisioningReconciler(
    IOptionsMonitor<GatewayOptions> options,
    IUcProvisioningStore store,
    IUnityCatalogPermissionsClient client,
    IUcTableContextSource context,
    ILogger<UcProvisioningReconciler> logger,
    TimeProvider? timeProvider = null) : IClusterScheduledJob
{
    public const int MaxGrantAttempts = 5;
    public const int OverdueRevokeAttempts = 3;
    public static readonly Sid Actor = new("system:uc-provisioning");
    private static readonly TimeSpan GrantBackoffCap = TimeSpan.FromHours(1);
    private static readonly TimeSpan RevokeBackoffCap = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _fencingToken;

    /// <summary>The configured provisioning sections (name, Databricks options, provisioning options).</summary>
    public static IEnumerable<(string Name, DatabricksDataSourceOptions Databricks, DatabricksProvisioningOptions Provisioning)> ProvisionedSources(GatewayOptions gateway)
    {
        foreach (var (name, connection) in gateway.DataSources.Connections)
        {
            if (connection?.Databricks is { Provisioning: { } provisioning } db &&
                DataSourceProvider.Is(connection.Provider, DatabaseDialect.Databricks))
            {
                yield return (name, db, provisioning);
            }
        }
    }

    public async Task ExecuteAsync(ClusterJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        _fencingToken = context.FencingToken;
        var sources = ProvisionedSources(options.CurrentValue).ToList();
        long overdue = 0;
        foreach (var (name, db, provisioning) in sources)
        {
            try
            {
                overdue += await ReconcileSourceAsync(name, db, provisioning, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not FencingTokenRejectedException)
            {
                // Type and code only: never response text or principal names.
                logger.LogWarning(
                    "Unity Catalog reconcile of data source {DataSource} ended without changes ({ExceptionType}, {Code}).",
                    name, ex.GetType().Name, (ex as UcProvisioningException)?.Code ?? "none");
                UcProvisioningMetrics.Count("cycle", "failed");
            }
        }

        overdue += await CountOrphanedRowsAsync(sources.Select(s => s.Name), ct).ConfigureAwait(false);
        UcProvisioningMetrics.SetOverdueRevokes(overdue);
    }

    /// <summary>Ledger rows of data sources that are no longer provisioning-managed cannot be revoked by Autheris: they are overdue.</summary>
    private async Task<long> CountOrphanedRowsAsync(IEnumerable<string> configured, CancellationToken ct)
    {
        var known = new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase);
        long orphaned = 0;
        foreach (var source in await store.ListLedgerDataSourcesAsync(ct).ConfigureAwait(false))
        {
            if (known.Contains(source))
            {
                continue;
            }

            var rows = await store.ListLedgerAsync(source, ct).ConfigureAwait(false);
            orphaned += rows.Count;
            logger.LogError(
                "Data source {DataSource} holds {Count} Autheris-applied Unity Catalog privileges but has no provisioning section; restore the section with Mode Off to revoke them.",
                source, rows.Count);
        }

        return orphaned;
    }

    private async Task<long> ReconcileSourceAsync(string name, DatabricksDataSourceOptions db, DatabricksProvisioningOptions provisioning, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var tenant = UcProvisioningPlanner.ProvisioningTenant(provisioning)
            ?? throw new UcProvisioningException(UcErrorCodes.Configuration);
        var ledger = await store.ListLedgerAsync(name, ct).ConfigureAwait(false);

        var desired = provisioning.Mode == DatabricksProvisioningMode.Off
            ? await PlanOffAsync(name, tenant, now, ct).ConfigureAwait(false)
            : await PlanAsync(name, db, tenant, now, ct).ConfigureAwait(false);

        // A revoke needs a stable consent set: when a second read differs (a renewal or revocation committed meanwhile), revokes wait
        // for the next cycle so a consent missed by a read is never revoked.
        var deferRevokes = provisioning.Mode != DatabricksProvisioningMode.DryRun &&
                           ledger.Any(row => !desired.Privileges.ContainsKey(row.Key)) &&
                           !await ConsentSetIsStableAsync(name, tenant, desired, now, ct).ConfigureAwait(false);
        var outcome = provisioning.Mode == DatabricksProvisioningMode.Enforce || provisioning.Mode == DatabricksProvisioningMode.Off
            ? await ApplyAsync(name, tenant, desired, ledger, deferRevokes, now, ct).ConfigureAwait(false)
            : DryRun(desired, ledger);

        await UpdateFeedbackAsync(name, provisioning.Mode, tenant, desired, outcome, now, ct).ConfigureAwait(false);
        return outcome.OverdueRevokes;
    }

    private async Task<bool> ConsentSetIsStableAsync(string name, TenantId tenant, UcDesiredState desired, DateTimeOffset now, CancellationToken ct)
    {
        var again = (await store.ListTableConsentsAsync(tenant, name, now, ct).ConfigureAwait(false))
            .Where(c => c.IsActive(now))
            .Select(c => c.Id)
            .ToHashSet();
        var stable = again.SetEquals(desired.Consents.Select(p => p.Consent.Id));
        if (!stable)
        {
            UcProvisioningMetrics.Count("revoke", "deferred");
        }

        return stable;
    }

    /// <summary>Provisioning off: nothing is wanted; every active consent is reported as not provisioned.</summary>
    private async Task<UcDesiredState> PlanOffAsync(string name, TenantId tenant, DateTimeOffset now, CancellationToken ct)
    {
        var consents = await store.ListTableConsentsAsync(tenant, name, now, ct).ConfigureAwait(false);
        var plans = consents.Where(c => c.IsActive(now))
            .Select(c => new UcConsentPlan(c, [], ProvisioningOffReason, null, null))
            .ToList();
        return new UcDesiredState(new Dictionary<UcPrivilegeKey, UcDesiredPrivilege>(), plans);
    }

    /// <summary>Evaluates every active consent of the tenant on the source and resolves the principals of the provisionable ones.</summary>
    private async Task<UcDesiredState> PlanAsync(string name, DatabricksDataSourceOptions db, TenantId tenant, DateTimeOffset now, CancellationToken ct)
    {
        var consents = (await store.ListTableConsentsAsync(tenant, name, now, ct).ConfigureAwait(false))
            .Where(c => c.IsActive(now))
            .ToList();
        if (consents.Count == 0)
        {
            return new UcDesiredState(new Dictionary<UcPrivilegeKey, UcDesiredPrivilege>(), []);
        }

        var patterns = (await context.GetActivePatternConsentsAsync(tenant, now, ct).ConfigureAwait(false)).Where(c => c.IsActive(now)).ToList();
        var virtualFilters = await context.HasMandatoryRowFiltersAsync(tenant, ct).ConfigureAwait(false);
        var facts = new Dictionary<TableIdentifier, UcTableFacts>();
        var evaluated = new List<(Consent, UcEligibility)>(consents.Count);
        foreach (var consent in consents)
        {
            if (!facts.TryGetValue(consent.TableIdentifier, out var table))
            {
                table = await context.GetTableAsync(tenant, consent.TableIdentifier, ct).ConfigureAwait(false);
                facts[consent.TableIdentifier] = table;
            }

            var restrictions = new UcTableRestrictions(
                UcProvisioningPlanner.HasDeny(consent.TableIdentifier, consents, patterns),
                virtualFilters,
                table.HasColumnClassifications);
            evaluated.Add((consent, UcProvisioningEligibility.Evaluate(name, db, consent, table.Table, table.Protection, restrictions)));
        }

        var resolutions = new Dictionary<(GranteeType, string), UcPrincipalResolution>();
        foreach (var (consent, eligibility) in evaluated)
        {
            if (!eligibility.IsProvisionable || UcProvisioningPlanner.NormalizeExternalId(consent.GranteeSid?.Value) is not { } externalId ||
                resolutions.ContainsKey((consent.GranteeType, externalId)))
            {
                continue;
            }

            resolutions[(consent.GranteeType, externalId)] = await client.ResolvePrincipalAsync(name, consent.GranteeType, externalId, ct).ConfigureAwait(false);
            UcProvisioningMetrics.Count("resolve", "ok");
        }

        return UcProvisioningPlanner.BuildDesired(evaluated, resolutions);
    }

    private AuditLogEntry Audit(TenantId tenant, string eventType, string decision, string dataSource, UcPrivilegeKey key, IEnumerable<Guid> consentIds, string? errorCode = null, int? attempts = null) => new()
    {
        TenantId = tenant,
        OccurredAt = _time.GetUtcNow(),
        EventType = eventType,
        ActorSid = Actor,
        TargetTable = dataSource + ":" + key.FullName,
        Decision = decision,
        TraceId = Guid.NewGuid().ToString("N"),
        DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            DataSource = dataSource,
            SecurableType = key.SecurableType.ToStored(),
            Securable = key.FullName,
            PrincipalExternalId = key.PrincipalExternalId,
            key.Privilege,
            ConsentIds = consentIds.Distinct().OrderBy(id => id).ToArray(),
            ErrorCode = errorCode,
            Attempts = attempts
        })
    };
}
