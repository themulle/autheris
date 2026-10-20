namespace Autheris.Application.Caching.TableCache;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;

/// <summary>
/// Evaluates whether incoming AST queries can be served from the governed table cache.
/// Conforms to SEC-BL04-11 and WP-TC-05.
/// </summary>
public sealed class TableCacheQueryRouter : ITableCacheQueryRouter
{
    public const string InternalTableAccessProhibitedCode = "INTERNAL_TABLE_ACCESS_PROHIBITED";

    private static readonly SearchValues<char> ValidIdentifierChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    private readonly ITableCacheCatalog _catalog;

    public TableCacheQueryRouter(ITableCacheCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public static void ValidateIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var trimmed = identifier.Trim();

        if (trimmed.Length > 128)
        {
            throw new SecurityException($"{InternalTableAccessProhibitedCode}: Identifier exceeds maximum allowed length of 128 characters: '{identifier}'");
        }

        if (trimmed.AsSpan().ContainsAnyExcept(ValidIdentifierChars))
        {
            throw new SecurityException($"{InternalTableAccessProhibitedCode}: Illegal characters detected in SQL identifier: '{identifier}'");
        }

        // SEC-BL04-11: Anti-Bypass: Reject direct queries to internal staging or shadow tables
        if (trimmed.StartsWith("staging_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("autheris_cache", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"{InternalTableAccessProhibitedCode}: Direct access to internal cache staging tables or namespaces is prohibited: '{identifier}'");
        }
    }

    public ValueTask<TableCacheRoutingDecision> EvaluateRoutingAsync(
        TableIdentifier table,
        IReadOnlyList<string> requestedColumns,
        string? whereClause,
        CancellationToken ct = default)
    {
        // SEC-BL04-11: Validate table identifiers against anti-bypass allowlist
        ValidateIdentifier(table.Domain);
        ValidateIdentifier(table.Schema);
        ValidateIdentifier(table.TableName);

        if (!_catalog.TryGet(table, out var metadata) || metadata is null)
        {
            return ValueTask.FromResult(new TableCacheRoutingDecision(
                IsCached: false,
                CanSatisfyFully: false,
                InternalStorageTarget: null,
                Metadata: null,
                FallbackReason: "Table is not registered in the cache catalog."));
        }

        if (!metadata.IsHealthy)
        {
            return ValueTask.FromResult(new TableCacheRoutingDecision(
                IsCached: true,
                CanSatisfyFully: false,
                InternalStorageTarget: null,
                Metadata: metadata,
                FallbackReason: $"Table cache is unhealthy: {metadata.LastError ?? "fenced pending resync"}"));
        }

        // Column coverage check
        if (requestedColumns != null && requestedColumns.Count > 0 && metadata.CachedColumns != null && metadata.CachedColumns.Count > 0)
        {
            var missing = requestedColumns
                .Where(rc => !metadata.CachedColumns.Contains(rc, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (missing.Count > 0)
            {
                return ValueTask.FromResult(new TableCacheRoutingDecision(
                    IsCached: true,
                    CanSatisfyFully: false,
                    InternalStorageTarget: null,
                    Metadata: metadata,
                    MissingColumns: missing,
                    FallbackReason: $"Cached table lacks requested columns: {string.Join(", ", missing)}"));
            }
        }

        // Row filter subsumption check
        if (!string.IsNullOrWhiteSpace(metadata.FilterPredicate))
        {
            if (string.IsNullOrWhiteSpace(whereClause) ||
                !whereClause.Contains(metadata.FilterPredicate, StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(new TableCacheRoutingDecision(
                    IsCached: true,
                    CanSatisfyFully: false,
                    InternalStorageTarget: null,
                    Metadata: metadata,
                    FallbackReason: $"Query WHERE clause does not subsume cache filter predicate: '{metadata.FilterPredicate}'"));
            }
        }

        return ValueTask.FromResult(new TableCacheRoutingDecision(
            IsCached: true,
            CanSatisfyFully: true,
            InternalStorageTarget: metadata.ActiveViewName,
            Metadata: metadata));
    }
}
