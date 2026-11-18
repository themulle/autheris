namespace Autheris.Application.Catalog;

/// <summary>
/// Provides atomic access to the latest immutable <see cref="CatalogSnapshot"/>.
/// Updates are synchronized monotonically with catalog version increments.
/// </summary>
public interface ICatalogSnapshotProvider
{
    /// <summary>
    /// Gets the current in-memory catalog snapshot synchronously.
    /// </summary>
    CatalogSnapshot CurrentSnapshot { get; }

    /// <summary>
    /// Gets whether the first snapshot has been loaded. Before that, <see cref="CurrentSnapshot"/> is an empty placeholder
    /// that consumers must never treat as an empty catalog.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Gets the latest catalog snapshot, refreshing if necessary. Throws <see cref="Autheris.Domain.Exceptions.CatalogSnapshotNotReadyException"/>
    /// while no snapshot has been loaded yet (typed unavailable, never an empty success).
    /// </summary>
    ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets whether the snapshot provider is degraded after consecutive refresh failures.
    /// </summary>
    bool IsDegraded => false;

    /// <summary>
    /// Forces a refresh check against the governance repository catalog version.
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default);
}
