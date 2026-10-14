namespace Autheris.Tests.Unit;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

/// <summary>
/// The configuration binder appends to pre-filled lists, so a non-empty default for CrossSource.AllowedTransports
/// could never be narrowed by an operator. The default is applied only when nothing is configured.
/// </summary>
public class CrossSourceAllowedTransportsBindingTests
{
    private static CrossSourceOptions Bind(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return config.Get<CrossSourceOptions>() ?? new CrossSourceOptions();
    }

    [Fact]
    public void Bind_SingleConfiguredTransport_IsNotMergedWithDefaults()
    {
        var options = Bind(new() { ["AllowedTransports:0"] = "Rest" });

        options.AllowedTransports.ShouldNotBeNull();
        options.AllowedTransports!.ToArray().ShouldBe(["Rest"]);
        options.GetEffectiveAllowedTransports().ToArray().ShouldBe(["Rest"]);
    }

    [Fact]
    public void Bind_NothingConfigured_UsesWebSqlAndTrinoDefault()
    {
        var options = Bind(new() { ["Enabled"] = "true" });

        options.GetEffectiveAllowedTransports().ToArray().ShouldBe(["WebSql", "Trino"]);
    }

    [Fact]
    public void ExplicitEmptyList_MeansNoTransportAllowed()
    {
        var options = new CrossSourceOptions { AllowedTransports = [] };

        options.GetEffectiveAllowedTransports().ShouldBeEmpty();
    }
}
