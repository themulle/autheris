namespace Autheris.Domain.Diagnostics;

public static class TelemetrySpanNames
{
    public const string SqlCompile = "autheris.sql.compile";
    public const string GovernanceDecide = "autheris.governance.decide";
    public const string AuditAppend = "autheris.audit.append";
    public const string GovernanceStore = "autheris.governance_store";
    public const string ClusterState = "autheris.cluster_state";
    public const string GraphQlExecute = "graphql.execute";
    public const string GenAiTool = "gen_ai.tool";
    public const string AsyncJobExecute = "autheris.asyncjob.execute";
    public const string WebhookDeliver = "autheris.webhook.deliver";
    public const string EventProcess = "autheris.event.process";
    public const string CdcPoll = "autheris.cdc.poll";
}
