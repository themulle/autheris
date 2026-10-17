namespace Autheris.Tests.Unit.Catalog;

using System.Linq;
using Autheris.Api.Configuration.Validation;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>T-W0-12 (validation), T-W0-14 and T-W0-11 (start-up diagnostics) for <c>Gateway:Tenancy:Mode</c> and the deprecated discovery flag.</summary>
[Trait("Category", "Security")]
public sealed class TenancyStartupTests
{
    private static ILogger Logger() => Substitute.For<ILogger>();

    private static (LogLevel Level, string Text)[] Logged(ILogger logger) =>
        logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Log")
            .Select(c => ((LogLevel)c.GetArguments()[0]!, c.GetArguments()[2]!.ToString()!))
            .ToArray();

    [Fact]
    public void DefaultedMode_LogsAWarningNamingTheOption()
    {
        var logger = Logger();

        GatewayStartupValidator.ReportTenancyAndCatalogSettings(new GatewayOptions(), logger);

        var entry = Logged(logger).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Text.ShouldContain("Gateway:Tenancy:Mode");
        entry.Text.ShouldContain("Single");
    }

    [Theory]
    [InlineData("Single")]
    [InlineData("Multi")]
    public void ExplicitMode_LogsInformationOnly(string mode)
    {
        var logger = Logger();

        GatewayStartupValidator.ReportTenancyAndCatalogSettings(new GatewayOptions { Tenancy = new TenancyOptions { Mode = mode } }, logger);

        var entry = Logged(logger).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Text.ShouldContain(mode);
    }

    [Fact]
    public void DeprecatedDiscoveryFlag_LogsOneWarning()
    {
        var logger = Logger();
        var options = new GatewayOptions
        {
            Tenancy = new TenancyOptions { Mode = "Multi" },
            Catalog = new DataCatalogOptions { AllowAuthenticatedCatalogDiscovery = true }
        };

        GatewayStartupValidator.ReportTenancyAndCatalogSettings(options, logger);

        Logged(logger).Count(e => e.Level == LogLevel.Warning && e.Text.Contains("AllowAuthenticatedCatalogDiscovery")).ShouldBe(1);
    }

    [Theory]
    [InlineData("Both", false)]
    [InlineData("1", false)]
    [InlineData("Single", true)]
    [InlineData("Multi", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void Validator_RejectsUnknownMode_AcceptsNamesAndEmpty(string? mode, bool valid)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        var validator = new GatewayCoreOptionsValidator(environment);

        var result = validator.Validate(null, new GatewayOptions { Tenancy = new TenancyOptions { Mode = mode } });

        (result.Failed && result.Failures!.Any(f => f.Contains("Gateway:Tenancy:Mode"))).ShouldBe(!valid);
    }

    [Fact]
    public void ConfigurationBinding_ReadsGatewayTenancyMode()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> { ["Gateway:Tenancy:Mode"] = "Single" })
            .Build();
        var options = new GatewayOptions();
        configuration.GetSection("Gateway").Bind(options);

        options.Tenancy.EffectiveMode.ShouldBe(TenancyMode.Single);
        options.Tenancy.IsExplicit.ShouldBeTrue();
    }
}
