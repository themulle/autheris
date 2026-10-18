using Autheris.Domain.Audit;

namespace Autheris.Application.Audit;

/// <summary>
/// WP-ARC-A-06: explicit <see cref="AuditFailurePolicy"/> per <see cref="AuditEventTypes"/> entry. Every catalog constant has a row
/// (enforced by <c>AuditPolicyTableTests</c>). Unknown event types default to <see cref="AuditFailurePolicy.FailClosed"/>.
/// DENY records are fail-closed for the audit step only; the denial itself never depends on the audit outcome.
/// </summary>
public static class AuditPolicyTable
{
    private static readonly IReadOnlyDictionary<string, AuditFailurePolicy> Table = new Dictionary<string, AuditFailurePolicy>(StringComparer.Ordinal)
    {
        [AuditEventTypes.AuthFailed] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AuthTokenRejected] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AuthSucceeded] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.AuthBruteForceDetected] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AuthzEndpointDenied] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.RateLimitExceeded] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.TokenRevoked] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.TokenRevokedHit] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.TenantCollisionDenied] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.TenantCollisionResolved] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CatalogRead] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.MetadataExport] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.TableQuery] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.WebSqlQuery] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.WebSqlQueryDenied] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.WebSqlCrossSourceQuery] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.WebSqlCrossSourceSourceRead] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.WebSqlDmlExecuted] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.WebSqlDmlRejected] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.WebSqlDmlFailed] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.QueryExecutionError] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.StreamSubscribe] = AuditFailurePolicy.FailClosed,
        // Tier B: aggregated per subscription; the raw CDC stream is high volume and nothing is denied by a failed audit write.
        [AuditEventTypes.CdcWebhookDeliveryBatch] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.EgressShadowCopy] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcDeliveryBatch] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.CdcConsumerRegistered] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcConsumerRejected] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcConsumerDeleted] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcConsumerPaused] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcConsumerResumed] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcConsumerOffsetReset] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcDeadLetterReplayed] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcStreamResync] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcStreamKeyRotated] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcPluginReceiverBound] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CdcPluginReceiverRejected] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AuditRead] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AuditExport] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AuditConfigChanged] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AuditPipelineFault] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AuditRetentionPurge] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CatalogKeyDefinitionChanged] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.ServiceStarted] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.ServiceStopped] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.ConsentRequested] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.ConsentApproved] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.ConsentGranted] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.ConsentDenied] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.ConsentRevoked] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.ConsentApplied] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.ConsentDescriptionChanged] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.ProfileInvalidationFailed] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.BreakGlassActivated] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.DatasourceTested] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AsyncJobSubmitted] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AsyncJobCompleted] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AsyncJobDownloaded] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AsyncJobCancelled] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.AsyncJobStatusRead] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.AsyncJobAccessDenied] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.ClassificationProposed] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.ClassificationTransitioned] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.GovernanceConfigHashRead] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.AccessPlanRead] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.TwoFactorEnrollmentStarted] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.TwoFactorEnrollmentVerified] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.StepUpApprovalsListed] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.StepUpApprovalAttempted] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.DocIngestionBundleApplied] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.DocIngestionDocumentApplied] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.DocDiscoveryRead] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.RebacTupleAdded] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.RebacTupleRemoved] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.RebacInvalidationFailed] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.RebacTupleSeed] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.AggregateQuery] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.MetricQuery] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.MetadataSyntheticSample] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.QueryAnalysisEvaluated] = AuditFailurePolicy.BestEffort,
        [AuditEventTypes.RowFilterEvaluationChanged] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.TableRowFilterStrategyChanged] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CallerAttributeDefinitionPending] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CallerAttributeDefinitionApplied] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CallerAttributeDefinitionRejected] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CallerAttributeDefinitionDeleted] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CallerAttributeServiceSetChanged] = AuditFailurePolicy.FailClosed,
        [AuditEventTypes.CallerAttributeSimulationRead] = AuditFailurePolicy.FailClosed,
    };

    public static IReadOnlyDictionary<string, AuditFailurePolicy> All => Table;

    public static AuditFailurePolicy For(string? eventType) =>
        eventType != null && Table.TryGetValue(eventType, out var policy) ? policy : AuditFailurePolicy.FailClosed;
}
