namespace Autheris.Application.OData.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Thread-safe cache manager for pre-rendered OpenAPI UTF-8 byte arrays.
/// </summary>
public interface IOpenApiCacheManager
{
    /// <summary>
    /// Gets or creates the cached UTF-8 byte representation for an OpenAPI specification.
    /// </summary>
    Task<byte[]> GetOrAddAsync(
        string? domainScope,
        bool isYaml,
        Func<CancellationToken, Task<string>> factory,
        CancellationToken ct = default);

    /// <summary>
    /// Gets or creates the cached UTF-8 byte representation for an OpenAPI specification with modular mode.
    /// </summary>
    Task<byte[]> GetOrAddAsync(
        string? domainScope,
        bool isYaml,
        bool isModular,
        Func<CancellationToken, Task<string>> factory,
        CancellationToken ct = default);

    /// <summary>
    /// Invalidates all cached OpenAPI documents (e.g. upon catalog schema refresh).
    /// </summary>
    void InvalidateCache();
}
