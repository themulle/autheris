namespace Autheris.Tests.Unit.Federation;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

/// <summary>SQLA-17: the staged projection of a same-source subtree covers the ORDER BY columns.</summary>
public sealed class SubtreeStagingProjectionTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata Table(string domain, string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier(domain, schema, table),
        Table = new Table
        {
            TableName = table,
            SchemaName = schema,
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            SourceName = domain,
            IsActive = true
        },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(System.StringComparer.OrdinalIgnoreCase)
    };

    private static SourceSubtree PartitionPg(string sql)
    {
        var (tree, _) = Engine.Parse(sql.AsMemory());
        var stmt = new SqlAstBuilder().BuildStatement(tree);
        var metadata = Engine.Analyze(sql.AsMemory());
        var a = Table("pg", "public", "a", "id", "k");
        var b = Table("pg", "public", "b", "id", "a_id", "created_at");
        var c = Table("ms", "dbo", "c", "k");
        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], a.Identifier, a, "pg"),
            new(metadata.ReferencedTables[1], b.Identifier, b, "pg"),
            new(metadata.ReferencedTables[2], c.Identifier, c, "ms")
        };

        return SubtreeGraphPartitioner.Partition(stmt, tables, maxTablesPerSubtree: 12).Subtrees.Single(s => s.SourceName == "pg");
    }

    private static IEnumerable<string> StagedNames(SourceSubtree subtree) =>
        subtree.ProjectedItems.OfType<ColumnSelectItem>().Select(i => i.Alias!.Value);

    [Fact]
    public void OrderByColumn_NotOtherwiseSelected_IsStaged()
    {
        var subtree = PartitionPg(
            "SELECT a.id FROM pg.public.a a JOIN pg.public.b b ON a.id = b.a_id JOIN ms.dbo.c c ON c.k = a.k ORDER BY b.created_at");

        StagedNames(subtree).ShouldContain("created_at");
    }

    [Fact]
    public void SharedColumnName_IsStagedUnderAliasPrefixedNames()
    {
        var subtree = PartitionPg(
            "SELECT a.id, b.id FROM pg.public.a a JOIN pg.public.b b ON a.id = b.a_id JOIN ms.dbo.c c ON c.k = a.k");

        StagedNames(subtree).ShouldContain("a_id");
        StagedNames(subtree).ShouldContain("b_id");
    }
}
