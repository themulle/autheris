namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Data.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>Issue #94 D-10 / SD-1: the shared default read ordering (all-or-nothing key candidates).</summary>
[Trait("Category", "Security")]
public sealed class DefaultReadOrderingTests
{
    private static readonly TableIdentifier Id = new("d", "s", "t");

    private static TableMetadata Meta(params (string Name, string Type)[] cols) => new()
    {
        Identifier = Id,
        Table = new Table { SchemaName = "s", TableName = "t", DataSourceType = DataSourceType.Sql, SourceType = "SqlServer" },
        Columns = cols.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList()
    };

    private static TableAccessDecision Levels(params (string Name, ColumnAccessLevel Level)[] levels) => TableAccessDecision.Allowed(
        Id,
        levels.ToDictionary(l => l.Name, l => l.Level, StringComparer.OrdinalIgnoreCase),
        hasUnconstrainedColumnAllow: true);

    private static TableAccessDecision AllClear(TableMetadata m) =>
        Levels(m.Columns.Select(c => (c.ColumnName, ColumnAccessLevel.Clear)).ToArray());

    private static TableColumn Col(string name, params (string K, string V)[] meta) => new()
    {
        ColumnName = name,
        DataType = "int",
        Meta = meta.ToDictionary(m => m.K, m => m.V)
    };

    [Fact]
    public void DeclaredPrimaryKey_Wins()
    {
        var m = Meta(("a", "int"), ("b", "int"), ("c", "int")) with
        {
            PrimaryKeyColumns = new[] { "b", "a" },
            Indexes = new[] { new TableIndexDefinition("u", new[] { "c" }, true) }
        };

        var o = DefaultReadOrdering.Resolve(m, AllClear(m));

        o.Kind.ShouldBe(DefaultOrderingKind.PrimaryKey);
        o.Columns.ShouldBe(new[] { "b", "a" });
    }

    [Fact]
    public void UniqueIndex_FewestColumns_NonUniqueIgnored()
    {
        var m = Meta(("a", "int"), ("b", "int"), ("c", "int")) with
        {
            Indexes = new[]
            {
                new TableIndexDefinition("n", new[] { "c" }, false),
                new TableIndexDefinition("u2", new[] { "a", "b" }, true),
                new TableIndexDefinition("u1", new[] { "b" }, true)
            }
        };

        var o = DefaultReadOrdering.Resolve(m, AllClear(m));

        o.Kind.ShouldBe(DefaultOrderingKind.UniqueIndex);
        o.Columns.ShouldBe(new[] { "b" });
    }

    [Fact]
    public void KeyWithMaskedColumn_SkippedWhole_NeverPrefix()
    {
        var m = Meta(("a", "int"), ("b", "int"), ("c", "int")) with { PrimaryKeyColumns = new[] { "a", "b" } };
        var o = DefaultReadOrdering.Resolve(m, Levels(("a", ColumnAccessLevel.Clear), ("b", ColumnAccessLevel.Mask), ("c", ColumnAccessLevel.Clear)));

        o.Kind.ShouldBe(DefaultOrderingKind.ClearColumnComposite);
        o.Columns.ShouldBe(new[] { "a", "c" });
    }

    [Fact]
    public void KeyWithDeniedColumn_Skipped()
    {
        var m = Meta(("a", "int"), ("b", "int")) with { PrimaryKeyColumns = new[] { "a", "b" } };
        var o = DefaultReadOrdering.Resolve(m, Levels(("a", ColumnAccessLevel.Clear), ("b", ColumnAccessLevel.Deny)));

        o.Columns.ShouldBe(new[] { "a" });
        o.Kind.ShouldBe(DefaultOrderingKind.ClearColumnComposite);
    }

    [Fact]
    public void UniqueIndexWithMaskedColumn_Skipped_NextIndexUsed()
    {
        var m = Meta(("a", "int"), ("b", "int"), ("c", "int")) with
        {
            Indexes = new[]
            {
                new TableIndexDefinition("u1", new[] { "a" }, true),
                new TableIndexDefinition("u2", new[] { "b", "c" }, true)
            }
        };

        var o = DefaultReadOrdering.Resolve(m, Levels(("a", ColumnAccessLevel.Mask), ("b", ColumnAccessLevel.Clear), ("c", ColumnAccessLevel.Clear)));

        o.Kind.ShouldBe(DefaultOrderingKind.UniqueIndex);
        o.Columns.ShouldBe(new[] { "b", "c" });
    }

    [Fact]
    public void HmacOrCatalogSensitiveColumn_NotUsed()
    {
        var m = Meta(("a", "int"), ("b", "int")) with
        {
            Columns = new[]
            {
                new TableColumn { ColumnName = "a", DataType = "int", IsSensitive = true },
                new TableColumn { ColumnName = "b", DataType = "int" }
            },
            PrimaryKeyColumns = new[] { "a" }
        };
        // No explicit Clear for the catalog-sensitive column "a": the effective access is not Clear.
        var decision = TableAccessDecision.Allowed(Id, new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["b"] = ColumnAccessLevel.Clear
        }, hasUnconstrainedColumnAllow: true);

        var o = DefaultReadOrdering.Resolve(m, decision);

        o.Columns.ShouldNotContain("a");
    }

    [Fact]
    public void NoClearOrderableColumn_None()
    {
        var m = Meta(("a", "int"), ("doc", "xml"));
        var o = DefaultReadOrdering.Resolve(m, Levels(("a", ColumnAccessLevel.Mask), ("doc", ColumnAccessLevel.Clear)));

        o.Kind.ShouldBe(DefaultOrderingKind.None);
        o.Columns.ShouldBeEmpty();
    }

    [Fact]
    public void MetaMarkerKey_UsedAfterDeclaredKeyAndUniqueIndex()
    {
        var m = Meta() with
        {
            Columns = new[] { Col("x"), Col("k2", ("primary_key_ordinal", "2")), Col("k1", ("primary_key_ordinal", "1")) }
        };

        var o = DefaultReadOrdering.Resolve(m, AllClear(m));

        o.Kind.ShouldBe(DefaultOrderingKind.MetaMarkerKey);
        o.Columns.ShouldBe(new[] { "k1", "k2" });

        var withIndex = m with { Indexes = new[] { new TableIndexDefinition("u", new[] { "x" }, true) } };
        DefaultReadOrdering.Resolve(withIndex, AllClear(withIndex)).Columns.ShouldBe(new[] { "x" });
    }

    [Fact]
    public void MetaMarkerKey_WithNonClearColumn_SkippedWhole()
    {
        var m = Meta() with { Columns = new[] { Col("k1", ("primary_key_ordinal", "1")), Col("k2", ("primary_key_ordinal", "2")) } };

        var o = DefaultReadOrdering.Resolve(m, Levels(("k1", ColumnAccessLevel.Clear), ("k2", ColumnAccessLevel.Mask)));

        o.Kind.ShouldBe(DefaultOrderingKind.ClearColumnComposite);
        o.Columns.ShouldBe(new[] { "k1" });
    }

    [Theory]
    [InlineData("0", "1")]
    [InlineData("1", "1")]
    [InlineData("1", "3")]
    [InlineData("abc", "1")]
    [InlineData("-1", "1")]
    public void MetaMarkerKey_CorruptOrdinals_AreInvalid(string o1, string o2)
    {
        var cols = new[] { Col("k1", ("primary_key_ordinal", o1)), Col("k2", ("primary_key_ordinal", o2)) };

        TableKeyMarkers.Parse(cols).Columns.ShouldBeEmpty();
    }

    [Fact]
    public void MetaMarkerFlags_UsedWhenNoOrdinals()
    {
        var cols = new[] { Col("a"), Col("b", ("is_primary_key", "true")), Col("c", ("role", "primary_key")) };

        TableKeyMarkers.Parse(cols).Columns.ShouldBe(new[] { "b", "c" });
    }

    [Fact]
    public void StaleMarkerOnSubsetOfDeclaredKey_IsIgnored_DeclaredRowsWin()
    {
        // Declared PK (tenant_id, id); tenant_id is denied for the caller; a stale marker flags only id.
        var m = Meta() with
        {
            Columns = new[] { Col("tenant_id"), Col("id", ("is_primary_key", "true")), Col("v") },
            PrimaryKeyColumns = new[] { "tenant_id", "id" }
        };

        var o = DefaultReadOrdering.Resolve(m, Levels(("tenant_id", ColumnAccessLevel.Deny), ("id", ColumnAccessLevel.Clear), ("v", ColumnAccessLevel.Clear)));

        o.Kind.ShouldBe(DefaultOrderingKind.ClearColumnComposite);
        o.Columns.ShouldBe(new[] { "id", "v" });
    }

    [Fact]
    public void ContradictingMarkerKinds_AreInvalidWhole()
    {
        var cols = new[] { Col("a", ("primary_key_ordinal", "1")), Col("b", ("is_primary_key", "true")) };

        var r = TableKeyMarkers.Parse(cols);

        r.IsValid.ShouldBeFalse();
        r.Columns.ShouldBeEmpty();
    }

    [Fact]
    public void Restrict_StaleMarkerWithDeclaredKey_DoesNotLeakPartialMarkerKey()
    {
        var m = Meta() with
        {
            Columns = new[] { Col("tenant_id"), Col("id", ("is_primary_key", "true")) },
            PrimaryKeyColumns = new[] { "tenant_id", "id" }
        };

        var restricted = TableKeyVisibility.Restrict(m, new[] { m.Columns[1] });

        restricted.PrimaryKeyColumns.ShouldBeEmpty();
        restricted.Columns[0].Meta.ShouldNotContainKey("is_primary_key");
    }

    [Fact]
    public void ConventionClientIdTs_OnlyWhenBothClear()
    {
        var m = Meta(("client_id", "varchar"), ("ts", "datetime"), ("v", "int"));

        var both = DefaultReadOrdering.Resolve(m, AllClear(m));
        both.Kind.ShouldBe(DefaultOrderingKind.ConventionKey);
        both.Columns.ShouldBe(new[] { "client_id", "ts" });

        var tsMasked = DefaultReadOrdering.Resolve(m, Levels(("client_id", ColumnAccessLevel.Clear), ("ts", ColumnAccessLevel.Mask), ("v", ColumnAccessLevel.Clear)));
        tsMasked.Kind.ShouldBe(DefaultOrderingKind.ClearColumnComposite);
    }

    [Fact]
    public void ClientIdAlone_IsNeverAKey()
    {
        var m = Meta(("client_id", "varchar"), ("v", "int"));

        var o = DefaultReadOrdering.Resolve(m, AllClear(m));

        o.Kind.ShouldBe(DefaultOrderingKind.ClearColumnComposite);
        o.Columns.ShouldBe(new[] { "client_id", "v" });
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "text", false)]
    [InlineData(DatabaseDialect.SqlServer, "nvarchar(max)", false)]
    [InlineData(DatabaseDialect.SqlServer, "varchar(20)", true)]
    [InlineData(DatabaseDialect.Oracle, "CLOB", false)]
    [InlineData(DatabaseDialect.Oracle, "RAW(16)", false)]
    [InlineData(DatabaseDialect.Oracle, "LONG RAW", false)]
    [InlineData(DatabaseDialect.Oracle, "VARCHAR2(40)", true)]
    [InlineData(DatabaseDialect.PostgreSql, "text", true)]
    [InlineData(DatabaseDialect.PostgreSql, "jsonb", false)]
    [InlineData(DatabaseDialect.PostgreSql, "bytea", false)]
    [InlineData(DatabaseDialect.PostgreSql, "integer[]", false)]
    [InlineData(DatabaseDialect.Sqlite, "TEXT", true)]
    [InlineData(DatabaseDialect.Sqlite, "BLOB", false)]
    [InlineData(DatabaseDialect.Databricks, "ARRAY<INT>", false)]
    [InlineData(DatabaseDialect.Databricks, "MAP<STRING,INT>", false)]
    [InlineData(DatabaseDialect.Databricks, "string", true)]
    [InlineData(DatabaseDialect.SqlServer, "geography", false)]
    [InlineData(DatabaseDialect.SqlServer, "sql_variant", false)]
    [InlineData(DatabaseDialect.SqlServer, "some_unknown_type", true)]
    [InlineData(DatabaseDialect.SqlServer, "", true)]
    public void IsOrderableType_PerDialect(DatabaseDialect dialect, string type, bool expected) =>
        DefaultReadOrdering.IsOrderableType(type, dialect).ShouldBe(expected);

    [Fact]
    public void TableRowIdentity_PrimaryKey_ThenFewestColumnUniqueIndex_ThenNone()
    {
        var m = Meta(("a", "int"), ("b", "int"));
        TableRowIdentity.Select(m).ShouldBeEmpty();

        var idx = m with { Indexes = new[] { new TableIndexDefinition("u2", new[] { "a", "b" }, true), new TableIndexDefinition("u1", new[] { "b" }, true) } };
        TableRowIdentity.Select(idx).ShouldBe(new[] { "b" });

        TableRowIdentity.Select(idx with { PrimaryKeyColumns = new[] { "a", "b" } }).ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public void TableRowIdentity_NeverReturnsSubsetOrGuess()
    {
        var m = Meta(("id", "int"), ("client_id", "varchar")) with { PrimaryKeyColumns = new[] { "id", "ghost" } };

        TableRowIdentity.Select(m).ShouldBeEmpty();
    }

    [Fact]
    public void TableRowIdentity_ConventionNeedsBothColumns()
    {
        TableRowIdentity.Select(Meta(("client_id", "varchar"), ("v", "int"))).ShouldBeEmpty();
        TableRowIdentity.Select(Meta(("client_id", "varchar"), ("ts", "datetime"))).ShouldBe(new[] { "client_id", "ts" });
    }
}
