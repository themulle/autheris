namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using Autheris.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class BorderlineRemovedKeysTests
{
    private static IConfiguration Config(string key, string? value = "true") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    [Theory]
    [InlineData("Analytics:Privacy:Noise:Enabled", "false")]
    [InlineData("Gateway:Caching:Cdn:Enabled", "false")]
    [InlineData("Gateway:Caching:Cdn:Cloudflare:ApiToken", "x")]
    [InlineData("Gateway:SchemaRegistry", "true")]
    [InlineData("Gateway:SchemaRegistry:Enabled", "false")]
    public void Removed_key_fails_startup(string key, string value)
    {
        var configuration = Config(key, value);
        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration));

        ex.Message.ShouldContain("Unknown configuration keys under 'Gateway'");
        if (key.StartsWith("Analytics:Privacy:Noise", StringComparison.OrdinalIgnoreCase))
        {
            ex.Message.ShouldContain("Analytics:Privacy:Noise");
            ex.Message.ShouldContain("removed (OD-11); small-group suppression applies");
        }
        else if (key.StartsWith("Gateway:Caching:Cdn", StringComparison.OrdinalIgnoreCase))
        {
            ex.Message.ShouldContain("Gateway:Caching:Cdn");
        }
        else if (key.StartsWith("Gateway:SchemaRegistry", StringComparison.OrdinalIgnoreCase))
        {
            ex.Message.ShouldContain("Gateway:SchemaRegistry");
        }
    }
}
