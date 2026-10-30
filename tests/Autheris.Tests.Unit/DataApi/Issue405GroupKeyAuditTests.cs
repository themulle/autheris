namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Services;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>#405: audit of the refused group-key type family. Binding conditions I405-SC-01..09 / I405-T-S01..S07 / I405_T_01..13.</summary>
[Trait("Category", "Security")]
public sealed class Issue405GroupKeyAuditTests
{
    private static readonly string[] ClosedLiterals = ["json", "xml", "spatial", "binary", "lob", "structured", "unknown"];

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "xml", GroupKeyTypeFamily.Xml)]
    [InlineData(DatabaseDialect.SqlServer, "geography", GroupKeyTypeFamily.Spatial)]
    [InlineData(DatabaseDialect.SqlServer, "geometry", GroupKeyTypeFamily.Spatial)]
    [InlineData(DatabaseDialect.SqlServer, "varbinary(max)", GroupKeyTypeFamily.Binary)]
    [InlineData(DatabaseDialect.SqlServer, "varbinary(50)", GroupKeyTypeFamily.Binary)]
    [InlineData(DatabaseDialect.SqlServer, "binary(16)", GroupKeyTypeFamily.Binary)]
    [InlineData(DatabaseDialect.SqlServer, "text", GroupKeyTypeFamily.Lob)]
    [InlineData(DatabaseDialect.SqlServer, "ntext", GroupKeyTypeFamily.Lob)]
    [InlineData(DatabaseDialect.SqlServer, "image", GroupKeyTypeFamily.Lob)]
    [InlineData(DatabaseDialect.SqlServer, "sql_variant", GroupKeyTypeFamily.Structured)]
    [InlineData(DatabaseDialect.SqlServer, "hierarchyid", GroupKeyTypeFamily.Structured)]
    [InlineData(DatabaseDialect.SqlServer, "udt_x", GroupKeyTypeFamily.Unknown)]
    [InlineData(DatabaseDialect.SqlServer, null, GroupKeyTypeFamily.Unknown)]
    [InlineData(DatabaseDialect.SqlServer, "  ", GroupKeyTypeFamily.Unknown)]
    [InlineData(DatabaseDialect.PostgreSql, "json", GroupKeyTypeFamily.Json)]
    [InlineData(DatabaseDialect.PostgreSql, "JSONB", GroupKeyTypeFamily.Json)]
    [InlineData(DatabaseDialect.PostgreSql, "xml", GroupKeyTypeFamily.Xml)]
    [InlineData(DatabaseDialect.PostgreSql, "bytea", GroupKeyTypeFamily.Binary)]
    [InlineData(DatabaseDialect.PostgreSql, "geometry", GroupKeyTypeFamily.Spatial)]
    [InlineData(DatabaseDialect.PostgreSql, "geography", GroupKeyTypeFamily.Spatial)]
    [InlineData(DatabaseDialect.PostgreSql, "point", GroupKeyTypeFamily.Spatial)]
    [InlineData(DatabaseDialect.PostgreSql, "polygon", GroupKeyTypeFamily.Spatial)]
    [InlineData(DatabaseDialect.PostgreSql, "circle", GroupKeyTypeFamily.Spatial)]
    [InlineData(DatabaseDialect.PostgreSql, "integer[]", GroupKeyTypeFamily.Structured)]
    [InlineData(DatabaseDialect.PostgreSql, "hstore", GroupKeyTypeFamily.Structured)]
    [InlineData(DatabaseDialect.PostgreSql, "tsvector", GroupKeyTypeFamily.Structured)]
    [InlineData(DatabaseDialect.PostgreSql, "int4range", GroupKeyTypeFamily.Structured)]
    [InlineData(DatabaseDialect.PostgreSql, "mystery", GroupKeyTypeFamily.Unknown)]
    [InlineData(DatabaseDialect.Oracle, "clob", GroupKeyTypeFamily.Lob)]
    [InlineData(DatabaseDialect.Oracle, "nclob", GroupKeyTypeFamily.Lob)]
    [InlineData(DatabaseDialect.Oracle, "blob", GroupKeyTypeFamily.Lob)]
    [InlineData(DatabaseDialect.Oracle, "long", GroupKeyTypeFamily.Lob)]
    [InlineData(DatabaseDialect.Oracle, "bfile", GroupKeyTypeFamily.Lob)]
    [InlineData(DatabaseDialect.Oracle, "raw(16)", GroupKeyTypeFamily.Binary)]
    [InlineData(DatabaseDialect.Oracle, "xmltype", GroupKeyTypeFamily.Xml)]
    [InlineData(DatabaseDialect.Oracle, "json", GroupKeyTypeFamily.Json)]
    [InlineData(DatabaseDialect.Oracle, "sdo_geometry", GroupKeyTypeFamily.Spatial)]
    [InlineData(DatabaseDialect.Oracle, "mystery", GroupKeyTypeFamily.Unknown)]
    [InlineData(DatabaseDialect.Databricks, "MAP<STRING,INT>", GroupKeyTypeFamily.Structured)]
    [InlineData(DatabaseDialect.Databricks, "struct<a:int>", GroupKeyTypeFamily.Structured)]
    [InlineData(DatabaseDialect.Databricks, "ARRAY<INT>", GroupKeyTypeFamily.Structured)]
    [InlineData(DatabaseDialect.Databricks, "variant", GroupKeyTypeFamily.Structured)]
    [InlineData(DatabaseDialect.Databricks, "binary", GroupKeyTypeFamily.Binary)]
    [InlineData(DatabaseDialect.Databricks, "mystery", GroupKeyTypeFamily.Unknown)]
    public void I405_T_01_Classify_Matrix_PerDialect_MapsToDocumentedFamily(DatabaseDialect dialect, string? type, GroupKeyTypeFamily expected) =>
        GroupKeyTypeFamilies.Classify(type, dialect).ShouldBe(expected);

    [Fact]
    public void I405_T_02_AuditLiterals_AreExactlyTheClosedSet()
    {
        var families = Enum.GetValues<GroupKeyTypeFamily>();
        var literals = families.Select(GroupKeyTypeFamilies.ToAuditLiteral).ToList();

        families.Length.ShouldBe(ClosedLiterals.Length);
        literals.Distinct().Count().ShouldBe(families.Length);
        literals.OrderBy(l => l, StringComparer.Ordinal).ShouldBe(ClosedLiterals.OrderBy(l => l, StringComparer.Ordinal));
    }

    [Fact]
    public void I405_T_03_Classify_NeverEchoesInput()
    {
        var rng = new Random(405);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz_()[]<> ,0123456789";
        var dialects = Enum.GetValues<DatabaseDialect>();
        for (var i = 0; i < 50; i++)
        {
            var text = "SENTINEL" + new string(Enumerable.Range(0, rng.Next(0, 40)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
            foreach (var dialect in dialects)
            {
                var literal = GroupKeyTypeFamilies.ToAuditLiteral(GroupKeyTypeFamilies.Classify(text, dialect));
                ClosedLiterals.ShouldContain(literal);
                literal.ShouldNotContain("SENTINEL", Case.Insensitive);
            }
        }
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "nvarchar(max)", false)]
    [InlineData(DatabaseDialect.SqlServer, "varchar(max)", false)]
    [InlineData(DatabaseDialect.SqlServer, "nvarchar(200)", false)]
    [InlineData(DatabaseDialect.SqlServer, "int", false)]
    [InlineData(DatabaseDialect.SqlServer, "varbinary(max)", true)]
    [InlineData(DatabaseDialect.SqlServer, "xml", true)]
    [InlineData(DatabaseDialect.SqlServer, "text", true)]
    [InlineData(DatabaseDialect.SqlServer, "geography", true)]
    [InlineData(DatabaseDialect.SqlServer, "some_clr_type", true)]
    [InlineData(DatabaseDialect.SqlServer, "", true)]
    [InlineData(DatabaseDialect.SqlServer, null, true)]
    [InlineData(DatabaseDialect.PostgreSql, "text", false)]
    [InlineData(DatabaseDialect.PostgreSql, "nvarchar(max)", true)]
    [InlineData(DatabaseDialect.PostgreSql, "jsonb", true)]
    [InlineData(DatabaseDialect.PostgreSql, "integer[]", true)]
    [InlineData(DatabaseDialect.Oracle, "varchar2(50)", false)]
    [InlineData(DatabaseDialect.Oracle, "clob", true)]
    [InlineData(DatabaseDialect.Oracle, "text", true)]
    [InlineData(DatabaseDialect.Databricks, "string", false)]
    [InlineData(DatabaseDialect.Databricks, "ARRAY<INT>", true)]
    [InlineData(DatabaseDialect.Sqlite, "BLOB", false)]
    [InlineData(DatabaseDialect.Sqlite, "mystery", false)]
    public void I405_T_04_EnsureGroupable_AgreesWithIsGroupableType(DatabaseDialect dialect, string? type, bool throws)
    {
        DefaultReadOrdering.IsGroupableType(type, dialect).ShouldBe(!throws);
        if (throws)
        {
            Should.Throw<AggregationGroupKeyNotSupportedException>(() => GroupKeyTypeFamilies.EnsureGroupable(type, dialect));
        }
        else
        {
            Should.NotThrow(() => GroupKeyTypeFamilies.EnsureGroupable(type, dialect));
        }
    }

    [Fact]
    public void I405_T_05_Exception_MessageAndReason_ByteIdenticalToPre405()
    {
        const string PreMessage = "Not yet supported by the governed SQL compiler: Construct (aggregation group key type).";
        foreach (var family in Enum.GetValues<GroupKeyTypeFamily>())
        {
            var ex = new AggregationGroupKeyNotSupportedException(family);
            ex.Message.ShouldBe(PreMessage);
            ex.ShouldBeAssignableTo<SqlCompileNotSupportedException>().Reason.ShouldBe(SqlCompileNotSupportedReason.Construct);
            ex.Family.ShouldBe(family);
            ex.ToString().ShouldNotContain(GroupKeyTypeFamilies.ToAuditLiteral(family) + "\"", Case.Insensitive);
        }

        typeof(AggregationGroupKeyNotSupportedException).IsSealed.ShouldBeTrue();
        typeof(AggregationGroupKeyNotSupportedException).GetConstructors().ShouldHaveSingleItem()
            .GetParameters().Select(p => p.ParameterType).ShouldBe([typeof(GroupKeyTypeFamily)]);
    }

    [Fact]
    public void I405_T_S04_Classify_OversizedOrMalformedInput_IsUnknown_NoThrow()
    {
        var oversized = "xml" + new string(' ', 10_000);
        string[] malformed = ["(", ")))", "[]", "<<<", "\0\0", "xml(", new string('(', 5000), "\uD800json", "json\r\njsonb"];
        foreach (var dialect in Enum.GetValues<DatabaseDialect>())
        {
            GroupKeyTypeFamilies.Classify(oversized, dialect).ShouldBe(GroupKeyTypeFamily.Unknown);
            GroupKeyTypeFamilies.Classify(new string('a', GroupKeyTypeFamilies.MaxTypeTextLength + 1), dialect).ShouldBe(GroupKeyTypeFamily.Unknown);
            foreach (var text in malformed)
            {
                Should.NotThrow(() => GroupKeyTypeFamilies.Classify(text, dialect));
            }
        }

        // the classifier never decides refusal: an oversized type is still refused by the allow-list.
        Should.Throw<AggregationGroupKeyNotSupportedException>(() => GroupKeyTypeFamilies.EnsureGroupable(oversized, DatabaseDialect.SqlServer))
            .Family.ShouldBe(GroupKeyTypeFamily.Unknown);
    }

    [Fact]
    public void I405_T_13_RunbookSection_ListsEveryLiteral()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        var text = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "operations-runbook.md"));
        var start = text.IndexOf("### 4.al ", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0);
        var end = text.IndexOf("\n## ", start, StringComparison.Ordinal);
        var section = text[start..end];

        foreach (var literal in ClosedLiterals)
        {
            section.ShouldContain($"`{literal}`");
        }

        section.ShouldContain("group_key_type_family");
        section.ShouldContain("refusal_reason");
        section.ShouldContain("caller-supplied");
        section.ShouldContain("never raw type text");
    }
}
