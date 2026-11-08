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
}
