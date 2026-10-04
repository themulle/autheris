namespace Autheris.Application.OData.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Generates dynamic OpenAPI 3.1 specifications from registered table metadata and contracts (F-API-03).
/// </summary>
public interface IDynamicOpenApiGenerator
{
    /// <summary>
    /// Generates an OpenAPI 3.1 specification formatted as JSON.
    /// </summary>
    /// <param name="domainScope">Optional domain filter. If null or empty, includes all domains.</param>
    /// <param name="modular">If true, partitions component schemas as remote $ref pointers instead of inlining them.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GenerateOpenApiJsonAsync(string? domainScope = null, bool modular = false, CancellationToken ct = default);

    /// <summary>
    /// Generates an OpenAPI 3.1 specification formatted as YAML.
    /// </summary>
    /// <param name="domainScope">Optional domain filter. If null or empty, includes all domains.</param>
    /// <param name="modular">If true, partitions component schemas as remote $ref pointers instead of inlining them.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GenerateOpenApiYamlAsync(string? domainScope = null, bool modular = false, CancellationToken ct = default);

    /// <summary>
    /// Generates an isolated JSON Schema for a specific entity table ($ref target).
    /// </summary>
    Task<string?> GenerateEntitySchemaJsonAsync(TableIdentifier tableId, CancellationToken ct = default);

    /// <summary>
    /// Gets the root catalog index listing all modular domain slices and specifications.
    /// </summary>
    Task<OpenApiIndexDocument> GetIndexDocumentAsync(string? baseUrl = null, CancellationToken ct = default);
}
