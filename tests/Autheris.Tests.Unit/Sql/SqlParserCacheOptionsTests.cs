using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine.Parsing;
using Xunit;

namespace Autheris.Tests.Unit.Sql;

/// <summary>ARCH-MNT-15-SC-04 / T-08: invalid cache-generation options stop the start; there is no "0 means unlimited".</summary>
public sealed class SqlParserCacheOptionsTests
{
    public static TheoryData<int, int, int> Invalid => new()
    {
        { 0, 32, 2_000 },
        { -1, 32, 2_000 },
        { AntlrCacheOptions.MaxAllowedStatesPerGeneration + 1, 32, 2_000 },
        { 50_000, 0, 2_000 },
        { 50_000, -5, 2_000 },
        { 50_000, 32, 0 },
        { 50_000, 32, -1 },
        { int.MinValue, int.MinValue, int.MinValue }
    };

    private static GatewayOptions Gateway(int cap, int interval, int minParses) => new()
    {
        SqlParserCache = new SqlParserCacheOptions
        {
            MaxStatesPerGeneration = cap,
            GenerationCheckInterval = interval,
            MinParsesPerGeneration = minParses
        }
    };

    [Theory]
    [Trait("Category", "Security")]
    [MemberData(nameof(Invalid))]
    public void Options_InvalidCapIntervalOrMinParses_FailStartup(int cap, int interval, int minParses)
    {
        // ARCH-MNT-15-SC-04, ARCH-MNT-15-T-08
        var result = new SqlParserCacheOptionsValidator().Validate(null, Gateway(cap, interval, minParses));
        result.Failed.ShouldBeTrue();

        Should.Throw<ArgumentException>(() => new AntlrCacheGenerations(new AntlrCacheOptions
        {
            MaxStatesPerGeneration = cap,
            GenerationCheckInterval = interval,
            MinParsesPerGeneration = minParses
        }));
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
