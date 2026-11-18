using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Docs;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class DocLinksMigration0026Tests
{
    [Fact]
    public void Migration0026_ExistsAndPassesStepSqlLintAndShapeContract()
    {
        GovernanceTable.DocLinks.ToSqlName().ShouldBe("DOC_LINKS");
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 26 && s.Name == "doc_links");
            step.Phase3Marker.ShouldBe("MIG-0026");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

            var shape = GovernanceSchemaShapeFolder.Fold(
                GovernanceSchemaShapeLoader.LoadAt(engine, 22, steps.ToDictionary(s => s.Version, s => s.ShapeDelta)),
                step.ShapeDelta);
            var links = shape.Tables.Single(t => string.Equals(t.Name, "DOC_LINKS", StringComparison.OrdinalIgnoreCase));
            links.PrimaryKey.ShouldBe(["link_id"]);
            links.Columns.Single(c => string.Equals(c.Name, "tenant_id", StringComparison.OrdinalIgnoreCase)).Nullable.ShouldBeFalse();
            links.Indexes.Any(i => i.Unique && i.Columns.Contains("tenant_id") && i.Columns.Contains("link_digest")).ShouldBeTrue();

            foreach (var table in new[] { "DOC_OFFICIAL_DOCUMENTS", "DOC_TENANT_DOCUMENTS" })
            {
                var docs = shape.Tables.Single(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
                var hidden = docs.Columns.Single(c => string.Equals(c.Name, "discovery_hidden", StringComparison.OrdinalIgnoreCase));
                hidden.Nullable.ShouldBeFalse();
            }
        }
    }

    [Fact]
    public void Migration0026_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 26 && s.Name == "doc_links");
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0026 doc_links ", StringComparison.Ordinal));
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2].ShouldBe(step.Checksum);
        }
    }

    [Fact]
    public void Migration0026_LinksAreTenantScopedNotGlobal_AndSqlIsAlwaysTenantFiltered()
    {
        GovernanceGlobalTables.IsGlobal("DOC_LINKS").ShouldBeFalse();
        DocLinkOperations.ListSourceSql.ShouldContain("tenant_id = @tenant_id");
        DocLinkOperations.DeleteSourceSql.ShouldContain("tenant_id = @tenant_id");
        DocLinkOperations.DeleteTenantSql.ShouldContain("tenant_id = @tenant_id");
        DocLinkOperations.InsertSql.ShouldContain("@tenant_id");
    }

    [Fact]
    public void Migration0026_DocumentDeleteAndPurgeRemoveLinksInTheSameTransaction()
    {
        DocLinkOperations.DeleteDeletedOfficialDocSql.ShouldContain("DOC_LINKS");
        DocLinkOperations.DeleteDeletedTenantDocSql.ShouldContain("tenant_id = @tenant_id");
        var deleteSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Autheris.Infrastructure", "Persistence", "Docs", "DocumentStoreOperations.cs"));
        deleteSource.ShouldContain("DocLinkOperations.DeleteForDeletedDocumentAsync");
        deleteSource.ShouldContain("DocLinkOperations.DeleteForTenantAsync");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
