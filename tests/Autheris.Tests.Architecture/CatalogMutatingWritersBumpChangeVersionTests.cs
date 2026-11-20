namespace Autheris.Tests.Architecture;

using System.IO;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogMutatingWritersBumpChangeVersionTests
{
    [Fact]
    public void IncrementTableEpochInternalAsync_Writes_ChangeVersion_On_Update_And_Insert()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var repoSource = File.ReadAllText(Path.Combine(root, "src/Autheris.Infrastructure/Persistence/GovernanceRepository.cs"));

        var method = ExtractMethod(repoSource, "IncrementTableEpochInternalAsync");
        method.ShouldContain("long? changeVersion = null");
        method.ShouldContain("change_version = COALESCE(@changeVersion, change_version)");
        method.ShouldContain("change_version, updated_at");
    }

    [Fact]
    public void CatalogBatchUpsertHelper_BumpsCatalogVersion_Before_Incrementing_Epochs_When_Not_Deferred()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var batchSource = File.ReadAllText(Path.Combine(root, "src/Autheris.Infrastructure/Persistence/Catalog/CatalogBatchUpsertHelper.cs"));

        var bumpIdx = batchSource.IndexOf("BumpCatalogVersionInternalAsync", StringComparison.Ordinal);
        var incIdx = batchSource.IndexOf("IncrementTableEpochInternalAsync(conn, tx, chunk[i].Identifier, ct, changeVersion)", StringComparison.Ordinal);

        bumpIdx.ShouldBeGreaterThan(0);
        incIdx.ShouldBeGreaterThan(bumpIdx);
        batchSource.ShouldContain("repo.RecordDeferredEpochTables(deferredTables)");
    }

    [Fact]
    public void All_CatalogMutating_Writers_Bump_Epoch_And_ChangeVersion()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var catalogSource = File.ReadAllText(Path.Combine(root, "src/Autheris.Infrastructure/Persistence/GovernanceRepository.Catalog.cs"));
        var relationsSource = File.ReadAllText(Path.Combine(root, "src/Autheris.Infrastructure/Persistence/GovernanceRepository.CatalogRelations.cs"));

        var writeCatalogSettings = ExtractMethod(catalogSource, "WriteCatalogSettingsAsync");
        writeCatalogSettings.ShouldContain("BumpCatalogVersionInternalAsync");
        writeCatalogSettings.ShouldContain("IncrementTableEpochInternalAsync(conn, tx, table, ct, changeVersion)");
        writeCatalogSettings.ShouldContain("InvalidateTableEpochAsync");

        var replaceKeys = ExtractMethod(catalogSource, "ReplaceTableKeyDefinitionAsync");
        replaceKeys.ShouldContain("IncrementTableEpochInternalAsync");
        replaceKeys.ShouldContain("SetTableEpochChangeVersionInternalAsync");

        var createRelation = ExtractMethod(relationsSource, "CreateRelationAsync");
        createRelation.ShouldContain("BumpRelationTableEpochsInternalAsync");
        createRelation.ShouldContain("RecordDeferredRelationEpochTables");
        createRelation.ShouldContain("InvalidateRelationEpochsAsync");

        var setCrossSource = ExtractMethod(relationsSource, "SetRelationCrossDataSourceOptInAsync");
        setCrossSource.ShouldContain("BumpRelationTableEpochsInternalAsync");
        setCrossSource.ShouldContain("InvalidateRelationEpochsAsync");

        var flushCatalogVersion = ExtractMethod(relationsSource, "FlushCatalogVersionAsync");
        flushCatalogVersion.ShouldContain("SnapshotDeferredEpochTables");
        flushCatalogVersion.ShouldContain("StampDeferredChangeVersionsAsync");
        flushCatalogVersion.ShouldContain("ClearDeferredEpochTables");
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(source, $@"\n    (?:public|private|internal|protected)\b[^\n]*\b{Regex.Escape(methodName)}\s*\(");
        match.Success.ShouldBeTrue($"Method {methodName} was not found.");
        var start = match.Index;
        var nextTopLevel = source.IndexOf("\n    public ", start + 1, StringComparison.Ordinal);
        var nextPrivate = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var nextInternal = source.IndexOf("\n    internal ", start + 1, StringComparison.Ordinal);

        var end = source.Length;
        foreach (var candidate in new[] { nextTopLevel, nextPrivate, nextInternal })
        {
            if (candidate > start && candidate < end)
            {
                end = candidate;
            }
        }

        return source[start..end];
    }
}
