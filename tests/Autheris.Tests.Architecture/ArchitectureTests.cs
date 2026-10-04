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

    [Fact]
    public void Domain_ShouldNotHaveDependencyOnOtherProjects()
    {
        var result = Types.InAssembly(typeof(Autheris.Domain.Common.Sid).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(ApplicationNamespace, InfrastructureNamespace, GraphQlNamespace, ApiNamespace)
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
        var result = Types.InAssembly(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly)
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
            typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
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
        // EXT-MOVE: HttpClient based connectors to foreign systems (catalogs, ITSM, lineage export, AI triage,
        // Backstage export, CDC sources) are implemented exclusively in Autheris.Extensions.
        var connectorInterfaces = new[]
        {
            typeof(Autheris.Application.DataCatalog.Interfaces.IDataCatalogClient),
            typeof(Autheris.Application.DataCatalog.Interfaces.IDataCatalogClientFactory),
            typeof(Autheris.Application.DataCatalog.Interfaces.IDataCatalogSyncService),
            typeof(Autheris.Application.Interfaces.IItsmWorkflowClient),
            typeof(Autheris.Application.Interfaces.IItsmWebhookHandler),
            typeof(Autheris.Application.Interfaces.IOpenLineageClient),
            typeof(Autheris.Application.Interfaces.IOpenJevClient),
            typeof(Autheris.Application.OpenMetadata.Interfaces.IOpenMetadataClient),
            typeof(Autheris.Application.Integrations.Backstage.IBackstageCatalogExportService),
            typeof(Autheris.Application.Streaming.Interfaces.IMssqlChangeTrackingPoller)
        };

        var coreAssemblies = new[]
        {
            typeof(Autheris.Domain.Common.Sid).Assembly,
            typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
            typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
            typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly
        };

        var violations = coreAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && connectorInterfaces.Any(i => i.IsAssignableFrom(t)))
            .Select(t => t.FullName)
            .ToList();

        violations.ShouldBeEmpty($"Foreign system connectors found in core layers: {string.Join(", ", violations)}");
    }
}
