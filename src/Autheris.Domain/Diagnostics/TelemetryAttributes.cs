namespace Autheris.Domain.Diagnostics;

public static class TelemetryAttributes
{
    // Service & Resource
    public const string ServiceName = "service.name";
    public const string ServiceNamespace = "service.namespace";
    public const string ServiceVersion = "service.version";
    public const string ServiceInstanceId = "service.instance.id";
    public const string DeploymentEnvironmentName = "deployment.environment.name";

    // Database Semantic Conventions
    public const string DbSystemName = "db.system.name";
    public const string DbNamespace = "db.namespace";
    public const string DbOperationName = "db.operation.name";
    public const string DbCollectionName = "db.collection.name";
    public const string DbQuerySummary = "db.query.summary";
    public const string DbQueryText = "db.query.text";
    public const string DbResponseStatusCode = "db.response.status_code";
    public const string DbClientOperationDuration = "db.client.operation.duration";

    // HTTP & General
    public const string HttpResponseStatusCode = "http.response.status_code";
    public const string HttpRoute = "http.route";
    public const string UrlFull = "url.full";
    public const string UrlPath = "url.path";
    public const string UrlQuery = "url.query";
    public const string ErrorType = "error.type";

    // Autheris Custom Telemetry
    public const string AutherisTenantId = "autheris.tenant.id";
    public const string AutherisDataSourceName = "autheris.datasource.name";
    public const string AutherisDbQueryHash = "autheris.db.query.hash";
    public const string AutherisDbRows = "autheris.db.rows";
    public const string AutherisDbBytes = "autheris.db.bytes";
    public const string AutherisCorrelationId = "autheris.correlation_id";
    public const string AutherisSurface = "autheris.surface";
    public const string AutherisDecisionStage = "autheris.decision.stage";
    public const string AutherisDecisionResult = "autheris.decision.result";
    public const string AutherisSqlStatementClass = "autheris.sql.statement_class";
    public const string AutherisCacheName = "autheris.cache.name";
    public const string AutherisCacheResult = "autheris.cache.result";
    public const string AutherisAuditTier = "autheris.audit.tier";
    public const string AutherisStore = "autheris.store";
    public const string AutherisLinkKind = "autheris.link.kind";
    public const string EnduserPseudoId = "enduser.pseudo.id";
}
