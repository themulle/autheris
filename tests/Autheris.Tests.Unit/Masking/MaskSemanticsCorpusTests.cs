using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Unit.Masking;

/// <summary>
/// WP-ARC-B-04 / ARC-C-12: one mask semantic. The same golden corpus runs against the SQL engine (typed <c>MaskSpec</c> rendered for
/// SQLite and executed) and the in-memory engine (<c>ColumnMaskingProvider</c>).
/// </summary>
[Trait("Category", "Security")]
public sealed class MaskSemanticsCorpusTests
{
    public static IEnumerable<object[]> Ids() => MaskSemanticsCorpus.Cases.Select(c => new object[] { c.Id });

    public static IEnumerable<object[]> NullRuleIndexes() => Enumerable.Range(0, MaskSemanticsCorpus.NullRules.Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Ids))]
    public void SqlEngine_ProducesTheCorpusOutput(string id)
    {
        var c = MaskSemanticsCorpus.Get(id);

        Normalize(RunSql(c.Rule, c.Clear, c.DataType)).ShouldBe(Normalize(c.Expected));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void InMemoryEngine_ProducesTheCorpusOutput(string id)
    {
        var c = MaskSemanticsCorpus.Get(id);

        Normalize(new ColumnMaskingProvider().MaskValue("v", c.Clear, c.Rule)).ShouldBe(Normalize(c.Expected));
    }

    [Theory]
    [MemberData(nameof(NullRuleIndexes))]
    public void NullInput_StaysNull_InBothEngines(int index)
    {
        var rule = MaskSemanticsCorpus.NullRules[index];

        new ColumnMaskingProvider().MaskValue("v", null, rule).ShouldBeNull();
        // Constant masks (REDACT, MASK_EMAIL, MASK_IBAN) may emit their constant for a NULL in SQL; either way nothing is revealed.
        var sql = RunSql(rule, null, rule.RuleType == "GEO_JITTER" ? "real" : "text")?.ToString();
        (sql is null || sql.All(c => c is '*' or '@')).ShouldBeTrue($"SQL mask of NULL revealed '{sql}'");
    }

    [Fact]
    public void Hmac_IsKeyedPerEngine_ButDeterministicAndNullPreserving()
    {
        // The in-memory key and the in-database key are different by design (tenant-scoped derivation), so only the shape is shared.
        var provider = new ColumnMaskingProvider();
        var rule = new MaskingRule { RuleType = "HMAC" };

        var first = provider.MaskValue("v", "abc", rule).ShouldBeOfType<string>();
        first.Length.ShouldBe(64);
        provider.MaskValue("v", "abc", rule).ShouldBe(first);
        provider.MaskValue("v", "abd", rule).ShouldNotBe(first);
        provider.MaskValue("v", null, rule).ShouldBeNull();
    }

    [Fact]
    public void InMemoryPartialMask_UsesTheSpecOfTheSqlPath()
    {
        var spec = SqlDataMaskingProvider.BuildUnkeyedMaskSpec(MaskSemanticsCorpus.Get("partial.text").Rule, "v", DatabaseDialect.PostgreSql);

        MaskSpecEvaluator.Evaluate(spec, "abcdef", "abcdef").ShouldBe("a****f");
    }

    [Fact]
    public void SpecEvaluator_RejectsKeyedKinds_FailClosed()
    {
        var spec = SqlDataMaskingProvider.BuildUnkeyedMaskSpec(MaskSemanticsCorpus.Get("geo.round").Rule, "v", DatabaseDialect.PostgreSql);

        Should.Throw<NotSupportedException>(() => MaskSpecEvaluator.Evaluate(spec, 1.0d, "1"));
    }

    private static object? RunSql(MaskingRule rule, object? clear, string dataType)
    {
        // The spec is the one of a non-SQLite dialect (variable-length partial mask); only its rendering is SQLite.
        var spec = SqlDataMaskingProvider.BuildUnkeyedMaskSpec(rule, "v", DatabaseDialect.PostgreSql);
        var rendered = new MaskSpecRenderer().Render(spec, "v", dataType, TargetSqlDialect.Sqlite, name => DatabaseDialect.Sqlite.FormatParameterMarker(name));

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {rendered.Sql} AS m FROM (SELECT @clear_value AS \"v\") AS t";
        command.Parameters.AddWithValue("@clear_value", clear switch
        {
            null => DBNull.Value,
            DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
            _ => clear
        });
        foreach (var (name, value) in rendered.Parameters)
        {
            command.Parameters.AddWithValue(name.TrimStart('@', ':'), value ?? DBNull.Value);
        }

        var scalar = command.ExecuteScalar();
        return scalar is DBNull ? null : scalar;
    }

    private static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        double d => Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture),
        float f => Math.Round(f, 6).ToString("R", CultureInfo.InvariantCulture),
        decimal m => Math.Round((double)m, 6).ToString("R", CultureInfo.InvariantCulture),
        _ => value
    };
}
