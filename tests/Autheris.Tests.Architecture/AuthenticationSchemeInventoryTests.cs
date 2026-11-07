namespace Autheris.Tests.Architecture;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-AUTH-INV-1 (WP-SIMPL-43): the set of registered authentication schemes is pinned per configuration fixture. Any scheme
/// outside the pinned set (a per-realm scheme, a new handler) fails the test and needs an explicit decision.
/// </summary>
public sealed class AuthenticationSchemeInventoryTests
{
    private static readonly string[] Baseline =
    [
        "GatewayDynamicScheme", "Basic", "RejectScheme", "Scim", "ForwardAuth", "Bearer",
    ];

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AUTH-INV-1")]
    public async Task Registered_schemes_match_inventory_production_default()
    {
        var schemes = await RegisteredSchemesAsync(new GatewayOptions(), Environments.Production);

        schemes.ShouldBe(Baseline.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AUTH-INV-1")]
    public async Task Registered_schemes_match_inventory_both_idps_and_kerberos()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EnableKerberos = true,
                EntraId = new EntraIdAuthOptions { Enabled = true, TenantId = "t", Audience = "a" },
                Adfs = new AdfsAuthOptions { Enabled = true, Authority = "https://adfs.example.test/adfs" },
            },
        };

        var schemes = await RegisteredSchemesAsync(options, Environments.Production);

        schemes.ShouldBe(Baseline.Concat(["BearerAdfs", "Negotiate"]).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AUTH-INV-1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_realm_scoped_bearer_scheme_is_registered(bool bothIdps)
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions { Enabled = true, TenantId = "t", Audience = "a" },
                Adfs = new AdfsAuthOptions { Enabled = bothIdps, Authority = "https://adfs.example.test/adfs" },
            },
        };

        var schemes = await RegisteredSchemesAsync(options, Environments.Production);

        schemes.ShouldNotContain(s => s.StartsWith("BearerRealm_", StringComparison.Ordinal));
        schemes.ShouldContain("GatewayDynamicScheme");
    }

    private static async Task<string[]> RegisteredSchemesAsync(GatewayOptions options, string environmentName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayAuth(options, new TestEnvironment(environmentName));
        await using var provider = services.BuildServiceProvider();
        var all = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        return all.Select(s => s.Name).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Autheris.Tests";

        public string ContentRootPath { get; set; } = System.IO.Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
