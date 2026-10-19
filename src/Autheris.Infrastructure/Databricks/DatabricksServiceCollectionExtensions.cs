namespace Autheris.Infrastructure.Databricks;

using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

public static class DatabricksServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Statement Execution client and OBO token provider as typed HttpClients with the hardened outbound stack
    /// (no redirects, SSRF checks, IP pinning), bounded response buffers and <c>Authorization</c> header redaction in HttpClient logs (SEC-ADG-09, SEC-OBO238-02).
    /// </summary>
    public static IServiceCollection AddDatabricksStatementExecution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<DatabricksTokenCache>();

        services.AddHttpClient<DatabricksOboTokenProvider>()
            .AddSecureOutboundHandlers(EgressIntegrations.Databricks)
            .RedactLoggedHeaders(["Authorization"]);

        services.TryAddTransient<IDatabricksCredentialProvider>(sp => sp.GetRequiredService<DatabricksOboTokenProvider>());
        services.TryAddTransient<IDelegatedCredentialProvider>(sp => sp.GetRequiredService<DatabricksOboTokenProvider>());
        services.TryAddTransient<IDelegationCapability>(sp => sp.GetRequiredService<DatabricksOboTokenProvider>());

        services.AddHttpClient<DatabricksStatementExecutionClient>((sp, client) =>
            {
                var maxByteLimit = sp.GetRequiredService<IOptions<GatewayOptions>>().Value.DataSources.Connections.Values
                    .Where(c => DataSourceProvider.Is(c.Provider, DatabaseDialect.Databricks) && c.Databricks is not null)
                    .Select(c => c.Databricks!.ByteLimit)
                    .DefaultIfEmpty(26214400L)
                    .Max();
                client.MaxResponseContentBufferSize = maxByteLimit + DatabricksStatementExecutionClient.ResponseOverheadBytes;
                client.Timeout = TimeSpan.FromSeconds(120);
            })
            .AddSecureOutboundHandlers(EgressIntegrations.Databricks)
            .RedactLoggedHeaders(["Authorization"]);

        return services;
    }
}
