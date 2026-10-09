namespace Autheris.Tests.Unit.Security;

using System;
using System.IO;
using Autheris.Api.Extensions;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CasbinStartupValidationTests
{
    private static IServiceProvider BuildProvider(GatewayOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(Substitute.For<IRlsFilterGenerator>());
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<CasbinEnforcementService>>(NullLogger<CasbinEnforcementService>.Instance);
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        GatewayServiceCollectionExtensions.AddGatewayInfrastructure(
            services,
            options,
            env);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Casbin_Disabled_Starts_Successfully()
    {
        var options = new GatewayOptions
        {
            Casbin = new CasbinOptions
            {
                Enabled = false,
                ModelPath = null,
                PolicyPath = null
            }
        };

        var provider = BuildProvider(options);
        var service = provider.GetRequiredService<IPolicyEnforcementService>();
        service.ShouldNotBeNull();
    }

    [Fact]
    public void Casbin_Enabled_With_Missing_ModelPath_Uses_Embedded_Default_Model()
    {
        var tempPolicy = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPolicy, "p, admin, domain1, data1, read, allow, (true)\n");

            var options = new GatewayOptions
            {
                Casbin = new CasbinOptions
                {
                    Enabled = true,
                    ModelPath = null,
                    PolicyPath = tempPolicy
                }
            };

            var provider = BuildProvider(options);
            var service = provider.GetRequiredService<IPolicyEnforcementService>();
            service.ShouldNotBeNull();
        }
        finally
        {
            if (File.Exists(tempPolicy)) File.Delete(tempPolicy);
        }
    }

    [Fact]
    public void Casbin_Enabled_With_NonExistent_ModelFile_Throws_FileNotFoundException()
    {
        var options = new GatewayOptions
        {
            Casbin = new CasbinOptions
            {
                Enabled = true,
                ModelPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_non_existent_model.conf"),
                PolicyPath = "dummy.csv"
            }
        };

        var provider = BuildProvider(options);
        var ex = Should.Throw<FileNotFoundException>(() => provider.GetRequiredService<IPolicyEnforcementService>());
        ex.Message.ShouldContain("model file");
    }

    [Fact]
    public void Casbin_Enabled_With_Missing_PolicyPath_Throws_InvalidOperationException()
    {
        var tempModel = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempModel, Autheris.Application.Governance.CasbinEnforcementService.DefaultModelText);

            var options = new GatewayOptions
            {
                Casbin = new CasbinOptions
                {
                    Enabled = true,
                    ModelPath = tempModel,
                    PolicyPath = null
                }
            };

            var provider = BuildProvider(options);
            var ex = Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IPolicyEnforcementService>());
            ex.Message.ShouldContain("PolicyPath is not configured");
        }
        finally
        {
            if (File.Exists(tempModel)) File.Delete(tempModel);
        }
    }

    [Fact]
    public void Casbin_Enabled_With_NonExistent_PolicyFile_Throws_FileNotFoundException()
    {
        var tempModel = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempModel, Autheris.Application.Governance.CasbinEnforcementService.DefaultModelText);

            var options = new GatewayOptions
            {
                Casbin = new CasbinOptions
                {
                    Enabled = true,
                    ModelPath = tempModel,
                    PolicyPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_non_existent_policy.csv")
                }
            };

            var provider = BuildProvider(options);
            var ex = Should.Throw<FileNotFoundException>(() => provider.GetRequiredService<IPolicyEnforcementService>());
            ex.Message.ShouldContain("policy file");
        }
        finally
        {
            if (File.Exists(tempModel)) File.Delete(tempModel);
        }
    }
}
