using System;
using System.Collections.Generic;
using System.Security;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using System.Collections.Frozen;
using Autheris.Tests.Unit.Sql;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
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

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("51.5074, -0.1278")]
    public void SR_M_20_GeoJitter_NonNumericInput_FailsClosed(string raw)
    {
        var rule = new MaskingRule { RuleType = "GEO_JITTER", Mode = "round", Decimals = 2 };

        _provider.MaskValue("lat", raw, rule).ShouldBeNull();
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

    #region Fail-Closed Predicate Guard (typed compiler)

    private static void Compile(string sql, string maskedCol, TargetSqlDialect dialect = TargetSqlDialect.PostgreSql)
    {
        string schema = dialect == TargetSqlDialect.SqlServer ? "dbo" : "public";
        var env = new TestCompileEnv(dialect, schema)
            .Table("employees", "tenant_id", "id:INTEGER", "name", "salary:INTEGER", "tenant_id")
            .Table("users", "tenant_id", "id:INTEGER", "name", "ssn", "tenant_id")
            .Table("sales", "tenant_id", "id:INTEGER", "department", "bonus:INTEGER", "tenant_id")
            .Table("a", "tenant_id", "id:INTEGER", "ssn", "tenant_id")
            .Table("b", "tenant_id", "info", "ssn", "tenant_id");
        var nullify = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        foreach (var table in new[] { "employees", "users", "sales", "a", "b" })
        {
            env.Mask(table, maskedCol, nullify);
        }

        env.Compile(sql);
    }

    [Fact]
    public void PredicateGuard_MaskedColumnInWhere_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() =>
            Compile("SELECT id, name FROM employees WHERE salary > 50000", "salary"));
    }

    [Fact]
    public void PredicateGuard_MaskedColumnInOrderBy_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() =>
            Compile("SELECT id, name FROM users ORDER BY ssn", "ssn"));
    }

    [Fact]
    public void PredicateGuard_MaskedColumnInHaving_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() =>
            Compile("SELECT department, sum(bonus) FROM sales GROUP BY department HAVING bonus > 1000", "bonus", TargetSqlDialect.SqlServer));
    }

    // WP-FEAT-02: the secured derived table feeds mask output into an aggregate, which is meaningless or an inference channel.
    // The compiler rejects every aggregate/grouping shape over a masked column with AGGREGATION_ON_MASKED_COLUMN.
    [Theory]
    [InlineData("SELECT sum(bonus) FROM sales")]
    [InlineData("SELECT avg(bonus) FROM sales GROUP BY department")]
    [InlineData("SELECT min(bonus), max(bonus) FROM sales")]
    [InlineData("SELECT count(bonus) FROM sales")]
    [InlineData("SELECT count(DISTINCT bonus) FROM sales")]
    [InlineData("SELECT sum(bonus * 2) FROM sales")]
    [InlineData("SELECT department, count(*) FROM sales GROUP BY bonus, department")]
    [InlineData("SELECT count(*) FROM sales GROUP BY bonus")]
    [InlineData("SELECT count(*) FILTER (WHERE bonus > 5) FROM sales")]
    [InlineData("SELECT department, string_agg(department, ',' ORDER BY bonus) FROM sales GROUP BY department")]
    [InlineData("SELECT department, sum(s.bonus) FROM sales s GROUP BY department")]
    public void AggregateGuard_MaskedColumnInAggregateOrGroupBy_Throws(string sql)
    {
        foreach (var dialect in new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql })
        {
            Should.Throw<MaskedAggregationException>(() => Compile(sql, "bonus", dialect));
        }
    }

    [Theory]
    [InlineData("SELECT department, sum(id) FROM sales GROUP BY department")]
    [InlineData("SELECT count(*) FROM sales")]
    [InlineData("SELECT department, max(id) FROM sales GROUP BY department")]
    public void AggregateGuard_AggregateOverClearColumn_Allowed(string sql)
    {
        foreach (var dialect in new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql })
        {
            Should.NotThrow(() => Compile(sql, "bonus", dialect));
        }
    }

    [Fact]
    public void AggregateGuard_MapsToTypedErrorCode()
    {
        var ex = Should.Throw<MaskedAggregationException>(() => Compile("SELECT sum(bonus) FROM sales", "bonus"));

        Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.TryMap(ex, out var mapping).ShouldBeTrue();
        mapping.Status.ShouldBe(403);
        mapping.Code.ShouldBe("AGGREGATION_ON_MASKED_COLUMN");
        ex.Message.ShouldNotContain("bonus");
    }

    [Fact]
    public void PredicateGuard_MaskedColumnInJoinOn_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() =>
            Compile("SELECT a.id, b.info FROM a JOIN b ON a.ssn = b.ssn", "ssn"));
    }

    [Fact]
    public void PredicateGuard_UnmaskedColumnsInPredicates_SucceedsNormally()
    {
        Should.NotThrow(() =>
            Compile("SELECT id, name, salary FROM employees WHERE id = 123 ORDER BY name", "salary"));
    }

    #endregion

    // The BuildDialectMaskExpression tests (GEO_JITTER, PARTIAL_MASK, REDACT per dialect, L-9) tested the removed string builder. The typed
    // mask renderer is covered per dialect by TrinoSqlEngine.Tests/Compiler/MaskTests (Redact_NumericOrTemporal_IsTypedNull,
    // PartialMask_AllArgumentsAreBound, GeoJitter_RoundsWithAStructuralScale_Clamped), SqliteMaskTests, and here by
    // TypedMaskSpecTests.RenderedMask_* (executed on SQLite).
}
