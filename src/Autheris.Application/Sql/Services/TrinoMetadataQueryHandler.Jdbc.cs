namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;

/// <summary>The sources of a metadata listing: the caller's discovery visible set and the single decision point.</summary>
public sealed record TrinoMetadataVisibility(IDiscoveryService Discovery, IAccessDecisionPoint DecisionPoint);

public static partial class TrinoMetadataQueryHandler
{
    private const int MaxDiscoveryPages = 1000;

    private static async Task<GovernedSqlResult?> ExecuteJdbcAsync(
        string rawSql,
        Match match,
        ClaimsPrincipal user,
        TenantId tenantId,
        ITableMetadataRepository metadataRepository,
        TrinoMetadataVisibility visibility,
        int? rowLimit,
        CancellationToken ct)
    {
        // The grammar is checked before any data is read; an unsupported shape never gets further than this line.
        var query = TrinoJdbcMetadataShapes.Parse(match);
        var pushdown = TrinoJdbcMetadataShapes.PushdownKeys(query);

        var sw = Stopwatch.StartNew();
        var discovered = await LoadVisibleObjectsAsync(visibility, user, tenantId, ct).ConfigureAwait(false);
        var filtered = FilterDiscovered(discovered, pushdown);
        var entries = await BuildEntriesAsync(visibility, user, tenantId, metadataRepository, filtered, ct).ConfigureAwait(false);

        var (columns, rows) = TrinoJdbcMetadataShapes.Execute(query, entries, rowLimit);
        return BuildResult(rawSql, columns, rows, sw.ElapsedMilliseconds, rowLimit);
    }

    private static Dictionary<string, DiscoveryObject> FilterDiscovered(
        Dictionary<string, DiscoveryObject> discovered,
        TrinoJdbcPushdownKeys pushdown)
    {
        if (pushdown.Catalogs is null && pushdown.Schemas is null && pushdown.Tables is null)
        {
            return discovered;
        }

        var filtered = new Dictionary<string, DiscoveryObject>(StringComparer.Ordinal);
        foreach (var (key, item) in discovered)
        {
            if (pushdown.Matches(item))
            {
                filtered[key] = item;
            }
        }

        return filtered;
    }

    private static GovernedSqlResult BuildResult(
        string rawSql,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        long elapsedMs,
        int? rowLimit)
    {
        if (rowLimit is >= 0 and var limit && rows.Count > limit)
        {
            var capped = rows.Take(limit).ToList();
            return new GovernedSqlResult(rawSql, columns, capped, capped.Count, elapsedMs, Truncated: true);
        }

        return new GovernedSqlResult(rawSql, columns, rows, rows.Count, elapsedMs);
    }

    private static string VisibleKey(string domain, string schema, string name) =>
        string.Concat(domain.ToUpperInvariant(), "\u0001", schema.ToUpperInvariant(), "\u0001", name.ToUpperInvariant());

    private static bool MatchesOrdinal(DiscoveryObject discovery, TableMetadata metadata) =>
        string.Equals(discovery.Domain, metadata.Identifier.Domain, StringComparison.Ordinal) &&
        string.Equals(discovery.Schema, metadata.Identifier.Schema, StringComparison.Ordinal) &&
        string.Equals(discovery.Name, metadata.Identifier.TableName, StringComparison.Ordinal);

    private static (Dictionary<string, TableMetadata> ByKey, HashSet<string> Ambiguous) IndexActiveMetadata(
        IReadOnlyList<TableMetadata> activeTables)
    {
        var byKey = new Dictionary<string, TableMetadata>(activeTables.Count, StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in activeTables)
        {
            var key = VisibleKey(table.Identifier.Domain, table.Identifier.Schema, table.Identifier.TableName);
            if (ambiguous.Contains(key))
            {
                continue;
            }

            if (!byKey.TryAdd(key, table))
            {
                byKey.Remove(key);
                ambiguous.Add(key);
            }
        }

        return (byKey, ambiguous);
    }

    /// <summary>The caller's visible tables and views from discovery (procedures excluded), keyed by identifier; ambiguous case collisions are omitted.</summary>
    private static async Task<Dictionary<string, DiscoveryObject>> LoadVisibleObjectsAsync(
        TrinoMetadataVisibility visibility, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct)
    {
        var caller = new CatalogCaller(user, tenantId, CatalogChannel.Rest);
        var result = new Dictionary<string, DiscoveryObject>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var after = 0;
        for (var page = 0; page < MaxDiscoveryPages; page++)
        {
            var query = new DiscoveryQuery(First: DiscoveryService.MaxPageSize, After: after, IncludeProcedures: false);
            var items = await visibility.Discovery.ListAsync(query, caller, ct).ConfigureAwait(false);
            foreach (var item in items.Items)
            {
                if (item.Kind == DiscoveryObjectKind.Procedure)
                {
                    continue;
                }

                var key = VisibleKey(item.Domain, item.Schema, item.Name);
                if (ambiguous.Contains(key))
                {
                    continue;
                }

                if (!result.TryAdd(key, item))
                {
                    result.Remove(key);
                    ambiguous.Add(key);
                }
            }

            if (!items.HasNextPage || items.Items.Count == 0)
            {
                break;
            }

            after += items.Items.Count;
        }

        return result;
    }

    /// <summary>
    /// One <see cref="IAccessDecisionPoint.DecideManyAsync"/> call over the visible set. A missing, throwing or partial answer
    /// marks the object not accessible; denied columns are omitted.
    /// </summary>
    private static async Task<List<TrinoJdbcEntry>> BuildEntriesAsync(
        TrinoMetadataVisibility visibility,
        ClaimsPrincipal user,
        TenantId tenantId,
        ITableMetadataRepository metadataRepository,
        Dictionary<string, DiscoveryObject> discovered,
        CancellationToken ct)
    {
        var entries = new List<TrinoJdbcEntry>(discovered.Count);
        IReadOnlySet<string> noColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (discovered.Count == 0)
        {
            return entries;
        }

        var activeTables = await CatalogTablePageReader.ReadAllAsync(
            metadataRepository, new CatalogTableQuery(IncludeInactive: false), static t => t.Table.IsActive, ct: ct).ConfigureAwait(false);
        var (metadataByKey, ambiguousMetadata) = IndexActiveMetadata(activeTables);

        var decideKeys = new List<string>();
        var decideTables = new List<TableMetadata>();
        foreach (var (key, item) in discovered)
        {
            if (ambiguousMetadata.Contains(key))
            {
                continue;
            }

            if (metadataByKey.TryGetValue(key, out var meta) && MatchesOrdinal(item, meta))
            {
                decideKeys.Add(key);
                decideTables.Add(meta);
            }
        }

        IReadOnlyList<TableAccessDecision>? decisions = decideTables.Count > 0
            ? await TryDecideManyAsync(visibility.DecisionPoint, user, tenantId, decideTables, ct).ConfigureAwait(false)
            : null;

        var decisionByKey = new Dictionary<string, (TableAccessDecision Decision, TableMetadata Meta)>(StringComparer.Ordinal);
        if (decisions != null)
        {
            for (var i = 0; i < decideKeys.Count; i++)
            {
                if (decisions[i] is { } decision)
                {
                    decisionByKey[decideKeys[i]] = (decision, decideTables[i]);
                }
            }
        }

        foreach (var (key, item) in discovered)
        {
            if (ambiguousMetadata.Contains(key))
            {
                continue;
            }

            if (decisionByKey.TryGetValue(key, out var found) && found.Decision.IsAllowed)
            {
                var allowed = item.Columns
                    .Where(c => found.Decision.GetEffectiveColumnAccess(c.Name, found.Meta) != ColumnAccessLevel.Deny)
                    .Select(static c => c.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                entries.Add(new TrinoJdbcEntry(item, true, allowed));
            }
            else
            {
                entries.Add(new TrinoJdbcEntry(item, false, noColumns));
            }
        }

        return entries;
    }

    private static async Task<IReadOnlyList<TableAccessDecision>?> TryDecideManyAsync(
        IAccessDecisionPoint decisionPoint,
        ClaimsPrincipal user,
        TenantId tenantId,
        IReadOnlyList<TableMetadata> tables,
        CancellationToken ct)
    {
        try
        {
            var subject = SecurityPrincipalContext.FromPrincipal(user, tenantId);
            var answer = await decisionPoint.DecideManyAsync(
                subject, tables, AccessOperation.Discover, AccessTransport.Trino, ct).ConfigureAwait(false);
            return answer is { } list && list.Count == tables.Count ? list : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
