namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using Autheris.Api.Mcp;
using Autheris.Application.Governance;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Pruning;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Microsoft.Extensions.DependencyInjection;

public static class GatewayMcpServiceExtensions
{
    public static IServiceCollection AddAutherisMcp(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        // Model Context Protocol (MCP) Server & AI Data Guardrails
        services.AddSingleton<ISemanticPromptGuardrail, SemanticPromptGuardrail>();
        services.AddSingleton<Autheris.Application.Docs.Interfaces.IDocSearchService, Autheris.Application.Docs.Services.InMemoryDocSearchService>();
        services.AddSingleton<Autheris.Application.Docs.Interfaces.IDocIngestor, Autheris.Application.Docs.Services.MarkdownDocIngestor>();
        services.AddSingleton<Autheris.Application.Docs.Interfaces.IUniversalDocStore>(sp => new Autheris.Application.Docs.Services.FileSystemUniversalDocStore(
            null,
            sp.GetRequiredService<Autheris.Application.Docs.Interfaces.IDocSearchService>(),
            sp.GetRequiredService<Autheris.Application.Docs.Interfaces.IDocIngestor>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Autheris.Application.Docs.Services.FileSystemUniversalDocStore>>()));
        services.AddSingleton<IGoldenQueryService, GoldenQueryService>();
        services.AddSingleton<ISemanticMcpCompiler, SemanticMcpCompiler>();
        services.AddScoped<IMcpDatasetCatalog, McpDatasetCatalog>();
        services.AddSingleton<IGraphQlCatalogMap, CatalogGraphQlMap>();
        services.AddTransient<IPreFlightQuerySimulator, PreFlightQuerySimulator>();
        services.AddSingleton<IMcpProvenanceEnricher, McpProvenanceEnricher>();
        services.AddSingleton<IMcpSessionStore, McpSessionStore>();
        // PLAN-SEC-115 C-115-8: one startup snapshot drives the registry, the guardrail, the dispatcher, the resources and the route mapping.
        services.AddSingleton<IReadPathSwitches>(sp => ReadPathSwitches.From(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value));
        services.AddSingleton<IMcpToolRegistry, McpToolRegistry>();
        services.AddSingleton<Autheris.Api.Hosting.Warmup.IStartupWarmup, Autheris.Api.Hosting.Warmup.McpSerializationWarmup>();
        services.AddSingleton<ISemanticToolPruner, SemanticToolPruner>();
        services.AddSingleton<IPersistedToolValidator, PersistedToolValidator>();
        services.AddScoped<IApiDispatcherService, ApiDispatcherService>();
        services.AddScoped<IMcpResourceProvider, McpResourceProvider>();
        services.AddScoped<IMcpToolExecutionHandler, McpToolExecutionHandler>();
        services.AddScoped<IMcpQueryExecutor, GatewayMcpQueryExecutor>();
        GatewayMcpServer.AddGatewayMcpServer(services, gatewayOptions);
        services.AddScoped<IAiDataGuardrailService, AiDataGuardrailService>();
        services.AddScoped<IMcpProtocolHandler, McpProtocolHandler>();
        services.AddScoped<IMcpStdioRunner, McpStdioRunner>();
        services.AddHostedService<McpSchemaDiscoveryService>();
        services.AddHostedService<Autheris.Application.Docs.Services.DocStoreStartupSyncService>();

        // Human-in-the-Loop Step-Up Approval (F-AI-05)
        services.AddSingleton<IHitLStepUpApprovalService, HitLStepUpApprovalService>();

        // Governance Access Plans & Control Plane (Track E, ADR-03, ADR-05, R-60..R-64)
        services.AddOptions<AccessPlanOptions>()
            .Configure<Microsoft.Extensions.Configuration.IConfiguration>((o, cfg) =>
                Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(cfg.GetSection(AccessPlanOptions.SectionName), o))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<AccessPlanOptions>, AccessPlanOptionsValidator>();
        services.AddAutherisGovernancePlans();

        return services;
    }
}
