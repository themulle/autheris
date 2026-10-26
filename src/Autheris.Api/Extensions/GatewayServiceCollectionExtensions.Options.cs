using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Hosting;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Federation;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Health;
using Autheris.Infrastructure.Idempotency;
using Autheris.Infrastructure.Messaging;
using Autheris.Extensions;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.RateLimiting;
using Autheris.Infrastructure.Security;
using Autheris.Api.Security;
using Autheris.Application.Plugins;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Lineage;
using Autheris.Application.Workflows;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.ResourceGroups;
using Autheris.Application.Observability;
using Autheris.Application.OData.Interfaces;
using Autheris.Application.OData.Services;
using Autheris.Infrastructure.Itsm;
using Autheris.Infrastructure.Lineage;
using Autheris.Infrastructure.Plugins;
using Autheris.Domain.Diagnostics;
using Autheris.Application.Caching.Interfaces;
using Autheris.Infrastructure.Garnet;
using Autheris.Infrastructure.Serialization;
using Autheris.Application.Caching.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Infrastructure.Streaming;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.Cdn;
using Autheris.Application.SchemaRegistry;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Application.Sql;
using Autheris.Application.Serialization;
using Autheris.Application.SchemaRegistry.Validation;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using StackExchange.Redis;
using HotChocolate.Execution.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Autheris.Api.Configuration.Validation;

namespace Autheris.Api.Extensions;

public static partial class GatewayServiceCollectionExtensions
{
    public static OptionsBuilder<TOptions> AddGatewaySection<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionPath)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        return services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionPath))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    public static OptionsBuilder<TOptions> AddGatewaySection<TOptions>(
        this IServiceCollection services,
        string sectionPath)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        return services.AddOptions<TOptions>()
            .BindConfiguration(sectionPath)
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    internal static GatewayOptions BindGatewayOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new GatewayOptions();
        configuration.GetSection(GatewaySections.Root).Bind(options);
        return options;
    }

    public static GatewayOptions AddGatewayOptions(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        Autheris.Api.Extensions.DependencyInjection.CdcWebhooksFeature.Capture(services, configuration);
        Autheris.Api.Extensions.DependencyInjection.GatewayStartupWarmupServiceExtensions.CaptureColdPathTiming(services, configuration);

        services.TryAddSingleton<IHostEnvironment>(environment);
        services.TryAddSingleton<IFileSystemProbe>(PhysicalFileSystemProbe.Instance);

        var gatewayOptions = BindGatewayOptions(configuration);

        services.AddOptions<GatewayOptions>()
            .Configure(opts => configuration.GetSection(GatewaySections.Root).Bind(opts))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayCoreOptionsValidator>();
        services.AddSingleton<IValidateOptions<GatewayOptions>, GraphQLAggregationOptionsValidator>();
        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewaySecurityOptionsValidator>();
        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayStorageOptionsValidator>();
        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayResilienceOptionsValidator>();
        services.AddSingleton<IValidateOptions<GatewayOptions>, CdcStreamOptionsValidator>();
        services.AddSingleton<IValidateOptions<GatewayOptions>, SqlParserCacheOptionsValidator>();
        services.AddSingleton<IValidateOptions<GatewayOptions>>(sp =>
            new CrossSourceOptionsValidator(
                configuration,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<CrossSourceOptionsValidator>>()));
        services.AddSingleton<IValidateOptions<GatewayOptions>>(sp =>
            new DuckDbOlapOptionsValidator(
                environment,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<DuckDbOlapOptionsValidator>>()));
        services.AddSingleton<IValidateOptions<GatewayOptions>>(_ => new ReadPathSwitchOptionsValidator(configuration));
        services.AddSingleton<IValidateOptions<GatewayOptions>>(sp =>
            new BulkStreamSigningKeyOptionsValidator(
                environment,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<BulkStreamSigningKeyOptionsValidator>>()));
        services.AddSingleton<IValidateOptions<GatewayOptions>>(sp =>
            new CoLocationOptionsValidator(
                configuration,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<CoLocationOptionsValidator>>()));
        services.AddSingleton<IValidateOptions<GatewayOptions>>(sp =>
            new Autheris.Api.Extensions.DependencyInjection.GatewayEmptyListConfigurationValidator(
                configuration,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<Autheris.Api.Extensions.DependencyInjection.GatewayEmptyListConfigurationValidator>>()));

        Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator.ValidateSqlRewriterEngine(configuration);
        Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator.ValidateRemovedWebSqlSettings(configuration);
        ValidateGatewayOptions(gatewayOptions, environment);

        services.Configure<HostOptions>(o =>
        {
            var drainBuffer = gatewayOptions.HighAvailability.DrainDelaySeconds + gatewayOptions.HighAvailability.ShutdownTimeoutSeconds + 5;
            o.ShutdownTimeout = TimeSpan.FromSeconds(Math.Min(drainBuffer, gatewayOptions.HighAvailability.TerminationGracePeriodSeconds));
        });

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

            if (gatewayOptions.ReverseProxy.Enabled)
            {
                // Preserve safe loopback defaults against spoofing
                options.KnownProxies.Add(System.Net.IPAddress.Loopback);
                options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);

                foreach (var netStr in gatewayOptions.ReverseProxy.GetEffectiveKnownNetworks())
                {
                    if (System.Net.IPNetwork.TryParse(netStr, out var network))
                    {
                        options.KnownIPNetworks.Add(network);
                    }
                }

                foreach (var proxyStr in gatewayOptions.ReverseProxy.KnownProxies)
                {
                    if (System.Net.IPAddress.TryParse(proxyStr, out var ip))
                    {
                        options.KnownProxies.Add(ip);
                    }
                }
            }
            else
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            }
        });

        return gatewayOptions;
    }
}
