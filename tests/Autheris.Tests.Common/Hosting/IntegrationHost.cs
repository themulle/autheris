namespace Autheris.Tests.Common.Hosting;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Hosting;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Recertification;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Configuration contract for <see cref="IntegrationHost{TConfig}"/> when used directly as an xUnit
/// <c>IClassFixture&lt;IntegrationHost&lt;TConfig&gt;&gt;</c>.
/// </summary>
public interface IIntegrationHostConfig
{
    void ConfigureConfiguration(IConfigurationBuilder builder) { }

    void ConfigureTestServices(IServiceCollection services) { }

    void ConfigureWebHost(IWebHostBuilder builder) { }
}

/// <summary>
/// Issue #520 (WP-OI-CI-3a): Shared <see cref="WebApplicationFactory{TEntryPoint}"/> subclass for integration tests.
/// Applies host configuration once in <see cref="ConfigureWebHost"/>, sets console log level to <see cref="LogLevel.Warning"/>,
/// and removes only explicitly allow-listed non-security background <see cref="IHostedService"/> loops
/// (search-index warm-up, recertification cycle, traffic drain) while preserving all startup validators and security checks.
/// </summary>
public class IntegrationHost<TConfig> : WebApplicationFactory<Program>
    where TConfig : class
{
    private readonly IReadOnlyDictionary<string, string?>? _settings;
    private readonly Action<IConfigurationBuilder>? _configureConfiguration;
    private readonly Action<IServiceCollection>? _configureTestServices;
    private readonly Action<IWebHostBuilder>? _configureWebHost;
    private readonly IIntegrationHostConfig? _configInstance;

    public IntegrationHost()
    {
        if (typeof(IIntegrationHostConfig).IsAssignableFrom(typeof(TConfig)) &&
            typeof(TConfig).GetConstructor(Type.EmptyTypes) is not null)
        {
            _configInstance = Activator.CreateInstance<TConfig>() as IIntegrationHostConfig;
        }
    }

    public IntegrationHost(
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureTestServices = null,
        Action<IConfigurationBuilder>? configureConfiguration = null,
        Action<IWebHostBuilder>? configureWebHost = null)
        : this()
    {
        _settings = settings;
        _configureTestServices = configureTestServices;
        _configureConfiguration = configureConfiguration;
        _configureWebHost = configureWebHost;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddFilter("Microsoft", LogLevel.Warning);
            logging.AddFilter("System", LogLevel.Warning);
        });

        // Program.cs reads builder.Configuration synchronously (for example the governance provider guard) before the
        // WebApplicationFactory configuration callbacks of IWebHostBuilder.ConfigureAppConfiguration are applied. The
        // configuration is therefore evaluated once here, promoted to host settings (UseSetting is visible to that early read),
        // and the very same configuration sources are added again later, so each value is computed exactly once.
        var early = new ConfigurationBuilder();
        if (_settings is { Count: > 0 })
        {
            early.AddInMemoryCollection(_settings);
        }

        _configInstance?.ConfigureConfiguration(early);
        _configureConfiguration?.Invoke(early);
        ConfigureHostConfiguration(early);

        foreach (var pair in early.Build().AsEnumerable())
        {
            if (pair.Value is not null)
            {
                builder.UseSetting(pair.Key, pair.Value);
            }
        }

        builder.ConfigureAppConfiguration((_, config) =>
        {
            foreach (var source in early.Sources)
            {
                config.Add(source);
            }
        });

        _configInstance?.ConfigureWebHost(builder);
        _configureWebHost?.Invoke(builder);

        builder.ConfigureTestServices(services =>
        {
            IntegrationHost.RemoveAllowedBackgroundHostedServices(services);
            _configInstance?.ConfigureTestServices(services);
            _configureTestServices?.Invoke(services);
            ConfigureHostServices(services);
        });
    }

    protected virtual void ConfigureHostConfiguration(IConfigurationBuilder config)
    {
    }

    protected virtual void ConfigureHostServices(IServiceCollection services)
    {
    }
}

/// <summary>
/// Non-generic <see cref="IntegrationHost{TConfig}"/> convenience type and static allow-list enforcement for
/// background <see cref="IHostedService"/> removal in integration tests.
/// </summary>
public class IntegrationHost : IntegrationHost<IntegrationHost.DefaultConfig>
{
    public sealed class DefaultConfig : IIntegrationHostConfig
    {
    }

    /// <summary>
    /// Explicit test-side allow-list of background <see cref="IHostedService"/> implementations that may be
    /// removed by <see cref="IntegrationHost{TConfig}"/>:
    /// - search-index warm-up / catalog snapshot (<see cref="CatalogSearchWarmupService"/>, <see cref="CatalogSnapshotBackgroundService"/>)
    /// - recertification / grant-expiry cycle (<see cref="GrantExpiryHostedService"/>, <see cref="RecertificationGovernorHostedService"/>)
    /// - traffic drain (<see cref="TrafficDrainHostedService"/>)
    /// Startup validators and security checks are strictly forbidden from this allow-list.
    /// </summary>
    public static IReadOnlySet<Type> AllowedRemovedHostedServices { get; } = new HashSet<Type>
    {
        typeof(CatalogSearchWarmupService),
        typeof(CatalogSnapshotBackgroundService),
        typeof(GrantExpiryHostedService),
        typeof(RecertificationGovernorHostedService),
        typeof(TrafficDrainHostedService),
    };

    private static readonly string[] ForbiddenTypeKeywords =
    [
        "Startup",
        "Validator",
        "Validate",
        "Auditor",
        "Security",
        "AuditChain",
        "Migration",
        "Preseed",
        "Bootstrap",
    ];

    public IntegrationHost(
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureTestServices = null,
        Action<IConfigurationBuilder>? configureConfiguration = null,
        Action<IWebHostBuilder>? configureWebHost = null)
        : base(settings, configureTestServices, configureConfiguration, configureWebHost)
    {
    }

    public static bool IsStartupValidatorOrSecurityCheck(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (typeof(IStartupFilter).IsAssignableFrom(type))
        {
            return true;
        }

        if (type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidateOptions<>)))
        {
            return true;
        }

        var name = type.FullName ?? type.Name;
        foreach (var keyword in ForbiddenTypeKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void ValidateRemovalCandidate(Type candidateType)
    {
        ArgumentNullException.ThrowIfNull(candidateType);

        if (IsStartupValidatorOrSecurityCheck(candidateType))
        {
            throw new InvalidOperationException(
                $"IntegrationHost must never remove startup validator or security check '{candidateType.FullName}'.");
        }

        if (!typeof(IHostedService).IsAssignableFrom(candidateType))
        {
            throw new InvalidOperationException(
                $"IntegrationHost may only remove IHostedService registrations, not '{candidateType.FullName}'.");
        }

        if (!AllowedRemovedHostedServices.Contains(candidateType))
        {
            throw new InvalidOperationException(
                $"Hosted service '{candidateType.FullName}' is not in IntegrationHost.AllowedRemovedHostedServices.");
        }
    }

    public static int RemoveAllowedBackgroundHostedServices(
        IServiceCollection services,
        IEnumerable<Type>? candidateTypes = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var targets = (candidateTypes ?? AllowedRemovedHostedServices).ToList();
        foreach (var candidate in targets)
        {
            ValidateRemovalCandidate(candidate);
        }

        var targetSet = targets.ToHashSet();
        var removed = 0;

        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(IHostedService))
            {
                continue;
            }

            var implType = GetHostedServiceImplementationType(descriptor);
            if (implType is not null && targetSet.Contains(implType))
            {
                ValidateRemovalCandidate(implType);
                services.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    private static Type? GetHostedServiceImplementationType(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType
        ?? descriptor.ImplementationInstance?.GetType()
        ?? descriptor.ImplementationFactory?.Method.ReturnType;
}
