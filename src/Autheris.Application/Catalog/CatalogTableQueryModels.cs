namespace Autheris.Application.Catalog;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Internal unfiltered catalog table page query for Application and Infrastructure callers.
/// Never map query results directly to endpoint DTOs; callers must enforce tenant, visibility, masking,
/// and consent filtering before paging, sorting, or building user-facing responses.
/// </summary>
/// <param name="DataSource">Optional case-insensitive exact filter on the table domain/source name.</param>
/// <param name="Schema">Optional case-insensitive exact filter on the table schema name.</param>
/// <param name="NamePrefix">Optional case-insensitive table name prefix filter with LIKE wildcards escaped safely.</param>
/// <param name="After">Optional strict keyset continuation token from a previous <see cref="CatalogTablePage.NextAfter"/>.</param>
/// <param name="Limit">Requested page size, clamped server-side to <c>1..500</c>.</param>
/// <param name="IncludeInactive">Defaults to <see langword="false"/> so only active tables are returned unless an admin path opts in.</param>
public sealed record CatalogTableQuery(
    string? DataSource = null,
    string? Schema = null,
    string? NamePrefix = null,
    string? After = null,
    int Limit = CatalogTableQuery.DefaultLimit,
    bool IncludeInactive = false)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;
    public const int MaxRequestIdentifiers = 500;
    public const int MaxFilterLength = 256;

    /// <summary>Gets the server-clamped page size in the inclusive range <c>1..500</c>.</summary>
    public int EffectiveLimit => Math.Clamp(Limit, 1, MaxLimit);

    /// <summary>
    /// Enforces the per-request cap of <see cref="MaxRequestIdentifiers"/> for identifier lists derived from external request input.
    /// </summary>
    public static void ValidateRequestIdentifiers(IReadOnlyCollection<TableIdentifier> identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        if (identifiers.Count > MaxRequestIdentifiers)
        {
            throw new ArgumentException(
                $"Identifier list cannot exceed {MaxRequestIdentifiers} items per request.",
                nameof(identifiers));
        }
    }

    /// <summary>
    /// Validates and normalizes filter strings and the optional <see cref="After"/> keyset cursor.
    /// </summary>
    public void EnsureValid(out string? normalizedDataSource, out string? normalizedSchema, out string? validatedNamePrefix, out TableIdentifier? afterIdentifier)
    {
        normalizedDataSource = ValidateOptionalFilter(DataSource, nameof(DataSource), trim: true);
        normalizedSchema = ValidateOptionalFilter(Schema, nameof(Schema), trim: true);
        validatedNamePrefix = ValidateOptionalFilter(NamePrefix, nameof(NamePrefix), trim: false);
        afterIdentifier = After is not null ? CatalogTableCursor.DecodeOrThrow(After) : null;
    }

    private static string? ValidateOptionalFilter(string? value, string paramName, bool trim)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = trim ? value.Trim() : value;
        if (normalized.Length > MaxFilterLength)
        {
            throw new ArgumentException(
                $"Catalog table query filter '{paramName}' exceeds maximum length of {MaxFilterLength} characters.",
                paramName);
        }

        if (normalized.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
        {
            throw new ArgumentException(
                $"Catalog table query filter '{paramName}' contains unsupported control characters.",
                paramName);
        }

        return normalized;
    }
}

/// <summary>Keyset page of raw unfiltered catalog table metadata.</summary>
/// <param name="Items">Unfiltered table metadata items ordered deterministically by <c>(domain, schema, table_name)</c>.</param>
/// <param name="NextAfter">Opaque continuation cursor when additional database rows exist, or <see langword="null"/> at end of scan.</param>
public sealed record CatalogTablePage(
    IReadOnlyList<TableMetadata> Items,
    string? NextAfter)
{
    public bool HasMore => NextAfter is not null;
}

/// <summary>Strict keyset cursor codec for catalog table paging.</summary>
public static class CatalogTableCursor
{
    public static string Encode(TableIdentifier identifier) => PageCursor.Encode(identifier.ToString());

    public static TableIdentifier DecodeOrThrow(string? cursor)
    {
        if (!PageCursor.TryDecode(cursor, out var raw))
        {
            throw new ArgumentException("Malformed catalog table keyset cursor.", nameof(cursor));
        }

        var parts = raw.Split('.');
        if (parts.Length != 3 ||
            !TableIdentifier.TryCreate(parts[0], parts[1], parts[2], out var identifier) ||
            !string.Equals(raw, identifier.ToString(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Malformed catalog table keyset cursor.", nameof(cursor));
        }

        return identifier;
    }

    public static int Compare(TableIdentifier left, TableIdentifier right)
    {
        var d = StringComparer.OrdinalIgnoreCase.Compare(left.Domain, right.Domain);
        if (d != 0)
        {
            return d;
        }

        var s = StringComparer.OrdinalIgnoreCase.Compare(left.Schema, right.Schema);
        return s != 0 ? s : StringComparer.OrdinalIgnoreCase.Compare(left.TableName, right.TableName);
    }
}

/// <summary>
/// Bounded keyset page helpers over <see cref="ITableMetadataRepository.QueryTablesAsync"/>.
/// Every loop advances via <see cref="CatalogTablePage.NextAfter"/> and fails closed if pagination stalls
/// or exceeds the safety page bound.
/// </summary>
public static class CatalogTablePageReader
{
    public const int DefaultMaxPages = 1_000;

    /// <summary>Reads all pages matching <paramref name="baseQuery"/> up to <paramref name="maxPages"/>.</summary>
    public static async Task<IReadOnlyList<TableMetadata>> ReadAllAsync(
        ITableMetadataRepository repository,
        CatalogTableQuery baseQuery,
        Func<TableMetadata, bool>? predicate = null,
        int maxPages = DefaultMaxPages,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(baseQuery);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPages);

        var results = new List<TableMetadata>();
        await ScanPagesAsync(
            repository,
            baseQuery,
            maxPages,
            page =>
            {
                foreach (var item in page.Items)
                {
                    if (predicate is null || predicate(item))
                    {
                        results.Add(item);
                    }
                }

                return false;
            },
            ct).ConfigureAwait(false);

        return results;
    }

    /// <summary>Returns the first matching table across bounded keyset pages, or <see langword="null"/>.</summary>
    public static async Task<TableMetadata?> FindFirstAsync(
        ITableMetadataRepository repository,
        CatalogTableQuery baseQuery,
        Func<TableMetadata, bool> predicate,
        int maxPages = DefaultMaxPages,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(baseQuery);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPages);

        TableMetadata? found = null;
        await ScanPagesAsync(
            repository,
            baseQuery,
            maxPages,
            page =>
            {
                foreach (var item in page.Items)
                {
                    if (predicate(item))
                    {
                        found = item;
                        return true;
                    }
                }

                return false;
            },
            ct).ConfigureAwait(false);

        return found;
    }

    private static async Task ScanPagesAsync(
        ITableMetadataRepository repository,
        CatalogTableQuery baseQuery,
        int maxPages,
        Func<CatalogTablePage, bool> onPage,
        CancellationToken ct)
    {
        string? after = baseQuery.After;
        for (int pageIndex = 0; pageIndex < maxPages; pageIndex++)
        {
            var page = await repository.QueryTablesAsync(baseQuery with { After = after }, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Catalog repository returned a null page.");

            if (onPage(page) || !page.HasMore || string.IsNullOrEmpty(page.NextAfter))
            {
                return;
            }

            if (string.Equals(after, page.NextAfter, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Catalog table keyset pagination failed to advance continuation cursor.");
            }

            after = page.NextAfter;
        }

        throw new InvalidOperationException($"Catalog table scan exceeded maximum page bound of {maxPages}.");
    }
}
