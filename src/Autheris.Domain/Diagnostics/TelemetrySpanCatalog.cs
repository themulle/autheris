namespace Autheris.Domain.Diagnostics;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

public static class TelemetrySpanCatalog
{
    private static readonly FrozenSet<string> GlobalBaseAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        TelemetryAttributes.AutherisTenantId,
        TelemetryAttributes.ErrorType,
        TelemetryAttributes.AutherisCorrelationId,
        TelemetryAttributes.ServiceName,
        TelemetryAttributes.ServiceVersion,
        TelemetryAttributes.ServiceNamespace,
        TelemetryAttributes.ServiceInstanceId,
        TelemetryAttributes.DeploymentEnvironmentName,
        TelemetryAttributes.EnduserPseudoId
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, FrozenSet<string>> SpanSpecificAllowlist = new Dictionary<string, FrozenSet<string>>(StringComparer.OrdinalIgnoreCase)
    {
        [TelemetrySpanNames.SqlCompile] = new[]
        {
            TelemetryAttributes.DbSystemName,
            TelemetryAttributes.AutherisSqlStatementClass,
            TelemetryAttributes.ErrorType
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        [TelemetrySpanNames.GovernanceDecide] = new[]
        {
            TelemetryAttributes.AutherisDecisionStage,
            TelemetryAttributes.AutherisDecisionResult,
            "policy.epoch",
            "decision"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        [TelemetrySpanNames.GovernanceStore] = new[]
        {
            TelemetryAttributes.DbSystemName,
            TelemetryAttributes.DbOperationName,
            TelemetryAttributes.AutherisStore
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        [TelemetrySpanNames.AuditAppend] = new[]
        {
            TelemetryAttributes.AutherisAuditTier,
            "audit.entry_id",
            "chain.height"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        [TelemetrySpanNames.ClusterState] = new[]
        {
            "operation",
            "reason",
            "key"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        [TelemetrySpanNames.GraphQlExecute] = new[]
        {
            "graphql.operation.type",
            "graphql.operation.name",
            TelemetryAttributes.HttpResponseStatusCode
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        [TelemetrySpanNames.GenAiTool] = new[]
        {
            "gen_ai.tool.name",
            "gen_ai.system",
            "gen_ai.tool.call.id"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        [TelemetrySpanNames.AsyncJobExecute] = new[]
        {
            "job.id",
            "job.status",
            TelemetryAttributes.AutherisLinkKind
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        [TelemetrySpanNames.WebhookDeliver] = new[]
        {
            "webhook.subscription_id",
            "webhook.event_id",
            TelemetryAttributes.HttpResponseStatusCode,
            TelemetryAttributes.AutherisLinkKind
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        [TelemetrySpanNames.EventProcess] = new[]
        {
            "message.type",
            TelemetryAttributes.AutherisLinkKind
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),

        [TelemetrySpanNames.CdcPoll] = new[]
        {
            TelemetryAttributes.AutherisDataSourceName,
            "cdc.batch_size",
            TelemetryAttributes.AutherisLinkKind
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> DataCommandAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        TelemetryAttributes.DbSystemName,
        TelemetryAttributes.DbNamespace,
        TelemetryAttributes.DbOperationName,
        TelemetryAttributes.DbCollectionName,
        TelemetryAttributes.DbQuerySummary,
        TelemetryAttributes.DbQueryText,
        TelemetryAttributes.DbResponseStatusCode,
        TelemetryAttributes.AutherisDataSourceName,
        TelemetryAttributes.AutherisDbQueryHash,
        TelemetryAttributes.AutherisDbRows,
        TelemetryAttributes.AutherisDbBytes,
        TelemetryAttributes.ErrorType
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowed(string? spanName, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (GlobalBaseAllowlist.Contains(key))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(spanName))
        {
            return false;
        }

        if (SpanSpecificAllowlist.TryGetValue(spanName, out var allowed) && allowed.Contains(key))
        {
            return true;
        }

        // Database client command spans: operation name starts with or contains SQL verb e.g. "SELECT", "INSERT", "UPDATE"
        if (spanName.Contains(' ') && DataCommandAllowlist.Contains(key))
        {
            return true;
        }

        return false;
    }
}
