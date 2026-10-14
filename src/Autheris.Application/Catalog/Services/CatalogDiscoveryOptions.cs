namespace Autheris.Application.Catalog.Services;

using System;
using Microsoft.Extensions.Options;

/// <summary>Options bound to <c>Gateway:Catalog</c>.</summary>
public sealed class CatalogDiscoveryOptions
{
    public const string SectionName = "Gateway:Catalog";

    /// <summary>SR-L-16: maximum size of a remotely downloaded OpenAPI spec (default 10 MiB).</summary>
    public long MaxSpecDownloadBytes { get; set; } = 10L * 1024 * 1024;
}

public sealed class CatalogDiscoveryOptionsValidator : IValidateOptions<CatalogDiscoveryOptions>
{
    public ValidateOptionsResult Validate(string? name, CatalogDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.MaxSpecDownloadBytes > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Gateway:Catalog:MaxSpecDownloadBytes must be greater than zero.");
    }
}
