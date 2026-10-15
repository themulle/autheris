namespace Autheris.Application.Catalog;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Port for computing a lightweight catalog version stamp so consumers (such as GraphQL schema change detection)
/// can detect catalog modifications without rebuilding full schema models on every poll tick.
/// </summary>
public interface ICatalogVersionProvider
{
    public const string CatalogChangedEventChannel = "catalog:changed";

    Task<string> GetVersionAsync(CancellationToken ct = default);
}
