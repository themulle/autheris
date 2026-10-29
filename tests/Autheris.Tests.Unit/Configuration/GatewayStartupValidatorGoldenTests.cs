namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Tests.Unit.Common;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Configuration.StartupValidatorGoldenSupport;

/// <summary>
/// ARCH-MNT-14 goldens for <see cref="GatewayStartupValidator.ValidateStartup"/>: one case per reachable inline throw site
/// (Production and Development), plus the warnings logged by valid configurations. The files in <c>Snapshots</c> were generated
/// on the unchanged validator; a refactoring must never change them (ARCH-MNT-14-SC-01).
/// </summary>
[Collection(GoldenConsoleCollection.Name)]
public sealed class GatewayStartupValidatorGoldenTests : IDisposable
{
    private readonly string _fixtureDir = CreateFixtureDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_fixtureDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup of the temp fixture
        }
    }

    private static void ValidatePublic(Autheris.Domain.Options.GatewayOptions options, Microsoft.Extensions.Hosting.IHostEnvironment env, Func<string, string?> getEnv, Microsoft.Extensions.Logging.ILogger logger) =>
        GatewayStartupValidator.ValidateStartup(options, env, getEnv, logger);

    internal static IReadOnlyList<GoldenCase> FailureCases()
    {
        const string Hash = "$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        (string, string)[] container = [("DOTNET_RUNNING_IN_CONTAINER", "true")];
        (string, string?)[] basic = [("Authentication:BasicAuth:Enabled", "true"), ("Authentication:RequireKerberosOnly", "false")];
        (string, string?)[] user(string password) =>
        [
            .. basic,
            ("Authentication:BasicAuth:Users:0:Username", "alice"),
            ("Authentication:BasicAuth:Users:0:Password", password),
            ("Authentication:BasicAuth:Users:0:Sid", "S-1-5-21-1"),
        ];
        (string, string?)[] federation = [("Federation:Enabled", "true"), ("Federation:SignContextHeaders", "true")];

        return
        [
            // baselines
            Dev("Baseline_Development_Default"),
            Prod("Baseline_Production_Valid"),

            // 1 ObjectGraph
            Prod("ObjectGraph_RangeViolation", ("HighAvailability:ShutdownTimeoutSeconds", "5")),
            Dev("ObjectGraph_RangeViolation", ("HighAvailability:ShutdownTimeoutSeconds", "5")),

            // per-area checks (one representative case each)
            Prod("OracleRuntime_PlaintextPassword", ("DataSources:Connections:ora:Provider", "Oracle"), ("DataSources:Connections:ora:ConnectionString", "User Id=app;Password=secret;Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example.org)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=svc)))")),
            Prod("SqlServerWindowsAuth_InvalidConnectionString", ("DataSources:Connections:sql:Provider", "SqlServer"), ("DataSources:Connections:sql:ConnectionString", "this is not a connection string")),
            Prod("Databricks_MissingSection", ("DataSources:Connections:dbx:Provider", "Databricks")),
            Prod("DefaultSchemas_InvalidSchema", ("DataSources:Connections:pg:Provider", "PostgreSql"), ("DataSources:Connections:pg:ConnectionString", "Host=h;Database=d"), ("DataSources:Connections:pg:DefaultSchema", "bad schema;")),
            Prod("TenantCollisions_StrictMode", ("TenantIsolation:StrictCollisionStartup", "true"), ("Authentication:ForwardAuth:AllowedTenantIds:0", "Acme"), ("Authentication:ForwardAuth:AllowedTenantIds:1", "ACME")),
            Prod("WebhookSubscriptions_MissingId", ("Webhooks:Subscriptions:0:Url", "https://example.org/hook")),
            Prod("QueryCost_FailureFeeRange", ("QueryCost:FailureFee", "0")),
            ProdMutated("QueryCost_Null", o => typeof(Autheris.Domain.Options.GatewayOptions).GetProperty("QueryCost")!.SetValue(o, null)),

            // 3 Egress
            Prod("Egress_TooBroadNetwork", ("Egress:TrustedInternalNetworks:0", "10.0.0.0/4")),

            // 4 CasbinModel
            Prod("CasbinModel_FileNotFound", ("Casbin:ModelPath", "{tmp}/missing.conf")),
            Prod("CasbinModel_FileEmpty", ("Casbin:ModelPath", "{tmp}/model-empty.conf")),
            Prod("CasbinModel_ContractViolation", ("Casbin:ModelPath", "{tmp}/model-invalid.conf")),

            // 5 CasbinPolicy
            Prod("CasbinPolicy_PathNotConfigured", ("Casbin:Enabled", "true"), ("Casbin:PolicyPath", "")),
            Prod("CasbinPolicy_FileNotFound", ("Casbin:Enabled", "true"), ("Casbin:PolicyPath", "{tmp}/missing.csv")),
            Prod("CasbinPolicy_FileEmpty", ("Casbin:Enabled", "true"), ("Casbin:PolicyPath", "{tmp}/policy-empty.csv")),
            Prod("CasbinPolicy_NoRules", ("Casbin:Enabled", "true"), ("Casbin:PolicyPath", "{tmp}/policy-norules.csv")),
            Prod("CasbinPolicy_InvalidFile", ("Casbin:Enabled", "true"), ("Casbin:PolicyPath", "{tmp}/policy-invalid.csv")),

            // 6 ReverseProxy
            Prod("ReverseProxy_InvalidNetwork", ("ReverseProxy:Enabled", "true"), ("ReverseProxy:KnownNetworks:0", "not-a-network")),
            Prod("ReverseProxy_WildcardNetwork", ("ReverseProxy:Enabled", "true"), ("ReverseProxy:KnownNetworks:0", "0.0.0.0/0")),
            Prod("ReverseProxy_InvalidProxy", ("ReverseProxy:Enabled", "true"), ("ReverseProxy:KnownProxies:0", "not-an-ip")),

            // 7 DevelopmentInContainer
            DevEnv("DevelopmentInContainer_NoOptIn", container),
            DevEnv("DevelopmentInContainer_OptInByOption", container, ("AllowDevelopmentInContainer", "true")),
            DevEnv("DevelopmentInContainer_OptInByEnvironment", [("DOTNET_RUNNING_IN_CONTAINER", "true"), ("AUTHERIS_ALLOW_DEV_IN_CONTAINER", "true")]),

            // 8 GraphQlTrustedDocuments
            Prod("GraphQlTrustedDocuments_AppScopedWithoutPersistedOnly", ("GraphQL:AppScopedTrustedDocuments:Enabled", "true")),
            Prod("GraphQlTrustedDocuments_DirectoryMissing", ("GraphQL:PersistedQueriesOnly", "true"), ("GraphQL:TrustedDocumentsDirectory", "{tmp}/missing-dir")),

            // 9 FederationSigning
            Prod("FederationSigning_KeyMissing", [.. federation]),
            Prod("FederationSigning_DefaultSecret", [.. federation, ("Federation:SigningKey", "autheris-federation-default-secret")]),
            Prod("FederationSigning_KeyTooShort", [.. federation, ("Federation:SigningKey", "short-key")]),
            Dev("FederationSigning_KeyMissingAllowedInDevelopment", [.. federation]),

            // 10 SecuritySwitchReport (reports, never throws on its own)
            Dev("SecuritySwitchReport_WebSqlBypassFlag", ("Insecure:danger_bypass_websql_governance", "true")),
            Prod("SecuritySwitchReport_WarnFlagsAndVirtualFilters", ("Insecure:warn_allow_all_cors_origins", "true"), ("AllowInsecureWarnFlagsInProduction", "true"), ("VirtualFilters:RequireApproval", "false")),

            // 11 OperationalInvariants
            Prod("OperationalInvariants_DmlWithoutRoles", ("WebSql:AllowDml", "true")),
            Prod("OperationalInvariants_QuickstartInProduction", ("Dev:Preset", "Quickstart")),
            Prod("OperationalInvariants_ShutdownTimeout", ("HighAvailability:QueryTimeoutSeconds", "30"), ("HighAvailability:ShutdownTimeoutSeconds", "35")),
            Prod("OperationalInvariants_TerminationGrace", ("HighAvailability:TerminationGracePeriodSeconds", "20")),
            Prod("OperationalInvariants_DevOptions", ("Dev:Preset", "bogus")),
            Dev("OperationalInvariants_DevOptions", ("Dev:Preset", "bogus")),

            // 12 BasicAuthSession
            Prod("BasicAuthSession_RequiresBasicAuth", ("Authentication:BasicAuth:Session:Enabled", "true")),

            // 13 BasicAuthUsers
            Prod("BasicAuthUsers_MissingPassword", user(string.Empty)),

            // 14 ForwardAuth
            Prod("ForwardAuth_InvalidDefaultTenant", ("Authentication:ForwardAuth:Enabled", "true"), ("Authentication:ForwardAuth:DefaultTenantId", "not a tenant!")),
            Prod("ForwardAuth_BasicAuthWithKerberosOnly", ("Authentication:BasicAuth:Enabled", "true"), ("Authentication:RequireKerberosOnly", "true")),
            Prod("ForwardAuth_SecretMissing", ("Authentication:ForwardAuth:Enabled", "true")),
            Prod("ForwardAuth_SecretTooShort", ("Authentication:ForwardAuth:Enabled", "true"), ("Authentication:ForwardAuth:SharedSecret", "short")),
            Prod("ForwardAuth_TrustedProxyNotRequired", ("Authentication:ForwardAuth:Enabled", "true"), ("Authentication:ForwardAuth:SharedSecretKeyVaultRef", "kv://fwd"), ("Authentication:ForwardAuth:RequireTrustedProxy", "false")),
            Prod("ForwardAuth_InvalidTrustedNetwork", ("Authentication:ForwardAuth:Enabled", "true"), ("Authentication:ForwardAuth:SharedSecretKeyVaultRef", "kv://fwd"), ("Authentication:ForwardAuth:TrustedNetworks:0", "not-a-network")),
            Prod("ForwardAuth_WildcardTrustedNetwork", ("Authentication:ForwardAuth:Enabled", "true"), ("Authentication:ForwardAuth:SharedSecretKeyVaultRef", "kv://fwd"), ("Authentication:ForwardAuth:TrustedNetworks:0", "0.0.0.0/0")),
            Prod("ForwardAuth_InvalidTrustedProxy", ("Authentication:ForwardAuth:Enabled", "true"), ("Authentication:ForwardAuth:SharedSecretKeyVaultRef", "kv://fwd"), ("Authentication:ForwardAuth:TrustedProxies:0", "not-an-ip")),

            // 15 ProductionGovernance
            Prod("ProductionGovernance_SeedDemoData", ("GovernanceDb:SeedDemoData", "true")),
            Prod("ProductionGovernance_MigrationsSkip", ("GovernanceDb:Migrations:Mode", "Skip")),
            Prod("ProductionGovernance_TestAuthHandler", ("Authentication:EnableTestAuthHandler", "true")),
            Prod("ProductionGovernance_EntraIdHttpsMetadata", ("Authentication:EntraId:RequireHttpsMetadata", "false")),
            Prod("ProductionGovernance_AnonymousAccess", ("Insecure:danger_allow_anonymous_access", "true")),
            Prod("ProductionGovernance_DemoRebacSeed", ("Rebac:SeedTuples:0:TenantId", "t"), ("Rebac:SeedTuples:0:User", "user:david"), ("Rebac:SeedTuples:0:Relation", "viewer"), ("Rebac:SeedTuples:0:Object", "table:orders")),
            Prod("ProductionGovernance_DangerBypass", ("Insecure:danger_disable_column_masking", "true")),
            Dev("ProductionGovernance_DangerBypassAllowedInDevelopment", ("Insecure:danger_disable_column_masking", "true")),

            // 16 ProductionTransportAndOrigins
            Prod("ProductionTransport_Introspection", ("GraphQL:EnableIntrospection", "true")),
            Prod("ProductionTransport_AllCors", ("Insecure:warn_allow_all_cors_origins", "true")),
            Prod("ProductionTransport_McpDeveloperCors", ("Mcp:EnableDeveloperCors", "true")),
            Prod("ProductionTransport_HmacReferenceMissing", ("DataMasking:HmacSecretKeyVaultRef", "")),
            Prod("ProductionTransport_OpenMetadataPlainHttp", ("OpenMetadata:Enabled", "true"), ("OpenMetadata:ServerUrl", "http://metadata.example.org/api")),
            Prod("ProductionTransport_DataSourceWithoutTls", ("DataSources:Connections:pg:Provider", "PostgreSql"), ("DataSources:Connections:pg:ConnectionString", "Host=h;Database=d;Username=u;Password=p"), ("DataSources:Connections:pg:DefaultSchema", "public")),
            Prod("ProductionTransport_WildcardOrigin", ("GraphQL:TrustedOrigins:0", "*")),
            Prod("ProductionTransport_PlainHttpOrigin", ("GraphQL:TrustedOrigins:0", "http://app.example.org")),
            Prod("ProductionTransport_RebacClusterWithoutRedis", ("HighAvailability:MultiNodeClusterMode", "true"), ("Rebac:Enabled", "true")),
            Prod("ProductionTransport_ReplicasWithoutRedis", ("HighAvailability:Replicas", "2")),
            Prod("ProductionTransport_PluginsWithoutManifest", ("Plugins:Directory", "{tmp}"), ("Plugins:RequireIntegrityManifest", "false")),

            // 17 ProductionAuthentication
            Prod("ProductionAuthentication_PlaintextPassword", user("plaintext")),
            Prod("ProductionAuthentication_WeakPbkdf2", user("$pbkdf2$1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")),
            Prod("ProductionAuthentication_TruncatedHash", user("$argon2id$v=19$m=65536,t=3,p=1$AAAA$AAAA")),
            Prod("ProductionAuthentication_EntraIdIncomplete", ("Authentication:EntraId:Enabled", "true")),
            Prod("ProductionAuthentication_AdfsIncomplete", ("Authentication:Adfs:Enabled", "true")),
            Prod("ProductionAuthentication_WellFormedHashAccepted", user(Hash)),

            // 18 ProductionStorageAndAudit
            Prod("ProductionStorage_WormWithoutS3Keys", ("Audit:Worm:Enabled", "true"), ("Audit:Worm:StorageType", "S3"), ("Audit:Worm:EnforceObjectLock", "true")),
            Prod("ProductionStorage_InMemorySqlite", ("GovernanceDb:ConnectionString", "Data Source=:memory:")),
            ProdEnv("ProductionStorage_ContainerRelativeSqlitePath", container, ("GovernanceDb:ConnectionString", "Data Source=governance.db")),
            Prod("ProductionStorage_FallbackAuditKey", ("Audit:HmacKeyIsFallback", "true")),
            Prod("ProductionStorage_AnchorMissing", ("Audit:ChainAnchorPath", "")),
            ProdEnv("ProductionStorage_AuditChecksSkippedInTestEnvironment", [("ASPNETCORE_ENVIRONMENT", "Test")], ("Audit:ChainAnchorPath", "")),

            // 19 GovernanceDbProvider
            Prod("GovernanceDbProvider_Unsupported", ("GovernanceDb:Provider", "Oracle")),
            Dev("GovernanceDbProvider_Unsupported", ("GovernanceDb:Provider", "Oracle")),

            // 20 ClusterState
            Prod("ClusterState_SqliteWithReplicas", ("HighAvailability:Replicas", "2"), ("Caching:Redis:Enabled", "true")),
            Prod("ClusterState_EmbeddedGarnetWithReplicas", ("HighAvailability:Replicas", "2"), ("Caching:Redis:Enabled", "true"), ("GovernanceDb:Provider", "PostgreSql"), ("Caching:Garnet:EnableEmbeddedServer", "true")),
        ];
    }

    internal static IReadOnlyList<GoldenCase> WarningCases() =>
    [
        Dev("Warnings_DevelopmentDefault"),
        Dev("Warnings_DevelopmentRecursiveCte", ("WebSql:AllowRecursiveCte", "true")),
        Dev("Warnings_DevelopmentWebSqlBypassFlag", ("Insecure:danger_bypass_websql_governance", "true")),
        Dev("Warnings_DevelopmentDangerBypassBanner", ("Insecure:danger_disable_column_masking", "true")),
        Dev("Warnings_DevelopmentTenancyExplicitSingle", ("Tenancy:Mode", "Single")),
        Dev("Warnings_DevelopmentCatalogDiscoveryDeprecated", ("Catalog:AllowAuthenticatedCatalogDiscovery", "true")),
        Prod("Warnings_ProductionDefault"),
        Prod("Warnings_ProductionWarnFlags", ("Insecure:warn_allow_all_cors_origins", "true"), ("AllowInsecureWarnFlagsInProduction", "true"), ("VirtualFilters:RequireApproval", "false")),
        Prod("Warnings_ProductionTenantCollisionLoggedCritical", ("Authentication:ForwardAuth:AllowedTenantIds:0", "Acme"), ("Authentication:ForwardAuth:AllowedTenantIds:1", "ACME")),
    ];

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "ARCH-MNT-14-SC-01")]
    public void EachInvalidCase_FailsWithGoldenMessage()
    {
        // ARCH-MNT-14-T-01
        var outcomes = FailureCases().Select(c => RunFailureOutcome(c, ValidatePublic, _fixtureDir));

        SnapshotFile.AssertMatches(string.Join("\n", outcomes), SnapshotFile.SnapshotPath("startup-validator-failures.txt"));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "ARCH-MNT-14-SC-01")]
    public void ValidConfigs_LogGoldenWarnings()
    {
        var outcomes = WarningCases().Select(c => RunWarningOutcome(c, ValidatePublic, _fixtureDir));

        SnapshotFile.AssertMatches(string.Join(string.Empty, outcomes), SnapshotFile.SnapshotPath("startup-validator-warnings.txt"));
    }
}

/// <summary>The goldens redirect <see cref="Console.Out"/>; the collection keeps every other test class from running at the same time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GoldenConsoleCollection
{
    public const string Name = "StartupValidatorGoldenConsole";
}
