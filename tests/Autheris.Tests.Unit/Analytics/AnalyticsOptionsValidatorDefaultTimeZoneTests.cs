using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Analytics;

public sealed class AnalyticsOptionsValidatorDefaultTimeZoneTests
{
    internal const string ExpectedMessage = "Analytics:Aggregation:DefaultTimeZone must be a recognized IANA time zone id.";

    private static ValidateOptionsResult Validate(string? zone)
    {
        var options = new AnalyticsOptions();
        options.Aggregation.DefaultTimeZone = zone!;
        return new AnalyticsOptionsValidator().Validate(null, options);
    }

    [Fact]
    public void Validate_DefaultConfig_Succeeds()
    {
        new AnalyticsOptions().Aggregation.DefaultTimeZone.ShouldBe("UTC");
        new AnalyticsOptionsValidator().Validate(null, new AnalyticsOptions()).Failed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Europe/Berlin")]
    [InlineData("America/New_York")]
    public void Validate_RegistryZone_Succeeds(string zone) => Validate(zone).Failed.ShouldBeFalse();

    [Fact]
    public void Validate_RegistryAlias_Succeeds() => Validate("Etc/UTC").Failed.ShouldBeFalse();

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("europe/berlin")]
    [InlineData("Europe/Berlin';--")]
    public void Validate_UnknownOrMiscasedZone_FailsWithKeyOnlyMessage(string zone)
    {
        var result = Validate(zone);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldBe(ExpectedMessage);
        result.FailureMessage.ShouldNotContain(zone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankZone_Fails(string? zone)
    {
        var result = Validate(zone);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldBe(ExpectedMessage);
    }

    [Fact]
    public async Task StartupValidation_UnknownZone_StopsStart()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Analytics:Aggregation:DefaultTimeZone"] = "Mars/Olympus" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddGatewayAnalyticsServices();
        using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<AggregateException>(() => provider.GetRequiredService<IAsyncStartupValidator>().ValidateAsync(CancellationToken.None));

        var failures = ex.InnerExceptions.OfType<OptionsValidationException>().Where(e => e.OptionsType == typeof(AnalyticsOptions)).ToList();
        failures.Count.ShouldBe(1);
        failures[0].Message.ShouldContain("Analytics:Aggregation:DefaultTimeZone");
        failures[0].Message.ShouldNotContain("Mars/Olympus");
    }
}
