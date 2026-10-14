using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>
/// Global limits of the <c>DeclarativeHttp</c> client (<c>DataSources:Http</c>). They apply to every user of that client:
/// REST, OData, GraphQL, WebSQL cross-source reads, data source tests and plugin HTTP sources.
/// </summary>
public sealed class HttpDataSourceOptions
{
    /// <summary>
    /// Largest response body (bytes) the client accepts from an upstream; a larger body aborts while it is read with
    /// <c>ROW_LIMIT_EXCEEDED</c> (F-GOV-14, SEC-17). Applies to every request of the client, not only to WebSQL
    /// cross-source reads; the per-table staging budget (<c>WebSql:CrossSource:MaxStagedBytesPerTable</c>) is separate.
    /// </summary>
    [Range(1048576, 1073741824)] public long MaxResponseBytes { get; init; } = 32L * 1024 * 1024;
}
