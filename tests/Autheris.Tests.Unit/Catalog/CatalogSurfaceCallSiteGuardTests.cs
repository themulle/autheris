namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #111 T-3 item 13, M-11, T-R2-1, T-R3-2, F-C13: source-level guards. The retired visibility surface switch stays gone; policy code never reads catalog tags, tier or view.
/// </summary>
public sealed class CatalogSurfaceCallSiteGuardTests
{
    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Autheris.sln not found.");
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepoRoot();
        var dirs = new List<string> { Path.Combine(root, "src") };
        var lakehouseModuleDir = Path.Combine(root, "modules", "Autheris.Modules.Lakehouse");
        if (Directory.Exists(lakehouseModuleDir))
        {
            dirs.Add(lakehouseModuleDir);
        }

        return dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    private static string Name(string path) => Path.GetFileName(path);

    [Fact]
    [Trait("Category", "Security")]
    public void CatalogSurface_NoLongerExists_AndNoSourceFileReferencesTheDiscoverySurface()
    {
        // The tier model is retired: authorized discovery has its own decision path, so no visibility surface switch remains.
        var offenders = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("CatalogSurface", StringComparison.Ordinal))
            .Select(Name)
            .ToList();

        offenders.ShouldBeEmpty();
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
            "TableModels.cs", "DatasetCatalogStore.cs", "CatalogTablePersistedProjection.cs",
            "McpDatasetCatalog.cs", "Query.Catalog.cs", "CatalogDiscoveryService.cs", "McpDatasetModels.cs",
            "DiscoveryService.cs", "DiscoveryAccessMapper.cs", "ExternalCatalogReader.cs",
            "DatasetCatalogAdminService.cs", "CatalogApiEndpoints.cs",
            // search_catalog tag filter: removes already authorized hits only, never widens visibility (#712).
            "McpToolExecutionHandler.cs"
        ];

        var offenders = SourceFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bCatalogTags\b"))
            .Select(Name)
            .Where(n => !allowed.Contains(n))
            .ToList();

        offenders.ShouldBeEmpty();
    }
}
