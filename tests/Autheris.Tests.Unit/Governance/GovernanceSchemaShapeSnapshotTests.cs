using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// Plan D-2: the embedded <c>governance-shape.sqlite.json</c> is generated from a freshly migrated database and compared strictly.
/// On a mismatch the test writes the actual snapshot to <c>AUTHERIS_SHAPE_SNAPSHOT_DIR</c> (default: temp) and fails; nobody edits the file by hand.
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernanceSchemaShapeSnapshotTests
{
    [Fact]
    public async Task EmbeddedSnapshot_EqualsShapeOfFreshlyMigratedDatabase()
    {
        var cs = $"Data Source=shape_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = cs, SeedDemoData = false }
        });
        using var repository = GovernanceStoreFactory.CreateUnmigratedSqlite(Substitute.For<IEpochValidationService>(), options);

        var reader = new SqliteSchemaCatalogReader();
        await using var connection = await repository.Connections.OpenAsync(CancellationToken.None);
        // The baseline DDL is applied directly: the runner itself depends on the snapshot that this test generates.
        new SqliteBaselineSchema(connection, null).Apply();
        var tables = await reader.ListTablesAsync(connection, CancellationToken.None);
        var actual = await reader.ReadAsync(connection, tables, CancellationToken.None);
        var actualJson = GovernanceSchemaShapeLoader.Serialize(actual);

        var resource = GovernanceSchemaShapeLoader.ResourceName("sqlite");
        using var stream = typeof(GovernanceSchemaShapeLoader).Assembly.GetManifestResourceStream(resource);
        var expectedJson = stream is null ? null : new StreamReader(stream).ReadToEnd().ReplaceLineEndings("\n");
        if (expectedJson != actualJson)
        {
            var dir = Environment.GetEnvironmentVariable("AUTHERIS_SHAPE_SNAPSHOT_DIR") ?? Path.GetTempPath();
            var path = Path.Combine(dir, resource);
            File.WriteAllText(path, actualJson);
            Assert.Fail($"The embedded snapshot '{resource}' differs from the migrated schema. The actual snapshot was written to '{path}'; copy it to src/Autheris.Infrastructure/Persistence/Migrations/Shape/.");
        }
    }

    [Fact]
    public void EmbeddedSnapshot_CoversTheDeclaredKeyAndIndexTables()
    {
        var shape = GovernanceSchemaShapeLoader.LoadEmbedded("sqlite");

        shape.Tables.Select(t => t.Name).ShouldContain("TABLE_PRIMARY_KEY_COLUMNS");
        shape.Tables.Select(t => t.Name).ShouldContain("TABLE_INDEXES");
        shape.Tables.Select(t => t.Name).ShouldContain("TABLE_INDEX_COLUMNS");
        shape.Tables.ShouldAllBe(t => t.Columns.Count > 0);
    }

    [Theory]
    [InlineData("sqlite", "TABLE_ROW_FILTER_STRATEGIES", "CONSENT_ROW_FILTERS")]
    [InlineData("postgresql", "table_row_filter_strategies", "consent_row_filters")]
    [InlineData("sqlserver", "TABLE_ROW_FILTER_STRATEGIES", "CONSENT_ROW_FILTERS")]
    public void EmbeddedSnapshot_CoversTheRowFilterStrategyTable(string engine, string strategies, string rowFilters)
    {
        var shape = GovernanceSchemaShapeLoader.LoadEmbedded(engine);

        var table = shape.Tables.Single(t => t.Name == strategies);
        table.Columns.Select(c => c.Name).ShouldBe(["table_id", "strategy", "duration_seconds", "max_keys", "updated_by", "updated_at"]);
        table.PrimaryKey.ShouldBe(["table_id"]);

        var filters = shape.Tables.Single(t => t.Name == rowFilters);
        filters.Columns.Select(c => c.Name).ShouldContain("key_set_strategy");
        filters.Columns.Select(c => c.Name).ShouldContain("key_set_duration_seconds");
        filters.Columns.Select(c => c.Name).ShouldContain("key_set_max_keys");
    }

    [Theory]
    [InlineData("sqlite", "TABLE_RELATIONS", "TABLE_RELATION_KEYS", "UX_TABLE_RELATIONS_NATURAL")]
    [InlineData("postgresql", "table_relations", "table_relation_keys", "ux_table_relations_natural")]
    [InlineData("sqlserver", "TABLE_RELATIONS", "TABLE_RELATION_KEYS", "UX_TABLE_RELATIONS_NATURAL")]
    public void EmbeddedSnapshot_CoversTheRelationKeyTable(string engine, string relations, string keys, string naturalIndex)
    {
        var shape = GovernanceSchemaShapeLoader.LoadEmbedded(engine);

        var keyTable = shape.Tables.Single(t => string.Equals(t.Name, keys, StringComparison.Ordinal));
        keyTable.Columns.Select(c => c.Name).ShouldBe(["relation_id", "key_ordinal", "parent_column", "child_column"]);
        keyTable.PrimaryKey.ShouldBe(["relation_id", "key_ordinal"]);
        keyTable.Indexes.Count(i => i.Unique).ShouldBe(2);

        var relationTable = shape.Tables.Single(t => string.Equals(t.Name, relations, StringComparison.Ordinal));
        var natural = relationTable.Indexes.Single(i => string.Equals(i.Name, naturalIndex, StringComparison.Ordinal));
        natural.Unique.ShouldBeTrue();
        natural.Columns.ShouldBe(["parent_table_id", "child_table_id", "relation_name"]);
        relationTable.Columns.Select(c => c.Name).ShouldNotContain("join_key_parent");
        relationTable.Columns.Select(c => c.Name).ShouldNotContain("join_key_child");
    }
}
