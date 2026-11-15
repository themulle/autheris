namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;

/// <summary>Serves one table list through every catalog read the production code uses, so a test double never answers a paged read with null.</summary>
internal static class CatalogRepositoryStubs
{
    public static void StubTables(this ITableMetadataRepository repo, IEnumerable<TableMetadata> tables)
    {
        var all = tables.ToList();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(all));
        repo.QueryTablesAsync(Arg.Any<CatalogTableQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(Page(all, ci.Arg<CatalogTableQuery>())));
        repo.GetTablesAsync(Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var wanted = ci.Arg<IReadOnlyCollection<TableIdentifier>>().ToHashSet();
                return Task.FromResult<IReadOnlyList<TableMetadata>>(all.Where(t => wanted.Contains(t.Identifier)).ToList());
            });
    }

    public static CatalogTablePage Page(IReadOnlyList<TableMetadata> tables, CatalogTableQuery query)
    {
        query.EnsureValid(out var domain, out var schema, out var namePrefix, out var after);
        var limit = query.EffectiveLimit;
        var matched = tables
            .Where(t => query.IncludeInactive || t.Table.IsActive)
            .Where(t => domain is null || string.Equals(t.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Where(t => schema is null || string.Equals(t.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase))
            .Where(t => namePrefix is null || t.Identifier.TableName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(t => after is null || CatalogTableCursor.Compare(t.Identifier, after.Value) > 0)
            .OrderBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Identifier.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Identifier.TableName, StringComparer.OrdinalIgnoreCase)
            .Take(limit + 1)
            .ToList();
        var items = matched.Count > limit ? matched.Take(limit).ToList() : matched;
        return new CatalogTablePage(items, matched.Count > limit ? CatalogTableCursor.Encode(items[^1].Identifier) : null);
    }
}
