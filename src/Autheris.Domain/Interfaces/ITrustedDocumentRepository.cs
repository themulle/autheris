namespace Autheris.Domain.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Storage of the runtime-managed, app-scoped trusted GraphQL documents (Issue #281). Every mutation changes the data and increments the
/// generation in one transaction (SEC-281-7), so another instance that reads a new generation always finds the matching data.
/// </summary>
public interface ITrustedDocumentRepository
{
    Task<long> GetTrustedDocumentGenerationAsync(CancellationToken ct = default);

    /// <summary>Active versions with hashes and origins plus the generation, read in one transaction.</summary>
    Task<TrustedDocumentStoreSnapshot> LoadTrustedDocumentSnapshotAsync(CancellationToken ct = default);

    /// <summary>The app with all versions, or <see langword="null"/> when the app is unknown.</summary>
    Task<TrustedDocumentAppView?> GetTrustedDocumentAppAsync(string appId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the hash set of a version and the origin set of the app (upsert) in one transaction and bumps the generation.
    /// Inputs are already validated and normalised by the caller.
    /// </summary>
    /// <exception cref="TrustedDocumentVersionRetiredException">The version exists and is retired.</exception>
    /// <exception cref="TrustedDocumentVersionLimitException">A new version would exceed <paramref name="maxActiveVersions"/>.</exception>
    Task<TrustedDocumentSetDelta> ReplaceTrustedDocumentVersionAsync(
        string appId, string version, IReadOnlyList<string> origins, IReadOnlyList<string> hashes, int maxActiveVersions, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Soft-retires a version (status change, rows kept) and bumps the generation unless it was already retired.</summary>
    Task<TrustedDocumentRetireOutcome> RetireTrustedDocumentVersionAsync(string appId, string version, DateTimeOffset at, CancellationToken ct = default);
}
