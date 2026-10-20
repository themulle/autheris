namespace Autheris.Application.Catalog;

using System;
using System.Threading;
using System.Threading.Tasks;

public readonly record struct CatalogVersion(long StructureVersion, long ContentVersion)
{
    public string Formatted => $"{StructureVersion}:{ContentVersion}";
    public override string ToString() => Formatted;
}

public sealed record CatalogChangedEvent(
    long StructureVersion,
    long ContentVersion,
    DateTimeOffset Timestamp);

/// <summary>
/// Port for computing a lightweight catalog version stamp so consumers (such as GraphQL schema change detection)
/// can detect catalog modifications without rebuilding full schema models on every poll tick.
/// </summary>
public interface ICatalogVersionProvider
{
    public const string CatalogChangedEventChannel = "catalog:changed";

    ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default);
    Task<string> GetVersionAsync(CancellationToken ct = default);
}
