using System;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class DiscoveryArchitectureTests
{
    private static readonly string[] ForbiddenConsentStoreTokens =
    [
        "ConsentResolutionService",
        "IConsentResolutionService",
        "GranteeMatcher",
        "GetAllActiveConsentsForSubjectsAsync",
        "GetActiveConsentsForSubjectsAsync",
        "GetActiveRoleConsentsAsync"
    ];

    private static readonly string[] ForbiddenExecutionTokens =
    [
        "IGatewayExecutionService",
        "ISqlExecutionService",
        "ISqlTableReadExecutor",
        "GovernedSqlExecutor",
        "IDataSourceClient",
        "IDataSourceConnectionFactory"
    ];

    [Fact]
    [Trait("Category", "Security")]
    public void Discovery_DoesNotReference_ConsentResolution_GranteeMatcher_Or_GrantStoreForAllow()
    {
        var files = RepositoryScan.ProductionFiles
            .Where(static f => f.RelativePath.StartsWith("src/Autheris.Application/Catalog/Discovery/", StringComparison.Ordinal))
            .ToList();
        files.ShouldNotBeEmpty();

        foreach (var file in files)
        {
            foreach (var token in ForbiddenConsentStoreTokens)
            {
                file.Text.Contains(token, StringComparison.Ordinal).ShouldBeFalse(
                    $"{file.RelativePath} must not reference '{token}'; discovery allow decisions come solely from IAccessDecisionPoint.");
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DiscoveryNamespace_DoesNotReference_DataSourceExecution()
    {
        var files = RepositoryScan.ProductionFiles
            .Where(static f => f.RelativePath.StartsWith("src/Autheris.Application/Catalog/Discovery/", StringComparison.Ordinal))
            .ToList();
        files.ShouldNotBeEmpty();

        foreach (var file in files)
        {
            foreach (var token in ForbiddenExecutionTokens)
            {
                file.Text.Contains(token, StringComparison.Ordinal).ShouldBeFalse(
                    $"{file.RelativePath} must not reference '{token}'; discovery must never touch data-source connections or execution.");
            }
        }
    }

    [Fact]
    public void NoProfileOrSampleGeneratorTypesExist()
    {
        var assemblies = new[]
        {
            typeof(Autheris.Domain.Model.Table).Assembly,
            typeof(Autheris.Application.Catalog.Discovery.DiscoveryService).Assembly,
            typeof(Autheris.Api.Errors.ApiErrorCodes).Assembly,
            typeof(Autheris.GraphQL.Types.Query).Assembly,
            typeof(Autheris.Modules.Dbt.DbtArtifactStreamingParser).Assembly
        };

        var forbidden = new[]
        {
            "SyntheticSampleRowGenerator",
            "ColumnValueProfileService",
            "IColumnValueProfileService",
            "ISyntheticSampleRowGenerator",
            "ComputeDatasetProfileHttpRequest"
        };

        var existingNames = assemblies
            .SelectMany(static a => a.GetTypes())
            .Select(static t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var typeName in forbidden)
        {
            existingNames.Contains(typeName).ShouldBeFalse($"Removed type '{typeName}' must not exist in production assemblies.");
        }
    }

    private static readonly Regex NoAccessComparison = new(
        @"(==|!=|\bis\s+(not\s+)?|\bcase\s+)\s*(Autheris\.Application\.Catalog\.Discovery\.)?DiscoveryColumnState\.NoAccess\b|DiscoveryColumnState\.NoAccess\s*=>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    [Trait("Category", "Security")]
    public void NoAccessStateIsOnlyComparedInVisibilityHelperAndMapper()
    {
        var files = RepositoryScan.ProductionFiles
            .Where(static f => f.RelativePath.StartsWith("src/Autheris.Application/", StringComparison.Ordinal)
                || f.RelativePath.StartsWith("src/Autheris.Api/", StringComparison.Ordinal)
                || f.RelativePath.StartsWith("src/Autheris.GraphQL/", StringComparison.Ordinal))
            .ToList();
        files.ShouldNotBeEmpty();

        foreach (var file in files)
        {
            if (file.RelativePath.EndsWith("/Catalog/Discovery/DiscoveryAccessMapper.cs", StringComparison.Ordinal)
                || file.RelativePath.EndsWith("/Catalog/Discovery/DiscoveryColumnVisibility.cs", StringComparison.Ordinal))
            {
                continue;
            }

            NoAccessComparison.IsMatch(file.Text).ShouldBeFalse(
                $"{file.RelativePath} compares a column state with NoAccess; use DiscoveryColumnVisibility.IsVisible for every person-facing column projection.");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PartitionColumns_AreOnlyWrittenThroughVisibilityFilter()
    {
        var writers = RepositoryScan.ProductionFiles
            .Where(static f => f.RelativePath.StartsWith("src/Autheris.Application/", StringComparison.Ordinal)
                || f.RelativePath.StartsWith("src/Autheris.Api/", StringComparison.Ordinal)
                || f.RelativePath.StartsWith("src/Autheris.GraphQL/", StringComparison.Ordinal))
            .Where(static f => Regex.IsMatch(f.Text, @"PartitionColumns\s*(=[^=]|:)"))
            .Select(static f => f.RelativePath)
            .ToList();

        writers.ShouldBe(["src/Autheris.Application/Catalog/Discovery/DiscoveryAccessMapper.cs"], ignoreOrder: true);

        var mapper = RepositoryScan.ProductionFiles.Single(static f => f.RelativePath.EndsWith("/Catalog/Discovery/DiscoveryAccessMapper.cs", StringComparison.Ordinal));
        mapper.Text.ShouldContain("FilterPartitionColumns(tableStats, mappedCols)");
    }
}
