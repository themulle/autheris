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
    [InlineData("Gateway:Arrow:FlightTicketSigningKey", "super-secret-hmac-key-for-tickets-32bytes")]
    [InlineData("Gateway:RowLimits:FlightSql", "true")]
    [InlineData("Gateway:RowLimits:FlightSql:DefaultMaxRows", "1000")]
    [InlineData("Gateway:RowLimits:FlightSql:MaxAllowedRows", "5000")]
    [InlineData("Analytics:Privacy:Noise:Enabled", "false")]
    [InlineData("Gateway:Caching:Cdn:Enabled", "false")]
    [InlineData("Gateway:Caching:Cdn:Cloudflare:ApiToken", "x")]
    [InlineData("Gateway:SchemaRegistry", "true")]
    [InlineData("Gateway:SchemaRegistry:Enabled", "false")]
    [InlineData("Gateway:Itsm:Enabled", "true")]
    [InlineData("Gateway:Itsm:Enabled", "false")]
    [InlineData("Gateway:Itsm:Provider", "ServiceNow")]
    [InlineData("Gateway:Itsm:ServiceNowBaseUrl", "https://example.service-now.com")]
    [InlineData("Gateway:Itsm:JiraBaseUrl", "https://example.atlassian.net")]
    [InlineData("Gateway:Itsm:WebhookSecret", "secret")]
    [InlineData("Gateway:Backstage:Enabled", "true")]
    [InlineData("Gateway:Backstage:Enabled", "false")]
    [InlineData("Gateway:Backstage:SystemName", "autheris")]
    [InlineData("Gateway:Catalog:Purview:Endpoint", "https://purview.azure.com")]
    [InlineData("Gateway:Catalog:Collibra:BaseUrl", "https://collibra.example.com")]
    [InlineData("Gateway:Catalog:Alation:BaseUrl", "https://alation.example.com")]
    [InlineData("Gateway:Catalog:DataHub:GmsUrl", "https://datahub.example.com")]
    [InlineData("Gateway:DataHub:Enabled", "false")]
    [InlineData("Gateway:Purview:Enabled", "false")]
    [InlineData("Gateway:Collibra:Enabled", "false")]
    [InlineData("Gateway:Alation:Enabled", "false")]
    [InlineData("Gateway:Extensibility:EnableBreakGlass", "false")]
    [InlineData("Gateway:Extensibility:RequireRoleForBreakGlass", "true")]
    [InlineData("Gateway:Extensibility:BreakGlassAllowedRoles:0", "BreakGlassOperator")]
    [InlineData("Gateway:Extensibility:RequireJustificationForHighSensitivity", "true")]
    [InlineData("Gateway:Extensibility:BreakGlassHeader", "X-Break-Glass")]
    [InlineData("Gateway:Extensibility:JustificationHeader", "X-Access-Justification")]
    [InlineData("Gateway:AccessProfiles:MaxBreakGlassDurationHours", "72")]
    [InlineData("Gateway:IndividualGrants:BreakGlassMaxHours", "72")]
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
    [InlineData("Gateway:Arrow:FlightTicketSigningKey")]
    [InlineData("Gateway:RowLimits:FlightSql")]
    public void FlightSql_keys_fail_startup(string key)
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.ThrowIfUnknownKeys(Config(key)));

        ex.Message.ShouldContain(key);
    }

    [Theory]
    [InlineData("Gateway:Itsm:Enabled", "true")]
    [InlineData("Gateway:Itsm:Enabled", "false")]
    [InlineData("Gateway:Itsm:Provider", "ServiceNow")]
    [InlineData("Gateway:Itsm:ServiceNowBaseUrl", "https://example.service-now.com")]
    [InlineData("Gateway:Itsm:JiraBaseUrl", "https://example.atlassian.net")]
    [InlineData("Gateway:Itsm:WebhookSecret", "secret")]
    public void Itsm_keys_fail_startup(string key, string value)
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.ThrowIfUnknownKeys(Config(key, value)));

        ex.Message.ShouldContain("Gateway:Itsm");
    }

    [Theory]
    [InlineData("Gateway:Backstage:Enabled", "true")]
    [InlineData("Gateway:Backstage:Enabled", "false")]
    [InlineData("Gateway:Backstage:SystemName", "autheris")]
    public void Backstage_keys_fail_startup(string key, string value)
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.ThrowIfUnknownKeys(Config(key, value)));

        ex.Message.ShouldContain("Gateway:Backstage");
    }

    [Theory]
    [InlineData("Gateway:Catalog:Purview:Endpoint", "https://purview.azure.com")]
    [InlineData("Gateway:Catalog:Collibra:BaseUrl", "https://collibra.example.com")]
    [InlineData("Gateway:Catalog:Alation:BaseUrl", "https://alation.example.com")]
    [InlineData("Gateway:Catalog:DataHub:GmsUrl", "https://datahub.example.com")]
    [InlineData("Gateway:DataHub:Enabled", "false")]
    [InlineData("Gateway:Purview:Enabled", "false")]
    [InlineData("Gateway:Collibra:Enabled", "false")]
    [InlineData("Gateway:Alation:Enabled", "false")]
    public void Catalog_vendor_keys_fail_startup(string key, string value)
    {
        var config = Config(key, value);
        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.ThrowIfUnknownKeys(config));

        var unknown = UnknownGatewayKeyGuard.FindUnknownKeys(config);
        unknown.ShouldContain(u => key.Equals(u, StringComparison.OrdinalIgnoreCase) || key.StartsWith(u + ":", StringComparison.OrdinalIgnoreCase));
        ex.Message.ShouldContain(unknown[0]);
    }

    [Theory]
    [InlineData("Gateway:Extensibility:EnableBreakGlass", "false")]
    [InlineData("Gateway:Extensibility:RequireRoleForBreakGlass", "true")]
    [InlineData("Gateway:Extensibility:BreakGlassAllowedRoles:0", "BreakGlassOperator")]
    [InlineData("Gateway:Extensibility:RequireJustificationForHighSensitivity", "true")]
    [InlineData("Gateway:Extensibility:BreakGlassHeader", "X-Break-Glass")]
    [InlineData("Gateway:Extensibility:JustificationHeader", "X-Access-Justification")]
    [InlineData("Gateway:AccessProfiles:MaxBreakGlassDurationHours", "72")]
    [InlineData("Gateway:IndividualGrants:BreakGlassMaxHours", "72")]
    public void Breakglass_interceptor_keys_fail_startup(string key, string value)
    {
        var config = Config(key, value);
        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.ThrowIfUnknownKeys(config));

        var unknown = UnknownGatewayKeyGuard.FindUnknownKeys(config);
        unknown.ShouldContain(u => key.Equals(u, StringComparison.OrdinalIgnoreCase) || key.StartsWith(u + ":", StringComparison.OrdinalIgnoreCase));
        ex.Message.ShouldContain(unknown[0]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Procedure_EnableHotReloadKey_FailsFast_SEC_PROC_08()
    {
        var config = Config("Gateway:SqlEndpoints:Procedures:EnableHotReload", "false");

        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(config));

        ex.Message.ShouldContain("SqlEndpoints:Procedures:EnableHotReload (removed (WP-SIMPL-39); definitions are re-scanned each interval)");
        UnknownGatewayKeyGuard.FindUnknownKeys(config).Count.ShouldBe(1);
        UnknownGatewayKeyGuard.FindUnknownKeys(Config("Gateway:SqlEndpoints:Procedures:Enabled", "false")).ShouldBeEmpty();
    }
}
