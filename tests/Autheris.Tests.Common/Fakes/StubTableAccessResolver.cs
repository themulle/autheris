using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Configurable <see cref="ITableAccessResolver"/> test double returning seeded or computed <see cref="ResolvedTableAccess"/> instances.
/// </summary>
public sealed class StubTableAccessResolver(Func<ClaimsPrincipal?, TableIdentifier, ResolvedTableAccess> resolve) : ITableAccessResolver
{
    private readonly Dictionary<TableIdentifier, ResolvedTableAccess> _byTable = new();

    public StubTableAccessResolver(params ResolvedTableAccess[] entries)
        : this((_, _) => throw new KeyNotFoundException("No resolved table access configured."))
    {
        foreach (var entry in entries)
        {
            _byTable[entry.Metadata.Identifier] = entry;
        }
    }

    public StubTableAccessResolver Add(ResolvedTableAccess entry)
    {
        _byTable[entry.Metadata.Identifier] = entry;
        return this;
    }

    public Task<ResolvedTableAccess> ResolveTableAccessAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        IReadOnlyList<string>? requestedFields,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        CancellationToken ct = default)
    {
        if (_byTable.TryGetValue(table, out var resolved))
        {
            return Task.FromResult(resolved);
        }

        return Task.FromResult(resolve(principal, table));
    }
}
