namespace Autheris.Tests.Unit.Governance;

using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public class CasbinDecisionCacheOptionsValidatorTests
{
    private static bool Valid(CasbinDecisionCacheOptions o) =>
        new CasbinDecisionCacheOptionsValidator().Validate(null, o).Succeeded;

    [Fact]
    public void Defaults_AreValid() => Valid(new CasbinDecisionCacheOptions()).ShouldBeTrue();

    [Theory]
    [InlineData(99)]
    [InlineData(1_000_001)]
    public void Capacity_OutOfRange_IsInvalid(int capacity) =>
        Valid(new CasbinDecisionCacheOptions { Capacity = capacity }).ShouldBeFalse();

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Ttl_OutOfRange_IsInvalid(int ttl) =>
        Valid(new CasbinDecisionCacheOptions { TtlSeconds = ttl }).ShouldBeFalse();

    [Fact]
    public void EpochCheck_AboveTtl_IsInvalid() =>
        Valid(new CasbinDecisionCacheOptions { TtlSeconds = 10, EpochCheckSeconds = 11 }).ShouldBeFalse();

    [Fact]
    public void EpochCheck_Zero_IsInvalid() =>
        Valid(new CasbinDecisionCacheOptions { EpochCheckSeconds = 0 }).ShouldBeFalse();

    [Theory]
    [InlineData(99)]
    [InlineData(10_001)]
    public void Backoff_OutOfRange_IsInvalid(int ms) =>
        Valid(new CasbinDecisionCacheOptions { UnavailableBackoffMilliseconds = ms }).ShouldBeFalse();
}
