namespace Autheris.Tests.Unit.Common;

using System;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class SqlIdentifierGuardTests
{
    [Theory]
    [InlineData("col")]
    [InlineData("_col")]
    [InlineData("COL_123")]
    [InlineData("a")]
    [InlineData("_")]
    [InlineData("orders_2026_q4")]
    public void IsValidIdentifier_AcceptsValidStrictIdentifiers(string identifier)
    {
        SqlIdentifierGuard.IsValidIdentifier(identifier).ShouldBeTrue();
        SqlIdentifierGuard.ValidateIdentifier(identifier).ShouldBe(identifier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("1col")]
    [InlineData("0")]
    [InlineData("col\n")]
    [InlineData("col\r\n")]
    [InlineData("\ncol")]
    [InlineData("col\0name")]
    [InlineData("col\u001fname")]
    [InlineData("col name")]
    [InlineData(" col")]
    [InlineData("col ")]
    [InlineData("col-name")]
    [InlineData("col.name")]
    [InlineData("col;DROP TABLE users")]
    [InlineData("col'quote")]
    [InlineData("col\"quote")]
    [InlineData("col`backtick")]
    [InlineData("col[bracket]")]
    [InlineData("col--comment")]
    [InlineData("col/*comment*/")]
    [InlineData("col$")]
    [InlineData("col#")]
    [InlineData("café")]
    [InlineData("ıd")]
    public void IsValidIdentifier_RejectsUnsafeOrMalformedIdentifiers(string? identifier)
    {
        SqlIdentifierGuard.IsValidIdentifier(identifier).ShouldBeFalse();

        var ex = Should.Throw<ArgumentException>(() =>
            SqlIdentifierGuard.ValidateIdentifier(identifier, "columnName"));
        ex.ParamName.ShouldBe("columnName");
    }

    [Theory]
    [InlineData("1col", true)]
    [InlineData("123", true)]
    [InlineData("0_id", true)]
    [InlineData("1col", false)]
    [InlineData("123", false)]
    public void IsValidIdentifier_RespectsAllowLeadingDigitOption(string identifier, bool allowLeadingDigit)
    {
        SqlIdentifierGuard.IsValidIdentifier(identifier, allowLeadingDigit: allowLeadingDigit)
            .ShouldBe(allowLeadingDigit);

        if (allowLeadingDigit)
        {
            SqlIdentifierGuard.ValidateIdentifier(identifier, allowLeadingDigit: true).ShouldBe(identifier);
        }
        else
        {
            Should.Throw<ArgumentException>(() =>
                SqlIdentifierGuard.ValidateIdentifier(identifier, allowLeadingDigit: false));
        }
    }

    [Fact]
    public void IsValidIdentifier_EnforcesMaxLengthBoundaries()
    {
        var maxDefault = new string('a', SqlIdentifierGuard.DefaultMaxLength);
        var tooLongDefault = new string('a', SqlIdentifierGuard.DefaultMaxLength + 1);

        SqlIdentifierGuard.IsValidIdentifier(maxDefault).ShouldBeTrue();
        SqlIdentifierGuard.IsValidIdentifier(tooLongDefault).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => SqlIdentifierGuard.ValidateIdentifier(tooLongDefault));

        SqlIdentifierGuard.IsValidIdentifier("abcd", maxLength: 4).ShouldBeTrue();
        SqlIdentifierGuard.IsValidIdentifier("abcde", maxLength: 4).ShouldBeFalse();
        SqlIdentifierGuard.IsValidIdentifier(tooLongDefault, maxLength: null).ShouldBeTrue();

        Should.Throw<ArgumentOutOfRangeException>(() => SqlIdentifierGuard.IsValidIdentifier("a", maxLength: 0));
    }

    [Theory]
    [InlineData("orders", 1, null, false, true)]
    [InlineData("dbo.orders", 1, null, false, true)]
    [InlineData("analytics.dbo.orders", 1, null, false, true)]
    [InlineData("a.b.c.d", 1, null, false, true)]
    [InlineData("dbo.orders", 2, 2, false, true)]
    [InlineData("orders", 2, 2, false, false)]
    [InlineData("a.b.c", 2, 2, false, false)]
    [InlineData("items.0.id", 1, null, true, true)]
    [InlineData("items.0.id", 1, null, false, false)]
    [InlineData(null, 1, null, false, false)]
    [InlineData("", 1, null, false, false)]
    [InlineData(".", 1, null, false, false)]
    [InlineData(".orders", 1, null, false, false)]
    [InlineData("dbo.", 1, null, false, false)]
    [InlineData("dbo..orders", 1, null, false, false)]
    [InlineData("dbo.orders\n", 1, null, false, false)]
    [InlineData("dbo.orders;DROP", 1, null, false, false)]
    [InlineData("dbo.\"orders\"", 1, null, false, false)]
    public void IsValidQualifiedIdentifier_ValidatesDotSeparatedSegments(
        string? identifier,
        int minSegments,
        int? maxSegments,
        bool allowLeadingDigit,
        bool expected)
    {
        SqlIdentifierGuard.IsValidQualifiedIdentifier(
            identifier,
            minSegments: minSegments,
            maxSegments: maxSegments,
            allowLeadingDigit: allowLeadingDigit).ShouldBe(expected);

        if (expected)
        {
            SqlIdentifierGuard.ValidateQualifiedIdentifier(
                identifier,
                paramName: "qualified",
                minSegments: minSegments,
                maxSegments: maxSegments,
                allowLeadingDigit: allowLeadingDigit).ShouldBe(identifier);
        }
        else
        {
            var ex = Should.Throw<ArgumentException>(() =>
                SqlIdentifierGuard.ValidateQualifiedIdentifier(
                    identifier,
                    paramName: "qualified",
                    minSegments: minSegments,
                    maxSegments: maxSegments,
                    allowLeadingDigit: allowLeadingDigit));
            ex.ParamName.ShouldBe("qualified");
        }
    }

    [Fact]
    public void IsValidQualifiedIdentifier_EnforcesPerSegmentMaxLength()
    {
        var okSegment = new string('x', 64);
        var longSegment = new string('x', 65);

        SqlIdentifierGuard.IsValidQualifiedIdentifier($"{okSegment}.{okSegment}", maxSegmentLength: 64).ShouldBeTrue();
        SqlIdentifierGuard.IsValidQualifiedIdentifier($"{okSegment}.{longSegment}", maxSegmentLength: 64).ShouldBeFalse();
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "tenant_id", "[tenant_id]")]
    [InlineData(DatabaseDialect.PostgreSql, "tenant_id", "\"tenant_id\"")]
    [InlineData(DatabaseDialect.Oracle, "tenant_id", "\"tenant_id\"")]
    [InlineData(DatabaseDialect.Databricks, "tenant_id", "`tenant_id`")]
    public void QuoteSafeIdentifier_ValidatesAndQuotesPerDialect(
        DatabaseDialect dialect,
        string identifier,
        string expectedQuoted)
    {
        SqlIdentifierGuard.QuoteSafeIdentifier(identifier, dialect).ShouldBe(expectedQuoted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1bad")]
    [InlineData("bad;DROP")]
    [InlineData("bad.col")]
    [InlineData("bad\n")]
    public void QuoteSafeIdentifier_RejectsInvalidIdentifiersBeforeQuoting(string? identifier)
    {
        var ex = Should.Throw<ArgumentException>(() =>
            SqlIdentifierGuard.QuoteSafeIdentifier(identifier, DatabaseDialect.SqlServer, paramName: "col"));
        ex.ParamName.ShouldBe("col");
    }

    [Fact]
    public void QuoteSafeIdentifier_EnforcesMaxLength()
    {
        var tooLong = new string('a', 129);
        Should.Throw<ArgumentException>(() =>
            SqlIdentifierGuard.QuoteSafeIdentifier(tooLong, DatabaseDialect.PostgreSql));
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "dbo.orders", "[dbo].[orders]")]
    [InlineData(DatabaseDialect.PostgreSql, "dbo.orders", "\"dbo\".\"orders\"")]
    [InlineData(DatabaseDialect.Oracle, "dbo.orders", "\"dbo\".\"orders\"")]
    [InlineData(DatabaseDialect.Databricks, "dbo.orders", "`dbo`.`orders`")]
    public void QuoteSafeQualifiedIdentifier_ValidatesAndQuotesEachSegment(
        DatabaseDialect dialect,
        string qualified,
        string expected)
    {
        SqlIdentifierGuard.QuoteSafeQualifiedIdentifier(qualified, dialect).ShouldBe(expected);
    }

    [Theory]
    [InlineData("a..b")]
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("a.1b")]
    [InlineData("a.b;DROP")]
    public void QuoteSafeQualifiedIdentifier_RejectsMalformedPaths(string qualified)
    {
        Should.Throw<ArgumentException>(() =>
            SqlIdentifierGuard.QuoteSafeQualifiedIdentifier(qualified, DatabaseDialect.SqlServer));
    }

    [Theory]
    [InlineData(null, "col")]
    [InlineData("", "col")]
    [InlineData("   ", "col")]
    [InlineData("customer_id", "customer_id")]
    [InlineData("first-name", "first_name")]
    [InlineData("123_col", "_123_col")]
    [InlineData("9", "_9")]
    [InlineData("a; DROP TABLE x--", "a__DROP_TABLE_x__")]
    [InlineData("café", "caf_")]
    public void SanitizeIdentifier_ProducesSafeAsciiIdentifier(string? raw, string expected)
    {
        SqlIdentifierGuard.SanitizeIdentifier(raw).ShouldBe(expected);
    }
}
