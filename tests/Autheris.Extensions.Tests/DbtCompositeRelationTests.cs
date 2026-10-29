namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #151 (WP-151-4): composite foreign keys in the dbt parser and importer, reason-coded warnings and limits.</summary>
public sealed class DbtCompositeRelationTests
{
    private static Dictionary<string, object> Fk(string to, string[]? columns, string[]? toColumns, string? expression = null)
    {
        var c = new Dictionary<string, object> { ["type"] = "foreign_key", ["to"] = $"ref('{to}')" };
        if (columns is not null) c["columns"] = columns;
        if (toColumns is not null) c["to_columns"] = toColumns;
        if (expression is not null) c["expression"] = expression;
        return c;
    }

    private static Dictionary<string, object> Node(string name, string[] columns, object[]? constraints = null, Dictionary<string, object>? columnObjects = null)
    {
        var cols = columnObjects ?? columns.ToDictionary(c => c, c => (object)new Dictionary<string, object> { ["name"] = c, ["data_type"] = "integer" });
        var node = new Dictionary<string, object>
        {
            ["name"] = name,
            ["database"] = "db",
            ["schema"] = "s",
            ["columns"] = cols
        };
        if (constraints is not null) node["constraints"] = constraints;
        return node;
    }

    private static MemoryStream Manifest(Dictionary<string, object> nodes) =>
        new(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["nodes"] = nodes }));

    private static MemoryStream OneChild(string[] parentCols, string[] childCols, object[] constraints) =>
        Manifest(new Dictionary<string, object>
        {
            ["model.p.dim_orders"] = Node("dim_orders", parentCols),
            ["model.p.fct_lines"] = Node("fct_lines", childCols, constraints)
        });

    // ---------------------------------------------------------------- parser

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Parse_ModelLevelForeignKey_KeepsAllPairsInOrder(int count)
    {
        var parent = new[] { "p1", "p2", "p3" }.Take(count).ToArray();
        var child = new[] { "c1", "c2", "c3" }.Take(count).ToArray();
        // Order differs from the column declaration order on purpose.
        var fkChild = child.Reverse().ToArray();
        var fkParent = parent.Reverse().ToArray();
        using var stream = OneChild(parent, child, [Fk("dim_orders", fkChild, fkParent)]);

        var result = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);

        result.Warnings.ShouldBeEmpty();
        var rel = result.Relationships.ShouldHaveSingleItem();
        rel.ParentColumns.ShouldBe(fkParent);
        rel.ChildColumns.ShouldBe(fkChild);
    }

    [Fact]
    public async Task Parse_CountMismatch_SkipsWithWarning()
    {
        using var stream = OneChild(["a", "b"], ["x", "y"], [Fk("dim_orders", ["x", "y"], ["a"])]);
        var result = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);
        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldBe("Relation 'dim_orders' (fct_lines -> dim_orders) skipped: COUNT_MISMATCH");
    }

    [Fact]
    public async Task Parse_MissingToColumns_SkipsWithoutIdDefault()
    {
        using var stream = OneChild(["id"], ["x"], [Fk("dim_orders", ["x"], null)]);
        var result = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);
        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: MISSING_COLUMNS");
    }

    [Fact]
    public async Task Parse_DuplicateColumn_SkipsWithWarning()
    {
        using var stream = OneChild(["a", "b"], ["x", "y"], [Fk("dim_orders", ["x", "X"], ["a", "b"])]);
        var result = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);
        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: DUPLICATE_COLUMN");
    }

    [Fact]
    public async Task Parse_ExpressionForm_SkipsUnsupportedForm()
    {
        using var stream = OneChild(["a"], ["x"], [Fk("dim_orders", null, null, expression: "dim_orders (a) WITH SECRET_FREE_TEXT")]);
        var result = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);
        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: UNSUPPORTED_FORM");
    }

    [Fact]
    public async Task Parse_MoreThan16Columns_SkipsTooWide()
    {
        var cols = Enumerable.Range(1, 17).Select(i => $"c{i}").ToArray();
        using var stream = OneChild(cols, cols, [Fk("dim_orders", cols, cols)]);
        var result = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);
        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: TOO_WIDE");
    }

    [Fact]
    public async Task Parse_ColumnLevelForeignKey_WithoutTargetColumn_SkipsWithWarning()
    {
        var childCols = new Dictionary<string, object>
        {
            ["x"] = new Dictionary<string, object>
            {
                ["name"] = "x",
                ["constraints"] = new object[] { Fk("dim_orders", null, null) }
            }
        };
        using var stream = Manifest(new Dictionary<string, object>
        {
            ["model.p.dim_orders"] = Node("dim_orders", ["id"]),
            ["model.p.fct_lines"] = Node("fct_lines", ["x"], columnObjects: childCols)
        });
        var result = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);
        result.Relationships.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldEndWith("skipped: MISSING_COLUMNS");
    }

    [Fact]
    public async Task Parse_Warnings_ContainNoManifestFreeText()
    {
        using var stream = OneChild(["a", "b"], ["x", "y"],
        [
            Fk("dim_orders", ["x", "y"], ["a"], expression: "SECRET_FREE_TEXT_EXPRESSION"),
            Fk("dim_orders", ["secret_col_one"], null, expression: "SECRET_FREE_TEXT_EXPRESSION")
        ]);
        var result = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);
        result.Warnings.ShouldNotBeEmpty();
        foreach (var w in result.Warnings)
        {
            w.ShouldNotContain("SECRET_FREE_TEXT");
            w.ShouldNotContain("secret_col_one");
            w.ShouldNotContain("\"");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parse_RelationCountAndIdentifierLimits_SkipWithWarning()
    {
        // T-REL151-06 (SEC-REL151-02): 10 001 relations, 257-char and control-char names, 17 keys.
        var childCols = new Dictionary<string, object>();
        for (var i = 0; i < 10_001; i++)
        {
            childCols[$"c{i}"] = new Dictionary<string, object>
            {
                ["name"] = $"c{i}",
                ["constraints"] = new object[] { Fk("dim_orders", null, ["id"]) }
            };
        }

        using (var stream = Manifest(new Dictionary<string, object>
        {
            ["model.p.dim_orders"] = Node("dim_orders", ["id"]),
            ["model.p.fct_lines"] = Node("fct_lines", [], columnObjects: childCols)
        }))
        {
            var many = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);
            many.Relationships.Count.ShouldBe(10_000);
            many.Warnings.Count(w => w.Contains("TOO_MANY_RELATIONS")).ShouldBe(1);
            many.Warnings.Count.ShouldBe(1);
        }

        var long257 = new string('c', 257);
        using var bad = OneChild(["a"], ["x"],
        [
            Fk("dim_orders", [long257], ["a"]),
            Fk("dim_orders", ["x\ny"], ["a"]),
            Fk("dim_orders", ["x"], ["a\u0007"]),
            Fk("dim_orders", [""], ["a"])
        ]);
        var result = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(bad);
        result.Relationships.ShouldBeEmpty();
        result.Warnings.Count.ShouldBe(4);
        result.Warnings.ShouldAllBe(w => w.EndsWith("skipped: INVALID_IDENTIFIER", StringComparison.Ordinal));
        result.Warnings.ShouldAllBe(w => w.Length < 300 && !w.Any(char.IsControl));

        var cols17 = Enumerable.Range(1, 17).Select(i => $"c{i}").ToArray();
        using var wide = OneChild(cols17, cols17, [Fk("dim_orders", cols17, cols17)]);
        (await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(wide)).Warnings
            .ShouldHaveSingleItem().ShouldEndWith("skipped: TOO_WIDE");
    }

    [Fact]
    public void ForLog_TruncatesAndReplacesControlCharacters()
    {
        var value = "ab\r\ncd" + new string('x', 500);
        var log = DbtIdentifierSanitizer.ForLog(value);
        log.Length.ShouldBe(128);
        log.ShouldStartWith("ab__cd");
        DbtIdentifierSanitizer.ForLog(null).ShouldBeEmpty();
    }

    // -------------------------------------------------------------- importer

    private sealed class Harness
    {
        public ITableRelationRepository Relations { get; } = Substitute.For<ITableRelationRepository>();
        public ITableMetadataRepository Metadata { get; } = Substitute.For<ITableMetadataRepository>();
        public List<TableRelation> Written { get; } = [];
        public DbtMetadataIngestionService Service { get; }

        public Harness()
        {
            Relations.CreateRelationAsync(Arg.Any<TableRelation>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var r = ci.Arg<TableRelation>();
                    Written.Add(r);
                    return Task.FromResult(new RelationWriteResult(r.Id, true, false));
                });
            Service = new DbtMetadataIngestionService(
                Substitute.For<IDbtProposalRepository>(), Metadata, Substitute.For<ILineageGraphStore>(), Relations,
                NullLogger<DbtMetadataIngestionService>.Instance);
        }

        public void Stored(string table, string[] columns, string[]? primaryKey = null, IReadOnlyList<TableIndexDefinition>? indexes = null)
        {
            var id = new TableIdentifier("db", "s", table);
            Metadata.GetTableMetadataAsync(id, Arg.Any<CancellationToken>()).Returns(new TableMetadata
            {
                Identifier = id,
                Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList(),
                PrimaryKeyColumns = primaryKey ?? [],
                Indexes = indexes ?? []
            });
        }
    }

    private static MemoryStream Composite(string[] childCols, string[] parentCols, string[]? parentModelCols = null, string[]? childModelCols = null,
        string parent = "dim_orders") =>
        OneChild(parentModelCols ?? parentCols, childModelCols ?? childCols, [Fk(parent, childCols, parentCols)]);

    [Fact]
    public async Task Ingest_UnknownModel_ReportsUnknownModel()
    {
        var h = new Harness();
        using var stream = OneChild(["a"], ["x"], [Fk("ghost", ["x"], ["a"])]);
        var result = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
        result.Warnings.ShouldContain("Relation 'ghost' (fct_lines -> ghost) skipped: UNKNOWN_MODEL");
        result.ImportedRelationsCount.ShouldBe(0);
        h.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ingest_UnknownKeyColumn_ReportsUnknownColumn()
    {
        var h = new Harness();
        using var stream = Composite(["x", "y"], ["a", "b"], parentModelCols: ["a"]);
        var result = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
        result.Warnings.ShouldContain("Relation 'dim_orders' (fct_lines -> dim_orders) skipped: UNKNOWN_COLUMN");
        h.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ingest_StoredMetadata_WinsOverManifestColumns()
    {
        var h = new Harness();
        h.Stored("dim_orders", ["a"]);
        using var stream = Composite(["x", "y"], ["a", "b"]);
        var result = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
        result.Warnings.ShouldContain(w => w.EndsWith("skipped: UNKNOWN_COLUMN", StringComparison.Ordinal));
        h.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ingest_UsesCanonicalCatalogColumnNames()
    {
        var h = new Harness();
        h.Stored("dim_orders", ["Order_Id", "Line_No"], primaryKey: ["Order_Id", "Line_No"]);
        h.Stored("fct_lines", ["Ord", "Ln"]);
        using var stream = Composite(["ord", "LN"], ["order_id", "line_no"]);
        var result = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
        result.ImportedRelationsCount.ShouldBe(1);
        var rel = h.Written.ShouldHaveSingleItem();
        rel.JoinKeysParent.ShouldBe(["Order_Id", "Line_No"]);
        rel.JoinKeysChild.ShouldBe(["Ord", "Ln"]);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ingest_ParentKeyNotUnique_WarnsAndImports()
    {
        var h = new Harness();
        h.Stored("dim_orders", ["a", "b"], primaryKey: ["a"]);
        using var stream = Composite(["x", "y"], ["a", "b"]);
        var result = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
        result.ImportedRelationsCount.ShouldBe(1);
        h.Written.ShouldHaveSingleItem();
        result.Warnings.ShouldHaveSingleItem().ShouldBe("Relation 'dim_orders' (fct_lines -> dim_orders) imported with warning: PARENT_KEY_NOT_UNIQUE");
    }

    [Fact]
    public async Task Ingest_ParentKeyUniqueIndex_AsSet_DoesNotWarn()
    {
        var h = new Harness();
        h.Stored("dim_orders", ["a", "b"], indexes: [new TableIndexDefinition("ux", ["b", "a"], true)]);
        using var stream = Composite(["x", "y"], ["a", "b"]);
        var result = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
        result.Warnings.ShouldBeEmpty();
        h.Written.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Ingest_PersistFailure_ReportsPersistFailed()
    {
        var h = new Harness();
        h.Relations.CreateRelationAsync(Arg.Any<TableRelation>(), Arg.Any<CancellationToken>())
            .Returns<Task<RelationWriteResult>>(_ => throw new InvalidRelationException(RelationKeyViolation.CountMismatch, "boom: sensitive detail"));
        using var stream = Composite(["x"], ["a"]);
        var result = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
        result.Success.ShouldBeTrue();
        result.ImportedRelationsCount.ShouldBe(0);
        var w = result.Warnings.ShouldHaveSingleItem();
        w.ShouldEndWith("skipped: PERSIST_FAILED");
        w.ShouldNotContain("sensitive");
    }

    [Fact]
    public async Task Ingest_DryRun_ReportsSameWarnings()
    {
        static MemoryStream Build() => Manifest(new Dictionary<string, object>
        {
            ["model.p.dim_orders"] = Node("dim_orders", ["a", "b"]),
            ["model.p.fct_lines"] = Node("fct_lines", ["x", "y", "z"],
            [
                Fk("dim_orders", ["x", "y"], ["a", "b"]),
                Fk("ghost", ["x"], ["a"]),
                Fk("dim_orders", ["x"], ["nope"]),
                Fk("dim_orders", ["x", "y"], ["a"])
            ])
        });

        var live = new Harness();
        using var s1 = Build();
        var liveResult = await live.Service.IngestManifestStreamAsClusterAdminAsync(s1, dryRun: false);

        var dry = new Harness();
        using var s2 = Build();
        var dryResult = await dry.Service.IngestManifestStreamAsClusterAdminAsync(s2, dryRun: true);

        dryResult.Warnings.ShouldBe(liveResult.Warnings);
        dryResult.ImportedRelationsCount.ShouldBe(liveResult.ImportedRelationsCount);
        liveResult.Warnings.Count.ShouldBeGreaterThanOrEqualTo(3);
        dry.Written.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Import_TwoForeignKeysSameParent_NoLastWinsSwap()
    {
        // T-REL151-05 (SEC-REL151-01): created_by / approved_by both point at dim_users; same natural key, different keys.
        var h = new Harness();
        using var stream = Manifest(new Dictionary<string, object>
        {
            ["model.p.dim_users"] = Node("dim_users", ["id"]),
            ["model.p.fct_docs"] = Node("fct_docs", ["created_by", "approved_by"],
            [
                Fk("dim_users", ["created_by"], ["id"]),
                Fk("dim_users", ["approved_by"], ["id"])
            ])
        });

        var forward = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
        h.Written.ShouldBeEmpty();
        forward.ImportedRelationsCount.ShouldBe(0);
        forward.Warnings.Count(w => w.EndsWith("skipped: DUPLICATE_RELATION", StringComparison.Ordinal)).ShouldBe(1);

        // Reversed manifest order must give the same outcome (no ordering dependence).
        var h2 = new Harness();
        using var reversed = Manifest(new Dictionary<string, object>
        {
            ["model.p.dim_users"] = Node("dim_users", ["id"]),
            ["model.p.fct_docs"] = Node("fct_docs", ["created_by", "approved_by"],
            [
                Fk("dim_users", ["approved_by"], ["id"]),
                Fk("dim_users", ["created_by"], ["id"])
            ])
        });
        await h2.Service.IngestManifestStreamAsClusterAdminAsync(reversed);
        h2.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ingest_IdenticalDuplicateRelation_ImportsOnce()
    {
        var h = new Harness();
        using var stream = OneChild(["a"], ["x"], [Fk("dim_orders", ["x"], ["a"]), Fk("dim_orders", ["X"], ["A"])]);
        var result = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
        h.Written.ShouldHaveSingleItem();
        result.ImportedRelationsCount.ShouldBe(1);
        result.Warnings.ShouldNotContain(w => w.Contains("DUPLICATE_RELATION"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Import_MaliciousColumnNames_NeverReachSql_AndWarningsAreSanitized()
    {
        // T-REL151-07: quotes, ;--, newlines, 5 000 chars. The repository mock records everything that would be persisted.
        var h = new Harness();
        var evil = new[] { "x'; DROP TABLE t;--", "a\r\nb", new string('q', 5000), "\"; --" };
        foreach (var name in evil)
        {
            using var stream = OneChild(["a"], ["x"], [Fk("dim_orders", [name], ["a"])]);
            var result = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
            result.Warnings.ShouldNotBeEmpty();
            result.Warnings.ShouldAllBe(w => w.Length < 300 && !w.Any(char.IsControl));
            result.Warnings.ShouldNotContain(w => w.Contains("DROP TABLE") || w.Contains("qqqq"));
        }

        h.Written.ShouldBeEmpty();

        // A malicious model name (5 000 chars, newline) is rejected and never logged in full.
        var evilModel = new string('m', 5000) + "\nforged";
        using var s2 = Manifest(new Dictionary<string, object>
        {
            ["model.p.dim_orders"] = Node("dim_orders", ["a"]),
            ["model.p.fct_lines"] = Node("fct_lines", ["x"], [Fk(evilModel, ["x"], ["a"])])
        });
        var r2 = await h.Service.IngestManifestStreamAsClusterAdminAsync(s2);
        r2.Warnings.ShouldAllBe(w => w.Length < 400 && !w.Any(char.IsControl));
        h.Written.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Import_CrossSourceCompositeRelation_NotExecutable_UntilApproved()
    {
        // T-REL151-04 (SEC-REL151-04/05): the importer never sets AllowCrossDataSource; a composite relation between two
        // sources stays non-executable. (The persisted allow_cross_data_source column arrives with I-18.)
        var h = new Harness();
        using var stream = Composite(["x", "y"], ["a", "b"]);
        var result = await h.Service.IngestManifestStreamAsClusterAdminAsync(stream);
        result.ImportedRelationsCount.ShouldBe(1);
        var rel = h.Written.ShouldHaveSingleItem();
        rel.IsComposite.ShouldBeTrue();
        rel.AllowCrossDataSource.ShouldBeFalse();

        var parent = new TableMetadata { Identifier = rel.ParentTableIdentifier, Table = new Table { SourceName = "pg" } };
        var child = new TableMetadata { Identifier = rel.ChildTableIdentifier, Table = new Table { SourceName = "mssql" } };
        RelationExecutability.IsSameExecutionSource(parent, child).ShouldBeFalse();
    }
}
