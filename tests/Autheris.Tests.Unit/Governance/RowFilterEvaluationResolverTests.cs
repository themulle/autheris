using Autheris.Application.Policy;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #107 (PLAN-GOV-107 A-7, C-3, AC-1): resolution rule > object > global with clamping, and save-time validation.</summary>
public sealed class RowFilterEvaluationResolverTests
{
    private static readonly RowFilterKeySetCacheOptions Global = new()
    {
        DefaultStrategy = RowFilterEvaluationStrategy.Exists,
        DefaultDuration = TimeSpan.FromMinutes(5),
        MaxDuration = TimeSpan.FromHours(1),
        MaxKeys = 2000
    };

    private static RowFilterEvaluationSettings S(RowFilterEvaluationStrategy? st = null, int? d = null, int? k = null) => new(st, d, k);

    public static TheoryData<bool, bool> Levels => new() { { false, false }, { false, true }, { true, false }, { true, true } };

    [Fact]
    public void Nothing_Set_UsesGlobalDefaults()
    {
        var e = RowFilterEvaluationResolver.Resolve(null, null, Global);

        e.Strategy.ShouldBe(RowFilterEvaluationStrategy.Exists);
        e.StrategySource.ShouldBe(RowFilterEvaluationSource.Global);
        e.Duration.ShouldBe(TimeSpan.FromMinutes(5));
        e.MaxKeys.ShouldBe(2000);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void EachSetting_ResolvesIndependently_RuleOverObjectOverGlobal(bool ruleSets, bool objSets)
    {
        // Strategy, duration and maxKeys are resolved independently of each other (AC-1 matrix: 4 level combinations x 3 settings).
        var rule = ruleSets ? S(RowFilterEvaluationStrategy.KeySetCache, 60, 100) : null;
        var obj = objSets ? S(RowFilterEvaluationStrategy.KeySetCache, 120, 200) : null;

        var e = RowFilterEvaluationResolver.Resolve(rule, obj, Global);

        e.StrategySource.ShouldBe(ruleSets ? RowFilterEvaluationSource.Rule : objSets ? RowFilterEvaluationSource.Object : RowFilterEvaluationSource.Global);
        e.Strategy.ShouldBe(ruleSets || objSets ? RowFilterEvaluationStrategy.KeySetCache : RowFilterEvaluationStrategy.Exists);
        e.Duration.ShouldBe(ruleSets ? TimeSpan.FromSeconds(60) : objSets ? TimeSpan.FromSeconds(120) : TimeSpan.FromMinutes(5));
        e.MaxKeys.ShouldBe(ruleSets ? 100 : objSets ? 200 : 2000);
    }

    [Fact]
    public void PartialRuleSettings_FallThroughPerSetting()
    {
        var e = RowFilterEvaluationResolver.Resolve(
            S(d: 30), S(RowFilterEvaluationStrategy.KeySetCache, 600, 50), Global);

        e.Strategy.ShouldBe(RowFilterEvaluationStrategy.KeySetCache);
        e.StrategySource.ShouldBe(RowFilterEvaluationSource.Object);
        e.Duration.ShouldBe(TimeSpan.FromSeconds(30));
        e.MaxKeys.ShouldBe(50);
    }

    [Fact]
    public void RuleExists_OverridesObjectKeySetCache()
    {
        var e = RowFilterEvaluationResolver.Resolve(
            S(RowFilterEvaluationStrategy.Exists), S(RowFilterEvaluationStrategy.KeySetCache), Global);

        e.Strategy.ShouldBe(RowFilterEvaluationStrategy.Exists);
        e.StrategySource.ShouldBe(RowFilterEvaluationSource.Rule);
    }

    [Fact]
    public void ValuesAboveCaps_AreClamped()
    {
        var e = RowFilterEvaluationResolver.Resolve(S(RowFilterEvaluationStrategy.KeySetCache, 999_999, 999_999), null, Global);

        e.Duration.ShouldBe(Global.MaxDuration);
        e.MaxKeys.ShouldBe(Global.MaxKeys);
    }

    [Fact]
    public void LoweredCaps_ClampTheGlobalDefaultsToo()
    {
        var lowered = new RowFilterKeySetCacheOptions { DefaultDuration = TimeSpan.FromMinutes(5), MaxDuration = TimeSpan.FromSeconds(30), MaxKeys = 10 };

        var e = RowFilterEvaluationResolver.Resolve(null, null, lowered);

        e.Duration.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void InvalidStoredNumbers_AreReadAsNotSet_AndStrategyAsExists(int bad)
    {
        // D-11 / AC-14: a directly loaded invalid value is safe (Exists) and exact.
        var e = RowFilterEvaluationResolver.Resolve(
            S(RowFilterEvaluationStrategy.KeySetCache, bad, bad), S(d: 90, k: 70), Global);

        e.Strategy.ShouldBe(RowFilterEvaluationStrategy.Exists);
        e.StrategySource.ShouldBe(RowFilterEvaluationSource.Rule);
        e.Duration.ShouldBe(TimeSpan.FromSeconds(90));
        e.MaxKeys.ShouldBe(70);
    }

    [Fact]
    public void UndefinedStoredEnum_IsReadAsExists()
    {
        var e = RowFilterEvaluationResolver.Resolve(null, S((RowFilterEvaluationStrategy)7), Global);

        e.Strategy.ShouldBe(RowFilterEvaluationStrategy.Exists);
        e.StrategySource.ShouldBe(RowFilterEvaluationSource.Object);
    }

    [Fact]
    public void HasInvalidStoredValue_ReportsInvalidLevels()
    {
        RowFilterEvaluationResolver.HasInvalidStoredValue(null).ShouldBeFalse();
        RowFilterEvaluationResolver.HasInvalidStoredValue(RowFilterEvaluationSettings.Inherit).ShouldBeFalse();
        RowFilterEvaluationResolver.HasInvalidStoredValue(S(RowFilterEvaluationStrategy.KeySetCache, 5, 5)).ShouldBeFalse();
        RowFilterEvaluationResolver.HasInvalidStoredValue(S(d: 0)).ShouldBeTrue();
        RowFilterEvaluationResolver.HasInvalidStoredValue(S(k: -3)).ShouldBeTrue();
        RowFilterEvaluationResolver.HasInvalidStoredValue(S((RowFilterEvaluationStrategy)42)).ShouldBeTrue();
    }

    // ---- Validate ----

    [Fact]
    public void Validate_ObjectLevel_ValidSettings_ReturnsNull()
        => RowFilterEvaluationResolver.Validate(S(RowFilterEvaluationStrategy.KeySetCache, 60, 10), Global).ShouldBeNull();

    [Fact]
    public void Validate_Inherit_IsAlwaysValid()
    {
        RowFilterEvaluationResolver.Validate(RowFilterEvaluationSettings.Inherit, Global).ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_NonPositiveDuration_Rejected(int seconds)
        => RowFilterEvaluationResolver.Validate(S(d: seconds), Global).ShouldNotBeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_NonPositiveMaxKeys_Rejected(int keys)
        => RowFilterEvaluationResolver.Validate(S(k: keys), Global).ShouldNotBeNull();

    [Fact]
    public void Validate_DurationAboveCap_Rejected_AndAtCapAccepted()
    {
        RowFilterEvaluationResolver.Validate(S(d: 3601), Global).ShouldNotBeNull();
        RowFilterEvaluationResolver.Validate(S(d: 3600), Global).ShouldBeNull();
    }

    [Fact]
    public void Validate_MaxKeysAboveCap_Rejected_AndAtCapAccepted()
    {
        RowFilterEvaluationResolver.Validate(S(k: 2001), Global).ShouldNotBeNull();
        RowFilterEvaluationResolver.Validate(S(k: 2000), Global).ShouldBeNull();
    }

    [Fact]
    public void Validate_UndefinedEnum_Rejected()
        => RowFilterEvaluationResolver.Validate(S((RowFilterEvaluationStrategy)99), Global).ShouldNotBeNull();
}
