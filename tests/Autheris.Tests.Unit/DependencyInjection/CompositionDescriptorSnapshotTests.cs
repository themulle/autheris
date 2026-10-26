namespace Autheris.Tests.Unit.DependencyInjection;

using System;
using System.Linq;
using System.Text;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// ARCH-MNT-14 / ARCH-MNT-09..12: the service descriptor list of the composition root is a refactoring guard. The snapshot lists
/// index, service type, lifetime, key and implementation (type, instance type or factory return type); it never records option
/// values, secrets or compiler-generated lambda names, so extracting registrations into other methods keeps it equal while any
/// reordering (decorators, authorization, audit) changes it.
/// </summary>
public sealed class CompositionDescriptorSnapshotTests
{
    [Theory]
    [InlineData("dev-sqlite")]
    [InlineData("production-like")]
    public void Composition_DescriptorSnapshot_IsStable(string profile)
    {
        var snapshot = BuildSnapshot(profile);

        SnapshotFile.AssertMatches(snapshot, SnapshotFile.SnapshotPath($"composition-{profile}.txt"));
    }

    [Fact]
    public void Snapshot_UpdateFlagIsRejectedInCi()
    {
        static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
            key => vars.Where(v => v.Key == key).Select(v => (string?)v.Value).FirstOrDefault();

        Should.Throw<InvalidOperationException>(() => SnapshotFile.EnsureUpdateAllowed(Env(("CI", "true"), (SnapshotFile.UpdateFlag, "1"))));
        Should.Throw<InvalidOperationException>(() => SnapshotFile.EnsureUpdateAllowed(Env(("GITHUB_ACTIONS", "true"), (SnapshotFile.UpdateFlag, "1"))));
        Should.NotThrow(() => SnapshotFile.EnsureUpdateAllowed(Env(("CI", "true"))));
        Should.NotThrow(() => SnapshotFile.EnsureUpdateAllowed(Env((SnapshotFile.UpdateFlag, "1"))));

        // the ambient run itself must not combine the flag with CI
        Func<string, string?> real = Environment.GetEnvironmentVariable;
        Should.NotThrow(() => SnapshotFile.EnsureUpdateAllowed(real));
    }

    private static string BuildSnapshot(string profile)
    {
        var production = profile == "production-like";
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(production ? Environments.Production : Environments.Development);
        environment.ContentRootPath.Returns(System.IO.Path.GetTempPath());

        var configuration = new ConfigurationManager();
        if (production)
        {
            configuration.AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["Gateway:DataMasking:HmacSecretKeyVaultRef"] = "kv://masking-hmac",
                ["Gateway:Audit:ChainAnchorPath"] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anchor"),
            });
        }

        // The sequence of Program.cs (lines 52-69); keep it in step with the composition root.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        var devReport = DevConfiguration.Apply(configuration, environment);
        services.AddSingleton(devReport);
        services.AddGatewayTelemetry(configuration);
        services.AddDataSourceHierarchicalConfiguration(configuration);
        var gatewayOptions = services.AddGatewayOptions(configuration, environment);
        services.AddGatewayInfrastructure(gatewayOptions, environment);
        services.AddGatewayAuth(gatewayOptions, environment);
        var demoDataEnabled = DemoDataSwitch.Resolve(gatewayOptions, environment.EnvironmentName);
        services.AddSingleton<IDemoDataSwitch>(new DemoDataSwitch(demoDataEnabled));
        services.AddGatewayGraphQL(gatewayOptions);
        services.AddPlan9Services(configuration);

        var text = new StringBuilder();
        for (var i = 0; i < services.Count; i++)
        {
            text.Append(Describe(i, services[i])).Append('\n');
        }

        return text.ToString();
    }

    private static string Describe(int index, ServiceDescriptor descriptor)
    {
        var key = descriptor.IsKeyedService ? FormatKey(descriptor.ServiceKey) : "-";
        string implementation;
        if (descriptor.IsKeyedService)
        {
            implementation = descriptor.KeyedImplementationType is { } keyedType ? $"type:{FormatType(keyedType)}"
                : descriptor.KeyedImplementationInstance is { } keyedInstance ? $"instance:{FormatType(keyedInstance.GetType())}"
                : descriptor.KeyedImplementationFactory is { } keyedFactory ? $"factory:{FormatFactory(keyedFactory)}"
                : "unknown";
        }
        else
        {
            implementation = descriptor.ImplementationType is { } type ? $"type:{FormatType(type)}"
                : descriptor.ImplementationInstance is { } instance ? $"instance:{FormatType(instance.GetType())}"
                : descriptor.ImplementationFactory is { } factory ? $"factory:{FormatFactory(factory)}"
                : "unknown";
        }

        return $"{index:D4} {FormatType(descriptor.ServiceType)} {descriptor.Lifetime} key={key} {implementation}";
    }

    private static string FormatKey(object? key) => key switch
    {
        null => "null",
        Type type => "type:" + FormatType(type),
        string text => "\"" + text + "\"",
        _ => key.GetType().Name + ":" + key,
    };

    /// <summary>The declared return type of the factory delegate; stable under extract-method, unlike the lambda method name.</summary>
    private static string FormatFactory(Delegate factory)
    {
        var args = factory.GetType().GetGenericArguments();
        return args.Length > 0 ? FormatType(args[^1]) : "unknown";
    }

    private static string FormatType(Type type)
    {
        if (!type.IsGenericType)
        {
            return (type.FullName ?? type.Name).Replace('+', '.');
        }

        var name = (type.GetGenericTypeDefinition().FullName ?? type.Name).Replace('+', '.');
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        var arguments = type.IsGenericTypeDefinition
            ? new string('_', type.GetGenericArguments().Length)
            : string.Join(",", type.GetGenericArguments().Select(FormatType));
        return $"{name}<{arguments}>";
    }
}
