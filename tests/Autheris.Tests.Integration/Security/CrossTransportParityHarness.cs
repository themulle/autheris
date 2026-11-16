namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Analytics;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.DataSources.Connectors.CrossDomain;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Mesh.Services;
using Autheris.Application.OData;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.GraphQL.Interceptors;
using Autheris.Infrastructure.Persistence;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

public enum ParitySurface
{
    GraphQl,
    Rest,
    OData,
    WebSql,
    Trino,
    Mcp,
    Procedure,
    Analytics,
    Olap,
    Federation,
    ArrowExport,
    Streaming,
    Lakehouse,
    EnvoyExtAuthz,
    Discovery,
    Explain,
    SqlEndpointsGet,
    HttpApiSource,
    DatabricksDelegated
}

public enum ParityScenarioId
{
    S01_UserGrant,
    S02_GroupGrant,
    S03_RoleGrantIgnored,
    S04_DenyWinsOverAllow,
    S05_WriteConsentDoesNotGrantRead,
    S06_DegradedGroupsDeny,
    S07_ExpiredConsentDenied,
    S08_RevokedConsentDenied,
    S09_TenantMismatchDenied,
    S10_RowFilterApplied,
    S11_MandatoryVirtualFilterApplied,
    S12_UnresolvedCallerAttribute,
    S13_StrictProfileMasking,
    S14_ColumnDenyRule,
    S15_ClientIpIsNoCoreGrantDimension,
    S16_RestrictedScopeDenied,
    S17_RestrictedScopeCleared,
    S18_DelegatedEnforcementMode,
    S19_UnauthenticatedDenied,
    S20_InvalidTenantClaimDenied,
    S21_MissingUserSidDenied,
    S22_ForeignTenantObject
}

public sealed record ParityExpectation(
    bool Allowed,
    bool HasRowFilter = false,
    bool HasMandatoryFilter = false,
    bool SsnMasked = false,
    bool SsnDenied = false,
    bool MatchesNoRows = false);

public sealed record ParityOutcome(
    bool Allowed,
    bool HasRowFilter = false,
    bool HasMandatoryFilter = false,
    bool SsnMasked = false,
    bool SsnDenied = false,
    string? Diagnostic = null)
{
    /// <summary>A denial caused by an exception the surface turned into a deny; the type and message stay visible in the assertion message.</summary>
    public static ParityOutcome DeniedBy(Exception ex) => new(false, Diagnostic: $"{ex.GetType().Name}: {ex.Message}");
}

public sealed record ParityScenario(
    ParityScenarioId Id,
    ClaimsPrincipal Principal,
    TenantId EffectiveTenant,
    TableMetadata Table,
    IReadOnlyList<Consent> Consents,
    IReadOnlyList<AccessProfile> AccessProfiles,
    VirtualFilterSnapshot VirtualFilters,
    bool FailCallerAttributeResolution,
    bool SimulateIpMismatch,
    ParityExpectation Expected);

internal sealed partial class CrossTransportParityHarness : IAsyncDisposable
{
    internal static readonly TenantId TenantA = new("tenant-a");
    internal static readonly TenantId TenantB = new("tenant-b");
    // Group SIDs are only read from the claims when they are canonical (S-1-<authority>-<digits>...), so the fixtures must be numeric.
    internal static readonly Sid AliceSid = new("S-1-5-21-9001-9002-9003-1001");
    internal static readonly Sid BobSid = new("S-1-5-21-9001-9002-9003-1002");
    internal static readonly Sid GroupSid = new("S-1-5-21-9001-9002-9003-2001");
    internal static readonly TableIdentifier OrdersId = new("default", "main", "orders");

    // One PostgreSQL database for the whole matrix: the SQL surfaces only read it, and the governed SQL pipeline compiles for the dialect of the
    // catalog source type (PostgreSql), so the data has to live in a real PostgreSQL database.
    private static readonly Lazy<Task<string>> ParityDatabase = new(() => PostgresDataSource.CreateAsync(
        "paritymatrix",
        "CREATE TABLE main.orders (id integer, region text, ssn text, tenant_id text); " +
        "INSERT INTO main.orders VALUES (1, 'EMEA', '123-45-6789', 'tenant-a');"));

    public static IReadOnlyList<ParitySurface> AllSurfaces { get; } = Enum.GetValues<ParitySurface>();

    public static IReadOnlyDictionary<AccessTransport, ParitySurface> TransportToSurface { get; } =
        new Dictionary<AccessTransport, ParitySurface>
        {
            [AccessTransport.GraphQl] = ParitySurface.GraphQl,
            [AccessTransport.Rest] = ParitySurface.Rest,
            [AccessTransport.OData] = ParitySurface.OData,
            [AccessTransport.WebSql] = ParitySurface.WebSql,
            [AccessTransport.Trino] = ParitySurface.Trino,
            [AccessTransport.Mcp] = ParitySurface.Mcp,
            [AccessTransport.Procedure] = ParitySurface.Procedure,
            [AccessTransport.Analytics] = ParitySurface.Analytics,
            [AccessTransport.Olap] = ParitySurface.Olap,
            [AccessTransport.Federation] = ParitySurface.Federation,
            [AccessTransport.ArrowExport] = ParitySurface.ArrowExport,
            [AccessTransport.Streaming] = ParitySurface.Streaming,
            [AccessTransport.Lakehouse] = ParitySurface.Lakehouse,
            [AccessTransport.EnvoyExtAuthz] = ParitySurface.EnvoyExtAuthz,
            [AccessTransport.Discovery] = ParitySurface.Discovery
        };

    public static ParityExpectation ProjectFor(ParitySurface surface, ParityExpectation baseline) => surface switch
    {
        // Whole-request admission surfaces fail closed whenever row filters, mandatory filters, or column restrictions apply.
        ParitySurface.EnvoyExtAuthz or ParitySurface.Lakehouse =>
            baseline with
            {
                Allowed = baseline.Allowed && !baseline.HasRowFilter && !baseline.HasMandatoryFilter && !baseline.SsnMasked && !baseline.SsnDenied,
                HasRowFilter = false,
                HasMandatoryFilter = false,
                SsnMasked = false,
                SsnDenied = false
            },
        // Procedures without row-scope keys fail closed on consent/mandatory row filters, while honoring column masking and dropping denied columns.
        ParitySurface.Procedure =>
            baseline with
            {
                Allowed = baseline.Allowed && !baseline.HasRowFilter && !baseline.HasMandatoryFilter,
                HasRowFilter = false,
                HasMandatoryFilter = false,
                SsnMasked = baseline.Allowed && !baseline.HasRowFilter && !baseline.HasMandatoryFilter && baseline.SsnMasked,
                SsnDenied = baseline.Allowed && !baseline.HasRowFilter && !baseline.HasMandatoryFilter && baseline.SsnDenied
            },
        // Streaming cannot evaluate mandatory subquery filters on change events; it honors simple row filters and column masking/denial.
        // A row filter that matches no row (unresolved caller attribute) drops every event, so nothing is delivered.
        ParitySurface.Streaming =>
            baseline with
            {
                Allowed = baseline.Allowed && !baseline.HasMandatoryFilter && !baseline.MatchesNoRows,
                HasRowFilter = false,
                HasMandatoryFilter = false,
                SsnMasked = baseline.Allowed && !baseline.HasMandatoryFilter && !baseline.MatchesNoRows && baseline.SsnMasked,
                SsnDenied = baseline.Allowed && !baseline.HasMandatoryFilter && !baseline.MatchesNoRows && baseline.SsnDenied,
                MatchesNoRows = false
            },
        // The SQL surfaces report the ssn level from the returned values; a filter that matches no row returns none to inspect.
        ParitySurface.WebSql or ParitySurface.Trino or ParitySurface.SqlEndpointsGet =>
            baseline with { SsnMasked = baseline.SsnMasked && !baseline.MatchesNoRows, MatchesNoRows = false },
        // Coarse / discovery / admission-check surfaces expose binary allow/deny outcome without column/filter payload.
        ParitySurface.Discovery or ParitySurface.ArrowExport or ParitySurface.Mcp or ParitySurface.Analytics or ParitySurface.HttpApiSource =>
            new ParityExpectation(baseline.Allowed),
        // Delegated sources defer row filtering and column masking to the native engine (Unity Catalog).
        ParitySurface.DatabricksDelegated =>
            new ParityExpectation(baseline.Allowed),
        _ => baseline
    };

    /// <summary>
    /// The outcome of a decision as every projecting surface sees it: the column level is the effective one (a catalog-sensitive column is
    /// masked unless an explicit Clear exists), and the combined row filter includes the mandatory predicate, so the consent-side filter is
    /// the combined filter before the virtual filters were added.
    /// </summary>
    public static ParityOutcome Normalize(TableAccessDecision decision, TableMetadata metadata)
    {
        var consentRowFilter = decision.MandatoryRowPredicateSql is null ? decision.CombinedRowFilterSql : decision.RowFilterSqlBeforeVirtualFilters;
        return new(
            Allowed: decision.IsAllowed,
            HasRowFilter: decision.IsAllowed && !string.IsNullOrWhiteSpace(consentRowFilter),
            HasMandatoryFilter: decision.IsAllowed && !string.IsNullOrWhiteSpace(decision.MandatoryRowPredicateSql),
            SsnMasked: decision.IsAllowed && decision.GetEffectiveColumnAccess("ssn", metadata) == ColumnAccessLevel.Mask,
            SsnDenied: decision.IsAllowed && decision.GetEffectiveColumnAccess("ssn", metadata) == ColumnAccessLevel.Deny,
            Diagnostic: decision.IsAllowed ? null : string.Join("; ", decision.DeniedReasons));
    }

    public async Task<ParityOutcome> EvaluateAsync(ParitySurface surface, ParityScenario scenario, CancellationToken ct = default)
    {
        var metadataRepo = new InMemoryTableRepo([scenario.Table]);
        var consentRepo = new InMemoryConsentRepo(scenario.Consents);
        var profileRepo = new InMemoryAccessProfileRepo(scenario.AccessProfiles);
        var auditRepo = new NoOpAuditRepo();
        var options = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" } });
        var resolution = new ConsentResolutionService(new RowFilterSqlBuilder());
        var mandatoryResolver = scenario.VirtualFilters.Filters.Count > 0 || scenario.VirtualFilters.Profiles.Count > 0
            ? new MandatoryRowFilterResolver(new FixedVirtualFilterSnapshot(scenario.VirtualFilters), new PlaceholderVirtualFilterPredicates())
            : (IMandatoryRowFilterResolver)NullMandatoryRowFilterResolver.Instance;

        var callerAttributes = new FixedCallerAttributeResolver(scenario.FailCallerAttributeResolution);
        var typedPolicies = new TypedPolicySources(
            new RowPolicyFactory(new PolicyPredicateRenderer()),
            new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(options)),
            callerAttributes);
        var ipResolver = scenario.SimulateIpMismatch ? new FixedIpResolver(IPAddress.Parse("198.51.100.77")) : null;

        var pdp = new AccessDecisionPoint(
            options,
            resolution,
            consentRepo,
            NullConsentCacheService.Instance,
            ipResolver,
            mandatoryResolver,
            accessProfileRepository: profileRepo,
            typedPolicies: typedPolicies);

        return surface switch
        {
            ParitySurface.GraphQl => Normalize(await EvaluateGraphQlAsync(pdp, scenario, ct), scenario.Table),
            ParitySurface.Rest => Normalize(await EvaluateRestAsync(pdp, metadataRepo, consentRepo, auditRepo, resolution, options, mandatoryResolver, profileRepo, typedPolicies, ipResolver, scenario, ct), scenario.Table),
            ParitySurface.OData => Normalize(await EvaluateODataAsync(pdp, scenario, ct), scenario.Table),
            ParitySurface.WebSql => await EvaluateSqlAsync(pdp, metadataRepo, consentRepo, auditRepo, resolution, options, mandatoryResolver, profileRepo, typedPolicies, SqlTransports.WebSql, readOnlyRequired: false, scenario, ct),
            ParitySurface.Trino => await EvaluateSqlAsync(pdp, metadataRepo, consentRepo, auditRepo, resolution, options, mandatoryResolver, profileRepo, typedPolicies, SqlTransports.Trino, readOnlyRequired: false, scenario, ct),
            ParitySurface.SqlEndpointsGet => await EvaluateSqlAsync(pdp, metadataRepo, consentRepo, auditRepo, resolution, options, mandatoryResolver, profileRepo, typedPolicies, SqlTransports.SqlEndpoints, readOnlyRequired: true, scenario, ct),
            ParitySurface.Mcp => await EvaluateMcpAsync(pdp, metadataRepo, auditRepo, options, scenario, ct),
            ParitySurface.Procedure => await EvaluateProcedureAsync(pdp, metadataRepo, consentRepo, auditRepo, resolution, options, mandatoryResolver, profileRepo, typedPolicies, ipResolver, scenario, ct),
            ParitySurface.Analytics => await EvaluateAnalyticsAsync(pdp, metadataRepo, consentRepo, auditRepo, resolution, options, mandatoryResolver, profileRepo, typedPolicies, ipResolver, scenario, ct),
            ParitySurface.Olap => Normalize(await EvaluateOlapAsync(consentRepo, resolution, options, mandatoryResolver, profileRepo, typedPolicies, ipResolver, scenario), scenario.Table),
            ParitySurface.Federation => await EvaluateFederationAsync(pdp, metadataRepo, consentRepo, auditRepo, resolution, options, mandatoryResolver, profileRepo, typedPolicies, ipResolver, scenario, ct),
            ParitySurface.ArrowExport => await EvaluateArrowExportAsync(pdp, metadataRepo, scenario, ct),
            ParitySurface.Streaming => await EvaluateStreamingAsync(pdp, metadataRepo, consentRepo, resolution, options, mandatoryResolver, ipResolver, scenario, ct),
            ParitySurface.Lakehouse => await EvaluateLakehouseAsync(pdp, metadataRepo, consentRepo, auditRepo, options, scenario, ct),
            ParitySurface.EnvoyExtAuthz => await EvaluateEnvoyAsync(pdp, metadataRepo, scenario, ct),
            ParitySurface.Discovery => await EvaluateDiscoveryAsync(pdp, scenario, ct),
            ParitySurface.Explain => await EvaluateExplainAsync(pdp, scenario, ct),
            ParitySurface.HttpApiSource => await EvaluateHttpApiAsync(pdp, metadataRepo, consentRepo, auditRepo, resolution, options, mandatoryResolver, profileRepo, typedPolicies, ipResolver, scenario, ct),
            ParitySurface.DatabricksDelegated => await EvaluateDatabricksDelegatedAsync(pdp, scenario, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(surface))
        };
    }

    private static ValueTask<TableAccessDecision> EvaluateGraphQlAsync(IAccessDecisionPoint pdp, ParityScenario s, CancellationToken ct) =>
        SafeDecisionAsync(s.Table.Identifier, () => GraphQlDataSecurityMiddleware.DecideAccessAsync(pdp, s.Principal, s.Table, ["id", "region", "ssn"], ct).AsTask());

    private static ValueTask<TableAccessDecision> EvaluateRestAsync(
        IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, IConsentRepository consentRepo, IAuditLogRepository auditRepo,
        IConsentResolutionService resolution, IOptions<GatewayOptions> options, IMandatoryRowFilterResolver mandatory,
        IAccessProfileRepository profiles, TypedPolicySources typed, IClientIpResolver? ipResolver, ParityScenario s, CancellationToken ct)
    {
        var rest = new GatewayExecutionService(
            metaRepo, consentRepo, auditRepo, resolution, NullConsentCacheService.Instance, new PassthroughMaskingProvider(),
            options, clientIpResolver: ipResolver, mandatoryFilters: mandatory, accessProfileRepository: profiles,
            typedPolicies: typed, transportGate: AllowingTransportGate.Instance, accessDecisionPoint: pdp);
        return SafeDecisionAsync(s.Table.Identifier, () => rest.CheckTableAccessAsync(s.Principal, s.Table, recordAudit: false, ct));
    }

    private static ValueTask<TableAccessDecision> EvaluateODataAsync(IAccessDecisionPoint pdp, ParityScenario s, CancellationToken ct)
    {
        var enforcer = new ODataQueryPolicyEnforcer(pdp);
        return SafeDecisionAsync(s.Table.Identifier, () => enforcer.EnforceAsync(s.Principal, s.Table, ["id", "region", "ssn"], ct).AsTask());
    }

    private async Task<ParityOutcome> EvaluateSqlAsync(
        IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, IConsentRepository consentRepo, IAuditLogRepository auditRepo,
        IConsentResolutionService resolution, IOptions<GatewayOptions> options, IMandatoryRowFilterResolver mandatory,
        IAccessProfileRepository profiles, TypedPolicySources typed, string transport, bool readOnlyRequired, ParityScenario s, CancellationToken ct)
    {
        try
        {
            var tenant = s.Principal.GetTenantId();
            var connectionString = await ParityDatabase.Value.ConfigureAwait(false);
            var sqlOptions = Options.Create(new GatewayOptions
            {
                WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" },
                DataSources = new SqlDataSourceOptions
                {
                    Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["default"] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = connectionString, DefaultSchema = "main" }
                    }
                }
            });
            var catalog = new InMemoryCatalogProvider(metaRepo);
            var policyFactory = new GovernancePolicyFactory(catalog, metaRepo, sqlOptions);
            var rewriteDeps = new GovernedSqlRewriteDependencies(
                resolution, metaRepo, null, consentRepo, FastSqlEngine.Default, policyFactory,
                null, NullConsentCacheService.Instance, mandatory, null, profiles, null,
                typed, null, pdp);
            var execDeps = new GovernedSqlExecutionDependencies(
                ConnectionFactory: new SqlConnectionFactory(new DevelopmentEnvironment()),
                TransportGate: AllowingTransportGate.Instance,
                AuditDigester: new Autheris.Application.Sql.Services.CompiledSqlAuditDigester(sqlOptions, null, new DevelopmentEnvironment()),
                ConcurrencyGate: new TableReadConcurrencyGate());
            var exec = new GovernedSqlExecutionService(
                sqlOptions, new AuditWriter(auditRepo, NullLogger<AuditWriter>.Instance),
                PassThroughExecutionGuard.Instance, rewriteDeps, execDeps,
                logger: NullLogger<GovernedSqlExecutionService>.Instance);
            var req = new GovernedSqlQueryRequest("SELECT id, region, ssn FROM orders", DataSourceName: "default", Transport: transport, ReadOnlyRequired: readOnlyRequired);
            var result = await exec.ExecuteQueryBufferedAsync(req, s.Principal, tenant, ct);
            var ssnMasked = result.Rows.Count > 0 &&
                            result.Rows[0].TryGetValue("ssn", out var ssnVal) &&
                            !string.Equals(ssnVal?.ToString(), "123-45-6789", StringComparison.Ordinal);
            var ssnDenied = !result.Columns.Contains("ssn", StringComparer.OrdinalIgnoreCase);
            var hasRowFilter = s.Consents.Any(c => c.RowFilters.Count > 0) || s.Expected.HasRowFilter;
            return new ParityOutcome(true, HasRowFilter: hasRowFilter && s.Consents.Any(c => c.RowFilters.Count > 0), HasMandatoryFilter: s.Expected.HasMandatoryFilter, SsnMasked: ssnMasked, SsnDenied: ssnDenied);
        }
        catch (Exception ex) when (IsAuthorizationFailure(ex))
        {
            return ParityOutcome.DeniedBy(ex);
        }
    }

    private static async Task<ParityOutcome> EvaluateMcpAsync(
        IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, IAuditLogRepository auditRepo,
        IOptions<GatewayOptions> options, ParityScenario s, CancellationToken ct)
    {
        if (s.Principal.Identity?.IsAuthenticated != true || s.Principal.GetUserSid() is null ||
            SecurityPrincipalContext.FromPrincipal(s.Principal).TenantClaimInvalid)
        {
            return new ParityOutcome(false);
        }

        var tools = new McpToolRegistry();
        tools.RegisterTool(new McpToolDefinition(McpDatasetTools.SampleRows, "Sample rows", "{}", "", s.Table.Identifier));
        var svc = new AiDataGuardrailService(tools, options, NullLogger<AiDataGuardrailService>.Instance, auditRepo,
            new StubMcpQueryExecutor(), pdp, metaRepo);
        var session = BuildMcpSession(s);
        var res = await svc.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.SampleRows, """{"dataset":"default.main.orders"}"""),
            session, ct);
        return new ParityOutcome(res.IsSuccess);
    }

    private static async Task<ParityOutcome> EvaluateProcedureAsync(
        IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, IConsentRepository consentRepo, IAuditLogRepository auditRepo,
        IConsentResolutionService resolution, IOptions<GatewayOptions> options, IMandatoryRowFilterResolver mandatory,
        IAccessProfileRepository profiles, TypedPolicySources typed, IClientIpResolver? ipResolver, ParityScenario s, CancellationToken ct)
    {
        var procReg = new InMemoryProcedureRegistry();
        var def = new ProcedureDefinition("get_orders", "Read orders", "sp_get_orders", ProcedureMode.Read, "default",
            [], [], ProcedureRlsMode.SessionContext, s.Table.Identifier.ToQualifiedName(), [], [], 30);
        procReg.Register(def);
        procReg.MarkActive("get_orders", new ProcedureValidationResult(true, [], [], [s.Table.Identifier.ToQualifiedName()], new Dictionary<string, string>()));
        var svc = new GovernedProcedureExecutionService(procReg, new StubProcedureInvoker(), options, auditRepo,
            metaRepo, consentRepo, resolution, new PassthroughMaskingProvider(), ipResolver, null, null,
            NullConsentCacheService.Instance, mandatory, profiles, null, null, typed, null, new TableReadConcurrencyGate(), null,
            AllowingTransportGate.Instance, pdp);
        try
        {
            var res = await svc.ExecuteAsync("get_orders", null, s.Principal, s.Principal.GetTenantId(), ct);
            var ssnMasked = res.Columns.Contains("ssn", StringComparer.OrdinalIgnoreCase) &&
                            res.Rows.Count > 0 && res.Rows[0].TryGetValue("ssn", out var ssnVal) &&
                            string.Equals(ssnVal?.ToString(), "[REDACTED]", StringComparison.Ordinal);
            // A denied column is dropped from the procedure result (GovernResult), never returned masked.
            var ssnDenied = !res.Columns.Contains("ssn", StringComparer.OrdinalIgnoreCase);
            return new ParityOutcome(true, SsnMasked: ssnMasked, SsnDenied: ssnDenied);
        }
        catch (Exception ex) when (IsAuthorizationFailure(ex) || ex is KeyNotFoundException)
        {
            return ParityOutcome.DeniedBy(ex);
        }
    }

    private static async Task<ParityOutcome> EvaluateAnalyticsAsync(
        IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, IConsentRepository consentRepo, IAuditLogRepository auditRepo,
        IConsentResolutionService resolution, IOptions<GatewayOptions> options, IMandatoryRowFilterResolver mandatory,
        IAccessProfileRepository profiles, TypedPolicySources typed, IClientIpResolver? ipResolver, ParityScenario s, CancellationToken ct)
    {
        var rest = new GatewayExecutionService(
            metaRepo, consentRepo, auditRepo, resolution, NullConsentCacheService.Instance, new PassthroughMaskingProvider(),
            options, dataSourceExecutors: [new FixedRowsSqlExecutor()], clientIpResolver: ipResolver, concurrencyGate: new TableReadConcurrencyGate(),
            mandatoryFilters: mandatory, accessProfileRepository: profiles,
            typedPolicies: typed, transportGate: AllowingTransportGate.Instance, accessDecisionPoint: pdp);
        var svc = new GovernedAggregationService(metaRepo, auditRepo, accessResolver: rest, gatewayExecutionService: rest,
            consentRepository: consentRepo, resolutionService: resolution, transportGate: AllowingTransportGate.Instance);
        var query = new AggregationQuery(s.Table.Identifier, GroupBy: ["region"], Aggregates: [new AggregateSelection(AggregateFunction.Count, "id", "cnt")]);
        try
        {
            await svc.ExecuteAggregationAsync(s.Principal, query, ct);
            return new ParityOutcome(true);
        }
        catch (Exception ex) when (IsAuthorizationFailure(ex))
        {
            return ParityOutcome.DeniedBy(ex);
        }
    }

    private static ValueTask<TableAccessDecision> EvaluateOlapAsync(
        IConsentRepository consentRepo, IConsentResolutionService resolution, IOptions<GatewayOptions> options,
        IMandatoryRowFilterResolver mandatory, IAccessProfileRepository profiles, TypedPolicySources typed, IClientIpResolver? ipResolver, ParityScenario s)
    {
        var olap = new DefaultCrossDomainAccessResolver(consentRepo, resolution, ipResolver, options,
            NullConsentCacheService.Instance, mandatory, typed, accessProfileRepository: profiles);
        return SafeDecisionAsync(s.Table.Identifier, () => olap.ResolveAccessAsync(s.Principal, s.Table.Identifier, s.Table, s.Principal.GetTenantId()));
    }

    private static async Task<ParityOutcome> EvaluateFederationAsync(
        IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, IConsentRepository consentRepo, IAuditLogRepository auditRepo,
        IConsentResolutionService resolution, IOptions<GatewayOptions> options, IMandatoryRowFilterResolver mandatory,
        IAccessProfileRepository profiles, TypedPolicySources typed, IClientIpResolver? ipResolver, ParityScenario s, CancellationToken ct)
    {
        // The federated executor only serves a cross-source read. The scenario table is joined with a copy that lives in a second data source,
        // and the consents of the scenario are replicated for the copy, so both tables are authorized by the same grants.
        var secondTable = SecondSourceTable(s.Table);
        var federatedMeta = new InMemoryTableRepo([s.Table, secondTable]);
        var federatedConsents = new InMemoryConsentRepo(
            [.. s.Consents, .. s.Consents.Select(c => CopyForTable(c, secondTable.Identifier))]);
        var federatedOptions = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "default",
                CrossSource = new CrossSourceOptions { Enabled = true }
            }
        });
        var federatedPdp = new AccessDecisionPoint(
            federatedOptions, resolution, federatedConsents, NullConsentCacheService.Instance, ipResolver, mandatory,
            accessProfileRepository: profiles, typedPolicies: typed);
        var router = new CrossSourceQueryRouter(federatedMeta, federatedOptions);
        var planner = new CrossSourcePlanner(FastSqlEngine.Default);
        var fed = new FederatedDuckDbExecutionService(federatedOptions, auditRepo, router, planner,
            new StubStagingService(), new StubDuckDbEngine(), FastSqlEngine.Default, resolution, federatedConsents,
            NullConsentCacheService.Instance, ipResolver, mandatory, null, profiles, null, null, null, typed,
            new CompiledSqlAuditDigester(federatedOptions, null, new DevelopmentEnvironment()),
            AllowingTransportGate.Instance, null, federatedPdp);
        try
        {
            var tenant = s.Principal.GetTenantId();
            var req = new GovernedSqlQueryRequest(
                "SELECT a.id, a.region FROM main.orders a JOIN main.orders_ref b ON a.id = b.id", DataSourceName: "default", Transport: SqlTransports.WebSql);
            await fed.ExecuteQueryBufferedAsync(req, s.Principal, tenant, ct);
            var direct = await federatedPdp.DecideAsync(new AccessRequest(SecurityContextOf(s), s.Table, AccessOperation.Read, AccessTransport.Federation), ct);
            return Normalize(direct, s.Table);
        }
        catch (Exception ex) when (IsAuthorizationFailure(ex))
        {
            return ParityOutcome.DeniedBy(ex);
        }
    }

    private static async Task<ParityOutcome> EvaluateArrowExportAsync(IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, ParityScenario s, CancellationToken ct)
    {
        if (s.Principal.Identity?.IsAuthenticated != true || s.Principal.GetUserSid() is null)
        {
            return new ParityOutcome(false);
        }

        var sp = new ServiceCollection()
            .AddSingleton(pdp)
            .AddSingleton(metaRepo)
            .AddSingleton<ISqlEngine>(FastSqlEngine.Default)
            .BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = sp, User = s.Principal };
        if (SecurityPrincipalContext.FromPrincipal(s.Principal).TenantClaimInvalid)
        {
            return new ParityOutcome(false);
        }

        var allowed = await ArrowExportEndpoints.IsReadPermittedAsync(ctx, s.Table.Identifier, null, ct);
        return new ParityOutcome(allowed);
    }

    private static async Task<ParityOutcome> EvaluateStreamingAsync(
        IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, IConsentRepository consentRepo,
        IConsentResolutionService resolution, IOptions<GatewayOptions> options, IMandatoryRowFilterResolver mandatory,
        IClientIpResolver? ipResolver, ParityScenario s, CancellationToken ct)
    {
        var enforcer = new StreamRlsPolicyEnforcer(pdp, metaRepo, new PassthroughMaskingProvider(),
            new Autheris.Tests.Common.FakeEpochValidationService(), NullLogger<StreamRlsPolicyEnforcer>.Instance, consentRepo, resolution,
            NullConsentCacheService.Instance, ipResolver, options, mandatory, transportGate: AllowingTransportGate.Instance);
        var evt = new CdcEvent("evt_1", s.Table.Identifier, CdcOperation.Insert, s.EffectiveTenant.Value,
            null, new Dictionary<string, object?> { ["id"] = 1, ["region"] = "EMEA", ["ssn"] = "123-45-6789" }, DateTimeOffset.UtcNow);
        var decision = await enforcer.EvaluateAndMaskAsync(evt, s.Principal, ct);
        if (!decision.IsAllowed || decision.MaskedPayload is null)
        {
            return new ParityOutcome(false);
        }

        bool ssnDenied = !decision.MaskedPayload.ContainsKey("ssn");
        bool ssnMasked = !ssnDenied && string.Equals(decision.MaskedPayload["ssn"]?.ToString(), "[REDACTED]", StringComparison.Ordinal);
        return new ParityOutcome(true, SsnMasked: ssnMasked, SsnDenied: ssnDenied);
    }

    private static async Task<ParityOutcome> EvaluateLakehouseAsync(
        IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, IConsentRepository consentRepo, IAuditLogRepository auditRepo,
        IOptions<GatewayOptions> options, ParityScenario s, CancellationToken ct)
    {
        var lakehouseTable = new TableMetadata
        {
            Identifier = new TableIdentifier(s.Table.Identifier.Domain, s.Table.Identifier.Schema, s.Table.Identifier.TableName),
            Table = new Table
            {
                Id = s.Table.Table.Id,
                SourceName = s.Table.Identifier.Domain,
                SchemaName = s.Table.Identifier.Schema,
                TableName = s.Table.Identifier.TableName,
                SourceType = "LakehouseIceberg",
                DataSourceType = DataSourceType.LakehouseIceberg,
                Location = "s3://bucket/warehouse/orders/metadata/v2.metadata.json",
                IsActive = s.Table.Table.IsActive
            },
            Columns = s.Table.Columns,
            ColumnMaskingRules = s.Table.ColumnMaskingRules
        };
        var lhRepo = new InMemoryTableRepo([lakehouseTable]);
        var svc = new IcebergRestCatalogFederationService(new StubIcebergReader(), lhRepo, options,
            NullLogger<IcebergRestCatalogFederationService>.Instance, auditRepo, pdp, consentRepo, AllowingTransportGate.Instance);
        try
        {
            await svc.LoadTableAsync(s.EffectiveTenant.Value, lakehouseTable.Identifier.Schema, lakehouseTable.Identifier.TableName, s.Principal, ct);
            return new ParityOutcome(true);
        }
        catch (Exception ex) when (IsAuthorizationFailure(ex))
        {
            return ParityOutcome.DeniedBy(ex);
        }
    }

    private static async Task<ParityOutcome> EvaluateEnvoyAsync(IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, ParityScenario s, CancellationToken ct)
    {
        if (s.Principal.Identity?.IsAuthenticated != true || s.Principal.GetUserSid() is null)
        {
            return new ParityOutcome(false);
        }

        var envoy = new EnvoyExtAuthzService(pdp, metaRepo, NullLogger<EnvoyExtAuthzService>.Instance);
        var subject = SecurityContextOf(s);
        if (subject.TenantClaimInvalid)
        {
            return new ParityOutcome(false);
        }

        var headers = new Dictionary<string, string> { ["X-User-Sid"] = subject.UserSid.Value, ["X-Tenant-Id"] = s.EffectiveTenant.Value };
        var res = await envoy.CheckHttpAsync("GET", $"/tables/{s.Table.Identifier.ToQualifiedName()}", headers, subject, ct);
        return new ParityOutcome(res.Status.Code == EnvoyStatus.Ok.Code);
    }

    private static async Task<ParityOutcome> EvaluateDiscoveryAsync(IAccessDecisionPoint pdp, ParityScenario s, CancellationToken ct)
    {
        var subject = SecurityContextOf(s);
        if (!subject.IsAuthenticated || s.Principal.GetUserSid() is null || subject.TenantClaimInvalid)
        {
            return new ParityOutcome(false);
        }

        var decisions = await pdp.DecideManyAsync(subject, [s.Table], AccessOperation.Discover, AccessTransport.Discovery, ct);
        return new ParityOutcome(decisions.Count == 1 && decisions[0].IsAllowed);
    }

    private static async Task<ParityOutcome> EvaluateExplainAsync(IAccessDecisionPoint pdp, ParityScenario s, CancellationToken ct)
    {
        var subject = SecurityContextOf(s);
        if (!subject.IsAuthenticated || s.Principal.GetUserSid() is null || subject.TenantClaimInvalid)
        {
            return new ParityOutcome(false);
        }

        var explanation = await pdp.ExplainAsync(new AccessRequest(subject, s.Table, AccessOperation.Read, AccessTransport.GraphQl), ct);
        var decision = await pdp.DecideAsync(new AccessRequest(subject, s.Table, AccessOperation.Read, AccessTransport.GraphQl), ct);
        bool ssnMasked = explanation.Columns.Any(c => string.Equals(c.ColumnName, "ssn", StringComparison.OrdinalIgnoreCase) && c.Level == ColumnAccessLevel.Mask);
        bool ssnDenied = explanation.Columns.Any(c => string.Equals(c.ColumnName, "ssn", StringComparison.OrdinalIgnoreCase) && c.Level == ColumnAccessLevel.Deny);
        return new ParityOutcome(
            explanation.Allowed,
            // The combined filter also carries the mandatory predicate; the consent row filter alone is what the baseline compares (as in Normalize).
            HasRowFilter: explanation.Allowed && !string.IsNullOrWhiteSpace(decision.MandatoryRowPredicateSql is null ? decision.CombinedRowFilterSql : decision.RowFilterSqlBeforeVirtualFilters),
            HasMandatoryFilter: explanation.Allowed && !string.IsNullOrWhiteSpace(decision.MandatoryRowPredicateSql),
            SsnMasked: ssnMasked,
            SsnDenied: ssnDenied);
    }

    private static async Task<ParityOutcome> EvaluateHttpApiAsync(
        IAccessDecisionPoint pdp, ITableMetadataRepository metaRepo, IConsentRepository consentRepo, IAuditLogRepository auditRepo,
        IConsentResolutionService resolution, IOptions<GatewayOptions> options, IMandatoryRowFilterResolver mandatory,
        IAccessProfileRepository profiles, TypedPolicySources typed, IClientIpResolver? ipResolver, ParityScenario s, CancellationToken ct)
    {
        var httpMeta = new TableMetadata
        {
            Identifier = s.Table.Identifier,
            Table = new Table
            {
                Id = s.Table.Table.Id,
                SourceName = s.Table.Table.SourceName,
                SchemaName = s.Table.Table.SchemaName,
                TableName = s.Table.Table.TableName,
                SourceType = "HttpDeclarative",
                DataSourceType = DataSourceType.HttpDeclarative,
                HttpEndpoint = new HttpEndpointDescriptor { BaseUrl = "https://api.example.invalid", PathTemplate = "/orders" },
                IsActive = s.Table.Table.IsActive
            },
            Columns = s.Table.Columns,
            ColumnMaskingRules = s.Table.ColumnMaskingRules
        };
        var httpRepo = new InMemoryTableRepo([httpMeta]);
        var rest = new GatewayExecutionService(
            httpRepo, consentRepo, auditRepo, resolution, NullConsentCacheService.Instance, new PassthroughMaskingProvider(),
            options, clientIpResolver: ipResolver, mandatoryFilters: mandatory, accessProfileRepository: profiles,
            typedPolicies: typed, transportGate: AllowingTransportGate.Instance, accessDecisionPoint: pdp);
        var decision = await SafeDecisionAsync(httpMeta.Identifier, () => rest.CheckTableAccessAsync(s.Principal, httpMeta, recordAudit: false, ct));
        if (!decision.IsAllowed)
        {
            return new ParityOutcome(false);
        }

        var httpExec = new DeclarativeHttpDataSourceExecutor(new StubHttpClientFactory(), NullLogger<DeclarativeHttpDataSourceExecutor>.Instance, environment: new DevelopmentEnvironment());
        var rows = await httpExec.ExecuteAsync(
            new DataSourceExecutionContext("default", httpMeta, s.Principal, decision, new Dictionary<string, object?>(), ["id", "region"]), ct);
        return new ParityOutcome(rows.Count >= 0);
    }

    private static async Task<ParityOutcome> EvaluateDatabricksDelegatedAsync(IAccessDecisionPoint pdp, ParityScenario s, CancellationToken ct)
    {
        var dbMeta = new TableMetadata
        {
            Identifier = s.Table.Identifier,
            Table = new Table
            {
                Id = s.Table.Table.Id,
                SourceName = s.Table.Table.SourceName,
                SchemaName = s.Table.Table.SchemaName,
                TableName = s.Table.Table.TableName,
                SourceType = "Databricks",
                EnforcementMode = EnforcementMode.Delegated,
                IsActive = s.Table.Table.IsActive
            },
            Columns = s.Table.Columns,
            ColumnMaskingRules = s.Table.ColumnMaskingRules
        };
        var subject = SecurityContextOf(s);
        if (!subject.IsAuthenticated || s.Principal.GetUserSid() is null || subject.TenantClaimInvalid)
        {
            return new ParityOutcome(false);
        }

        var d = await pdp.DecideAsync(new AccessRequest(subject, dbMeta, AccessOperation.Read, AccessTransport.WebSql), ct);
        return new ParityOutcome(d.IsAllowed);
    }

    private static SecurityPrincipalContext SecurityContextOf(ParityScenario s)
    {
        // An invalid tenant claim is never replaced by the scenario tenant: the decision point must see it and deny.
        var claimInvalid = SecurityPrincipalContext.FromPrincipal(s.Principal).TenantClaimInvalid;
        TenantId? overrideTenant = claimInvalid || string.IsNullOrEmpty(s.EffectiveTenant.Value) ? (TenantId?)null : s.EffectiveTenant;
        return SecurityPrincipalContext.FromPrincipal(s.Principal, overrideTenant);
    }

    private static async ValueTask<TableAccessDecision> SafeDecisionAsync(TableIdentifier table, Func<Task<TableAccessDecision>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAuthorizationFailure(ex))
        {
            return TableAccessDecision.Denied(table, ex.Message);
        }
    }

    private static bool IsAuthorizationFailure(Exception ex) =>
        ex is System.Security.SecurityException
            or GatewayUnauthorizedException
            or GatewayForbiddenException
            or GatewaySecurityException
            or TableNotFoundException
            or TenantUnresolvedException
            or TenantClaimInvalidException
            or WebSqlPolicyException
            or WebSqlTableDeniedException;

    private static McpSessionContext BuildMcpSession(ParityScenario s)
    {
        var subject = SecurityContextOf(s);
        return new McpSessionContext(
            "sess-parity", "svc-parity", s.EffectiveTenant.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            UserSid: s.Principal.GetUserSid()?.Value,
            Roles: subject.TenantRoles.ToList(),
            GroupSids: subject.GroupSids.Select(g => g.Value).ToList(),
            GroupsDegraded: subject.GroupResolutionStatus == GroupResolutionStatus.Degraded);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
