namespace Autheris.Infrastructure.Databricks;

using System.Collections.Immutable;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Databricks SQL Statement Execution API 2.0 client (design §6.3, SEC-ADG-09).</summary>
public sealed class DatabricksStatementExecutionClient(
    HttpClient http,
    IOptions<GatewayOptions> options,
    IKeyVaultSecretProvider? secrets = null,
    IHostEnvironment? environment = null,
    DatabricksTokenCache? tokenCache = null,
    ILogger<DatabricksStatementExecutionClient>? logger = null,
    TimeProvider? timeProvider = null)
{
    private readonly object[] _stub = [http, options, secrets!, environment!, tokenCache!, logger!, timeProvider!];

    public Task<DatabricksStatementResult> ExecuteAsync(DatabricksStatementRequest request, CancellationToken ct) =>
        Task.FromResult(new DatabricksStatementResult(string.Empty, ImmutableArray<DatabricksColumn>.Empty, []));

    public Task CancelAsync(string statementId, CancellationToken ct) => Task.CompletedTask;
}
