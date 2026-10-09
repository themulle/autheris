namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using Autheris.Api.Mcp;
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
        services.AddSingleton<IGoldenQueryService, GoldenQueryService>();
        services.AddSingleton<ISemanticMcpCompiler, SemanticMcpCompiler>();
        services.AddScoped<IMcpDatasetCatalog, McpDatasetCatalog>();
        services.AddSingleton<IGraphQlCatalogMap, CatalogGraphQlMap>();
        services.AddTransient<IPreFlightQuerySimulator, PreFlightQuerySimulator>();
        services.AddSingleton<IMcpProvenanceEnricher, McpProvenanceEnricher>();
        services.AddSingleton<IMcpSessionStore, McpSessionStore>();
        services.AddSingleton<IMcpToolRegistry, McpToolRegistry>();
        services.AddSingleton<ISemanticToolPruner, SemanticToolPruner>();
        services.AddSingleton<IPersistedToolValidator, PersistedToolValidator>();
        services.AddScoped<IMcpQueryExecutor, GatewayMcpQueryExecutor>();
        GatewayMcpServer.AddGatewayMcpServer(services, gatewayOptions);
        services.AddScoped<IAiDataGuardrailService, AiDataGuardrailService>();
        services.AddScoped<IMcpProtocolHandler, McpProtocolHandler>();
        services.AddScoped<IMcpStdioRunner, McpStdioRunner>();
        services.AddHostedService<McpSchemaDiscoveryService>();

        // Human-in-the-Loop Step-Up Approval (F-AI-05)
        services.AddSingleton<IHitLStepUpApprovalService, HitLStepUpApprovalService>();

        return services;
    }
}
