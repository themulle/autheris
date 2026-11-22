using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class GovernanceDomainsMigration0036Tests
{
    private const string StepName = "governance_domains_and_protection";
    private const string UnassignedDomainId = "00000000-0000-0000-0000-000000000000";

    private static readonly string[] ExpectedTables =
    [
        "GOVERNANCE_DOMAINS",
        "GOVERNANCE_DOMAIN_TABLES",
        "OBJECT_PROTECTION",
        "ACCESS_POLICY_SETTINGS",
        "GOVERNANCE_EPOCHS"
    ];

    [Fact]
    public void Migration0036_ExistsPassesStepSqlLintAndMatchesShape()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 36 && s.Name == StepName);
            step.Phase3Marker.ShouldBe("MIG-0036");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

            foreach (var token in new[]
                     {
                         "governance_domains",
                         "governance_domain_tables",
                         "object_protection",
                         "access_policy_settings",
                         "governance_epochs",
                         "mask_category",
                         "protection_class",
                         "Unassigned",
                         UnassignedDomainId,
                         "TENANT_REGISTRY",
                         "collided",
                         "column_classifications",
                         "data_classifications",
                         "table_columns"
                     })
            {
                step.Sql.ShouldContain(token, Case.Insensitive);
            }

            step.Sql.ShouldNotContain("pii_flag", Case.Insensitive);
            step.Sql.ShouldNotContain("is_sensitive", Case.Insensitive);

            var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 36, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
            var isSqlServer = engine == "sqlserver";

            foreach (var upperName in ExpectedTables)
            {
                var expectedName = isSqlServer ? upperName : upperName.ToLowerInvariant();
                var table = shape.Tables.Single(t => string.Equals(t.Name, expectedName, StringComparison.Ordinal));
                table.PrimaryKey[0].ShouldBe("tenant_id");
                table.Columns.Single(c => c.Name == "tenant_id").Nullable.ShouldBeFalse();
            }

            AssertGovernanceDomainsShape(shape, isSqlServer);
            AssertGovernanceDomainTablesShape(shape, isSqlServer);
            AssertObjectProtectionShape(shape, isSqlServer);
            AssertAccessPolicySettingsShape(shape, isSqlServer);
            AssertGovernanceEpochsShape(shape, isSqlServer);
        }
    }

    [Fact]
    public void Migration0036_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 36 && s.Name == StepName);
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0036 " + StepName + " ", StringComparison.Ordinal));
            var expectedChecksum = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
            step.Checksum.ShouldBe(expectedChecksum);
            SqlMigrationStep.ComputeChecksum(engine, 36, StepName, step.Sql).ShouldBe(expectedChecksum);
        }
    }

    [Fact]
    public void Migration0036_SqlServerSplitsIntoMultipleBatches_PostgreSqlIsSingleBatch()
    {
        var pg = SqlMigrationStep.LoadEmbedded("postgresql").Single(s => s.Version == 36 && s.Name == StepName);
        pg.Batches.Count.ShouldBe(1);

        var ss = SqlMigrationStep.LoadEmbedded("sqlserver").Single(s => s.Version == 36 && s.Name == StepName);
        ss.Batches.Count.ShouldBeGreaterThan(1);
        ss.Batches.ShouldAllBe(b => !string.IsNullOrWhiteSpace(b));
    }

    private static void AssertGovernanceDomainsShape(GovernanceSchemaShape shape, bool isSqlServer)
    {
        var table = shape.Tables.Single(t => t.Name == (isSqlServer ? "GOVERNANCE_DOMAINS" : "governance_domains"));
        table.Columns.ShouldBe([
            new SchemaColumn("tenant_id", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(128)", SchemaTypeFamily.String, false),
            new SchemaColumn("id", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(36)", SchemaTypeFamily.String, false),
            new SchemaColumn("name", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(128)", SchemaTypeFamily.String, false),
            new SchemaColumn("description", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(1024)", SchemaTypeFamily.String, true),
            new SchemaColumn("contact", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(256)", SchemaTypeFamily.String, true),
            new SchemaColumn("owner_group_sid", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(184)", SchemaTypeFamily.String, true),
            new SchemaColumn("default_class", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(16)", SchemaTypeFamily.String, true),
            new SchemaColumn("visibility", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(16)", SchemaTypeFamily.String, false),
            new SchemaColumn("is_system", isSqlServer ? "BIT" : "BOOLEAN", SchemaTypeFamily.Boolean, false),
            new SchemaColumn("version", isSqlServer ? "INT" : "INTEGER", SchemaTypeFamily.Integer, false),
            new SchemaColumn("created_by", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(256)", SchemaTypeFamily.String, false),
            new SchemaColumn("created_at", isSqlServer ? "DATETIMEOFFSET" : "TIMESTAMP WITH TIME ZONE", SchemaTypeFamily.DateTime, false),
            new SchemaColumn("updated_at", isSqlServer ? "DATETIMEOFFSET" : "TIMESTAMP WITH TIME ZONE", SchemaTypeFamily.DateTime, false)
        ]);
        table.PrimaryKey.ShouldBe(["tenant_id", "id"]);
        var indexName = isSqlServer ? "UX_GOVERNANCE_DOMAINS_NAME" : "ux_governance_domains_name";
        var idx = table.Indexes.Single();
        idx.Name.ShouldBe(indexName);
        idx.Unique.ShouldBeTrue();
        idx.Columns.ShouldBe(["tenant_id", "name"]);
    }

    private static void AssertGovernanceDomainTablesShape(GovernanceSchemaShape shape, bool isSqlServer)
    {
        var table = shape.Tables.Single(t => t.Name == (isSqlServer ? "GOVERNANCE_DOMAIN_TABLES" : "governance_domain_tables"));
        table.Columns.ShouldBe([
            new SchemaColumn("tenant_id", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(128)", SchemaTypeFamily.String, false),
            new SchemaColumn("table_id", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(256)", SchemaTypeFamily.String, false),
            new SchemaColumn("domain_id", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(36)", SchemaTypeFamily.String, false),
            new SchemaColumn("assigned_by", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(256)", SchemaTypeFamily.String, false),
            new SchemaColumn("assigned_at", isSqlServer ? "DATETIMEOFFSET" : "TIMESTAMP WITH TIME ZONE", SchemaTypeFamily.DateTime, false)
        ]);
        table.PrimaryKey.ShouldBe(["tenant_id", "table_id"]);
        var indexName = isSqlServer ? "IX_GOVERNANCE_DOMAIN_TABLES_DOMAIN" : "ix_governance_domain_tables_domain";
        var idx = table.Indexes.Single();
        idx.Name.ShouldBe(indexName);
        idx.Unique.ShouldBeFalse();
        idx.Columns.ShouldBe(["tenant_id", "domain_id"]);
    }

    private static void AssertObjectProtectionShape(GovernanceSchemaShape shape, bool isSqlServer)
    {
        var table = shape.Tables.Single(t => t.Name == (isSqlServer ? "OBJECT_PROTECTION" : "object_protection"));
        table.Columns.ShouldBe([
            new SchemaColumn("tenant_id", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(128)", SchemaTypeFamily.String, false),
            new SchemaColumn("table_id", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(256)", SchemaTypeFamily.String, false),
            new SchemaColumn("column_name", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(256)", SchemaTypeFamily.String, false),
            new SchemaColumn("protection_class", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(16)", SchemaTypeFamily.String, true),
            new SchemaColumn("mask_category", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(64)", SchemaTypeFamily.String, true),
            new SchemaColumn("updated_by", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(256)", SchemaTypeFamily.String, false),
            new SchemaColumn("updated_at", isSqlServer ? "DATETIMEOFFSET" : "TIMESTAMP WITH TIME ZONE", SchemaTypeFamily.DateTime, false)
        ]);
        table.Columns.ShouldNotContain(c => string.Equals(c.Name, "pii_flag", StringComparison.OrdinalIgnoreCase));
        table.PrimaryKey.ShouldBe(["tenant_id", "table_id", "column_name"]);
        table.Indexes.ShouldBeEmpty();
    }

    private static void AssertAccessPolicySettingsShape(GovernanceSchemaShape shape, bool isSqlServer)
    {
        var table = shape.Tables.Single(t => t.Name == (isSqlServer ? "ACCESS_POLICY_SETTINGS" : "access_policy_settings"));
        table.Columns.ShouldBe([
            new SchemaColumn("tenant_id", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(128)", SchemaTypeFamily.String, false),
            new SchemaColumn("settings_json", isSqlServer ? "NVARCHAR" : "TEXT", SchemaTypeFamily.String, false),
            new SchemaColumn("version", isSqlServer ? "INT" : "INTEGER", SchemaTypeFamily.Integer, false),
            new SchemaColumn("updated_by", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(256)", SchemaTypeFamily.String, false),
            new SchemaColumn("updated_at", isSqlServer ? "DATETIMEOFFSET" : "TIMESTAMP WITH TIME ZONE", SchemaTypeFamily.DateTime, false)
        ]);
        table.PrimaryKey.ShouldBe(["tenant_id"]);
        table.Indexes.ShouldBeEmpty();
    }

    private static void AssertGovernanceEpochsShape(GovernanceSchemaShape shape, bool isSqlServer)
    {
        var table = shape.Tables.Single(t => t.Name == (isSqlServer ? "GOVERNANCE_EPOCHS" : "governance_epochs"));
        table.Columns.ShouldBe([
            new SchemaColumn("tenant_id", isSqlServer ? "NVARCHAR" : "CHARACTER VARYING(128)", SchemaTypeFamily.String, false),
            new SchemaColumn("epoch", "BIGINT", SchemaTypeFamily.Integer, false),
            new SchemaColumn("updated_at", isSqlServer ? "DATETIMEOFFSET" : "TIMESTAMP WITH TIME ZONE", SchemaTypeFamily.DateTime, false)
        ]);
        table.PrimaryKey.ShouldBe(["tenant_id"]);
        table.Indexes.ShouldBeEmpty();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
