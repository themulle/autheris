namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #111 T-3 item 13, M-11, T-R2-1, T-R3-2, F-C13: source-level guards. A new catalog call site without an explicit decision stays on
/// <c>CatalogSurface.DataAccess</c>; policy code never reads catalog tags, tier or view.
/// </summary>
public sealed class CatalogSurfaceCallSiteGuardTests
{
    private static string SrcRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
            {
                return Path.Combine(current.FullName, "src");
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Autheris.sln not found.");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Name(string path) => Path.GetFileName(path);

    [Fact]
    [Trait("Category", "Security")]
    public void OnlyTheDiscoveryCallSites_PassCatalogSurfaceDiscovery()
    {
        // Definition files plus the three discovery sites of the plan (section 1.2 rows 1-6); everything else keeps the DataAccess default.
        string[] allowed =
        [
            "CatalogSurface.cs", "CatalogVisibility.cs", "McpCatalogVisibility.cs",
            "QueryTypes.cs", // P3 removes it (SEC-CAT138-18)
            "CatalogVisibilityService.cs" // #138 INC-1 (SEC-CAT138-18): the only service entry; channel files leave the list when migrated.
        ];

        var offenders = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("CatalogSurface.Discovery", StringComparison.Ordinal))
            .Select(Name)
            .Where(n => !allowed.Contains(n))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("AiDataGuardrailService.cs")]
    [InlineData("SemanticMcpCompiler.cs")]
    [InlineData("TrinoMetadataQueryHandler.cs")]
    [InlineData("ArrowFlightSqlServer.cs")]
    [InlineData("IcebergRestCatalogFederationService.cs")]
    [InlineData("EffectiveAccessExplanationService.cs")]
    [Trait("Category", "Security")]
    public void DataPathAndMetadataSurfaces_NeverReferenceTheDiscoverySurface(string file)
    {
        var path = SourceFiles().Single(f => Name(f) == file);

        File.ReadAllText(path).ShouldNotContain("CatalogSurface");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DiscoverySurface_IsNeverTheDefaultOfAnyVisibilityOverload()
    {
        foreach (var file in new[] { "CatalogVisibility.cs", "McpCatalogVisibility.cs" })
        {
            var text = File.ReadAllText(SourceFiles().Single(f => Name(f) == file));

            Regex.IsMatch(text, @"CatalogSurface\s+\w+\s*=\s*CatalogSurface\.Discovery").ShouldBeFalse(file);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_R2_1_PolicyCode_NeverReadsCatalogTagsTierOrView()
    {
        foreach (var file in new[] { "TableAccessPolicy.cs", "GrantClassifier.cs" })
        {
            var text = File.ReadAllText(SourceFiles().Single(f => Name(f) == file));

            text.ShouldNotContain("CatalogTags", customMessage: file);
            text.ShouldNotContain("CatalogView", customMessage: file);
            text.ShouldNotContain(".Visibility", customMessage: file);
        }

        var visibility = File.ReadAllText(SourceFiles().Single(f => Name(f) == "CatalogVisibility.cs"));
        var start = visibility.IndexOf("public static IReadOnlyList<TableMetadata> FilterByContract", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0);
        var end = visibility.IndexOf("public static ", start + 10, StringComparison.Ordinal);
        var body = visibility[start..(end > 0 ? end : visibility.Length)];

        body.ShouldNotContain("CatalogTags");
        body.ShouldNotContain("CatalogView");
        body.ShouldNotContain("Visibility");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_R3_2_CatalogTagsReferences_AreLimitedToTheAllowList()
    {
        // Writers/readers of persisted catalog tags only: the model, the repositories' shared store, and the discovery shaping (WP-3).
        // Policy consumers (TableAccessPolicy, FilterByContract, GrantClassifier) are not on the list (T_R2_1).
        string[] allowed =
        [
            "TableModels.cs", "DatasetCatalogStore.cs",
            "McpDatasetCatalog.cs", "QueryTypes.cs", "CatalogDiscoveryService.cs", "McpDatasetModels.cs",
            "DatasetCatalogAdminService.cs", "CatalogApiEndpoints.cs"
        ];

        var offenders = SourceFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bCatalogTags\b"))
            .Select(Name)
            .Where(n => !allowed.Contains(n))
            .ToList();

        offenders.ShouldBeEmpty();
    }
}
