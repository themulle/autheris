namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>A DML target must not be a table that a row policy of the caller reads: writing it would change that policy's outcome.</summary>
internal static class DmlPolicyDependencyGuard
{
    public static bool IsReadByRowPolicy(IEnumerable<IRowPolicy?> policies, TableIdentifier target, string dataSource)
    {
        var wanted = target.ToString();
        foreach (var policy in policies)
        {
            if (policy is null)
            {
                continue;
            }

            // A policy that is not a typed predicate cannot be analyzed: refuse (fail-closed).
            if (policy is not PolicyPredicateRowPolicy typed)
            {
                return true;
            }

            foreach (var name in PolicySubqueryTables.Names(typed.Predicate))
            {
                foreach (var candidate in PolicySubqueryTables.Candidates(name, dataSource))
                {
                    if (string.Equals(candidate.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}

/// <summary>
/// Scans the caller's decisions over the catalog once per statement and stops at the first policy that reads the target; later targets of the
/// same statement reuse the decisions already made.
/// </summary>
internal sealed class DmlDependencyScan(
    ITableMetadataRepository repository,
    Func<TableMetadata, TableIdentifier, List<string>, Task<TableAccessDecision>> decide,
    string dataSource)
{
    private readonly List<IRowPolicy?> _seen = [];
    private IReadOnlyList<TableMetadata> _pageItems = [];
    private int _next;
    private string? _after;
    private int _pagesRead;
    private bool _exhausted;

    public async Task<bool> IsReadAsync(TableIdentifier target, CancellationToken ct)
    {
        if (DmlPolicyDependencyGuard.IsReadByRowPolicy(_seen, target, dataSource))
        {
            return true;
        }

        while (true)
        {
            while (_next < _pageItems.Count)
            {
                var table = _pageItems[_next++];

                // Only an inactive table is skipped (not part of the catalog); a table without columns is decided like any other, and a denied
                // decision carries no row policy the caller is subject to.
                if (!table.Table.IsActive)
                {
                    continue;
                }

                var decision = await decide(table, table.Identifier, table.Columns.Select(c => c.ColumnName).ToList()).ConfigureAwait(false);
                if (!decision.IsAllowed)
                {
                    continue;
                }

                _seen.Add(decision.RowPolicy);
                if (DmlPolicyDependencyGuard.IsReadByRowPolicy([decision.RowPolicy], target, dataSource))
                {
                    return true;
                }
            }

            if (_exhausted)
            {
                return false;
            }

            if (_pagesRead >= CatalogTablePageReader.DefaultMaxPages)
            {
                throw new InvalidOperationException($"Catalog table scan exceeded maximum page bound of {CatalogTablePageReader.DefaultMaxPages}.");
            }

            var page = await repository.QueryTablesAsync(
                    new CatalogTableQuery(After: _after, Limit: CatalogTableQuery.DefaultLimit),
                    ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Catalog repository returned a null page.");

            _pagesRead++;
            _pageItems = page.Items;
            _next = 0;

            if (!page.HasMore || string.IsNullOrEmpty(page.NextAfter))
            {
                _exhausted = true;
            }
            else
            {
                if (string.Equals(_after, page.NextAfter, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Catalog table keyset pagination failed to advance continuation cursor.");
                }

                _after = page.NextAfter;
            }
        }
    }
}
