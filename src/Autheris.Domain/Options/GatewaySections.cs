namespace Autheris.Domain.Options;

/// <summary>
/// Canonical configuration section path constants for the Autheris Gateway.
/// </summary>
public static class GatewaySections
{
    public const string Root = "Gateway";
    public const string Gateway = "Gateway";

    // Security & Authentication
    public const string Authentication = "Gateway:Authentication";
    public const string Jwt = "Gateway:Jwt";
    public const string BasicAuth = "Gateway:Authentication:BasicAuth";
    public const string BasicAuthSession = "Gateway:Authentication:BasicAuth:Session";
    public const string EntraId = "Gateway:Authentication:EntraId";
    public const string Adfs = "Gateway:Authentication:Adfs";
    public const string ForwardAuth = "Gateway:Authentication:ForwardAuth";
    public const string ApiKey = "Gateway:ApiKey";
    public const string Totp = "Gateway:Totp";
    public const string HitLStepUp = "Gateway:HitLStepUp";
    public const string Casbin = "Gateway:Casbin";
    public const string CasbinDecisionCache = "Gateway:Casbin:DecisionCache";
    public const string Rebac = "Gateway:Rebac";
    public const string RowFilters = "Gateway:RowFilters";
    public const string CallerAttributes = "Gateway:CallerAttributes";
    public const string VirtualFilters = "Gateway:VirtualFilters";
    public const string VirtualFilterFederation = "Gateway:VirtualFilters:Federation";
    public const string DataMasking = "Gateway:DataMasking";
    public const string ColumnEncryption = "Gateway:ColumnEncryption";
    public const string Classification = "Gateway:Classification";
    public const string Egress = "Gateway:Egress";
    public const string ReverseProxy = "Gateway:ReverseProxy";
    public const string Hosting = "Gateway:Hosting";
    public const string Audit = "Gateway:Audit";
    public const string AuditRetention = "Gateway:Audit:Retention";
    public const string AuditWorm = "Gateway:Audit:Worm";
    public const string Logging = "Gateway:Logging";
    public const string Insecure = "Gateway:Insecure";
    public const string Dev = "Gateway:Dev";
    public const string RevocationWatch = "Gateway:RevocationWatch";

    // Resilience & Governance Limits
    public const string HighAvailability = "Gateway:HighAvailability";
    public const string RateLimiting = "Gateway:RateLimiting";
    public const string PreAuthIpRateLimit = "Gateway:RateLimiting:PreAuthIpRateLimit";
    public const string PostAuthSidRateLimit = "Gateway:RateLimiting:PostAuthSidRateLimit";
    public const string ClientTiers = "Gateway:RateLimiting:ClientTiers";
    public const string ResourceGroups = "Gateway:ResourceGroups";
    public const string RowLimits = "Gateway:RowLimits";
    public const string SystemMetrics = "Gateway:SystemMetrics";
    public const string TrafficShadowing = "Gateway:TrafficShadowing";
    public const string FinOps = "Gateway:FinOps";
    public const string Governance = "Gateway:Governance";
    public const string QueryAnalysis = "Gateway:Governance:QueryAnalysis";
    public const string QueryCost = "Gateway:QueryCost";

    // Storage & Caching
    public const string GovernanceDb = "Gateway:GovernanceDb";
    public const string GovernanceDbMigrations = "Gateway:GovernanceDb:Migrations";
    public const string Caching = "Gateway:Caching";
    public const string L1MemoryCache = "Gateway:Caching:L1MemoryCache";
    public const string Redis = "Gateway:Caching:Redis";
    public const string Garnet = "Gateway:Caching:Garnet";
    public const string EpochValidation = "Gateway:Caching:EpochValidation";
    public const string Cdn = "Gateway:Caching:Cdn";
    public const string DataSources = "Gateway:DataSources";
    public const string DataSourcesHttp = "Gateway:DataSources:Http";
    public const string Lakehouse = "Gateway:Lakehouse";
    public const string DuckDbOlap = "Gateway:DuckDbOlap";
    public const string State = "Gateway:State";
    public const string EventBus = "Gateway:EventBus";
    public const string AccessPlan = "Gateway:AccessPlan";
    public const string Analytics = "Gateway:Analytics";

    // Protocols
    public const string GraphQL = "Gateway:GraphQL";
    public const string GraphQLMetrics = "Gateway:GraphQL:Metrics";
    public const string IncrementalDelivery = "Gateway:IncrementalDelivery";
    public const string GoldenQueries = "Gateway:GoldenQueries";
    public const string OData = "Gateway:OData";
    public const string ParquetEgress = "Gateway:ParquetEgress";
    public const string Arrow = "Gateway:Arrow";
    public const string Mcp = "Gateway:Mcp";
    public const string WebSql = "Gateway:WebSql";
    public const string CrossSource = "Gateway:WebSql:CrossSource";
    public const string SqlEndpoints = "Gateway:SqlEndpoints";
    public const string Procedures = "Gateway:SqlEndpoints:Procedures";
    public const string MssqlChangeTracking = "Gateway:MssqlChangeTracking";
    public const string PostgreSqlCdc = "Gateway:PostgreSqlCdc";
    public const string CdcStream = "Gateway:CdcStream";

    // Integrations
    public const string OpenMetadata = "Gateway:OpenMetadata";
    public const string Catalog = "Gateway:Catalog";
    public const string CatalogSearch = "Gateway:Catalog:Search";
    public const string Itsm = "Gateway:Itsm";
    public const string Dbt = "Gateway:Dbt";
    public const string Backstage = "Gateway:Backstage";
    public const string Plugins = "Gateway:Plugins";
    public const string Extensibility = "Gateway:Extensibility";
    public const string Webhooks = "Gateway:Webhooks";

    // Tenancy & Federation
    public const string TenantIsolation = "Gateway:TenantIsolation";
    public const string TenantRegistry = "Gateway:TenantIsolation:Registry";
    public const string SchemaContracts = "Gateway:SchemaContracts";
    public const string Federation = "Gateway:Federation";
    public const string AsyncJobs = "Gateway:AsyncJobs";
}
