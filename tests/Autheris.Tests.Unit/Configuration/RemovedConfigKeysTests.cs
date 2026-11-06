namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using Autheris.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class RemovedConfigKeysTests
{
    private static IConfiguration Config(string key, string? value = "true") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    [Theory]
    [InlineData("Gateway:Caching:Garnet:EnableEmbeddedServer", "true")]
    [InlineData("Gateway:Caching:Garnet:EnableEmbeddedServer", "false")]
    [InlineData("Gateway:Caching:Garnet:Host", "127.0.0.1")]
    [InlineData("Gateway:Caching:Garnet:Port", "3278")]
    [InlineData("Gateway:Caching:Garnet:MemorySizeMb", "512")]
    [InlineData("Gateway:Caching:Garnet:EnableTls", "false")]
    [InlineData("Gateway:Caching:Garnet:TlsCertFile", "/certs/server.pfx")]
    [InlineData("Gateway:Caching:Garnet:TlsCertPasswordSecretRef", "kv://pwd")]
    [InlineData("Gateway:Caching:Garnet:ClientPasswordSecretRef", "kv://client")]
    [InlineData("Gateway:TrafficShadowing:Enabled", "true")]
    [InlineData("Gateway:TrafficShadowing:Enabled", "false")]
    [InlineData("Gateway:TrafficShadowing:TargetBaseUrl", "https://shadow.internal")]
    [InlineData("Gateway:TrafficShadowing:SampleRatePercentage", "5.0")]
    [InlineData("Gateway:TrafficShadowing:ChannelCapacity", "1000")]
    [InlineData("Gateway:TrafficShadowing:TimeoutMs", "2000")]
    [InlineData("Gateway:TrafficShadowing:StripPiiHeaders", "true")]
    [InlineData("Gateway:Arrow:EnableFlightSql", "true")]
    [InlineData("Gateway:RowLimits:FlightSql", "true")]
    [InlineData("Gateway:RowLimits:FlightSql:DefaultMaxRows", "1000")]
    [InlineData("Gateway:RowLimits:FlightSql:MaxAllowedRows", "5000")]
    public void Every_removed_key_fails_startup(string key, string value)
    {
        var configuration = Config(key, value);
        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration));
        ex.Message.ShouldContain("Unknown configuration keys under 'Gateway'");
    }

    [Theory]
    [InlineData("Gateway:Caching:Garnet:EnableEmbeddedServer", "true")]
    [InlineData("Gateway:Caching:Garnet:EnableEmbeddedServer", "false")]
    [InlineData("Gateway:Caching:Garnet:Host", "127.0.0.1")]
    [InlineData("Gateway:Caching:Garnet:Port", "3278")]
    [InlineData("Gateway:Caching:Garnet:MemorySizeMb", "512")]
    [InlineData("Gateway:Caching:Garnet:EnableTls", "true")]
    [InlineData("Gateway:Caching:Garnet:EnableTls", "false")]
    [InlineData("Gateway:Caching:Garnet:TlsCertFile", "/path/cert.pfx")]
    [InlineData("Gateway:Caching:Garnet:TlsCertPasswordSecretRef", "secret-ref")]
    [InlineData("Gateway:Caching:Garnet:ClientPasswordSecretRef", "secret-ref")]
    [InlineData("Gateway:Caching:Garnet:CheckpointDir", "/tmp/checkpoints")]
    [InlineData("Gateway:Caching:Garnet", "")]
    public void Garnet_keys_fail_startup(string key, string value)
    {
        var configuration = Config(key, value);
        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration));
        ex.Message.ShouldContain("Gateway:Caching:Garnet");
    }

    [Theory]
    [InlineData("Gateway:TrafficShadowing:Enabled", "true")]
    [InlineData("Gateway:TrafficShadowing:Enabled", "false")]
    [InlineData("Gateway:TrafficShadowing:TargetBaseUrl", "https://shadow.internal")]
    [InlineData("Gateway:TrafficShadowing:SampleRatePercentage", "10")]
    [InlineData("Gateway:TrafficShadowing:ChannelCapacity", "5000")]
    [InlineData("Gateway:TrafficShadowing:TimeoutMs", "3000")]
    [InlineData("Gateway:TrafficShadowing:StripPiiHeaders", "false")]
    [InlineData("Gateway:TrafficShadowing:AllowedPathSegments:0", "api")]
    [InlineData("Gateway:TrafficShadowing", "")]
    public void TrafficShadowing_keys_fail_startup(string key, string value)
    {
        var configuration = Config(key, value);
        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration));
        ex.Message.ShouldContain("Gateway:TrafficShadowing");
    }

    [Theory]
    [InlineData("Gateway:Arrow:EnableFlightSql")]
    [InlineData("Gateway:RowLimits:FlightSql")]
    public void FlightSql_keys_fail_startup(string key)
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.ThrowIfUnknownKeys(Config(key)));

        ex.Message.ShouldContain(key);
    }
}
