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
/// GQL-3 / GQL-4 / WP-ARC-A-13: admission and audit for CDC subscriptions.
/// <list type="bullet">
/// <item><c>cdc_all</c> is reserved for global governance administrators.</item>
/// <item>A table topic requires a catalog table the caller may read (same decision as table queries).</item>
/// <item>Open subscriptions are limited per subject (tenant + SID), and cluster-wide per tenant when <see cref="IClusterStateTopology.IsClusterWide"/> is active.</item>
/// <item>Subscribe (allowed and denied) and the aggregated delivery are audited.</item>
/// </list>
/// </summary>
public sealed class CdcSubscriptionGovernor
{
    public const string AllTopic = "cdc_all";
    private const string DeniedMessage = "Forbidden: subscription to this topic is not permitted.";
    private static readonly TimeSpan ClusterSubscriptionCounterTtl = TimeSpan.FromHours(24);

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
        CancellationToken ct)
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

        var max = Math.Max(1, _options.Value.GraphQL.MaxSubscriptionsPerSubject);
        var key = $"{tenant.Value}|{sid.Value}";
        var open = _openPerSubject.AddOrUpdate(key, 1, (_, current) => current + 1);
        if (open > max)
        {
            Release(key);
            await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "subscription limit" }, ct).ConfigureAwait(false);
            throw Denied("TOO_MANY_REQUESTS", $"Too many open subscriptions (limit {max}).");
        }

        string? clusterKey = null;
        if (_topology?.IsClusterWide == true && _clusterState is not null)
        {
            clusterKey = $"cdc:subs:{tenant.Value}";
            long? clusterOpen;
            try
            {
                clusterOpen = await _clusterState.IncrementAsync(clusterKey, 1, ClusterSubscriptionCounterTtl, ct).ConfigureAwait(false);
            }
            catch (ClusterStateUnavailableException ex)
            {
                Release(key);
                _logger.LogError(ex, "Cluster state unavailable while admitting CDC subscription to {Topic} for tenant {Tenant}; failing closed.", topic, tenant.Value);
                await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "cluster state unavailable" }, ct).ConfigureAwait(false);
                throw Denied("SERVICE_UNAVAILABLE", "Cluster state store is unavailable; subscription rejected (fail-closed).");
            }

            if (clusterOpen is null)
            {
                Release(key);
                _logger.LogError("Cluster state returned null while admitting CDC subscription to {Topic} for tenant {Tenant}; failing closed.", topic, tenant.Value);
                await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "cluster state unavailable" }, ct).ConfigureAwait(false);
                throw Denied("SERVICE_UNAVAILABLE", "Cluster state store is unavailable; subscription rejected (fail-closed).");
            }

            if (clusterOpen.Value > max)
            {
                await ReleaseClusterKeyAsync(clusterKey).ConfigureAwait(false);
                Release(key);
                await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "DENY", new { topic, reason = "cluster subscription limit" }, ct).ConfigureAwait(false);
                throw Denied("TOO_MANY_REQUESTS", $"Too many open subscriptions across cluster (limit {max}).");
            }
        }

        try
        {
            await WriteAuditAsync(audit, tenant, sid, topic, "STREAM_SUBSCRIBE", "ALLOW", new { topic }, ct).ConfigureAwait(false);
        }
        catch
        {
            if (clusterKey is not null)
            {
                await ReleaseClusterKeyAsync(clusterKey).ConfigureAwait(false);
            }
            Release(key);
            throw;
        }

        return new Lease(this, key, clusterKey, tenant, sid, topic, audit);
    }

    internal int OpenSubscriptions(TenantId tenant, Sid sid) =>
        _openPerSubject.TryGetValue($"{tenant.Value}|{sid.Value}", out var n) ? n : 0;

    private async ValueTask ReleaseClusterKeyAsync(string clusterKey)
    {
        if (_clusterState is null)
        {
            return;
        }

        try
        {
            await _clusterState.IncrementAsync(clusterKey, -1, ClusterSubscriptionCounterTtl, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrement cluster subscription counter for {ClusterKey}.", clusterKey);
        }
    }

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

    /// <summary>An admitted subscription. Counts delivered events and writes the aggregated delivery audit on dispose.</summary>
    public sealed class Lease : IAsyncDisposable
    {
        private readonly CdcSubscriptionGovernor _owner;
        private readonly string _key;
        private readonly string? _clusterKey;
        private readonly TenantId _tenant;
        private readonly Sid _sid;
        private readonly string _topic;
        private readonly IAuditLogRepository _audit;
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private long _delivered;
        private int _disposed;

        internal Lease(CdcSubscriptionGovernor owner, string key, string? clusterKey, TenantId tenant, Sid sid, string topic, IAuditLogRepository audit)
        {
            _owner = owner;
            _key = key;
            _clusterKey = clusterKey;
            _tenant = tenant;
            _sid = sid;
            _topic = topic;
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }

        public void CountDelivered() => Interlocked.Increment(ref _delivered);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Release(_key);
            if (_clusterKey is not null)
            {
                await _owner.ReleaseClusterKeyAsync(_clusterKey).ConfigureAwait(false);
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
