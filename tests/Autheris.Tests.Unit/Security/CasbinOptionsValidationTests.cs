using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

public sealed class CasbinOptionsValidationTests
{
    private static (IServiceProvider sp, GatewayOptions options) BuildServiceProvider(Dictionary<string, string?> inMemorySettings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        var envMock = Substitute.For<IHostEnvironment>();
        envMock.EnvironmentName.Returns(Environments.Development);

        services.AddLogging();
        var gatewayOptions = services.AddGatewayOptions(configuration, envMock);
        services.AddGatewayInfrastructure(gatewayOptions, envMock);

        return (services.BuildServiceProvider(), gatewayOptions);
    }

    [Fact]
    public void Casbin_Enabled_With_Empty_ModelPath_Succeeds_And_Resolves_Service_With_Embedded_Model()
    {
        var tempPolicy = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPolicy, "p, alice, tenant1, data, read, true, allow\n");

            // Arrange: ModelPath is empty -> embedded default model
            var settings = new Dictionary<string, string?>
            {
                ["Gateway:Casbin:Enabled"] = "true",
                ["Gateway:Casbin:ModelPath"] = "",
                ["Gateway:Casbin:PolicyPath"] = tempPolicy
            };

            var (sp, options) = BuildServiceProvider(settings);

            // Act & Assert: Options validation passes
            options.Casbin.Enabled.ShouldBeTrue();
            options.Casbin.ModelPath.ShouldBeEmpty();

            var casbinService = sp.GetRequiredService<IPolicyEnforcementService>();
            casbinService.ShouldBeOfType<CasbinEnforcementService>();

            var concrete = (CasbinEnforcementService)casbinService;
            concrete.ModelText.Trim().ShouldBe(CasbinEnforcementService.DefaultModelText.Trim());
        }
        finally
        {
            if (File.Exists(tempPolicy)) File.Delete(tempPolicy);
        }
    }

    [Fact]
    public void Casbin_Enabled_With_NonExistent_ModelPath_Fails_Eager_Startup_Validation()
    {
        var tempPolicy = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPolicy, "p, alice, tenant1, data, read, true, allow\n");
            var nonExistentModelPath = Path.Combine(Path.GetTempPath(), $"missing_model_{Guid.NewGuid():N}.conf");

            var settings = new Dictionary<string, string?>
            {
                ["Gateway:Casbin:Enabled"] = "true",
                ["Gateway:Casbin:ModelPath"] = nonExistentModelPath,
                ["Gateway:Casbin:PolicyPath"] = tempPolicy
            };

            var act = () => BuildServiceProvider(settings);
            var ex = Should.Throw<ValidationException>(() => act());
            ex.Message.ShouldContain("was not found");
        }
        finally
        {
            if (File.Exists(tempPolicy)) File.Delete(tempPolicy);
        }
    }

    [Fact]
    public void Casbin_Enabled_With_NonExistent_PolicyPath_Fails_Eager_Startup_Validation()
    {
        var nonExistentPolicyPath = Path.Combine(Path.GetTempPath(), $"missing_policy_{Guid.NewGuid():N}.csv");
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Casbin:Enabled"] = "true",
            ["Gateway:Casbin:ModelPath"] = "",
            ["Gateway:Casbin:PolicyPath"] = nonExistentPolicyPath
        };

        var act = () => BuildServiceProvider(settings);
        var ex = Should.Throw<ValidationException>(() => act());
        ex.Message.ShouldContain("was not found");
    }

    [Fact]
    public void Casbin_Enabled_With_Existing_Model_And_Policy_Files_Succeeds()
    {
        // Arrange
        var tempModel = Path.GetTempFileName();
        var tempPolicy = Path.GetTempFileName();

        try
        {
            File.WriteAllText(tempModel, CasbinEnforcementService.DefaultModelText);
            File.WriteAllText(tempPolicy, "p, alice, tenant1, data, read, true, allow\n");

            var settings = new Dictionary<string, string?>
            {
                ["Gateway:Casbin:Enabled"] = "true",
                ["Gateway:Casbin:ModelPath"] = tempModel,
                ["Gateway:Casbin:PolicyPath"] = tempPolicy
            };

            var (sp, options) = BuildServiceProvider(settings);

            // Act & Assert
            options.Casbin.Enabled.ShouldBeTrue();

            var casbinService = sp.GetRequiredService<IPolicyEnforcementService>();
            casbinService.ShouldNotBeNull();
        }
        finally
        {
            if (File.Exists(tempModel)) File.Delete(tempModel);
            if (File.Exists(tempPolicy)) File.Delete(tempPolicy);
        }
    }
}
