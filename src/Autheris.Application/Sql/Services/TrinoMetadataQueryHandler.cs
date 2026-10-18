namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Handles Trino wire-protocol metadata introspection queries (<c>SHOW CATALOGS</c>, <c>SHOW SCHEMAS</c>,
/// <c>SHOW TABLES</c>, and <c>information_schema.schemata</c> / <c>tables</c> / <c>columns</c>) enforced
/// strictly through the caller's governed catalog visibility.
/// </summary>
public static class TrinoMetadataQueryHandler
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex ShowCatalogsRegex = new(
        @"^SHOW\s+CATALOGS(?:\s+LIKE\s+'([^']*)')?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex ShowSchemasRegex = new(
        @"^SHOW\s+SCHEMAS(?:\s+FROM\s+([A-Za-z0-9_""]+))?(?:\s+LIKE\s+'([^']*)')?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex ShowTablesRegex = new(
        @"^SHOW\s+TABLES(?:\s+FROM\s+([A-Za-z0-9_.""]+))?(?:\s+LIKE\s+'([^']*)')?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex InfoSchemaRegex = new(
        @"^SELECT\s+.+?\s+FROM\s+(?:[A-Za-z0-9_""]+\.)?information_schema\.(columns|tables|schemata)\b(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex WhereEqualityRegex = new(
        @"\b(table_catalog|table_schema|table_name|catalog_name|schema_name)\s*=\s*'([^']*)'",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    public static Task<GovernedSqlResult?> TryExecuteAsync(
        string? rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        ITableMetadataRepository? metadataRepository,
        IConsentRepository? consentRepository = null,
        CancellationToken ct = default)
        => TryExecuteAsync(rawSql, user, tenantId, metadataRepository, consentRepository, gatewayOptions: null, ct);

    /// <summary>Same as above with the deployment options (tenancy mode and shared tables) for the catalog visibility rule.</summary>
    public static async Task<GovernedSqlResult?> TryExecuteAsync(
        string? rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        ITableMetadataRepository? metadataRepository,
        IConsentRepository? consentRepository,
        GatewayOptions? gatewayOptions,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawSql) || metadataRepository == null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(user);

        var normalized = NormalizeSql(rawSql);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var showCatalogsMatch = ShowCatalogsRegex.Match(normalized);
        var showSchemasMatch = !showCatalogsMatch.Success ? ShowSchemasRegex.Match(normalized) : Match.Empty;
        var showTablesMatch = !showCatalogsMatch.Success && !showSchemasMatch.Success ? ShowTablesRegex.Match(normalized) : Match.Empty;
        var infoSchemaMatch = !showCatalogsMatch.Success && !showSchemasMatch.Success && !showTablesMatch.Success
            ? InfoSchemaRegex.Match(normalized)
            : Match.Empty;

        if (!showCatalogsMatch.Success && !showSchemasMatch.Success && !showTablesMatch.Success && !infoSchemaMatch.Success)
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        var allTables = await metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var activeTables = allTables.Where(t => t.Table.IsActive).ToList();
        var visibleTables = await CatalogVisibility.VisibleTablesAsync(activeTables, user, tenantId, consentRepository, contract: null, gatewayOptions?.Tenancy.EffectiveMode ?? TenancyMode.Multi, gatewayOptions?.Catalog.SharedTables, ct).ConfigureAwait(false);

        if (showCatalogsMatch.Success)
        {
            var likePattern = showCatalogsMatch.Groups[1].Success ? showCatalogsMatch.Groups[1].Value : null;
            var catalogs = visibleTables
                .Select(t => t.Identifier.Domain)
                .Append("system")
                .Where(d => !string.IsNullOrWhiteSpace(d) && MatchesLike(d, likePattern))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = catalogs
                .Select(c => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Catalog"] = c })
                .ToList();

            return new GovernedSqlResult(rawSql, ["Catalog"], rows, rows.Count, sw.ElapsedMilliseconds);
        }

        if (showSchemasMatch.Success)
        {
            var catalogFilter = showSchemasMatch.Groups[1].Success ? Unquote(showSchemasMatch.Groups[1].Value) : null;
            var likePattern = showSchemasMatch.Groups[2].Success ? showSchemasMatch.Groups[2].Value : null;

            var filteredTables = string.IsNullOrWhiteSpace(catalogFilter)
                ? visibleTables
                : visibleTables.Where(t => string.Equals(t.Identifier.Domain, catalogFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            var schemas = filteredTables
                .Select(t => t.Identifier.Schema)
                .Append("information_schema")
                .Where(s => !string.IsNullOrWhiteSpace(s) && MatchesLike(s, likePattern))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = schemas
                .Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Schema"] = s })
                .ToList();

            return new GovernedSqlResult(rawSql, ["Schema"], rows, rows.Count, sw.ElapsedMilliseconds);
        }

        if (showTablesMatch.Success)
        {
            var fromClause = showTablesMatch.Groups[1].Success ? showTablesMatch.Groups[1].Value : null;
            var likePattern = showTablesMatch.Groups[2].Success ? showTablesMatch.Groups[2].Value : null;

            string? catalogFilter = null;
            string? schemaFilter = null;
            if (!string.IsNullOrWhiteSpace(fromClause))
            {
                var parts = fromClause.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(Unquote).ToArray();
                if (parts.Length >= 2)
                {
                    catalogFilter = parts[0];
                    schemaFilter = parts[1];
                }
                else if (parts.Length == 1)
                {
                    schemaFilter = parts[0];
                }
            }

            var tables = visibleTables
                .Where(t => string.IsNullOrWhiteSpace(catalogFilter) || string.Equals(t.Identifier.Domain, catalogFilter, StringComparison.OrdinalIgnoreCase))
                .Where(t => string.IsNullOrWhiteSpace(schemaFilter) || string.Equals(t.Identifier.Schema, schemaFilter, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Identifier.TableName)
                .Where(n => !string.IsNullOrWhiteSpace(n) && MatchesLike(n, likePattern))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = tables
                .Select(t => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Table"] = t })
                .ToList();

            return new GovernedSqlResult(rawSql, ["Table"], rows, rows.Count, sw.ElapsedMilliseconds);
        }

        var viewName = infoSchemaMatch.Groups[1].Value.ToLowerInvariant();
        var trailingSql = infoSchemaMatch.Groups[2].Success ? infoSchemaMatch.Groups[2].Value : string.Empty;
        var filters = ParseWhereFilters(trailingSql);

        if (viewName == "schemata")
        {
            var pairs = visibleTables
                .Select(t => (Catalog: t.Identifier.Domain, Schema: t.Identifier.Schema))
                .Concat(visibleTables.Select(t => (Catalog: t.Identifier.Domain, Schema: "information_schema")))
                .Distinct()
                .Where(p => MatchesFilter(filters, "catalog_name", p.Catalog) && MatchesFilter(filters, "schema_name", p.Schema))
                .OrderBy(p => p.Catalog, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Schema, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = pairs
                .Select(p => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["catalog_name"] = p.Catalog,
                    ["schema_name"] = p.Schema
                })
                .ToList();

            return new GovernedSqlResult(rawSql, ["catalog_name", "schema_name"], rows, rows.Count, sw.ElapsedMilliseconds);
        }

        if (viewName == "tables")
        {
            var matchedTables = visibleTables
                .Where(t => MatchesFilter(filters, "table_catalog", t.Identifier.Domain) &&
                            MatchesFilter(filters, "table_schema", t.Identifier.Schema) &&
                            MatchesFilter(filters, "table_name", t.Identifier.TableName))
                .OrderBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = matchedTables
                .Select(t => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = t.Identifier.Domain,
                    ["table_schema"] = t.Identifier.Schema,
                    ["table_name"] = t.Identifier.TableName,
                    // SEC-112-23: the same helper as the visibility engine; never a schema string alone.
                    ["table_type"] = Autheris.Application.Dbt.Services.SemanticVirtualTables.IsVirtualTable(t) ? "VIEW" : "BASE TABLE"
                })
                .ToList();

            return new GovernedSqlResult(rawSql, ["table_catalog", "table_schema", "table_name", "table_type"], rows, rows.Count, sw.ElapsedMilliseconds);
        }

        // information_schema.columns
        var colRows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var table in visibleTables
            .Where(t => MatchesFilter(filters, "table_catalog", t.Identifier.Domain) &&
                        MatchesFilter(filters, "table_schema", t.Identifier.Schema) &&
                        MatchesFilter(filters, "table_name", t.Identifier.TableName))
            .OrderBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Identifier.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Identifier.TableName, StringComparer.OrdinalIgnoreCase))
        {
            for (int i = 0; i < table.Columns.Count; i++)
            {
                var col = table.Columns[i];
                colRows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = table.Identifier.Domain,
                    ["table_schema"] = table.Identifier.Schema,
                    ["table_name"] = table.Identifier.TableName,
                    ["column_name"] = col.ColumnName,
                    ["ordinal_position"] = i + 1,
                    ["is_nullable"] = "YES",
                    ["data_type"] = string.IsNullOrWhiteSpace(col.DataType) ? "varchar" : col.DataType
                });
            }
        }

        string[] colNames = ["table_catalog", "table_schema", "table_name", "column_name", "ordinal_position", "is_nullable", "data_type"];
        return new GovernedSqlResult(rawSql, colNames, colRows, colRows.Count, sw.ElapsedMilliseconds);
    }

    private static string NormalizeSql(string sql)
    {
        var lines = sql.Split('\n')
            .Select(line =>
            {
                var idx = line.IndexOf("--", StringComparison.Ordinal);
                return idx >= 0 ? line[..idx] : line;
            });
        return string.Join(' ', lines).Trim().TrimEnd(';').Trim();
    }

    private static string Unquote(string identifier) =>
        identifier.Trim().Trim('"');

    private static bool MatchesLike(string value, string? likePattern)
    {
        if (string.IsNullOrEmpty(likePattern))
        {
            return true;
        }

        var regexPattern = "^" + Regex.Escape(likePattern).Replace("%", ".*").Replace("_", ".") + "$";
        return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase, RegexTimeout);
    }

    private static Dictionary<string, string> ParseWhereFilters(string trailingSql)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(trailingSql))
        {
            return dict;
        }

        foreach (Match m in WhereEqualityRegex.Matches(trailingSql))
        {
            dict[m.Groups[1].Value] = m.Groups[2].Value;
        }

        return dict;
    }

    private static bool MatchesFilter(IReadOnlyDictionary<string, string> filters, string key, string candidate) =>
        !filters.TryGetValue(key, out var expected) || string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase);
}
