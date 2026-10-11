namespace Autheris.Infrastructure.Databricks;

using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

public static class DatabricksServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Statement Execution client as a typed HttpClient with the hardened outbound stack (no redirects, SSRF checks,
    /// IP pinning), a bounded response buffer and <c>Authorization</c> header redaction in HttpClient logs (SEC-ADG-09). It does not
    /// wire Databricks into WebSQL admission (WP-X0j).
    /// </summary>
    public static IServiceCollection AddDatabricksStatementExecution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<DatabricksTokenCache>();
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
