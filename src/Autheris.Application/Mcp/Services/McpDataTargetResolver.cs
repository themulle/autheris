namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using TrinoSqlEngine;

/// <summary>
/// SR-H-01: Resolves every table a row-reading MCP tool call (<c>query_sql</c>, <c>invoke_api</c>) really touches, so that
/// ABAC, the inactive-table check and the Four-Eyes gate run on the same tables the governed SQL / data path will read.
/// SQL is analysed with the real SQL parser (joins, comma joins, subqueries, set operations, CTEs, comments and string
/// literals are handled by the grammar, not by a regex). Any input that cannot be resolved yields <c>null</c> (fail-closed).
/// </summary>
internal static class McpDataTargetResolver
{
    private const string DataApiPrefix = "/api/v1/data";
    private const string StatementEndpoint = "/api/v1/statement";

    /// <summary>The <c>SyntaxError</c> value of <see cref="TryResolveSqlTables"/> that denotes an unsupported construct (not a parse error).</summary>
    internal const string ConstructNotSupportedMarker = TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.ConstructNotSupported;

    /// <summary>Stand-in for a tool call that reads no table (for example <c>SELECT 1</c>); still checked by ABAC.</summary>
    internal static readonly TableIdentifier SqlNoTable = new("governance", "sql", "query");

    /// <summary>Stand-in for <c>invoke_api</c> calls on catalog / self-service endpoints that read no dataset rows.</summary>
    internal static readonly TableIdentifier ApiNoTable = new("governance", "api", "invoke");

    internal static (IReadOnlyList<TableIdentifier>? Tables, string? SyntaxError) TryResolveSqlTables(string? sql, ISqlEngine? sqlEngine, string? defaultDataSourceName)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return (null, "SQL query cannot be empty.");
        }

        try
        {
            var engine = sqlEngine ?? FastSqlEngine.Default;
            var metadata = engine.Analyze(sql.AsMemory());

            // SEC-277-04: a keyword-less self-reference would be resolved as a table below (existence-dependent ABAC answer). It is refused
            // from the analysis alone, with a fixed, catalog-independent marker the guardrail turns into SQL_CONSTRUCT_NOT_SUPPORTED.
            if (metadata.HasNonRecursiveSelfReference)
            {
                return (null, ConstructNotSupportedMarker);
            }

            if (metadata.ReferencedTables.Count == 0)
            {
                return ([SqlNoTable], null);
            }

            var tables = new List<TableIdentifier>(metadata.ReferencedTables.Count);
            foreach (var target in metadata.ReferencedTables)
            {
                var id = SqlDataSourceResolver.ResolveTableIdentifier(target, defaultDataSourceName);
                if (!tables.Contains(id))
                {
                    tables.Add(id);
                }
            }

            return (tables, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>All tables of a SQL statement; null when the statement cannot be parsed and analysed.</summary>
    internal static IReadOnlyList<TableIdentifier>? ResolveSqlTables(string? sql, ISqlEngine? sqlEngine, string? defaultDataSourceName) =>
        TryResolveSqlTables(sql, sqlEngine, defaultDataSourceName).Tables;

    /// <summary>
    /// Tables of an <c>invoke_api</c> call: the dataset of a <c>/api/v1/data</c> call (path and the <c>dataset</c> parameter the
    /// dispatcher prefers) or every table of the SQL sent to <c>/api/v1/statement</c>. Other endpoints read no dataset rows.
    /// </summary>
    internal static IReadOnlyList<TableIdentifier>? ResolveInvokeApiTables(string? argumentsJson, ISqlEngine? sqlEngine, string? defaultDataSourceName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Same normalisation as ApiDispatcherService.InvokeApiAsync.
            var endpoint = root.TryGetProperty("endpoint", out var ep) && ep.ValueKind == JsonValueKind.String
                ? ep.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                : string.Empty;

            var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("parameters", out var pProp) && pProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in pProp.EnumerateObject())
                {
                    parameters[prop.Name] = prop.Value.ToString();
                }
            }

            if (endpoint.StartsWith(DataApiPrefix, StringComparison.Ordinal))
            {
                var effective = ApiDispatcherService.ExtractTableIdentifier(endpoint, parameters);
                if (effective == null)
                {
                    return null;
                }

                var tables = new List<TableIdentifier> { effective.Value };
                var fromPath = ApiDispatcherService.ExtractTableIdentifier(endpoint, new Dictionary<string, object?>());
                if (fromPath != null && !tables.Contains(fromPath.Value))
                {
                    tables.Add(fromPath.Value);
                }

                return tables;
            }

            if (endpoint.Equals(StatementEndpoint, StringComparison.Ordinal))
            {
                object? body = root.TryGetProperty("body", out var bProp) ? bProp : null;
                return ResolveSqlTables(ApiDispatcherService.ExtractStatementSql(parameters, body), sqlEngine, defaultDataSourceName);
            }

            return [ApiNoTable];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
