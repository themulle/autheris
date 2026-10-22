namespace Autheris.GraphQL.Subscriptions;

using System;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using HotChocolate;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// GQL-3 / GQL-4 / GQL-10 / WP-ARC-A-13: admission, audit and distributed slot leasing for CDC subscriptions.
/// <list type="bullet">
/// <item><c>cdc_all</c> is reserved for global governance administrators.</item>
/// <item>A table topic requires a catalog table the caller may read (same decision as table queries).</item>
/// <item>Open subscriptions are limited per subject (tenant + SID), and cluster-wide per subject and per tenant when cluster state is active.</item>
/// <item>Subscribe (allowed and denied) and the aggregated delivery are audited.</item>
/// </list>
/// </summary>
public sealed class CdcSubscriptionGovernor
{
    public const string AllTopic = "cdc_all";
    private const string DeniedMessage = "Forbidden: subscription to this topic is not permitted.";
    public static readonly TimeSpan SlotTtl = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(40);

    private readonly ConcurrentDictionary<string, int> _openPerSubject = new(StringComparer.Ordinal);
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<CdcSubscriptionGovernor> _logger;
    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly IClusterStateTopology? _topology;

    public CdcSubscriptionGovernor(IOptions<GatewayOptions> options, ILogger<CdcSubscriptionGovernor> logger)
        : this(options, logger, null, null)
    {
    }

    public CdcSubscriptionGovernor(
        IOptions<GatewayOptions> options,
        ILogger<CdcSubscriptionGovernor> logger,
        IDistributedClusterStateProvider? clusterState = null,
        IClusterStateTopology? topology = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clusterState = clusterState;
        _topology = topology;
    }

    /// <summary>Resolves the topic for a <c>table</c> argument: <c>cdc_all</c>, <c>cdc_{schema.table}</c> or <c>cdc_{table}</c>.</summary>
    public static (string Topic, TableIdentifier? Table) ResolveTopic(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        var clean = table.Trim().ToLowerInvariant();
        if (clean.StartsWith("cdc_", StringComparison.Ordinal))
        {
            clean = clean[4..];
        }

        if (clean == "all")
        {
            return (AllTopic, null);
        }

        var id = TableIdentifierNormalizer.Normalize(clean);
        var topic = clean.Contains('.', StringComparison.Ordinal)
            ? $"cdc_{id.ToQualifiedName().ToLowerInvariant()}"
            : $"cdc_{id.TableName.ToLowerInvariant()}";
        return (topic, id);
    }

    public async Task<Lease> AdmitAsync(
        ClaimsPrincipal principal,
        string topic,
        TableIdentifier? table,
        ITableAccessResolver? accessResolver,
        IAuditLogRepository audit,
        CancellationToken ct,
        CancellationTokenSource? streamCts = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var tenant = principal.GetTenantId();
        var sid = principal.GetUserSid() ?? throw Denied("AUTH_NOT_AUTHENTICATED", "Unauthorized: no user SID in the token.");

        string? denial = null;
        if (table == null)
        {
            var roles = principal.GetUserRoles();
            bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
            bool isClusterAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
            if (!isGovAdmin && !isClusterAdmin)
            {
                denial = "cdc_all requires a global governance role";
            }
        }
        else if (accessResolver == null)
        {
            denial = "access resolver unavailable (fail-closed)";
        }
        else
        {
            try
            {
                var access = await accessResolver.ResolveTableAccessAsync(principal, table.Value, null, null, ct).ConfigureAwait(false);
                if (!access.Decision.IsAllowed)
                {
                    denial = "no access to table";
                }
            }
            catch (TableNotFoundException)
            {
                denial = "table not in catalog";
            }
        }

        if (denial != null)
        {
            _logger.LogWarning("CDC subscription to {Topic} denied for {Sid}: {Reason}.", topic, sid.Value, denial);
            await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = denial }, ct).ConfigureAwait(false);
            throw Denied("FORBIDDEN", DeniedMessage);
        }

        var maxPerSubject = Math.Max(1, _options.Value.GraphQL.MaxSubscriptionsPerSubject);
        var maxPerTenant = Math.Max(1, _options.Value.GraphQL.MaxSubscriptionsPerTenant);
        var localKey = $"{tenant.Value}|{sid.Value}";
        var open = _openPerSubject.AddOrUpdate(localKey, 1, (_, current) => current + 1);
        if (open > maxPerSubject)
        {
            Release(localKey);
            await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "subscription limit" }, ct).ConfigureAwait(false);
            throw Denied("TOO_MANY_REQUESTS", $"Too many open subscriptions (limit {maxPerSubject}).");
        }

        bool useClusterState = _clusterState is not null && (_topology is null || _topology.IsClusterWide);
        string? subjectSlotKey = null;
        string? tenantSlotKey = null;
        string? leaseMember = null;

        if (useClusterState)
        {
            leaseMember = Guid.NewGuid().ToString("N");
            subjectSlotKey = $"cdc:subs:{tenant.Value}:{sid.Value}";
            tenantSlotKey = $"cdc:subs:{tenant.Value}";

            // Acquire Slot 1: Subject Slot
            bool? acquiredSubject;
            try
            {
                acquiredSubject = await _clusterState!.TryAcquireSlotAsync(subjectSlotKey, leaseMember, maxPerSubject, SlotTtl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Release(localKey);
                _logger.LogError(ex, "Cluster state unavailable while acquiring subject slot for {Topic} for tenant {Tenant}; failing closed.", topic, tenant.Value);
                await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "cluster state unavailable" }, ct).ConfigureAwait(false);
                throw Denied("SERVICE_UNAVAILABLE", "Cluster state store is unavailable; subscription rejected (fail-closed).");
            }

            if (acquiredSubject is null)
            {
                Release(localKey);
                _logger.LogError("Cluster state returned null while acquiring subject slot for {Topic} for tenant {Tenant}; failing closed.", topic, tenant.Value);
                await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "cluster state unavailable" }, ct).ConfigureAwait(false);
                throw Denied("SERVICE_UNAVAILABLE", "Cluster state store is unavailable; subscription rejected (fail-closed).");
            }

            if (!acquiredSubject.Value)
            {
                Release(localKey);
                await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "cluster subject subscription limit" }, ct).ConfigureAwait(false);
                throw Denied("TOO_MANY_REQUESTS", $"Too many open subscriptions across cluster for subject (limit {maxPerSubject}).");
            }

            // Acquire Slot 2: Tenant Slot
            bool? acquiredTenant;
            try
            {
                acquiredTenant = await _clusterState.TryAcquireSlotAsync(tenantSlotKey, leaseMember, maxPerTenant, SlotTtl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Second slot failed with error: release first slot!
                await _clusterState.ReleaseSlotAsync(subjectSlotKey, leaseMember, CancellationToken.None).ConfigureAwait(false);
                Release(localKey);
                _logger.LogError(ex, "Cluster state unavailable while acquiring tenant slot for {Topic} for tenant {Tenant}; failing closed.", topic, tenant.Value);
                await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "cluster state unavailable" }, ct).ConfigureAwait(false);
                throw Denied("SERVICE_UNAVAILABLE", "Cluster state store is unavailable; subscription rejected (fail-closed).");
            }

            if (acquiredTenant is null)
            {
                await _clusterState.ReleaseSlotAsync(subjectSlotKey, leaseMember, CancellationToken.None).ConfigureAwait(false);
                Release(localKey);
                _logger.LogError("Cluster state returned null while acquiring tenant slot for {Topic} for tenant {Tenant}; failing closed.", topic, tenant.Value);
                await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "cluster state unavailable" }, ct).ConfigureAwait(false);
                throw Denied("SERVICE_UNAVAILABLE", "Cluster state store is unavailable; subscription rejected (fail-closed).");
            }

            if (!acquiredTenant.Value)
            {
                // Second slot exceeded limit: release first slot!
                await _clusterState.ReleaseSlotAsync(subjectSlotKey, leaseMember, CancellationToken.None).ConfigureAwait(false);
                Release(localKey);
                await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "cluster tenant subscription limit" }, ct).ConfigureAwait(false);
                throw Denied("TOO_MANY_REQUESTS", $"Too many open subscriptions across cluster for tenant (limit {maxPerTenant}).");
            }
        }

        try
        {
            await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "ALLOW", new { topic }, ct).ConfigureAwait(false);
        }
        catch
        {
            if (useClusterState && subjectSlotKey != null && tenantSlotKey != null && leaseMember != null)
            {
                await _clusterState!.ReleaseSlotAsync(subjectSlotKey, leaseMember, CancellationToken.None).ConfigureAwait(false);
                await _clusterState!.ReleaseSlotAsync(tenantSlotKey, leaseMember, CancellationToken.None).ConfigureAwait(false);
            }
            Release(localKey);
            throw;
        }

        return new Lease(this, localKey, useClusterState, subjectSlotKey, tenantSlotKey, leaseMember, tenant, sid, topic, audit, streamCts);
    }

    internal int OpenSubscriptions(TenantId tenant, Sid sid) =>
        _openPerSubject.TryGetValue($"{tenant.Value}|{sid.Value}", out var n) ? n : 0;

    private void Release(string key)
    {
        while (_openPerSubject.TryGetValue(key, out var current))
        {
            if (current <= 1)
            {
                if (_openPerSubject.TryRemove(new System.Collections.Generic.KeyValuePair<string, int>(key, current)))
                {
                    return;
                }
            }
            else if (_openPerSubject.TryUpdate(key, current - 1, current))
            {
                return;
            }
        }
    }

    private static Task WriteAuditAsync(IAuditLogRepository audit, TenantId tenant, Sid sid, string topic, string eventType, string decision, object details, CancellationToken ct)
    {
        return audit.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = eventType,
            ActorSid = sid,
            TargetTable = topic,
            Decision = decision,
            TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = JsonSerializer.Serialize(details)
        }, ct);
    }

    private static GraphQLException Denied(string code, string message) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode(code).Build());

    /// <summary>An admitted subscription. Renews slots periodically, counts delivered events, and audits on dispose.</summary>
    public sealed class Lease : IAsyncDisposable
    {
        private readonly CdcSubscriptionGovernor _owner;
        private readonly string _localKey;
        private readonly bool _useClusterState;
        private readonly string? _subjectSlotKey;
        private readonly string? _tenantSlotKey;
        private readonly string? _leaseMember;
        private readonly TenantId _tenant;
        private readonly Sid _sid;
        private readonly string _topic;
        private readonly IAuditLogRepository _audit;
        private readonly CancellationTokenSource _cts;
        private readonly Task? _renewalTask;
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private long _delivered;
        private int _disposed;

        public CancellationToken StreamCancellationToken => _cts.Token;
        public bool IsStreamCompleted => _cts.IsCancellationRequested;

        internal Lease(
            CdcSubscriptionGovernor owner,
            string localKey,
            bool useClusterState,
            string? subjectSlotKey,
            string? tenantSlotKey,
            string? leaseMember,
            TenantId tenant,
            Sid sid,
            string topic,
            IAuditLogRepository audit,
            CancellationTokenSource? streamCts = null)
        {
            _owner = owner;
            _localKey = localKey;
            _useClusterState = useClusterState;
            _subjectSlotKey = subjectSlotKey;
            _tenantSlotKey = tenantSlotKey;
            _leaseMember = leaseMember;
            _tenant = tenant;
            _sid = sid;
            _topic = topic;
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
            _cts = streamCts ?? new CancellationTokenSource();

            if (_useClusterState && _owner._clusterState != null)
            {
                _renewalTask = Task.Run(RunRenewalLoopAsync);
            }
        }

        public void CountDelivered() => Interlocked.Increment(ref _delivered);

        private async Task RunRenewalLoopAsync()
        {
            using var timer = new PeriodicTimer(RenewalInterval);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (!await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var success = await TryRenewWithRetryAsync().ConfigureAwait(false);
                if (!success)
                {
                    _owner._logger.LogWarning("CDC subscription slot renewal failed after retries for tenant {Tenant}, sid {Sid}; terminating stream.", _tenant.Value, _sid.Value);
                    try
                    {
                        _cts.Cancel();
                    }
                    catch
                    {
                    }
                    break;
                }
            }
        }

        internal async Task<bool> TriggerRenewalAsync()
        {
            var success = await TryRenewWithRetryAsync().ConfigureAwait(false);
            if (!success)
            {
                try
                {
                    _cts.Cancel();
                }
                catch
                {
                }
            }
            return success;
        }

        private async Task<bool> TryRenewWithRetryAsync()
        {
            if (!_useClusterState || _owner._clusterState == null || _subjectSlotKey == null || _tenantSlotKey == null || _leaseMember == null)
            {
                return true;
            }

            for (int attempt = 0; attempt <= 2; attempt++)
            {
                try
                {
                    var r1 = await _owner._clusterState.RenewSlotAsync(_subjectSlotKey, _leaseMember, SlotTtl, _cts.Token).ConfigureAwait(false);
                    var r2 = await _owner._clusterState.RenewSlotAsync(_tenantSlotKey, _leaseMember, SlotTtl, _cts.Token).ConfigureAwait(false);
                    if (r1 && r2)
                    {
                        return true;
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    _owner._logger.LogWarning(ex, "Exception during slot renewal attempt {Attempt}", attempt + 1);
                }

                if (attempt < 2)
                {
                    try
                    {
                        await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch
            {
            }

            if (_renewalTask != null)
            {
                try
                {
                    await _renewalTask.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _owner.Release(_localKey);

            if (_useClusterState && _owner._clusterState != null && _subjectSlotKey != null && _tenantSlotKey != null && _leaseMember != null)
            {
                try
                {
                    await _owner._clusterState.ReleaseSlotAsync(_subjectSlotKey, _leaseMember, CancellationToken.None).ConfigureAwait(false);
                    await _owner._clusterState.ReleaseSlotAsync(_tenantSlotKey, _leaseMember, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _owner._logger.LogWarning(ex, "Failed to release CDC cluster slots for {Member}.", _leaseMember);
                }
            }

            try
            {
                await WriteAuditAsync(_audit, _tenant, _sid, _topic, "STREAM_DELIVERY", "ALLOW",
                    new { topic = _topic, delivered = Interlocked.Read(ref _delivered), started = _started, ended = DateTimeOffset.UtcNow },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _owner._logger.LogError(ex, "Failed to write the CDC delivery audit for {Topic}.", _topic);
            }
        }
    }
}
