using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine.Parsing;
using Xunit;

namespace Autheris.Tests.Unit.Sql;

/// <summary>ARCH-MNT-15-SC-04 / T-08: invalid cache-generation options stop the start; there is no "0 means unlimited".</summary>
public sealed class SqlParserCacheOptionsTests
{
    public static TheoryData<int, int, int, int> Invalid => new()
    {
        { 0, 32, 2_000, 4 },
        { -1, 32, 2_000, 4 },
        { AntlrCacheOptions.MaxAllowedStatesPerGeneration + 1, 32, 2_000, 4 },
        { 50_000, 0, 2_000, 4 },
        { 50_000, -5, 2_000, 4 },
        { 50_000, AntlrCacheOptions.MaxAllowedCount + 1, 2_000, 4 },
        { 50_000, 32, 0, 4 },
        { 50_000, 32, -1, 4 },
        { 50_000, 32, AntlrCacheOptions.MaxAllowedCount + 1, 4 },
        { 50_000, 32, 2_000, 1 },
        { 50_000, 32, 2_000, 0 },
        { 50_000, 32, 2_000, AntlrCacheOptions.MaxHardCapFactor + 1 },
        { int.MinValue, int.MinValue, int.MinValue, int.MinValue }
    };

    private static GatewayOptions Gateway(int cap, int interval, int minParses, int factor = 4) => new()
    {
        SqlParserCache = new SqlParserCacheOptions
        {
            MaxStatesPerGeneration = cap,
            GenerationCheckInterval = interval,
            MinParsesPerGeneration = minParses,
            HardCapFactor = factor
        }
    };

    [Theory]
    [Trait("Category", "Security")]
    [MemberData(nameof(Invalid))]
    public void Options_InvalidCapIntervalOrMinParses_FailStartup(int cap, int interval, int minParses, int factor)
    {
        // ARCH-MNT-15-SC-04, ARCH-MNT-15-T-08
        var result = new SqlParserCacheOptionsValidator().Validate(null, Gateway(cap, interval, minParses, factor));
        result.Failed.ShouldBeTrue();

        Should.Throw<ArgumentException>(() => new AntlrCacheGenerations(new AntlrCacheOptions
        {
            MaxStatesPerGeneration = cap,
            GenerationCheckInterval = interval,
            MinParsesPerGeneration = minParses,
            HardCapFactor = factor
        }));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Options_BoundariesAreInclusive_OneToOneMillion_FactorTwoToSixteen()
    {
        var validator = new SqlParserCacheOptionsValidator();
        validator.Validate(null, Gateway(1, 1, 1, 2)).Succeeded.ShouldBeTrue();
        validator.Validate(null, Gateway(1_000_000, 1_000_000, 1_000_000, 16)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Options_Defaults_AreValid_AndTheValidatorMessagesNameOnlyPaths()
    {
        new SqlParserCacheOptionsValidator().Validate(null, new GatewayOptions()).Succeeded.ShouldBeTrue();
        new AntlrCacheOptions().Validate().ShouldBeEmpty();

        var failed = new SqlParserCacheOptionsValidator().Validate(null, Gateway(0, 32, 2_000));
        failed.FailureMessage!.ShouldContain("Gateway:SqlParserCache:MaxStatesPerGeneration");
    }
}
