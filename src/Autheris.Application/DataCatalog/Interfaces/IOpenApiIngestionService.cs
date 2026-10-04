namespace Autheris.Application.DataCatalog.Interfaces;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public sealed record OpenApiIngestionResult(
    bool Success,
    string ServiceTitle,
    int IngestedTablesCount,
    int IngestedColumnsCount,
    IReadOnlyList<string> IngestedTableNames,
    IReadOnlyList<string> Warnings,
    string? ErrorMessage = null
);

public interface IOpenApiIngestionService
{
    Task<OpenApiIngestionResult> IngestOpenApiJsonAsync(
        string openApiJson,
        string domain = "external",
        string? defaultBaseUrl = null,
        CancellationToken ct = default);

    Task<OpenApiIngestionResult> IngestOpenApiStreamAsync(
        Stream stream,
        string domain = "external",
        string? defaultBaseUrl = null,
        CancellationToken ct = default);
}
