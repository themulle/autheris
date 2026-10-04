using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Olap;

public sealed class DuckDbOlapEngine : IDuckDbOlapEngine
{
    private readonly DuckDbOlapOptions _options;
    private readonly ILogger<DuckDbOlapEngine>? _logger;
    private static readonly Regex SafeIdentifierRegex = new(@"^[a-zA-Z0-9_]+$", RegexOptions.Compiled);

    public DuckDbOlapEngine(
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<DuckDbOlapEngine>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(gatewayOptions);
        _options = gatewayOptions.Value.DuckDbOlap ?? new DuckDbOlapOptions();
        _logger = logger;
    }

    public DuckDbOlapEngine(
        DuckDbOlapOptions options,
        ILogger<DuckDbOlapEngine>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
    }

    public async Task<OlapQueryResult> ExecuteOlapQueryAsync(
        OlapQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sql);

        var sw = Stopwatch.StartNew();

        // SEC-OLAP-02: Enforce MaxStagedRowsPerTable limits before opening DuckDB
        if (request.Sources != null)
        {
            foreach (var src in request.Sources)
            {
                if (src.GovernedRows.Count > _options.MaxStagedRowsPerTable)
                {
                    throw new InvalidOperationException(
                        $"MaxStagedRowsPerTable limit exceeded. Table '{src.Table}' has {src.GovernedRows.Count} rows, limit is {_options.MaxStagedRowsPerTable}.");
                }
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_options.QueryTimeoutSeconds > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(_options.QueryTimeoutSeconds));
        }

        // SEC-OLAP-03: Completely transient in-memory database session
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(cts.Token).ConfigureAwait(false);

        // SEC-OLAP-01 & SEC-OLAP-02: Sandbox isolation, disable external filesystem/network access, limit RAM & threads
        using (var setupCmd = connection.CreateCommand())
        {
            var maxMem = SanitizeMemoryString(_options.MaxMemory);
            int threads = Math.Clamp(_options.MaxThreads, 1, 8);
            setupCmd.CommandText = $"SET enable_external_access = false; PRAGMA max_memory = '{maxMem}'; PRAGMA threads = {threads}; SET lock_configuration = true;";
            await setupCmd.ExecuteNonQueryAsync(cts.Token).ConfigureAwait(false);
        }

        // Stage all authorized source tables into the transient DuckDB session
        if (request.Sources != null)
        {
            foreach (var source in request.Sources)
            {
                await StageTableAsync(connection, source, cts.Token).ConfigureAwait(false);
            }
        }

        // Execute analytical query
        ValidateUserSql(request.Sql, request.Sources);

        var columns = new List<string>();
        var rows = new List<IReadOnlyList<object?>>();

        using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = request.Sql;
            using var reader = await queryCmd.ExecuteReaderAsync(cts.Token).ConfigureAwait(false);

            int fieldCount = reader.FieldCount;
            for (int i = 0; i < fieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            const int HardMaxLimit = 50000;
            int rowLimit = request.Limit.HasValue && request.Limit.Value > 0
                ? Math.Min(request.Limit.Value, HardMaxLimit)
                : HardMaxLimit;

            while (await reader.ReadAsync(cts.Token).ConfigureAwait(false) && rows.Count < rowLimit)
            {
                var row = new object?[fieldCount];
                for (int i = 0; i < fieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }
        }

        sw.Stop();
        return new OlapQueryResult(columns, rows, rows.Count, sw.Elapsed);
    }

    private static async Task StageTableAsync(
        DuckDBConnection connection,
        OlapTableSource source,
        CancellationToken ct)
    {
        var cleanTableName = SanitizeIdentifier(source.Table.TableName);
        var columns = source.Metadata?.Columns != null && source.Metadata.Columns.Count > 0
            ? source.Metadata.Columns
            : InferColumns(source.GovernedRows);

        if (columns.Count == 0)
        {
            // Default dummy column if no columns known
            columns = [new TableColumn { ColumnName = "id", DataType = "int" }];
        }

        var colDefs = columns.Select(c => $"\"{SanitizeIdentifier(c.ColumnName)}\" {MapDuckDbType(c)}");
        var createTableSql = $"CREATE TABLE \"{cleanTableName}\" ({string.Join(", ", colDefs)});";

        using (var createCmd = connection.CreateCommand())
        {
            createCmd.CommandText = createTableSql;
            await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Also create domain-prefixed view if distinct (e.g. crm_customers -> customers)
            if (!string.IsNullOrWhiteSpace(source.Table.Domain))
            {
                var qualifiedViewName = SanitizeIdentifier($"{source.Table.Domain}_{source.Table.TableName}");
                if (!string.Equals(qualifiedViewName, cleanTableName, StringComparison.OrdinalIgnoreCase))
                {
                    using var viewCmd = connection.CreateCommand();
                    viewCmd.CommandText = $"CREATE VIEW \"{qualifiedViewName}\" AS SELECT * FROM \"{cleanTableName}\";";
                    await viewCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }

        if (source.GovernedRows.Count == 0)
        {
            return;
        }

        // Insert rows in batches
        const int batchSize = 250;
        var cleanColNames = columns.Select(c => SanitizeIdentifier(c.ColumnName)).ToList();

        for (int i = 0; i < source.GovernedRows.Count; i += batchSize)
        {
            var chunk = source.GovernedRows.Skip(i).Take(batchSize).ToList();
            var sqlBuilder = new StringBuilder();
            sqlBuilder.Append($"INSERT INTO \"{cleanTableName}\" ({string.Join(", ", cleanColNames.Select(c => $"\"{c}\""))}) VALUES ");

            using var insertCmd = connection.CreateCommand();
            int paramIndex = 1;

            for (int r = 0; r < chunk.Count; r++)
            {
                if (r > 0) sqlBuilder.Append(", ");
                sqlBuilder.Append('(');

                var row = chunk[r];
                for (int c = 0; c < columns.Count; c++)
                {
                    if (c > 0) sqlBuilder.Append(", ");
                    var colName = columns[c].ColumnName;
                    row.TryGetValue(colName, out var val);

                    sqlBuilder.Append($"$p{paramIndex}");

                    var p = insertCmd.CreateParameter();
                    p.ParameterName = $"p{paramIndex}";
                    p.Value = val ?? DBNull.Value;
                    insertCmd.Parameters.Add(p);

                    paramIndex++;
                }
                sqlBuilder.Append(')');
            }

            insertCmd.CommandText = sqlBuilder.ToString();
            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<TableColumn> InferColumns(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return Array.Empty<TableColumn>();
        var first = rows[0];
        return first.Keys.Select(k => new TableColumn { ColumnName = k, DataType = "string" }).ToList();
    }

    private static string SanitizeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return "col";
        var cleaned = Regex.Replace(identifier, @"[^a-zA-Z0-9_]", "_");
        if (char.IsDigit(cleaned[0])) cleaned = "_" + cleaned;
        return cleaned;
    }

    private static string SanitizeMemoryString(string? mem)
    {
        if (string.IsNullOrWhiteSpace(mem)) return "1GB";
        var match = Regex.Match(mem.Trim(), @"^(\d+)\s*(MB|GB|TB|B)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : "1GB";
    }

    private static string MapDuckDbType(TableColumn column)
    {
        var type = column.DataType?.ToLowerInvariant() ?? "";
        if (type.Contains("int") || type.Contains("short") || type.Contains("byte")) return "BIGINT";
        if (type.Contains("float") || type.Contains("double") || type.Contains("real")) return "DOUBLE";
        if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("money")) return "DECIMAL(38,18)";
        if (type.Contains("bool")) return "BOOLEAN";
        if (type.Contains("date") || type.Contains("time")) return "TIMESTAMP";
        if (type.Contains("binary") || type.Contains("byte[]")) return "BLOB";
        return "VARCHAR";
    }

    private static void ValidateUserSql(string sql, IReadOnlyList<OlapTableSource>? sources)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL query cannot be empty.", nameof(sql));
        }

        var trimmed = sql.Trim();

        // 1. Single statement check: reject semicolons outside quotes
        bool inQuotes = false;
        char quoteChar = '\0';
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if ((c == '\'' || c == '"') && (i == 0 || trimmed[i - 1] != '\\'))
            {
                if (!inQuotes)
                {
                    inQuotes = true;
                    quoteChar = c;
                }
                else if (c == quoteChar)
                {
                    inQuotes = false;
                }
            }
            else if (c == ';' && !inQuotes)
            {
                if (i < trimmed.Length - 1 && !string.IsNullOrWhiteSpace(trimmed[(i + 1)..]))
                {
                    throw new System.Security.SecurityException("Cannot execute query: configuration is locked and multi-statement queries are prohibited in OLAP sandbox.");
                }
            }
        }

        // 2. Statement type check: must start with SELECT or WITH
        var firstToken = GetFirstKeyword(trimmed);
        if (!string.Equals(firstToken, "SELECT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(firstToken, "WITH", StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.SecurityException($"Cannot execute query: configuration is locked and statement type '{firstToken}' is prohibited. Only SELECT queries are permitted.");
        }

        // 3. Prohibit dangerous keywords and configuration overrides
        var dangerousKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CREATE", "DROP", "ALTER", "INSERT", "UPDATE", "DELETE",
            "ATTACH", "DETACH", "COPY", "EXPORT", "IMPORT", "INSTALL", "LOAD",
            "PRAGMA", "SET"
        };

        var tokens = ExtractTokens(trimmed);
        foreach (var token in tokens)
        {
            if (dangerousKeywords.Contains(token))
            {
                throw new System.Security.SecurityException($"Cannot execute query: configuration is locked and command or keyword '{token}' is prohibited in OLAP queries.");
            }
        }

        // 4. Prohibit unbounded generator table functions if no sources were staged
        if (sources == null || sources.Count == 0)
        {
            var disallowedGenerators = new[] { "range(", "generate_series(", "repeat(" };
            foreach (var gen in disallowedGenerators)
            {
                if (trimmed.Contains(gen, StringComparison.OrdinalIgnoreCase))
                {
                    throw new System.Security.SecurityException($"Table generator function '{gen.TrimEnd('(')}' is not permitted without staged tables.");
                }
            }
        }
    }

    private static string GetFirstKeyword(string sql)
    {
        int i = 0;
        while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
        int start = i;
        while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
        return i > start ? sql[start..i] : string.Empty;
    }

    private static List<string> ExtractTokens(string sql)
    {
        var list = new List<string>();
        bool inQuotes = false;
        char quoteChar = '\0';
        int start = -1;

        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if ((c == '\'' || c == '"') && (i == 0 || sql[i - 1] != '\\'))
            {
                if (!inQuotes)
                {
                    inQuotes = true;
                    quoteChar = c;
                    if (start >= 0)
                    {
                        list.Add(sql[start..i]);
                        start = -1;
                    }
                }
                else if (c == quoteChar)
                {
                    inQuotes = false;
                }
            }
            else if (!inQuotes)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    if (start < 0) start = i;
                }
                else
                {
                    if (start >= 0)
                    {
                        list.Add(sql[start..i]);
                        start = -1;
                    }
                }
            }
        }

        if (start >= 0 && !inQuotes)
        {
            list.Add(sql[start..]);
        }

        return list;
    }
}

