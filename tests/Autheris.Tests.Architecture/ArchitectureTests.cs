using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public class ArchitectureTests
{
    private const string DomainNamespace = "Autheris.Domain";
    private const string ApplicationNamespace = "Autheris.Application";
    private const string InfrastructureNamespace = "Autheris.Infrastructure";
    private const string GraphQlNamespace = "Autheris.GraphQL";
    private const string ApiNamespace = "Autheris.Api";
    private const string ExtensionsNamespace = "Autheris.Extensions";
    private const string TrinoSqlEngineNamespace = "TrinoSqlEngine";

    [Fact]
    public void Domain_ShouldNotHaveDependencyOnOtherProjects()
    {
        var result = Types.InAssembly(typeof(Autheris.Domain.Common.Sid).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(ApplicationNamespace, InfrastructureNamespace, GraphQlNamespace, ApiNamespace, TrinoSqlEngineNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Domain layer violates Clean Architecture dependencies: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Application_ShouldNotHaveDependencyOnInfrastructureOrApi()
    {
        var result = Types.InAssembly(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespace, GraphQlNamespace, ApiNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Application layer violates Clean Architecture dependencies: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Infrastructure_ShouldNotHaveDependencyOnApi()
    {
        var result = Types.InAssembly(typeof(Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(ApiNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Infrastructure layer violates Clean Architecture dependencies: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Application_ShouldNotHaveDependencyOnAspNetCore()
    {
        var result = Types.InAssembly(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Application layer has forbidden dependency on Microsoft.AspNetCore: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Domain_ShouldNotHaveDependencyOnAspNetCore()
    {
        var result = Types.InAssembly(typeof(Autheris.Domain.Common.Sid).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Domain layer has forbidden dependency on Microsoft.AspNetCore: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void CoreLayers_ShouldNotHaveDependencyOnExtensions()
    {
        // EXT-MOVE: only the composition root (Api) references Autheris.Extensions; Domain, Application,
        // Infrastructure and GraphQL only provide interfaces, orchestration and governance logic.
        var coreAssemblies = new[]
        {
            typeof(Autheris.Domain.Common.Sid).Assembly,
            typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
            typeof(Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository).Assembly,
            typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly
        };

        foreach (var assembly in coreAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn(ExtensionsNamespace)
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(
                $"{assembly.GetName().Name} must not depend on Autheris.Extensions: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
        }
    }

    [Fact]
    public void Extensions_ShouldNotHaveDependencyOnInfrastructureGraphQlOrApi()
    {
        var result = Types.InAssembly(typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespace, GraphQlNamespace, ApiNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Autheris.Extensions may only depend on Application/Domain: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void CoreLayers_ShouldNotContainForeignSystemClients()
    {
        // EXT-MOVE: HttpClient based connectors to foreign systems (catalogs, lineage export, AI triage,
        // CDC sources) are implemented exclusively in Autheris.Extensions.
        var connectorInterfaces = new[]
        {
            typeof(Autheris.Application.Catalog.Interfaces.IDataCatalogClient),
            typeof(Autheris.Application.Catalog.Interfaces.IDataCatalogSyncService),
            typeof(Autheris.Application.Lineage.IOpenLineageClient),
            typeof(Autheris.Application.Common.Interfaces.IOpenJevClient),
            typeof(Autheris.Extensions.OpenMetadata.IOpenMetadataClient),
            typeof(Autheris.Application.Streaming.Interfaces.IMssqlChangeTrackingPoller)
        };

        var coreAssemblies = new[]
        {
            typeof(Autheris.Domain.Common.Sid).Assembly,
            typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
            typeof(Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository).Assembly,
            typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly
        };

        var violations = coreAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && connectorInterfaces.Any(i => i.IsAssignableFrom(t)))
            .Select(t => t.FullName)
            .ToList();

        violations.ShouldBeEmpty($"Foreign system connectors found in core layers: {string.Join(", ", violations)}");
    }

    [Fact]
    public void TrinoSqlEngine_ShouldNotHaveDependencyOnAutheris()
    {
        var result = Types.InAssembly(typeof(TrinoSqlEngine.FastSqlEngine).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(DomainNamespace, ApplicationNamespace, InfrastructureNamespace, GraphQlNamespace, ApiNamespace, ExtensionsNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"TrinoSqlEngine violates autonomous boundary dependencies: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void GraphQl_ShouldNotHaveDependencyOnInfrastructureOrApi()
    {
        var result = Types.InAssembly(typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespace, ApiNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"GraphQL layer violates Clean Architecture dependencies: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Application_Interfaces_ShouldStartWithI()
    {
        var result = Types.InAssembly(typeof(Autheris.Application.Services.ConsentResolutionService).Assembly)
            .That()
            .AreInterfaces()
            .Should()
            .HaveNameStartingWith("I")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Application layer interfaces must follow standard 'I' prefix naming: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Domain_Interfaces_ShouldStartWithI()
    {
        var result = Types.InAssembly(typeof(Autheris.Domain.Common.Sid).Assembly)
            .That()
            .AreInterfaces()
            .Should()
            .HaveNameStartingWith("I")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Domain layer interfaces must follow standard 'I' prefix naming: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    /// <summary>
    /// WP-ARC-B-07: endpoints whose use cases moved into Application (<c>IEffectiveFilterInspector</c>, <c>IOlapStagingService</c>)
    /// no longer touch a repository directly. <c>GovernanceEndpoints</c> is not listed: it still contains routes that use repositories.
    /// </summary>
    [Fact]
    public void MigratedEndpoints_DoNotDependOnRepositories()
    {
        var repositoryInterfaces = typeof(Autheris.Application.Services.ConsentResolutionService).Assembly.GetTypes()
            .Concat(typeof(Autheris.Domain.Common.Sid).Assembly.GetTypes())
            .Where(t => t.IsInterface && t.Name.EndsWith("Repository", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .ToArray();
        repositoryInterfaces.ShouldContain("Autheris.Application.Common.Interfaces.ITableMetadataRepository", "positive control: the repository list is populated");

        var result = Types.InAssembly(typeof(Autheris.Api.Endpoints.DuckDbOlapEndpoints).Assembly)
            .That()
            .HaveNameStartingWith("VirtualFilterEndpoints")
            .Or()
            .HaveNameStartingWith("DuckDbOlapEndpoints")
            .ShouldNot()
            .HaveDependencyOnAny(repositoryInterfaces)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Migrated endpoints must go through their use case, not a repository: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Application_has_no_vendor_named_namespaces()
    {
        var forbiddenPrefixes = new[]
        {
            $"{ApplicationNamespace}.Auditing",
            $"{ApplicationNamespace}.DataCatalog",
            $"{ApplicationNamespace}.Dbt",
            $"{ApplicationNamespace}.OpenMetadata"
        };

        var violations = typeof(Autheris.Application.Services.ConsentResolutionService).Assembly
            .GetTypes()
            .Where(t => t.Namespace is not null && forbiddenPrefixes.Any(prefix =>
                t.Namespace.Equals(prefix, StringComparison.Ordinal) ||
                t.Namespace.StartsWith(prefix + ".", StringComparison.Ordinal)))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            $"Autheris.Application must not contain types in duplicate or vendor-named namespaces: {string.Join(", ", violations)}");
    }

    // WP-SIMPL-11 (SEC-SIMPL-05, 12.1 #7): the SQLite governance family is gone. The pattern is narrowed so that the data-source guards
    // (SqliteExecutionGuard, SqliteProviderExecutionGuard) and data-source readers stay legal.
    private static readonly Regex SqliteGovernanceTypeName = new(
        @"^Sqlite\w*Governance\w*$|^SqliteBaselineSchema$|^SqliteSchemaCatalogReader$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static List<string> FindSqliteGovernanceTypes(IEnumerable<Type> types) =>
        types.Where(t => SqliteGovernanceTypeName.IsMatch(t.Name))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    [Fact]
    [Trait("Category", "Security")]
    public void No_Sqlite_governance_types_or_steps_folder()
    {
        var infrastructure = typeof(Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository).Assembly;
        var api = typeof(Autheris.Api.Configuration.GovernanceProviderGuard).Assembly;

        FindSqliteGovernanceTypes(infrastructure.GetTypes().Concat(api.GetTypes())).ShouldBeEmpty(
            "SEC-SIMPL-05: the SQLite governance store is removed; no Sqlite*Governance* type may exist in Autheris.Infrastructure or Autheris.Api");

        var stepsFolder = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Infrastructure", "Persistence", "Migrations", "Steps", "sqlite");
        Directory.Exists(stepsFolder).ShouldBeFalse("SEC-SIMPL-05: Migrations/Steps/sqlite must not exist");

        var shapeFolder = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Infrastructure", "Persistence", "Migrations", "Shape");
        Directory.EnumerateFiles(shapeFolder, "governance-shape.sqlite.*").ShouldBeEmpty("SEC-SIMPL-05: no SQLite governance shape file");
        infrastructure.GetManifestResourceNames()
            .Where(n => n.StartsWith("governance-shape.sqlite.", StringComparison.Ordinal) || n.StartsWith("governance-step.sqlite", StringComparison.Ordinal))
            .ShouldBeEmpty("SEC-SIMPL-05: no embedded SQLite governance shape or step resource");

        typeof(Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == "IsInMemorySqlite")
            .ShouldBeEmpty("SEC-SIMPL-05: the governance in-memory SQLite check is removed with the store");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Governance_sql_is_built_only_from_constants_and_parameters()
    {
        // SEC-SIMPL-26: Roslyn statement-level scan; every pattern must match at least one existing file.
        var persistenceDir = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Infrastructure", "Persistence");
        var files = new List<string>();
        foreach (var pattern in new[] { "GovernanceRepository*.cs", "AuditSqlCore.cs", "*GovernanceDialect.cs" })
        {
            var matched = Directory.GetFiles(persistenceDir, pattern, SearchOption.AllDirectories);
            matched.ShouldNotBeEmpty($"SEC-SIMPL-26 scan pattern '{pattern}' matches no file.");
            files.AddRange(matched);
        }

        var hits = files.SelectMany(f => GovernanceSqlConstructionScanner.Scan(Path.GetFileName(f), File.ReadAllText(f))).ToList();
        var allowed = GovernanceSqlAllowlist.Entries;
        var violations = hits.Where(h => !allowed.ContainsKey((h.File, h.Member))).Select(h => $"{h.File}:{h.Line} [{h.Member}] {h.Rule}").Distinct().ToList();
        violations.ShouldBeEmpty("SEC-SIMPL-26: Governance SQL must be built only from constants and parameters.");

        var stale = allowed.Keys.Where(k => !hits.Any(h => (h.File, h.Member) == k)).ToList();
        stale.ShouldBeEmpty("SEC-SIMPL-26: stale allowlist entries must be removed.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Scanner_flags_planted_violation()
    {
        // SEC-SIMPL-26 negative control on synthetic snippets.
        static List<string> Rules(string body) => GovernanceSqlConstructionScanner
            .Scan("Synthetic.cs", "class C { void M(string x, System.Text.StringBuilder sb) {" + body + "} }").Select(h => h.Rule).ToList();

        Rules("var s = $\"SELECT * FROM T WHERE a = {x}\";").ShouldContain("interpolated-sql");
        Rules("var s = string.Format(\"SELECT {0}\", x);").ShouldContain("string.Format");
        Rules("var s = \"SELECT * FROM T WHERE a = \" + x;").ShouldContain("non-constant-concatenation");
        Rules("sb.Append(x);").ShouldContain("StringBuilder.Append(non-constant)");
        Rules("var s = \"SELECT 1 \" + \"FROM T\"; sb.Append(\"A\");").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Governance_repository_logs_only_through_executor()
    {
        // SEC-SIMPL-25: logger calls in GovernanceRepository*.cs live only in these members, with exactly these call counts.
        var persistenceDir = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Infrastructure", "Persistence");
        var files = Directory.GetFiles(persistenceDir, "GovernanceRepository*.cs", SearchOption.AllDirectories);
        files.ShouldNotBeEmpty();
        var allowed = new Dictionary<string, int>
        {
            ["GovernanceRepository.cs|ExecuteReaderAsync"] = 1,
            ["GovernanceRepository.cs|ExecuteNonQueryAsync"] = 1,
            ["GovernanceRepository.cs|ExecuteScalarAsync"] = 1,
            ["GovernanceRepository.cs|LogExecutionFailure"] = 1,
            ["GovernanceRepository.ConsentGrants.cs|RecordTenantRefusalAuditAsync"] = 1,
        };

        var actual = files.SelectMany(f => GovernanceSqlConstructionScanner.LoggerCalls(Path.GetFileName(f), File.ReadAllText(f)))
            .GroupBy(c => $"{c.File}|{c.Member}").ToDictionary(g => g.Key, g => g.Count());
        actual.OrderBy(k => k.Key).ShouldBe(allowed.OrderBy(k => k.Key), "SEC-SIMPL-25: governance repository logs only through the executor.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Logger_scanner_flags_planted_logger_call()
    {
        // SEC-SIMPL-25 negative control: conditional-access and plain member calls are both seen.
        var calls = GovernanceSqlConstructionScanner.LoggerCalls("S.cs", "class C { void M() { _logger?.LogError(\"x\"); logger.LogInformation(\"y\"); other.Log(); } }");
        calls.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_A20_10_Tenant_refusal_log_carries_only_constant_code_and_consent_id()
    {
        // SEC-A20-10: the single allowlisted log call outside the executor passes only the template, the constant code and the opaque consent id.
        var file = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Infrastructure", "Persistence", "GovernanceRepository.ConsentGrants.cs");
        var call = GovernanceSqlConstructionScanner.LoggerCalls("GovernanceRepository.ConsentGrants.cs", File.ReadAllText(file)).Single(c => c.Member == "RecordTenantRefusalAuditAsync");
        call.Arguments.ShouldBe(["\"Consent tenant integrity refusal {Code} for consent {ConsentId}.\"", "ConsentTenantIntegrityException.Code", "refusal.ConsentId"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_A20_09_Tenantless_consent_overloads_used_only_from_allowlisted_files()
    {
        // SEC-A20-09: the tenant-less (cross-tenant) overloads have exactly these src callers (the dialect facades forwarding to the unified repository; the attribute-validating wrapper passes tenantId: null to the fail-closed typed overload); every other caller passes a tenant.
        var expected = new[]
        {
            "src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Consent.cs|GetActiveConsentsForSubjectsAsync|1",
            "src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Consent.cs|GetAllActiveConsentsForSubjectsAsync|1",
            "src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Consent.cs|GetActiveConsentsForSubjectsAsync|1",
            "src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Consent.cs|GetAllActiveConsentsForSubjectsAsync|1",
        };

        var actual = RepositoryScan.ProductionFiles
            .SelectMany(f => GovernanceSqlConstructionScanner.TenantlessConsentCalls(f.RelativePath, f.Text))
            .GroupBy(c => $"{c.File}|{c.Method}").Select(g => $"{g.Key}|{g.Count()}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        actual.ShouldBe(expected.OrderBy(x => x, StringComparer.Ordinal).ToList());

        // negative control
        GovernanceSqlConstructionScanner.TenantlessConsentCalls("X.cs", "class C { void M() { r.GetActiveConsentsForSubjectsAsync(s, t, now, ct); r.GetAllActiveConsentsForSubjectsAsync(s, roles, now, tenantId, ct); r.GetActiveConsentsForSubjectsAsync(s, t, now); } }")
            .Count.ShouldBe(2);
    }

    /// <summary>Mutation check of 12.1 #7: a dummy Sqlite governance type is flagged, the data-source guards are not.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public void No_Sqlite_governance_types_detector_flags_a_dummy_type_and_spares_data_source_guards()
    {
        var infrastructure = typeof(Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository).Assembly;
        var guards = infrastructure.GetTypes()
            .Where(t => t.Name is "SqliteExecutionGuard" or "SqliteProviderExecutionGuard")
            .ToList();
        guards.Count.ShouldBe(2, "the data-source SQLite guards stay (WP-SIMPL-31)");

        FindSqliteGovernanceTypes(guards).ShouldBeEmpty();
        FindSqliteGovernanceTypes([typeof(SqliteMutationGovernanceStoreDummy)]).ShouldHaveSingleItem();
        FindSqliteGovernanceTypes([typeof(SqliteBaselineSchema), typeof(SqliteSchemaCatalogReader)]).Count.ShouldBe(2);
    }

    private sealed class SqliteMutationGovernanceStoreDummy;

    private sealed class SqliteBaselineSchema;

    private sealed class SqliteSchemaCatalogReader;
}
