using System;
using System.Collections.Generic;
using System.Security;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

namespace Autheris.Tests.Unit.Masking;

public class AdvancedMaskingRuleTests
{
    private readonly ColumnMaskingProvider _provider = new();

    #region R-53: GEO_JITTER Tests

    [Fact]
    public void GeoJitter_RoundMode_RoundsToSpecifiedDecimalsAwayFromZero()
    {
        var rule = new MaskingRule
        {
            RuleType = "GEO_JITTER",
            Mode = "round",
            Decimals = 2
        };

        var lat = 48.298197967;
        var lon = 9.700374517;

        var maskedLat = _provider.MaskValue("latitude", lat, rule);
        var maskedLon = _provider.MaskValue("longitude", lon, rule);

        maskedLat.ShouldBe(48.30);
        maskedLon.ShouldBe(9.70);
    }

    [Fact]
    public void GeoJitter_RoundMode_DecimalClamping_ClampsBetween0And6()
    {
        var ruleNegative = new MaskingRule
        {
            RuleType = "GEO_JITTER",
            Mode = "round",
            Decimals = -5
        };
        ruleNegative.Decimals.ShouldBe(0);

        var ruleExcessive = new MaskingRule
        {
            RuleType = "GEO_JITTER",
            Mode = "round",
            Decimals = 12
        };
        ruleExcessive.Decimals.ShouldBe(6);

        var lat = 48.298197967;
        var masked0 = _provider.MaskValue("latitude", lat, ruleNegative);
        masked0.ShouldBe(48.0);

        var masked6 = _provider.MaskValue("latitude", lat, ruleExcessive);
        masked6.ShouldBe(48.298198);
    }

    [Fact]
    public void GeoJitter_ZeroGuard_CoordinatesAtZeroRemainStrictlyZero()
    {
        var ruleRound = new MaskingRule
        {
            RuleType = "GEO_JITTER",
            Mode = "round",
            Decimals = 2
        };
        var ruleNoise = new MaskingRule
        {
            RuleType = "GEO_JITTER",
            Mode = "noise",
            RadiusMeters = 500.0
        };

        var latZeroRound = _provider.MaskValue("lat", 0.0, ruleRound);
        var lonZeroRound = _provider.MaskValue("lon", 0.0m, ruleRound);
        var latZeroNoise = _provider.MaskValue("lat", 0.0, ruleNoise);
        var lonZeroNoise = _provider.MaskValue("lon", 0.0f, ruleNoise);

        latZeroRound.ShouldBe(0.0);
        lonZeroRound.ShouldBe(0.0m);
        latZeroNoise.ShouldBe(0.0);
        Convert.ToDouble(lonZeroNoise).ShouldBe(0.0);
    }

    [Fact]
    public void GeoJitter_NullCoordinate_ReturnsNull()
    {
        var rule = new MaskingRule
        {
            RuleType = "GEO_JITTER",
            Mode = "round",
            Decimals = 2
        };

        var result = _provider.MaskValue("lat", null, rule);
        result.ShouldBeNull();
    }

    [Fact]
    public void GeoJitter_NoiseMode_IsDeterministicForSameSeed()
    {
        var rule = new MaskingRule
        {
            RuleType = "GEO_JITTER",
            Mode = "noise",
            RadiusMeters = 500.0
        };

        var lat = 48.298197967;
        var tenant = "tenant-prod-1";

        var result1 = ColumnMaskingProvider.MaskValue(lat, rule, tenant);
        var result2 = ColumnMaskingProvider.MaskValue(lat, rule, tenant);

        result1.ShouldNotBeNull();
        result1.ShouldBe(result2);

        // Different tenant yields different jitter
        var resultDifferentTenant = ColumnMaskingProvider.MaskValue(lat, rule, "tenant-prod-2");
        resultDifferentTenant.ShouldNotBe(result1);

        // Coordinate was perturbed but within reasonable delta (~500m jitter is ~0.0045 degrees)
        var delta = Math.Abs((double)result1! - lat);
        delta.ShouldBeGreaterThan(0.0);
        delta.ShouldBeLessThan(0.05);
    }

    #endregion

    #region R-25: PARTIAL_MASK Tests

    [Fact]
    public void PartialMask_StandardKeepPrefixAndSuffix_MasksMiddleCorrectly()
    {
        var rule = new MaskingRule
        {
            RuleType = "PARTIAL_MASK",
            KeepPrefix = 1,
            KeepSuffix = 0,
            MaskChar = '*'
        };

        var result = _provider.MaskValue("last_name", "Mustermann", rule);
        result.ShouldBe("M*********");
    }

    [Fact]
    public void PartialMask_PrefixAndSuffix_PreservesBoth()
    {
        var rule = new MaskingRule
        {
            RuleType = "PARTIAL_MASK",
            KeepPrefix = 3,
            KeepSuffix = 2,
            MaskChar = '#'
        };

        var result = _provider.MaskValue("account_number", "DE123456789XX", rule);
        result.ShouldBe("DE1########XX");
    }

    [Fact]
    public void PartialMask_RuneSafe_MasksUnicodeCharactersCorrectly()
    {
        var rule = new MaskingRule
        {
            RuleType = "PARTIAL_MASK",
            KeepPrefix = 1,
            KeepSuffix = 0,
            MaskChar = '*'
        };

        // "Österreicher" has 12 runes. 1 prefix kept + 11 mask chars
        var result = _provider.MaskValue("name", "Österreicher", rule);
        result.ShouldBe("Ö***********");
        result!.ToString()!.Length.ShouldBe("Österreicher".Length);
    }

    [Fact]
    public void PartialMask_ShortString_LengthOracleProtection_ReturnsFixedMask()
    {
        var rule = new MaskingRule
        {
            RuleType = "PARTIAL_MASK",
            KeepPrefix = 2,
            KeepSuffix = 1,
            MaskChar = '*'
        };

        // Length of "AB" (2) <= KeepPrefix (2) + KeepSuffix (1)
        var result = _provider.MaskValue("code", "AB", rule);
        result.ShouldBe("*****");
    }

    [Fact]
    public void PartialMask_FixedLengthEnabled_ReturnsFixedMask()
    {
        var rule = new MaskingRule
        {
            RuleType = "PARTIAL_MASK",
            KeepPrefix = 1,
            KeepSuffix = 1,
            MaskChar = '*',
            FixedLength = true
        };

        var result = _provider.MaskValue("ssn", "123-45-6789", rule);
        result.ShouldBe("1*****9");

        // When length <= prefix + suffix, it outputs exactly 5 mask chars
        var shortResult = _provider.MaskValue("ssn", "AB", rule);
        shortResult.ShouldBe("*****");
    }

    #endregion

    #region TOKENIZE Tests

    [Fact]
    public void Tokenize_FormatAndDeterminism()
    {
        var rule = new MaskingRule
        {
            RuleType = "TOKENIZE",
            TokenDomain = "CUST"
        };

        var value = "user-12345";
        var tenant = "tenant-a";

        var token1 = ColumnMaskingProvider.MaskValue(value, rule, tenant)?.ToString();
        var token2 = ColumnMaskingProvider.MaskValue(value, rule, tenant)?.ToString();

        token1.ShouldNotBeNull();
        token1.ShouldStartWith("TOK_CUST_");
        token1.Length.ShouldBe("TOK_CUST_".Length + 8);
        token1.ShouldBe(token2);

        // Different value produces different token
        var tokenOther = ColumnMaskingProvider.MaskValue("user-99999", rule, tenant)?.ToString();
        tokenOther.ShouldNotBe(token1);
    }

    #endregion

    #region B-06: Typed REDACT Tests

    [Fact]
    public void Redact_NumericAndTemporalTypes_ReturnStrictlyNull()
    {
        var rule = new MaskingRule { RuleType = "REDACT", Replacement = "[REDACTED]" };

        // Numbers must return null, never 0 or "[REDACTED]"
        _provider.MaskValue("amount", 12500.50m, rule).ShouldBeNull();
        _provider.MaskValue("count", 42, rule).ShouldBeNull();
        _provider.MaskValue("balance", 99.99, rule).ShouldBeNull();
        _provider.MaskValue("id", 10000000000L, rule).ShouldBeNull();

        // Temporals must return null
        _provider.MaskValue("created_at", DateTime.UtcNow, rule).ShouldBeNull();
        _provider.MaskValue("offset", DateTimeOffset.UtcNow, rule).ShouldBeNull();
        _provider.MaskValue("date", DateOnly.FromDateTime(DateTime.Today), rule).ShouldBeNull();

        // Booleans return null
        _provider.MaskValue("is_active", true, rule).ShouldBeNull();

        // String returns redacted token
        _provider.MaskValue("notes", "Top Secret", rule).ShouldBe("[REDACTED]");
    }

    #endregion

    #region Fail-Closed Predicate Guard (AstSecurityVisitor)

    private readonly FastSqlEngine _engine = new();

    private void SecureAst(string sql, string maskedCol, TargetSqlDialect dialect = TargetSqlDialect.PostgreSql)
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("1=1"),
            TargetDialect = dialect,
            RejectMaskedColumnsInPredicates = true,
            TableColumnsProvider = _ => new[] { "id", "name", "salary", "ssn", "bonus", "department", "info" },
            ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
                hasMaskPredicate: (_, c) => c.Equals(maskedCol, StringComparison.OrdinalIgnoreCase),
                maskExpressionProvider: (_, _) => "'***'")
        };

        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        var builder = new SqlAstBuilder(AstBuilderOptions.FromRlsOptions(options));
        var ast = builder.BuildStatement(tree);
        var visitor = new AstSecurityVisitor(options, _engine);
        visitor.Visit(ast);
    }

    [Fact]
    public void PredicateGuard_MaskedColumnInWhere_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() =>
            SecureAst("SELECT id, name FROM employees WHERE salary > 50000", "salary"));
    }

    [Fact]
    public void PredicateGuard_MaskedColumnInOrderBy_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() =>
            SecureAst("SELECT id, name FROM users ORDER BY ssn", "ssn"));
    }

    [Fact]
    public void PredicateGuard_MaskedColumnInHaving_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() =>
            SecureAst("SELECT department, sum(bonus) FROM sales GROUP BY department HAVING bonus > 1000", "bonus", TargetSqlDialect.SqlServer));
    }

    [Fact]
    public void PredicateGuard_MaskedColumnInJoinOn_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() =>
            SecureAst("SELECT a.id, b.info FROM a JOIN b ON a.ssn = b.ssn", "ssn"));
    }

    [Fact]
    public void PredicateGuard_UnmaskedColumnsInPredicates_SucceedsNormally()
    {
        Should.NotThrow(() =>
            SecureAst("SELECT id, name, salary FROM employees WHERE id = 123 ORDER BY name", "salary"));
    }

    #endregion

    #region AstSecurityVisitor Dialect SQL Mask Generation

    [Fact]
    public void BuildDialectMaskExpression_GeoJitter_GeneratesCorrectDialectSql()
    {
        var pgSql = AstSecurityVisitor.BuildDialectMaskExpression(
            "latitude", "GEO_JITTER", TargetSqlDialect.PostgreSql, decimals: 2);
        pgSql.ShouldBe("CASE WHEN \"latitude\" IS NULL OR \"latitude\" = 0.0 THEN \"latitude\" ELSE ROUND(\"latitude\"::numeric, 2)::double precision END");

        var msSql = AstSecurityVisitor.BuildDialectMaskExpression(
            "latitude", "GEO_JITTER", TargetSqlDialect.SqlServer, decimals: 3);
        msSql.ShouldBe("CASE WHEN [latitude] IS NULL OR [latitude] = 0.0 THEN [latitude] ELSE ROUND([latitude], 3) END");

        var sqlite = AstSecurityVisitor.BuildDialectMaskExpression(
            "lat", "GEO_JITTER", TargetSqlDialect.Sqlite, decimals: 1);
        sqlite.ShouldBe("CASE WHEN \"lat\" IS NULL OR \"lat\" = 0.0 THEN \"lat\" ELSE ROUND(\"lat\", 1) END");
    }

    [Fact]
    public void BuildDialectMaskExpression_PartialMask_GeneratesCorrectDialectSql()
    {
        var pgSql = AstSecurityVisitor.BuildDialectMaskExpression(
            "card_number", "PARTIAL_MASK", TargetSqlDialect.PostgreSql, keepPrefix: 2, keepSuffix: 4, maskChar: 'X');
        pgSql.ShouldContain("REPEAT('X'");
        pgSql.ShouldContain("SUBSTRING(\"card_number\"");

        var msSql = AstSecurityVisitor.BuildDialectMaskExpression(
            "card_number", "PARTIAL_MASK", TargetSqlDialect.SqlServer, keepPrefix: 1, keepSuffix: 0, maskChar: '*');
        msSql.ShouldContain("LEFT([card_number], 1)");
        msSql.ShouldContain("REPLICATE('*'");
    }

    [Fact]
    public void BuildDialectMaskExpression_Redact_GeneratesTypedNullCast()
    {
        var pgSql = AstSecurityVisitor.BuildDialectMaskExpression(
            "balance", "REDACT", TargetSqlDialect.PostgreSql, dataType: "DECIMAL(18,2)");
        pgSql.ShouldBe("CAST(NULL AS NUMERIC)");

        var msSql = AstSecurityVisitor.BuildDialectMaskExpression(
            "balance", "REDACT", TargetSqlDialect.SqlServer, dataType: "DECIMAL(18,2)");
        msSql.ShouldBe("CAST(NULL AS DECIMAL(18,2))");

        var defaultSql = AstSecurityVisitor.BuildDialectMaskExpression(
            "notes", "REDACT", TargetSqlDialect.SqlServer, replacement: "[CUSTOM_REDACT]");
        defaultSql.ShouldBe("N'[CUSTOM_REDACT]'");
    }

    #endregion
}
