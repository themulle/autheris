using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using System.Threading.Tasks;
using NSubstitute;
using NSubstitute.Core;

// Global namespace on purpose: the helper replaces the former `GetAllTablesAsync(...).Returns(...)` stub in many test files.

/// <summary>
/// NSubstitute substitutes do not run default interface methods, so a catalog test double must serve every read shape
/// the production callers use (full list, keyset pages, by identifier) from the same table list.
/// </summary>
internal static class CatalogRepositoryStubExtensions
{
    /// <summary>Serves <paramref name="tables"/> through <c>GetAllTablesAsync</c>, <c>QueryTablesAsync</c> and <c>GetTablesAsync</c>.</summary>
    public static void StubAllTables(this ITableMetadataRepository repository, IReadOnlyList<TableMetadata> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        repository.StubAllTables(_ => Task.FromResult(tables));
    }

    /// <summary>Serves the (possibly lazily produced) <paramref name="tables"/> task through every read shape.</summary>
    public static void StubAllTables(this ITableMetadataRepository repository, Task<IReadOnlyList<TableMetadata>> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        repository.StubAllTables(_ => tables);
    }

    /// <summary>Serves the tables produced by <paramref name="source"/> (evaluated per call) through every read shape.</summary>
    public static void StubAllTables(this ITableMetadataRepository repository, Func<CallInfo, Task<IReadOnlyList<TableMetadata>>> source)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(source);

        repository.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(call => source(call));
        repository.QueryTablesAsync(Arg.Any<CatalogTableQuery>(), Arg.Any<CancellationToken>())
            .Returns(async call => Page(await source(call).ConfigureAwait(false), call.Arg<CatalogTableQuery>()));
        repository.GetTablesAsync(Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var wanted = call.Arg<IReadOnlyCollection<TableIdentifier>>().ToHashSet();
                var all = await source(call).ConfigureAwait(false);
                return (IReadOnlyList<TableMetadata>)all.Where(t => wanted.Contains(t.Identifier)).ToList();
            });
    }

    /// <summary>Serves each list in turn, one per read call (the last list repeats), through every read shape.</summary>
    public static void StubAllTablesSequence(this ITableMetadataRepository repository, params IReadOnlyList<TableMetadata>[] lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        var calls = 0;
        repository.StubAllTables(_ => Task.FromResult(lists[Math.Min(Interlocked.Increment(ref calls) - 1, lists.Length - 1)]));
    }

    /// <summary>Fails <c>GetAllTablesAsync</c>, <c>QueryTablesAsync</c> and <c>GetTablesAsync</c> with <paramref name="exception"/>.</summary>
    public static void FailAllTables(this ITableMetadataRepository repository, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(exception);

        repository.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TableMetadata>>(_ => throw exception);
        repository.QueryTablesAsync(Arg.Any<CatalogTableQuery>(), Arg.Any<CancellationToken>())
            .Returns<CatalogTablePage>(_ => throw exception);
        repository.GetTablesAsync(Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TableMetadata>>(_ => throw exception);
    }

    private static CatalogTablePage Page(IReadOnlyList<TableMetadata> tables, CatalogTableQuery query)
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

        var hasMore = matched.Count > limit;
        var items = hasMore ? matched.Take(limit).ToList() : matched;
        return new CatalogTablePage(items, hasMore ? CatalogTableCursor.Encode(items[^1].Identifier) : null);
    }
}
