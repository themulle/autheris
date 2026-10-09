namespace Autheris.Application.DataCatalog.Interfaces;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public sealed record OpenApiIngestionResult(
    bool Success,
    string ServiceTitle,
    int IngestedTablesCount,
    int IngestedColumnsCount,
    IReadOnlyList<string> IngestedTableNames,
    IReadOnlyList<string> Warnings,
    string? ErrorMessage = null,
    IReadOnlyList<string>? SkippedTableNames = null
);

public interface IOpenApiIngestionService
{
    Task<OpenApiIngestionResult> IngestOpenApiJsonAsync(
        string openApiJson,
        string domain = "external",
        string? defaultBaseUrl = null,
        CancellationToken ct = default);

    Task<OpenApiIngestionResult> IngestOpenApiJsonAsync(
        string openApiJson,
        string domain,
        string? defaultBaseUrl,
        DatasourceAuthDto? auth,
        bool dryRun = false,
        CancellationToken ct = default);

    Task<OpenApiIngestionResult> IngestOpenApiStreamAsync(
        Stream stream,
        string domain = "external",
        string? defaultBaseUrl = null,
        CancellationToken ct = default);

    Task<OpenApiIngestionResult> IngestOpenApiStreamAsync(
        Stream stream,
        string domain,
        string? defaultBaseUrl,
        DatasourceAuthDto? auth,
        bool dryRun = false,
        CancellationToken ct = default);
}
