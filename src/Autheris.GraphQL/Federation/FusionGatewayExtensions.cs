namespace Autheris.GraphQL.Federation;

using System;
using System.IO;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Federation.Services;
using Autheris.Domain.Options;
using HotChocolate.Execution.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class FusionGatewayExtensions
{
    /// <summary>
    /// Registers Hot Chocolate Fusion Federated Subgraph Router services and Zero-Trust context handlers (P7).
    /// </summary>
    public static IServiceCollection AddFusionFederationServices(
        this IServiceCollection services,
        GatewayOptions options)
    {
        services.AddSingleton<ISubgraphContextPropagationService, SubgraphContextPropagationService>();
        services.AddSingleton<ISubgraphResultMasker, SubgraphResultMasker>();
        services.AddSingleton<Autheris.Application.Federation.Interfaces.ISubgraphCanaryRouter, Autheris.Application.Federation.Services.SubgraphCanaryRouter>();

        // Register HTTP Clients with Zero-Trust DelegatingHandler for each configured subgraph
        foreach (var subgraph in options.Federation.Subgraphs)
        {
            if (string.IsNullOrWhiteSpace(subgraph.Name) || string.IsNullOrWhiteSpace(subgraph.Url))
            {
                continue;
            }

            var subgraphName = subgraph.Name;
            services.AddTransient(sp => new SubgraphSecurityDelegatingHandler(
                subgraphName,
                sp.GetRequiredService<ISubgraphContextPropagationService>(),
                sp.GetRequiredService<IHttpContextAccessor>(),
                sp.GetRequiredService<ILogger<SubgraphSecurityDelegatingHandler>>(),
                sp.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>>(),
                sp.GetService<Autheris.Application.Federation.Interfaces.ISubgraphCanaryRouter>()
            ));

            services.AddHttpClient(subgraphName, client =>
            {
                client.BaseAddress = new Uri(subgraph.Url);
                client.Timeout = TimeSpan.FromSeconds(subgraph.TimeoutSeconds > 0 ? subgraph.TimeoutSeconds : 30);
            })
            .ConfigurePrimaryHttpMessageHandler(sp => Autheris.Application.Security.SecureOutboundHttp.CreatePrimaryHandler(sp, subgraphName))
            .AddHttpMessageHandler(sp => sp.GetRequiredService<SubgraphSecurityDelegatingHandler>());
        }

        return services;
    }

    /// <summary>
    /// Adds the Hot Chocolate Fusion Subgraph Router and result masking middleware to the GraphQL executor builder.
    /// </summary>
    public static IRequestExecutorBuilder AddFusionResultMasking(
        this IRequestExecutorBuilder builder)
    {
        return builder.UseRequest<SubgraphResultMaskingMiddleware>();
    }
}
