namespace Autheris.GraphQL.Catalog;

/// <summary>
/// Issue #802: whether the GraphQL schema this instance serves reflects every catalog change it knows about. Reports state
/// only (no catalog names, counts or versions), so it backs an authenticated status endpoint (the answer reveals write timing).
/// </summary>
public interface ICatalogSchemaFreshness
{
    /// <summary>
    /// True when no change signal waits for a check, no rebuild is pending, requested or running, the last build succeeded and
    /// the catalog version is not newer than the one the schema was built from. Any unknown state reports false (fail-closed).
    /// </summary>
    ValueTask<bool> IsSchemaCurrentAsync(CancellationToken ct = default);
}
