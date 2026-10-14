using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Plugins;

public interface IHttpDataSourcePlugin
{
    /// <summary>Unique plugin identifier (referenced by Table.PluginName or Table.SourceName).</summary>
    string Name { get; }

    /// <summary>
    /// Executes the call and returns the raw rows as dictionaries.
    /// Column masking, row-level post-filtering and audit logging are enforced centrally by the gateway afterwards.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        PluginExecutionContext context,
        CancellationToken ct = default);
}

public sealed record PluginExecutionContext(
    string OperationName,
    TableMetadata Metadata,
    ClaimsPrincipal Principal,
    IReadOnlyDictionary<string, object?> Arguments,
    IHttpClientFactory HttpClientFactory,
    IReadOnlyDictionary<string, string[]>? RequestHeaders = null
);
