namespace Autheris.Domain.Diagnostics;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

public static class TelemetryMetricCatalog
{
    private static readonly FrozenDictionary<string, FrozenSet<string>> MetricAttributesAllowlist = new Dictionary<string, FrozenSet<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["autheris.request.duration"] = new[]
        {
            TelemetryAttributes.AutherisSurface,
            TelemetryAttributes.ErrorType,
            TelemetryAttributes.AutherisTenantId
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.governance.decision.duration"] = new[]
        {
            TelemetryAttributes.AutherisDecisionStage,
            TelemetryAttributes.AutherisDecisionResult,
            TelemetryAttributes.AutherisTenantId
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.sql.compile.duration"] = new[]
        {
            TelemetryAttributes.DbSystemName,
            TelemetryAttributes.AutherisSqlStatementClass,
            TelemetryAttributes.ErrorType
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["db.client.operation.duration"] = new[]
        {
            TelemetryAttributes.DbSystemName,
            TelemetryAttributes.AutherisDataSourceName,
            TelemetryAttributes.DbOperationName,
            TelemetryAttributes.ErrorType
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.query.rows"] = new[]
        {
            TelemetryAttributes.AutherisSurface,
            TelemetryAttributes.AutherisDataSourceName
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.query.bytes"] = new[]
        {
            TelemetryAttributes.AutherisSurface,
            TelemetryAttributes.AutherisDataSourceName
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.cache.requests"] = new[]
        {
            TelemetryAttributes.AutherisCacheName,
            TelemetryAttributes.AutherisCacheResult
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.audit.append.duration"] = new[]
        {
            TelemetryAttributes.AutherisAuditTier
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.eventbus.dropped"] = new[]
        {
            "autheris.eventbus.kind"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.redis.failures"] = new[]
        {
            "autheris.redis.component",
            "autheris.redis.operation"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.cluster.lease.events"] = new[]
        {
            "reason"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.ratelimit.rejections"] = new[]
        {
            "tier",
            "limit_kind"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        ["autheris.query.slow"] = new[]
        {
            TelemetryAttributes.DbSystemName,
            TelemetryAttributes.AutherisDataSourceName,
            TelemetryAttributes.DbOperationName
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowed(string metricName, string attributeKey)
    {
        if (string.IsNullOrWhiteSpace(metricName) || string.IsNullOrWhiteSpace(attributeKey))
        {
            return false;
        }

        if (MetricAttributesAllowlist.TryGetValue(metricName, out var allowed) && allowed.Contains(attributeKey))
        {
            return true;
        }

        return false;
    }
}
