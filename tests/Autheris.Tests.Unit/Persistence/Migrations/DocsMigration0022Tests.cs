using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Docs;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class DocsMigration0022Tests
{
    [Fact]
    public void Migration0022_ExistsAndPassesStepSqlLintAndShapeContract()
    {
        var registeredNames = Enum.GetValues<GovernanceTable>()
            .Select(t => t.ToSqlName())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        registeredNames.ShouldContain("DOC_OFFICIAL_DOCUMENTS");
        registeredNames.ShouldContain("DOC_TENANT_DOCUMENTS");
        registeredNames.ShouldContain("DOC_TENANT_USAGE");

        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 22 && s.Name == "docs_store");
            step.Phase3Marker.ShouldBe("MIG-0022");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

            var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 22, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));

            var official = shape.Tables.Single(t => string.Equals(t.Name, "DOC_OFFICIAL_DOCUMENTS", StringComparison.OrdinalIgnoreCase));
            official.Columns.Count.ShouldBeLessThanOrEqualTo(12);
            official.PrimaryKey.ShouldBe(["app_id", "category", "doc_id"]);
            official.Indexes.Any(i => i.Columns.SequenceEqual(["updated_at"], StringComparer.OrdinalIgnoreCase)).ShouldBeTrue();

            var tenant = shape.Tables.Single(t => string.Equals(t.Name, "DOC_TENANT_DOCUMENTS", StringComparison.OrdinalIgnoreCase));
            tenant.Columns.Count.ShouldBeLessThanOrEqualTo(12);
            tenant.PrimaryKey.ShouldBe(["tenant_id", "app_id", "category", "doc_id"]);
            tenant.Columns.Single(c => string.Equals(c.Name, "tenant_id", StringComparison.OrdinalIgnoreCase)).Nullable.ShouldBeFalse();
            tenant.Indexes.Any(i => i.Columns.SequenceEqual(["updated_at"], StringComparer.OrdinalIgnoreCase)).ShouldBeTrue();

            var usage = shape.Tables.Single(t => string.Equals(t.Name, "DOC_TENANT_USAGE", StringComparison.OrdinalIgnoreCase));
            usage.Columns.Count.ShouldBeLessThanOrEqualTo(12);
            usage.PrimaryKey.ShouldBe(["tenant_id"]);
        }
    }

    [Fact]
    public void Migration0022_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 22 && s.Name == "docs_store");
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0022 docs_store ", StringComparison.Ordinal));
            var recordedSha = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
            step.Checksum.ShouldBe(recordedSha);
        }
    }

    [Fact]
    public void Migration0022_GlobalTableAllowlistMatchesDocumentScope()
    {
        GovernanceGlobalTables.IsGlobal("DOC_OFFICIAL_DOCUMENTS").ShouldBeTrue();
        GovernanceGlobalTables.IsGlobal("DOC_TENANT_DOCUMENTS").ShouldBeFalse();
        GovernanceGlobalTables.IsGlobal("DOC_TENANT_USAGE").ShouldBeFalse();
        DocumentStoreSql.CreateTenantPgSql.ShouldContain("tenant_id");
        DocumentStoreSql.CreateTenantSsSql.ShouldContain("tenant_id");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
