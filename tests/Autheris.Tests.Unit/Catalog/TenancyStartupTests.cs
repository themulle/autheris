namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Common;
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

/// <summary>T-W0-12 (validation), T-W0-14 and T-W0-11 (start-up diagnostics) for <c>Gateway:Tenancy:DefaultTenantId</c> and the deprecated discovery flag.</summary>
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
    public void NoDefaultTenant_LogsAWarningNamingTheOption()
    {
        var logger = Logger();

        GatewayStartupValidator.ReportTenancyAndCatalogSettings(new GatewayOptions(), logger);

        var entry = Logged(logger).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Text.ShouldContain("Gateway:Tenancy:DefaultTenantId");
        entry.Text.ShouldNotContain("Gateway:Tenancy:Mode");
    }

    [Fact]
    public void ConfiguredDefaultTenant_LogsNothing()
    {
        var logger = Logger();

        GatewayStartupValidator.ReportTenancyAndCatalogSettings(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = "default-tenant" } }, logger);

        Logged(logger).ShouldBeEmpty();
    }

    [Fact]
    public void DeprecatedDiscoveryFlag_LogsOneWarning()
    {
        var logger = Logger();
        var options = new GatewayOptions
        {
            Tenancy = new TenancyOptions(),
            Catalog = new DataCatalogOptions { AllowAuthenticatedCatalogDiscovery = true }
        };

        GatewayStartupValidator.ReportTenancyAndCatalogSettings(options, logger);

        Logged(logger).Count(e => e.Level == LogLevel.Warning && e.Text.Contains("AllowAuthenticatedCatalogDiscovery")).ShouldBe(1);
    }

    [Fact]
    public void ConfigurationBinding_ReadsGatewayTenancyDefaultTenantId()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = "default-tenant" })
            .Build();
        var options = new GatewayOptions();
        configuration.GetSection("Gateway").Bind(options);

        options.Tenancy.DefaultTenantId.ShouldBe("default-tenant");
    }
}
