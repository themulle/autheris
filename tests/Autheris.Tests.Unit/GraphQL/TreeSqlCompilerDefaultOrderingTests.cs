using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>Issue #94 (SD-2): the GraphQL tree compiler uses the shared default ordering, incl. the composite fallback.</summary>
[Trait("Category", "Security")]
public sealed class TreeSqlCompilerDefaultOrderingTests
{
    private static readonly TableIdentifier Parent = new("d", "main", "parents");
    private static readonly TableIdentifier Child = new("d", "main", "children");

    private static TableMetadata Meta(TableIdentifier id, IReadOnlyList<string> pk, params string[] cols) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = "d", SchemaName = "main", TableName = id.TableName, SourceType = "Sqlite" },
        PrimaryKeyColumns = pk,
        Columns = cols.Select(c => new TableColumn { ColumnName = c, DataType = "integer" }).ToList()
    };

    private static TreeTableAccess Access(TableMetadata meta, params (string Name, ColumnAccessLevel Level)[] levels) =>
        new(meta, TableAccessDecision.Allowed(
            meta.Identifier,
            levels.ToDictionary(l => l.Name, l => l.Level, StringComparer.OrdinalIgnoreCase),
            hasUnconstrainedColumnAllow: true));

    private static string Compile(TreeQueryNode root, params TreeTableAccess[] access) =>
        TreeSqlCompiler.Compile(root, access.ToDictionary(a => a.Metadata.Identifier), DatabaseDialect.Sqlite).Sql;

    [Fact]
    public void MaskedKey_SkippedToNextCandidate()
    {
        var meta = Meta(Parent, ["a", "b"], "a", "b", "c") with
        {
            Indexes = [new TableIndexDefinition("ux_c", ["c"], true)]
        };
        var access = Access(meta, ("a", ColumnAccessLevel.Clear), ("b", ColumnAccessLevel.Mask), ("c", ColumnAccessLevel.Clear));

        var sql = Compile(new TreeQueryNode(Parent, ["a"]), access);

        sql.ShouldContain("ORDER BY \"t0\".\"c\"");
        sql.ShouldNotContain("\"t0\".\"b\"");
    }

    [Fact]
    public void RootWithoutKey_OrdersByAllClearOrderableColumns()
    {
        var meta = Meta(Parent, [], "a", "b", "c");
        var access = Access(meta, ("a", ColumnAccessLevel.Clear), ("b", ColumnAccessLevel.Deny), ("c", ColumnAccessLevel.Clear));

        var sql = Compile(new TreeQueryNode(Parent, ["a", "c"]), access);

        sql.ShouldContain("ORDER BY \"t0\".\"a\", \"t0\".\"c\"");
    }

    [Fact]
    public void NestedList_StopsAfterKeyRules_NoCompositeSort()
    {
        var parent = Meta(Parent, ["id"], "id");
        var child = Meta(Child, [], "pid", "v");
        var root = new TreeQueryNode(Parent, ["id"])
        {
            Relations = [new TreeRelationNode("kids", ["id"], ["pid"], true, new TreeQueryNode(Child, ["v"]))]
        };

        var sql = Compile(
            root,
            Access(parent, ("id", ColumnAccessLevel.Clear)),
            Access(child, ("pid", ColumnAccessLevel.Clear), ("v", ColumnAccessLevel.Clear)));

        sql.ShouldNotContain("ORDER BY \"t1\".\"pid\"");
    }
}
