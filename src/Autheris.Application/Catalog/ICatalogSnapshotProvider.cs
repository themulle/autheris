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
    /// Gets the latest catalog snapshot, refreshing if necessary or returning the current snapshot.
    /// </summary>
    ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Forces a refresh check against the governance repository catalog version.
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default);
}
