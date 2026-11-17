namespace Autheris.Tests.Unit.Catalog;

using System.Linq;
using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #678: <c>Gateway:Catalog:Cache</c> limits fail start-up validation, naming the key and never echoing the value.</summary>
public sealed class CatalogCacheOptionsValidatorTests
{
    private static string[] Failures(CatalogCacheOptions cache)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        var result = new GatewayCoreOptionsValidator(environment).Validate(null, new GatewayOptions { Catalog = new DataCatalogOptions { Cache = cache } });
        return result.Failures?.Where(f => f.Contains("Gateway:Catalog:Cache")).ToArray() ?? [];
    }

    [Fact]
    public void RejectsZeroOrNegativeLimits()
    {
        Failures(new CatalogCacheOptions { MetadataMaxEntries = 0 }).ShouldContain(f => f.Contains("MetadataMaxEntries"));
        Failures(new CatalogCacheOptions { MetadataTtlSeconds = 0 }).ShouldContain(f => f.Contains("MetadataTtlSeconds"));
        Failures(new CatalogCacheOptions { ProviderMaxEntries = -1 }).ShouldContain(f => f.Contains("ProviderMaxEntries"));
        Failures(new CatalogCacheOptions { MetadataMaxEntries = 999 }).ShouldNotBeEmpty();
        Failures(new CatalogCacheOptions { ProviderMaxEntries = 99 }).ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RejectsUpperBoundViolations()
    {
        var failures = Failures(new CatalogCacheOptions { MetadataMaxEntries = 1_234_567, MetadataTtlSeconds = 301, ProviderMaxEntries = 7_654_321 });

        failures.Length.ShouldBe(3);
        failures.ShouldAllBe(f => !f.Contains("1234567") && !f.Contains("7654321") && !f.Contains("301 "));
        Failures(new CatalogCacheOptions { MetadataMaxEntries = 1_000_000, MetadataTtlSeconds = 300, ProviderMaxEntries = 1_000_000 }).ShouldBeEmpty();
    }

    [Fact]
    public void DefaultsUnchanged()
    {
        var defaults = new CatalogCacheOptions();

        defaults.MetadataMaxEntries.ShouldBe(20_000);
        defaults.MetadataTtlSeconds.ShouldBe(30);
        defaults.ProviderMaxEntries.ShouldBe(10_000);
        Failures(defaults).ShouldBeEmpty();
    }
}
