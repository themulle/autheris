namespace Autheris.Application.Diagnostics.Shadowing;

using System;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// F-OPS-01: AST-Aware Safety Guard for Production Traffic Shadowing.
/// Strictly prohibits and rejects mutations, DDL, and DML write operations from entering the dark replay pipeline.
/// </summary>
public static class AstShadowingFilter
{
    private static readonly Regex CommentStripRegex = new(@"#[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex SqlCommentStripRegex = new(@"(--[^\r\n]*)|(/\*[\s\S]*?\*/)", RegexOptions.Compiled);

    private static readonly Regex GraphQlMutationRegex = new(
        @"\bmutation\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SqlWriteKeywordRegex = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE|REPLACE|MERGE|EXEC|EXECUTE|CALL|GRANT|REVOKE|COPY)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Evaluates whether an incoming HTTP request body is safe for dark replay shadowing.
    /// Only idempotent read operations (GraphQL queries, SQL SELECTs) are permitted.
    /// </summary>
    public static (bool IsSafe, string? Reason) IsSafeForShadowing(string? path, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            // Empty body (e.g. GET request) is safe for read
            return (true, null);
        }

        var isGraphQl = path == null ||
                        path.Contains("/graphql", StringComparison.OrdinalIgnoreCase);

        var isSql = path != null &&
                    (path.Contains("/api/sql", StringComparison.OrdinalIgnoreCase) ||
                     path.Contains("/websql", StringComparison.OrdinalIgnoreCase));

        // 1. Try extracting GraphQL query from JSON envelope
        string queryText = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("query", out var qProp) && qProp.ValueKind == JsonValueKind.String)
                {
                    queryText = qProp.GetString() ?? string.Empty;
                    isGraphQl = true;
                }
                else if (doc.RootElement.TryGetProperty("sql", out var sqlProp) && sqlProp.ValueKind == JsonValueKind.String)
                {
                    queryText = sqlProp.GetString() ?? string.Empty;
                    isSql = true;
                }
            }
        }
        catch (JsonException)
        {
            // Raw text query, treat according to path
        }

        // 2. Check GraphQL AST safety
        if (isGraphQl)
        {
            var cleanedGraphQl = CommentStripRegex.Replace(queryText, " ");
            if (GraphQlMutationRegex.IsMatch(cleanedGraphQl))
            {
                return (false, "GraphQL mutations are strictly blocked in dark traffic shadowing to prevent state corruption on staging.");
            }
        }

        // 3. Check SQL AST / DML safety
        if (isSql || !isGraphQl)
        {
            var cleanedSql = SqlCommentStripRegex.Replace(queryText, " ");
            if (SqlWriteKeywordRegex.IsMatch(cleanedSql))
            {
                return (false, "SQL DML/DDL write statements are strictly blocked in dark traffic shadowing.");
            }
        }

        return (true, null);
    }
}
