using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Plan D-3 / D-5: comparison rules and the deterministic drift message.</summary>
[Trait("Category", "Security")]
public sealed class GovernanceSchemaShapeComparerTests
{
    private static readonly GovernanceSchemaShape Snapshot = GovernanceSchemaShapeLoader.LoadEmbedded("sqlite");

    private static GovernanceSchemaShape Without(Func<IReadOnlyList<SchemaTable>, IReadOnlyList<SchemaTable>> change) => Snapshot with { Tables = change(Snapshot.Tables) };

    private static GovernanceSchemaShape WithTable(string name, Func<SchemaTable, SchemaTable> change) =>
        Without(tables => tables.Select(t => t.Name == name ? change(t) : t).ToArray());

    public static IEnumerable<object[]> Tables => Snapshot.Tables.Select(t => new object[] { t.Name });

    public static IEnumerable<object[]> Columns => Snapshot.Tables.SelectMany(t => t.Columns.Select(c => new object[] { t.Name, c.Name }));

    public static IEnumerable<object[]> Indexes => Snapshot.Tables.SelectMany(t => t.Indexes.Select(i => new object[] { t.Name, i.Name }));

    [Fact]
    public void IdenticalShape_HasNoDeviations() =>
        GovernanceSchemaShapeComparer.Compare(Snapshot, Snapshot).ShouldBeEmpty();

    [Theory]
    [MemberData(nameof(Tables))]
    public void DroppedTable_ReportsOnlyTheMissingTable(string table)
    {
        var deviations = GovernanceSchemaShapeComparer.Compare(Snapshot, Without(t => t.Where(x => x.Name != table).ToArray()));

        var single = deviations.ShouldHaveSingleItem();
        single.Kind.ShouldBe(SchemaDeviationKind.MissingTable);
        single.Table.ShouldBe(table);
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public void DroppedColumn_ReportsOnlyTheMissingColumn(string table, string column)
    {
        var deviations = GovernanceSchemaShapeComparer.Compare(
            Snapshot, WithTable(table, t => t with { Columns = t.Columns.Where(c => c.Name != column).ToArray() }));

        var single = deviations.ShouldHaveSingleItem();
        single.Kind.ShouldBe(SchemaDeviationKind.MissingColumn);
        (single.Table, single.Name).ShouldBe((table, column));
    }

    [Theory]
    [MemberData(nameof(Indexes))]
    public void DroppedIndex_ReportsOnlyTheMissingIndex(string table, string index)
    {
        var deviations = GovernanceSchemaShapeComparer.Compare(
            Snapshot, WithTable(table, t => t with { Indexes = t.Indexes.Where(i => i.Name != index).ToArray() }));

        var single = deviations.ShouldHaveSingleItem();
        single.Kind.ShouldBe(SchemaDeviationKind.MissingIndex);
        (single.Table, single.Name).ShouldBe((table, index));
    }

    [Fact]
    public void ChangedTypeFamily_PrimaryKeyAndIndex_AreReportedWithKind()
    {
        var table = Snapshot.Tables.First(t => t.Indexes.Count > 0 && t.PrimaryKey.Count > 0);
        var column = table.Columns.First();
        var otherFamily = column.Family == "integer" ? "string" : "integer";
        var index = table.Indexes[0];

        var actual = WithTable(table.Name, t => t with
        {
            Columns = t.Columns.Select(c => c == column ? c with { Family = otherFamily } : c).ToArray(),
            PrimaryKey = ["other_key"],
            Indexes = t.Indexes.Select(i => i == index ? i with { Unique = !i.Unique } : i).ToArray()
        });

        var kinds = GovernanceSchemaShapeComparer.Compare(Snapshot, actual).Select(d => d.Kind).ToArray();

        kinds.ShouldBe([SchemaDeviationKind.WrongType, SchemaDeviationKind.WrongPrimaryKey, SchemaDeviationKind.WrongIndex]);
    }

    [Fact]
    public void ExtraTablesColumnsAndIndexes_AreTolerated_AndNamesAreCaseInsensitive()
    {
        var actual = Without(tables => tables
            .Select(t => t with
            {
                Name = t.Name.ToLowerInvariant(),
                Columns = [.. t.Columns, new SchemaColumn("extra_col", "TEXT", "string")],
                Indexes = [.. t.Indexes, new SchemaIndex("extra_idx", false, ["extra_col"])]
            })
            .Append(new SchemaTable("EXTRA_TABLE", [new SchemaColumn("x", "TEXT", "string")], [], []))
            .ToArray());

        GovernanceSchemaShapeComparer.Compare(Snapshot, actual).ShouldBeEmpty();
    }

    [Fact]
    public void Message_IsDeterministicBoundedAndFree_OfConnectionDetails()
    {
        var deviations = new[]
        {
            new SchemaDeviation(SchemaDeviationKind.MissingIndex, "AUDIT_LOG_ENTRIES", "idx_audit_seq", "UNIQUE (seq)", null),
            new SchemaDeviation(SchemaDeviationKind.MissingTable, "TABLE_PRIMARY_KEY_COLUMNS", null, string.Empty, null),
            new SchemaDeviation(SchemaDeviationKind.MissingColumn, "CONSENT_COLUMN_RULES", "applicable_scopes", "TEXT, family string", null),
            new SchemaDeviation(SchemaDeviationKind.WrongPrimaryKey, "ACCESS_PROFILES", null, "tenant_id, profile_id", "id"),
            new SchemaDeviation(SchemaDeviationKind.WrongType, "TABLES", "data_source_type", "family integer", "string")
        };

        var message = GovernanceSchemaShapeComparer.FormatMessage("SQLite", "gov.db", [1], [], deviations);

        message.ShouldStartWith("Governance schema drift on SQLite database 'gov.db': the schema does not match this build (applied migrations: 0001; pending: none).\n");
        message.ShouldContain("  missing table      TABLE_PRIMARY_KEY_COLUMNS\n");
        message.ShouldContain("  missing column     CONSENT_COLUMN_RULES.applicable_scopes (expected TEXT, family string)\n");
        message.ShouldContain("  wrong primary key  ACCESS_PROFILES (expected tenant_id, profile_id; found id)\n");
        message.ShouldContain("  wrong type         TABLES.data_source_type (expected family integer; found string)\n");
        message.ShouldContain("  missing index      AUDIT_LOG_ENTRIES.idx_audit_seq (expected UNIQUE (seq))\n");
        message.ShouldEndWith("see docs/features/f-gov-12-multi-engine-governance-storage.md#schema-shape-verification.");
        GovernanceSchemaShapeComparer.FormatMessage("SQLite", "gov.db", [1], [], deviations).ShouldBe(message);
    }

    [Fact]
    public void Message_ListsAtMostFiftyDeviations_ThenTheRemainingCount()
    {
        var deviations = Enumerable.Range(0, 130)
            .Select(i => new SchemaDeviation(SchemaDeviationKind.MissingTable, $"T{i:D3}", null, string.Empty, null))
            .ToArray();

        var message = GovernanceSchemaShapeComparer.FormatMessage("SQLite", "gov.db", [], [1], deviations);

        message.Split('\n').Count(l => l.StartsWith("  missing table", StringComparison.Ordinal)).ShouldBe(50);
        message.ShouldContain("  ... and 80 more\n");
        message.ShouldContain("applied migrations: none; pending: 0001");
    }
}
