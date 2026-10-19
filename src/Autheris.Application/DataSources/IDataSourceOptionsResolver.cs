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
    /// Checks whether the specified ingress transport is authorized for the given data source (SEC-DS05-03).
    /// </summary>
    bool IsTransportAllowed(string? dataSourceName, DataSourceTransportFlags transport);
}
