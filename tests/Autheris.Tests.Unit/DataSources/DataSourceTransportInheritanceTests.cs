namespace Autheris.Tests.Unit.DataSources;

using System;
using System.Collections.Generic;
using Autheris.Api.Configuration.Validation;
using Autheris.Application.DataSources;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

/// <summary>Issue #447 decision 13 (SEC-ATR-01): AllowedTransports inherits Defaults; an explicit None stays None.</summary>
[Trait("Category", "Security")]
public sealed class DataSourceTransportInheritanceTests
{
    private static HierarchicalDataSourceOptionsResolver Build(DataSourceProfileOptions? defaults, params (string Name, DataSourceProfileOptions Profile)[] profiles)
    {
        var options = new DataSourceConfigurationOptions { Defaults = defaults ?? new DataSourceProfileOptions() };
        foreach (var (name, profile) in profiles)
        {
            options.Profiles[name] = profile;
        }

        return new HierarchicalDataSourceOptionsResolver(new DataSourceTransportGateTests.FixedMonitor<DataSourceConfigurationOptions>(options));
    }

    [Fact]
    public void ProfileWithoutAllowedTransports_InheritsDefaults()
    {
        using var resolver = Build(
            new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.GraphQL },
            ("src", new DataSourceProfileOptions { MaxResponseRows = 5 }));

        resolver.Resolve("src").AllowedTransports.ShouldBe(DataSourceTransportFlags.GraphQL);
    }

    [Fact]
    public void ProfileWithExplicitNone_StaysNone_NotDefaults()
    {
        using var resolver = Build(
            new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.GraphQL },
            ("src", new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.None }));

        resolver.Resolve("src").AllowedTransports.ShouldBe(DataSourceTransportFlags.None);
    }

    [Fact]
    public void NoProfile_UsesDefaults()
    {
        using var resolver = Build(new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.Rest });
        resolver.Resolve("anything").AllowedTransports.ShouldBe(DataSourceTransportFlags.Rest);
    }

    [Fact]
    public void DefaultsUnset_EffectiveAll()
    {
        using var resolver = Build(null, ("src", new DataSourceProfileOptions { MaxResponseRows = 5 }));
        resolver.Resolve("src").AllowedTransports.ShouldBe(DataSourceTransportFlags.All);
        resolver.Resolve("other").AllowedTransports.ShouldBe(DataSourceTransportFlags.All);
    }

    [Fact]
    public void ProfileExplicitValue_OverridesDefaults()
    {
        using var resolver = Build(
            new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.GraphQL },
            ("src", new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.Mcp }));

        resolver.Resolve("src").AllowedTransports.ShouldBe(DataSourceTransportFlags.Mcp);
    }

    [Fact]
    public void ExplicitNoneInDefaults_InheritedAsNone()
    {
        using var resolver = Build(
            new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.None },
            ("src", new DataSourceProfileOptions { MaxResponseRows = 5 }));

        resolver.Resolve("src").AllowedTransports.ShouldBe(DataSourceTransportFlags.None);
    }

    [Theory]
    [InlineData("Defaults:AllowedTransports", "")]
    [InlineData("Defaults:AllowedTransports", "   ")]
    [InlineData("Defaults:AllowedTransports", "GraphQl,Nope")]
    [InlineData("Profiles:src:AllowedTransports", "")]
    [InlineData("Profiles:src:AllowedTransports", "NotATransport")]
    [InlineData("Profiles:src:AllowedTransports", "999999")]
    public void InvalidOrEmptyValue_FailsValidationAtStartup(string key, string value)
    {
        var section = Section((key, value));
        var result = new DataSourceTransportRawValueValidator(section).Validate(null, new DataSourceConfigurationOptions());
        result.Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("None")]
    [InlineData("GraphQL")]
    [InlineData("WebSql, Rest")]
    [InlineData("All")]
    [InlineData("3")]
    public void ValidValue_PassesStartupValidation(string value)
    {
        var section = Section(("Defaults:AllowedTransports", value), ("Profiles:src:AllowedTransports", value));
        new DataSourceTransportRawValueValidator(section).Validate(null, new DataSourceConfigurationOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void UndefinedBitsInBoundOptions_FailTheOptionsValidator()
    {
        var options = new DataSourceConfigurationOptions
        {
            Defaults = new DataSourceProfileOptions { AllowedTransports = (DataSourceTransportFlags)(1 << 20) }
        };
        new DataSourceOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    private static IConfigurationSection Section(params (string Key, string Value)[] values)
    {
        var data = new Dictionary<string, string?>();
        foreach (var (key, value) in values)
        {
            data["Gateway:DataSources:Configuration:" + key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build().GetSection("Gateway:DataSources:Configuration");
    }
}
