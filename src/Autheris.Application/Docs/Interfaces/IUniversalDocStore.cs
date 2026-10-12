namespace Autheris.Application.Docs.Interfaces;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Manages the physical and in-memory multi-application document lake,
/// supporting atomic bundle extraction, individual document deposits, and manifest resolution.
/// </summary>
public interface IUniversalDocStore
{
    /// <summary>
    /// Lists all registered applications, optionally filtered by business domain or tag.
    /// </summary>
    Task<IReadOnlyList<DocAppSummary>> ListApplicationsAsync(string? domain = null, string? tag = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the manifest definition of a specific application.
    /// </summary>
    Task<DocAppManifest?> GetManifestAsync(string appId, CancellationToken ct = default);

    /// <summary>
    /// Deposits or updates an individual documentation asset (markdown, runbook, or specification).
    /// Safely writes to the application directory and indexes chunks into the search engine.
    /// </summary>
    Task<DepositDocResult> DepositDocumentAsync(DepositDocRequest request, CancellationToken ct = default);

    /// <summary>
    /// Safely unpacks and applies an application documentation ZIP archive with Zip-Slip defense.
    /// </summary>
    Task<DepositDocResult> ExtractBundleAsync(string appId, Stream zipStream, CancellationToken ct = default);
}
