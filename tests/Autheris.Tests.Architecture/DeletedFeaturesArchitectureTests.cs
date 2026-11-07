namespace Autheris.Tests.Architecture;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DeletedFeaturesArchitectureTests
{
    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(Autheris.Domain.Options.GatewayOptions).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
        typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly,
        typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly,
        typeof(Program).Assembly
    ];

    [Theory]
    [InlineData("Autheris.Infrastructure.Garnet")]
    [InlineData("Autheris.Application." + "Diagnostics.Shadowing")]
    [InlineData("Autheris.Extensions.Itsm")]
    [InlineData("Autheris.Extensions.Backstage")]
    [InlineData("Autheris.Infrastructure.Itsm")]
    [InlineData("Autheris.Application.Integrations.Backstage")]
    public void Deleted_feature_namespaces_have_no_types(string deletedNamespace)
    {
        var typesInNamespace = ProductionAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace != null && (t.Namespace == deletedNamespace || t.Namespace.StartsWith(deletedNamespace + ".", StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .ToList();

        typesInNamespace.ShouldBeEmpty($"Types in deleted namespace '{deletedNamespace}' must not exist.");
    }

    [Theory]
    [InlineData("Autheris.Infrastructure.Garnet", "src/Autheris.Infrastructure/Garnet")]
    [InlineData("Autheris.Application." + "Diagnostics.Shadowing", "src/Autheris.Application/Diagnostics/Shadowing")]
    [InlineData("Autheris.Extensions.Itsm", "src/Autheris.Extensions/Itsm")]
    [InlineData("Autheris.Extensions.Backstage", "src/Autheris.Extensions/Backstage")]
    [InlineData("Autheris.Infrastructure.Itsm", "src/Autheris.Infrastructure/Itsm")]
    [InlineData("Autheris.Application.Integrations.Backstage", "src/Autheris.Application/Integrations/Backstage")]
    public void Deleted_feature_directories_do_not_exist(string featureName, string relativeDirectory)
    {
        var fullPath = Path.Combine(RepositoryScan.RepoRoot, relativeDirectory);
        Directory.Exists(fullPath).ShouldBeFalse($"Deleted feature directory '{relativeDirectory}' ({featureName}) must not exist.");
    }

    [Theory]
    [InlineData("ArrowFlightSqlEndpoints")]
    [InlineData("ArrowFlightSqlServer")]
    [InlineData("IArrowFlightSqlServer")]
    [InlineData("DevPortalEndpoints")]
    [InlineData("DevPortalAssets")]
    [InlineData("CatalogWebhookHandler")]
    [InlineData("DataCatalogClientFactory")]
    [InlineData("IDataCatalogClientFactory")]
    [InlineData("IDataCatalogWebhookHandler")]
    [InlineData("JustificationAndBreakGlassInterceptor")]
    public void Deleted_Types_Do_Not_Exist_In_Production_Assemblies(string typeName)
    {
        foreach (var assembly in ProductionAssemblies)
        {
            var found = assembly.GetTypes().Any(t => t.Name == typeName || t.FullName?.EndsWith("." + typeName, StringComparison.Ordinal) == true);
            found.ShouldBeFalse($"Type {typeName} was found in {assembly.GetName().Name}, but should have been deleted.");
        }
    }

    [Theory]
    [InlineData("Purview")]
    [InlineData("Collibra")]
    [InlineData("Alation")]
    [InlineData("DataHub")]
    [InlineData("Backstage")]
    [InlineData("ServiceNow")]
    [InlineData("JiraCloud")]
    [InlineData("ItsmWebhook")]
    [InlineData("ItsmOutbox")]
    [InlineData("ItsmWorkflow")]
    [InlineData("ItsmConsentRequest")]
    public void Deleted_Type_Prefixes_Do_Not_Exist_In_Production_Assemblies(string prefix)
    {
        foreach (var assembly in ProductionAssemblies)
        {
            var matches = assembly.GetTypes()
                .Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(t => t.FullName)
                .ToList();
            matches.ShouldBeEmpty($"Types starting with '{prefix}' were found in {assembly.GetName().Name}: {string.Join(", ", matches)}");
        }
    }
}
