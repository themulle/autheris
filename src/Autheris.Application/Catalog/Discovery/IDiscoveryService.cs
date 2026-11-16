namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public enum DiscoverySurface
{
    Rest = 0,
    GraphQl = 1,
    Mcp = 2,
    Search = 3
}

public sealed record DiscoveryQuery(
    string? Domain = null,
    string? Schema = null,
    string? Search = null,
    string? Tag = null,
    string? Language = null,
    int First = 50,
    int After = 0,
    DiscoverySurface Surface = DiscoverySurface.Rest);

public sealed record DiscoverySearchOptions(
    string? Domain = null,
    int Limit = 25,
    string? Mode = null,
    string? Language = null,
    DiscoverySurface Surface = DiscoverySurface.Rest);

public sealed record DiscoveryPage(
    IReadOnlyList<DiscoveryObject> Items,
    int TotalCount,
    bool HasNextPage);

public enum DiscoveryFailureKind
{
    Unauthenticated = 0,
    Forbidden = 1,
    RateLimited = 2,
    InvalidQuery = 3
}

public sealed class DiscoveryStatusException : Exception
{
    public DiscoveryStatusException(
        DiscoveryFailureKind kind,
        string code,
        string message,
        int? retryAfterSeconds = null)
        : base(message)
    {
        Kind = kind;
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public DiscoveryFailureKind Kind { get; }
    public string Code { get; }
    public int? RetryAfterSeconds { get; }
}

public interface IDiscoveryService
{
    ValueTask<DiscoveryPage> ListAsync(DiscoveryQuery query, CatalogCaller caller, CancellationToken ct = default);
    ValueTask<DiscoveryObject?> GetAsync(TableIdentifier id, CatalogCaller caller, CancellationToken ct = default);
    ValueTask<IReadOnlyList<DiscoveryObject>> SearchAsync(string text, DiscoverySearchOptions options, CatalogCaller caller, CancellationToken ct = default);
}
