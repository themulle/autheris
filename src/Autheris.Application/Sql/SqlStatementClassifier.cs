namespace Autheris.Application.Sql;

using System;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.State.Caching;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

public enum RequestStatementClass
{
    Read,
    Write,
    Other,
    Unparsable
}

/// <summary>
/// Single AST-based SQL statement classifier shared by request guards and SQL endpoint registration/execution.
/// Uses the exact normalized SQL text executed by the governed rewriter.
/// </summary>
public static class SqlStatementClassifier
{
    private static readonly BoundedCache<string, RequestStatementClass> EndpointClassCache = new(4_096);

    public static string NormalizeEndpointSql(string? rawSql) =>
        string.IsNullOrWhiteSpace(rawSql)
            ? string.Empty
            : SqlParameterExtractor.StripComments(rawSql).Trim().TrimEnd(';').Trim();

    public static RequestStatementClass Classify(string? sql, ILogger? logger = null, ISqlEngine? sqlEngine = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return RequestStatementClass.Unparsable;
        }

        try
        {
            var normalized = SqlParameterExtractor.NormalizeForAst(sql.Trim());
            var engine = sqlEngine ?? FastSqlEngine.Default;
            var (tree, _) = engine.Parse(normalized.AsMemory());
            if (tree.statement() is SqlBaseParser.MergeContext)
            {
                return RequestStatementClass.Write;
            }

            return new SqlQueryAnalyzer().Analyze(tree).StatementType switch
            {
                SqlStatementType.Select => RequestStatementClass.Read,
                SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete => RequestStatementClass.Write,
                _ => RequestStatementClass.Other
            };
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Statement classification failed; the statement is treated as unparsable.");
            return RequestStatementClass.Unparsable;
        }
    }

    public static RequestStatementClass ClassifyEndpointSql(string? rawSql, ILogger? logger = null, ISqlEngine? sqlEngine = null)
    {
        string normalized = NormalizeEndpointSql(rawSql);
        return Classify(normalized, logger, sqlEngine);
    }

    public static RequestStatementClass ClassifyEndpoint(SqlEndpointDefinition definition, ILogger? logger = null, ISqlEngine? sqlEngine = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        string cacheKey = BuildCacheKey(definition.Name, definition.RawSql);
        if (EndpointClassCache.TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        var classified = ClassifyEndpointSql(definition.RawSql, logger, sqlEngine);
        EndpointClassCache.Set(cacheKey, classified);
        return classified;
    }

    private static string BuildCacheKey(string name, string rawSql)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawSql ?? string.Empty)));
        return $"{name.ToUpperInvariant()}\0{hash}";
    }
}
