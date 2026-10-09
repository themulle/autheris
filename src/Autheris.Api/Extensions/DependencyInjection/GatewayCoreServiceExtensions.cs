namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Diagnostics;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Application.Interfaces;
using Autheris.Application.Observability;
using Autheris.Application.OData.Interfaces;
using Autheris.Application.OData.Services;
using Autheris.Application.ResourceGroups;
using Autheris.Application.SchemaRegistry;
using Autheris.Application.SchemaRegistry.Validation;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Diagnostics;
using Autheris.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

public static class GatewayCoreServiceExtensions
{
    public static IServiceCollection AddAutherisCore(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment? environment = null)
    {
        var isDev = environment?.IsDevelopment() ?? false;

        // OpenAPI
        services.AddSingleton<IOpenApiCacheManager, OpenApiCacheManager>();
        services.AddSingleton(new OpenApiDocumentOptions
        {
            IncludeStoredProcedureSpec = gatewayOptions.SqlEndpoints.Procedures.Enabled,
            IncludeWebSqlSpec = gatewayOptions.WebSql.Enabled
        });
        services.AddSingleton<IDynamicOpenApiGenerator, DynamicOpenApiGenerator>();

        // Resource Groups & Workload Isolation (F-PERF-08)
        services.AddSingleton<IResourceGroupManager, ResourceGroupManager>();
        // SEC H-07: Per-principal / per-tenant limits for long-lived connections (WebSocket, SSE)
        services.AddSingleton(sp => new PersistentConnectionLimiter(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.ResourceGroups));

        // Canonical System Metadata & Monitoring (F-API-07)
        services.AddSingleton<IGatewaySystemMetricsService, GatewaySystemMetricsService>();

        // Explicit CORS policy configuration
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                var trusted = gatewayOptions.GraphQL.TrustedOrigins;
                if (trusted.Count > 0)
                {
                    if (trusted.Contains("*"))
                    {
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                    }
                    else
                    {
                        policy.WithOrigins(trusted.Where(o => o != "*").ToArray())
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .AllowCredentials();
                    }
                }
                else if (isDev)
                {
                    policy.WithOrigins("http://localhost:5000", "https://localhost:5001")
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                }
            });

            if (isDev && gatewayOptions.Mcp.EnableDeveloperCors)
            {
                options.AddPolicy("McpDeveloperCors", policy =>
                {
                    var extraOrigins = gatewayOptions.Mcp.DeveloperCorsOrigins != null
                        ? new HashSet<string>(gatewayOptions.Mcp.DeveloperCorsOrigins, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    policy.SetIsOriginAllowed(origin =>
                    {
                        if (string.IsNullOrWhiteSpace(origin))
                        {
                            return false;
                        }

                        if (extraOrigins.Contains(origin))
                        {
                            return true;
                        }

                        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                        {
                            if (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                                (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)) &&
                                uri.Port > 0)
                            {
                                return true;
                            }
                        }

                        return false;
                    })
                    .WithHeaders("Content-Type", "Authorization", "Mcp-Session-Id", "MCP-Protocol-Version", "Last-Event-ID")
                    .WithExposedHeaders("Mcp-Session-Id", "WWW-Authenticate")
                    .WithMethods("GET", "POST", "DELETE", "OPTIONS");
                });
            }
        });

        // OpenTelemetry Tracing & Metrics with OTLP Exporter
        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing.AddSource(GatewayDiagnostics.ActivitySourceName);
                tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(GatewayDiagnostics.MeterName);
                metrics.AddOtlpExporter();
            });

        // Extensibility Pipeline & Interceptors (P9)
        services.AddSingleton<IExtensibilityPipeline, ExtensibilityPipeline>();
        services.AddSingleton<IIngressInterceptor, JustificationAndBreakGlassInterceptor>();
        services.AddSingleton<IEgressInterceptor, AuditLineageEgressInterceptor>();

        // Schema Registry & AST Linter (P8)
        services.AddSingleton<IValidator<SchemaRegistrationRequest>, SchemaRegistrationRequestValidator>();
        services.AddSingleton<ISchemaLinter, SchemaLinter>();
        services.AddSingleton<ISchemaRegistryRepository, InMemorySchemaRegistryRepository>();
        services.AddSingleton<ISchemaRegistryService, SchemaRegistryService>();

        return services;
    }
}
