namespace Autheris.Application.DataSources;

using Autheris.Domain.Options.DataSources;

/// <summary>
/// Zero-allocation O(1) resolver for hierarchical per-data-source options (BL-05).
/// Guaranteed to never throw and never return null.
/// </summary>
public interface IDataSourceOptionsResolver
{
    /// <summary>
    /// Resolves the effective runtime profile for the specified data source name.
    /// Returns the global baseline profile if the data source name is null, empty, or unknown.
    /// </summary>
    EffectiveDataSourceProfile Resolve(string? dataSourceName);

    /// <summary>
    /// Attempts to resolve an explicitly registered profile for the data source name.
    /// If not found, outputs the default profile and returns false.
    /// </summary>
    bool TryResolve(string? dataSourceName, out EffectiveDataSourceProfile profile);

    /// <summary>
    /// Maps a caller-supplied (logical) data source name through <c>WebSql.DataSourceMappings</c> to the name a profile is keyed by.
    /// An unmapped name is returned unchanged; a blank name yields an empty string (Issue #447).
    /// </summary>
    string CanonicalName(string? dataSourceName);

    /// <summary>
    /// Increases after every reload that swapped the profile snapshot. A cache of anything derived from the allowed transports keys on it,
    /// so a reload that newly denies a source never serves a stale entry (Issue #447). Resolvers without reload report 0.
    /// </summary>
    long Generation => 0;

    /// <summary>
    /// Indicates whether a configuration reload failed, leaving the resolver running on the last good configuration snapshot.
    /// </summary>
    bool IsConfigurationStale => false;

    /// <summary>
    /// The exception caught during the most recent failed configuration rebuild, or null if healthy.
    /// </summary>
    Exception? LastRebuildError => null;
}
