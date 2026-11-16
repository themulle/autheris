using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Masking;

/// <summary>
/// WP-ARC-B-04 / ARC-C-12: in-memory mask semantics against the golden corpus (<c>ColumnMaskingProvider</c> and <c>MaskSpecEvaluator</c>).
/// SQL execution of the corpus runs in PostgreSQL integration tests (<c>MaskSemanticsCorpusSqlTests</c>).
/// </summary>
[Trait("Category", "Security")]
public sealed class MaskSemanticsCorpusTests
{
    public static IEnumerable<object[]> Ids() => MaskSemanticsCorpus.Cases.Select(c => new object[] { c.Id });

    public static IEnumerable<object[]> NullRuleIndexes() => Enumerable.Range(0, MaskSemanticsCorpus.NullRules.Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Ids))]
    public void InMemoryEngine_ProducesTheCorpusOutput(string id)
    {
        var c = MaskSemanticsCorpus.Get(id);

        Normalize(new ColumnMaskingProvider().MaskValue("v", c.Clear, c.Rule)).ShouldBe(Normalize(c.Expected));
    }

    [Theory]
    [MemberData(nameof(NullRuleIndexes))]
    public void NullInput_StaysNull_InInMemoryEngine(int index)
    {
        var rule = MaskSemanticsCorpus.NullRules[index];

        new ColumnMaskingProvider().MaskValue("v", null, rule).ShouldBeNull();
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

    private static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        double d => Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture),
        float f => Math.Round(f, 6).ToString("R", CultureInfo.InvariantCulture),
        decimal m => Math.Round((double)m, 6).ToString("R", CultureInfo.InvariantCulture),
        _ => value
    };
}
