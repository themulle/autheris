namespace Autheris.Tests.Unit.Tenancy;

using System.Linq;
using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>DT-A-1 / DT-SC-13 (WP-DT-1 part): <c>Gateway:Tenancy:DefaultTenantId</c> fails startup when present but unusable.</summary>
[Trait("Category", "Security")]
public sealed class DefaultTenantOptionsValidationTests
{
    private static string[] Failures(GatewayOptions options)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        var result = new GatewayCoreOptionsValidator(environment).Validate(null, options);
        return result.Failures?.Where(f => f.Contains("Gateway:Tenancy:DefaultTenantId")).ToArray() ?? [];
    }

    private static GatewayOptions With(string? id) => new() { Tenancy = new TenancyOptions { DefaultTenantId = id } };

    [Fact]
    public void DefaultTenant_Unset_Is_Valid() => Failures(With(null)).ShouldBeEmpty();

    [Fact]
    public void DefaultTenant_Valid_Is_Accepted() => Failures(With("default")).ShouldBeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultTenant_Empty_Fails_Startup(string value) => Failures(With(value)).ShouldNotBeEmpty();

    [Theory]
    [InlineData("has space")]
    [InlineData("bad!")]
    [InlineData("this-id-is-way-too-long-this-id-is-way-too-long-this-id-is-way-too-long-x")]
    public void DefaultTenant_Invalid_Format_Fails_Startup(string value) => Failures(With(value)).ShouldNotBeEmpty();

    [Theory]
    [InlineData("_platform")]
    [InlineData("_PLATFORM")]
    [InlineData("_anything")]
    public void DefaultTenant_Reserved_Fails_Startup(string value) => Failures(With(value)).ShouldNotBeEmpty();

    [Fact]
    public void DefaultTenant_Collision_Fails_Startup()
    {
        var options = new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = "Acme" },
            Itsm = new ItsmOptions { InstanceToTenantMap = new() { ["i1"] = "acme" } }
        };

        Failures(options).ShouldNotBeEmpty();
    }

    [Fact]
    public void DefaultTenant_Same_Spelling_As_Another_Configured_Tenant_Is_Not_A_Collision()
    {
        var options = new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = "acme" },
            Itsm = new ItsmOptions { InstanceToTenantMap = new() { ["i1"] = "acme" } }
        };

        Failures(options).ShouldBeEmpty();
    }

    [Fact]
    public void Failure_Message_Never_Echoes_The_Value()
    {
        var failures = Failures(With("sec ret!"));

        failures.ShouldNotBeEmpty();
        failures.ShouldAllBe(f => !f.Contains("sec ret"));
    }

    [Fact]
    public void ConfigurationBinding_ReadsDefaultTenantId_And_EmptyIsPresent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = "" })
            .Build();
        var options = new GatewayOptions();
        configuration.GetSection("Gateway").Bind(options);

        options.Tenancy.DefaultTenantId.ShouldNotBeNull();
        Failures(options).ShouldNotBeEmpty();
    }
}
