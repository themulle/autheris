namespace Autheris.Application.Docs.Interfaces;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Services;
using Autheris.Domain.Model;

/// <summary>
/// Manages the persisted multi-application document store and synchronizes searchable chunks.
/// </summary>
public interface IUniversalDocStore
{
    /// <summary>
    /// Lists applications visible to <paramref name="scope"/>, optionally filtered by business domain or tag.
    /// </summary>
    Task<IReadOnlyList<DocAppSummary>> ListApplicationsAsync(
        DocAccessScope scope,
        string? domain = null,
        string? tag = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves the manifest definition of a specific application visible to <paramref name="scope"/>.
    /// </summary>
    Task<DocAppManifest?> GetManifestAsync(
        DocAccessScope scope,
        string appId,
        CancellationToken ct = default);

    /// <summary>
    /// Reads one active document visible to <paramref name="scope"/>: the caller tenant's own document wins over an official one.
    /// A document that is unknown, deleted, foreign, or hidden from discovery for this scope yields <c>null</c> in every case.
    /// </summary>
    Task<DocRecord?> ReadDocumentAsync(
        DocAccessScope scope,
        string appId,
        DocCategory category,
        string docId,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a new document owned by <paramref name="writer"/> and indexes its chunks on success.
    /// </summary>
    Task<DepositDocResult> CreateAsync(
        DocWriter writer,
        DepositDocRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces an existing document owned by <paramref name="writer"/> using optimistic concurrency <paramref name="expectedVersion"/>.
    /// </summary>
    Task<DepositDocResult> ReplaceAsync(
        DocWriter writer,
        DepositDocRequest request,
        long expectedVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a document identified by <paramref name="key"/> and removes its chunks from the search index.
    /// </summary>
    Task<bool> DeleteAsync(
        DocWriter writer,
        DocKey key,
        CancellationToken ct = default);

    /// <summary>
    /// Validates, sanitizes, and imports a documentation ZIP bundle for <paramref name="writer"/>.
    /// Tenant bundles are persisted atomically; official bundles create or replace each entry.
    /// </summary>
    Task<DepositDocResult> ImportBundleAsync(
        DocWriter writer,
        string appId,
        Stream zipStream,
        CancellationToken ct = default);

    /// <summary>
    /// Purges all documents and quota records belonging to <paramref name="tenantId"/>.
    /// </summary>
    Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Reloads all persisted active documents into the search index and returns indexed section count.
    /// </summary>
    Task<int> RefreshIndexAsync(CancellationToken ct = default);
}
