namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using Autheris.Application.Data.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>#323 (GAP-030) startswith/endswith/contains and #324 (GAP-033) eq/ne null; SEC-RG-10, SEC-RG-14.</summary>
public sealed class DataApiFilterTranslatorTests
{
    private static TableMetadata Meta() => new()
    {
        Identifier = new TableIdentifier("d", "s", "t"),
        Table = new Table { TableName = "t", SchemaName = "s", SourceType = "Sqlite", SourceName = "d" },
        Columns =
        [
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "amount", DataType = "int" },
            new TableColumn { ColumnName = "nullable_col", DataType = "int" }
        ]
    };

    [Fact]
    public void StartsWith_EmitsEscapedLike() =>
        DataApiFilterTranslator.TranslateFilterToSql("startswith(region, 'No')").ShouldBe("region LIKE 'No%' ESCAPE '!'");

    [Fact]
    public void EndsWithContains()
    {
        DataApiFilterTranslator.TranslateFilterToSql("endswith(region, 'th')").ShouldBe("region LIKE '%th' ESCAPE '!'");
        DataApiFilterTranslator.TranslateFilterToSql("CONTAINS( region , 'or' )").ShouldBe("region LIKE '%or%' ESCAPE '!'");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("50%", "'50!%%'")]
    [InlineData("a_b", "'a!_b%'")]
    [InlineData("hi!", "'hi!!%'")]
    public void ContainsPatternMetacharacters_Escaped(string literal, string expectedPattern) =>
        DataApiFilterTranslator.TranslateFilterToSql($"startswith(region, '{literal}')")
            .ShouldBe($"region LIKE {expectedPattern} ESCAPE '!'");

    [Fact]
    public void WildcardsInLiteral_AreEscaped() =>
        DataApiFilterTranslator.TranslateFilterToSql("contains(region, '%_%')").ShouldBe("region LIKE '%!%!_!%%' ESCAPE '!'");

    [Fact]
    public void QuoteInLiteral_StaysOneLiteral() =>
        DataApiFilterTranslator.TranslateFilterToSql("startswith(region, 'O''Brien')").ShouldBe("region LIKE 'O''Brien%' ESCAPE '!'");

    [Fact]
    public void CombinedWithOperators_StillTranslated() =>
        DataApiFilterTranslator.TranslateFilterToSql("startswith(region, 'N') and amount gt 5")
            .ShouldBe("region LIKE 'N%' ESCAPE '!' AND amount > 5");

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("startswith(region, amount)")]
    [InlineData("startswith(region, 'a', 'b')")]
    [InlineData("startswith('a', 'b')")]
    [InlineData("startswith(lower(region), 'a')")]
    public void OtherArgumentShapes_StayUntouched(string filter) =>
        DataApiFilterTranslator.TranslateFilterToSql(filter).ShouldNotContain("LIKE");

    [Trait("Category", "Security")]
    [Fact]
    public void FunctionTextInsideLiteral_IsNotRewritten() =>
        DataApiFilterTranslator.TranslateFilterToSql("region eq 'startswith(a, ''b'')'").ShouldBe("region = 'startswith(a, ''b'')'");

    [Fact]
    public void TrinoTranslation_QuotesColumn_KeepsEscapeKeyword() =>
        DataApiFilterTranslator.TranslateFilterToTrino("startswith(region, 'No')", Meta())
            .ShouldBe("\"region\" LIKE 'No%' ESCAPE '!'");

    [Fact]
    public void ExtractedColumns_IncludeLikeTarget_NotTheFunctionName()
    {
        var columns = DataApiFilterTranslator.ExtractReferencedColumns("startswith(region, 'No') and amount gt 1", Meta());

        columns.ShouldBe(["region", "amount"], ignoreOrder: true);
    }

    [Fact]
    public void EqNull_IsNull() => DataApiFilterTranslator.TranslateFilterToSql("amount eq null").ShouldBe("amount IS NULL");

    [Fact]
    public void NeNull_IsNotNull() => DataApiFilterTranslator.TranslateFilterToSql("amount ne null").ShouldBe("amount IS NOT NULL");

    [Fact]
    public void EqNull_CaseInsensitiveAndCombined() =>
        DataApiFilterTranslator.TranslateFilterToSql("amount EQ NULL and region ne 'x'").ShouldBe("amount IS NULL AND region <> 'x'");

    [Fact]
    public void LiteralNullText_Untouched() =>
        DataApiFilterTranslator.TranslateFilterToSql("region eq 'null'").ShouldBe("region = 'null'");

    [Trait("Category", "Security")]
    [Fact]
    public void EqNullInsideLiteral_Untouched() =>
        DataApiFilterTranslator.TranslateFilterToSql("region eq 'x eq null y'").ShouldBe("region = 'x eq null y'");

    [Fact]
    public void NullPrefixedIdentifier_NotRewritten() =>
        DataApiFilterTranslator.TranslateFilterToSql("amount eq nullable_col").ShouldBe("amount = nullable_col");

    [Fact]
    public void TrinoTranslation_EqNull_QuotesColumn() =>
        DataApiFilterTranslator.TranslateFilterToTrino("amount eq null", Meta()).ShouldBe("\"amount\" IS NULL");
}
