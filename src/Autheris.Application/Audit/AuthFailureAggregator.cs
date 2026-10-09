using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Audit;

public sealed class AuthFailureAggregator : IAuthFailureAggregator
{
    private sealed class FailureBucket
    {
        public int Count;
        public bool BruteForceReported;
        public DateTimeOffset FirstSeen = DateTimeOffset.UtcNow;
        public string? SamplePrincipal;
        public string? TenantId;
    }

    private readonly IAuditLogRepository _auditRepository;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<AuthFailureAggregator> _logger;
    private readonly int _bruteForceThreshold;
    private readonly ConcurrentDictionary<string, FailureBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxBuckets = 5000;

    public AuthFailureAggregator(
        IAuditLogRepository auditRepository,
        IOptions<GatewayOptions> options,
        ILogger<AuthFailureAggregator> logger,
        int bruteForceThreshold = 20)
    {
        _auditRepository = auditRepository ?? throw new ArgumentNullException(nameof(auditRepository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bruteForceThreshold = bruteForceThreshold;
    }

    public async Task RecordFailureAsync(
        string sourceIp,
        string reasonCode,
        string? principal = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var key = BuildKey(sourceIp, reasonCode);
        bool isFirst = false;

        var bucket = _buckets.AddOrUpdate(
            key,
            _ =>
            {
                isFirst = true;
                return new FailureBucket
                {
                    Count = 1,
                    SamplePrincipal = principal,
                    TenantId = tenantId
                };
            },
            (_, existing) =>
            {
                Interlocked.Increment(ref existing.Count);
                if (existing.SamplePrincipal == null && principal != null)
                {
                    existing.SamplePrincipal = principal;
                }
                return existing;
            });

        if (isFirst)
        {
            var details = new AuditDetailsBuilder()
                .WithField("sourceIp", sourceIp)
                .WithField("reasonCode", reasonCode)
                .WithField("principal", principal ?? "anonymous")
                .WithField("count", 1)
                .Build();

            await SafeRecordAuditAsync(new AuditLogEntry
            {
                TenantId = new TenantId(tenantId ?? TenantId.LegacySingleTenant.Value),
                EventType = AuditEventTypes.AuthFailed,
                ActorSid = new Sid(principal != null ? "S-1-5-21-UNCHECKED" : "S-1-0-0"),
                Decision = "DENY",
                TargetTable = "auth/session",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = details
            }, ct).ConfigureAwait(false);
        }
        else if (bucket.Count >= _bruteForceThreshold && !bucket.BruteForceReported)
        {
            bucket.BruteForceReported = true;
            var bfDetails = new AuditDetailsBuilder()
                .WithField("sourceIp", sourceIp)
                .WithField("reasonCode", reasonCode)
                .WithField("failureCount", bucket.Count)
                .WithField("threshold", _bruteForceThreshold)
                .Build();

            await SafeRecordAuditAsync(new AuditLogEntry
            {
                TenantId = new TenantId(tenantId ?? TenantId.LegacySingleTenant.Value),
                EventType = AuditEventTypes.AuthBruteForceDetected,
                ActorSid = new Sid("S-1-0-0"),
                Decision = "DENY",
                TargetTable = "auth/bruteforce",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = bfDetails
            }, ct).ConfigureAwait(false);
        }
    }

    public async Task RecordRateLimitAsync(
        string sourceIp,
        string? sid = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var key = BuildKey(sourceIp, "RATE_LIMIT");
        bool isFirst = false;

        var bucket = _buckets.AddOrUpdate(
            key,
            _ =>
            {
                isFirst = true;
                return new FailureBucket
                {
                    Count = 1,
                    SamplePrincipal = sid,
                    TenantId = tenantId
                };
            },
            (_, existing) =>
            {
                Interlocked.Increment(ref existing.Count);
                return existing;
            });

        if (isFirst)
        {
            var details = new AuditDetailsBuilder()
                .WithField("sourceIp", sourceIp)
                .WithField("reasonCode", "RATE_LIMIT_EXCEEDED")
                .WithField("sid", sid ?? "anonymous")
                .WithField("count", 1)
                .Build();

            await SafeRecordAuditAsync(new AuditLogEntry
            {
                TenantId = new TenantId(tenantId ?? TenantId.LegacySingleTenant.Value),
                EventType = AuditEventTypes.RateLimitExceeded,
                ActorSid = new Sid(sid ?? "S-1-0-0"),
                Decision = "DENY",
                TargetTable = "gateway/ratelimit",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = details
            }, ct).ConfigureAwait(false);
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        foreach (var (key, bucket) in _buckets)
        {
            if (bucket.Count > 1)
            {
                var parts = key.Split('|');
                var ip = parts.Length > 0 ? parts[0] : "unknown";
                var reason = parts.Length > 1 ? parts[1] : "AUTH_FAILED";

                var details = new AuditDetailsBuilder()
                    .WithField("sourceIp", ip)
                    .WithField("reasonCode", reason)
                    .WithField("totalFailuresInWindow", bucket.Count)
                    .WithField("firstSeen", bucket.FirstSeen.ToString("O"))
                    .Build();

                await SafeRecordAuditAsync(new AuditLogEntry
                {
                    TenantId = new TenantId(bucket.TenantId ?? TenantId.LegacySingleTenant.Value),
                    EventType = AuditEventTypes.AuthFailed,
                    ActorSid = new Sid("S-1-0-0"),
                    Decision = "DENY",
                    TargetTable = "auth/aggregate",
                    TraceId = Guid.NewGuid().ToString("N"),
                    DetailsJson = details
                }, ct).ConfigureAwait(false);
            }
        }

        _buckets.Clear();
    }

    private string BuildKey(string ip, string reasonCode)
    {
        if (_buckets.Count >= MaxBuckets)
        {
            ip = ToSubnetPrefix(ip);
        }
        return $"{ip}|{reasonCode}";
    }

    private static string ToSubnetPrefix(string ipString)
    {
        if (IPAddress.TryParse(ipString, out var ip))
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
            }
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var bytes = ip.GetAddressBytes();
                return $"{Convert.ToHexString(bytes, 0, 8)}::/64";
            }
        }
        return ipString;
    }

    private async Task SafeRecordAuditAsync(AuditLogEntry entry, CancellationToken ct)
    {
        try
        {
            await _auditRepository.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record audit event for auth failure / rate limit: {Message}", ex.Message);
        }
    }
}
