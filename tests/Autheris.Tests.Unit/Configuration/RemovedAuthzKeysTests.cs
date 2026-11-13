namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Api.Configuration;
using Autheris.Application.Policy;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-AZ-08: keys of the removed relationship-tuple authorization model fail startup with the replacement message (never silent no-ops),
/// and its routes and types no longer exist, so every caller gets the router's 404.
/// </summary>
[Trait("Category", "Security")]
public sealed class RemovedAuthzKeysTests
{
    private const string ReplacementMessage = "removed by ADR-020; data access is granted by grant records";

    private static IConfiguration Config(string key, string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    private static string StartupFailure(string key, string? value)
    {
        var configuration = Config(key, value);
        return Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration)).Message;
    }

    [Theory]
    [InlineData("Gateway:Rebac:Enabled", "false")]
    [InlineData("Gateway:Rebac:Enabled", "true")]
    [InlineData("Gateway:Rebac:EnforceOnQueryPath", "")]
    [InlineData("Gateway:Rebac:SeedTuples:0:Subject", "user:alice")]
    public void RebacSection_FailsStartup(string key, string value)
    {
        var message = StartupFailure(key, value);

        message.ShouldContain("Gateway:Rebac");
        message.ShouldContain(ReplacementMessage);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void DangerBypassRebac_FailsStartup(string value)
    {
        var message = StartupFailure("Gateway:Insecure:danger_bypass_rebac", value);

        message.ShouldContain("Gateway:Insecure:danger_bypass_rebac");
        message.ShouldContain(ReplacementMessage);
    }

    [Fact]
    public void RebacRoutes_Return404()
    {
        // Nothing maps /api/v1/rebac/*: the endpoint group, its filter, its store and its options type are gone, so the router answers 404.
        var assemblies = new[]
        {
            typeof(UnknownGatewayKeyGuard).Assembly,
            typeof(GatewayOptions).Assembly,
            typeof(IAccessDecisionPoint).Assembly,
            typeof(Autheris.GraphQL.Directives.McpToolDirectiveType).Assembly,
            typeof(Autheris.Infrastructure.Cache.ConsentCacheService).Assembly,
        };
        var leftovers = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.Contains("Rebac", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName)
            .ToList();
        leftovers.ShouldBeEmpty();

        typeof(GatewayOptions).GetProperty("Rebac").ShouldBeNull();
        typeof(GatewayOptions).GetProperty("IsRebacBypassed").ShouldBeNull();
        var endpointMethods = typeof(UnknownGatewayKeyGuard).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("Rebac", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.DeclaringType!.Name + "." + m.Name)
            .ToList();
        endpointMethods.ShouldBeEmpty();
    }

    [Fact]
    public void Rebac_and_casbin_keys_fail_startup_and_routes_404()
    {
        // ReBAC rows only; the Casbin rows are appended when the Casbin engine is removed.
        foreach (var (key, value) in new[]
                 {
                     ("Gateway:Rebac:Enabled", "true"),
                     ("Gateway:Insecure:danger_bypass_rebac", "true"),
                 })
        {
            StartupFailure(key, value).ShouldContain(ReplacementMessage);
        }

        RebacRoutes_Return404();
    }
}
