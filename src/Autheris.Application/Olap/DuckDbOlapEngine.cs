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
    private readonly SemaphoreSlim _concurrencySemaphore;

    public DuckDbOlapEngine(
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<DuckDbOlapEngine>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(gatewayOptions);
        _options = gatewayOptions.Value.DuckDbOlap ?? new DuckDbOlapOptions();
        _logger = logger;
        int maxConcurrent = Math.Clamp(_options.MaxConcurrentQueries > 0 ? _options.MaxConcurrentQueries : 4, 1, 64);
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public DuckDbOlapEngine(
        DuckDbOlapOptions options,
        ILogger<DuckDbOlapEngine>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
        int maxConcurrent = Math.Clamp(_options.MaxConcurrentQueries > 0 ? _options.MaxConcurrentQueries : 4, 1, 64);
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public async Task<OlapQueryResult> ExecuteOlapQueryAsync(
        OlapQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sql);

        var sw = Stopwatch.StartNew();

        // 1. Early validation of SQL before any staging or connection allocation
        ValidateUserSql(request.Sql, request.Sources);

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

        // SG-02: Concurrency throttle to prevent system-wide memory exhaustion
        var waitTimeout = TimeSpan.FromSeconds(Math.Min(10, _options.QueryTimeoutSeconds > 0 ? _options.QueryTimeoutSeconds : 10));
        if (!await _concurrencySemaphore.WaitAsync(waitTimeout, cts.Token).ConfigureAwait(false))
        {
            throw new System.Security.SecurityException("OLAP engine is at maximum concurrent query capacity. Please retry later.");
        }

        // SG-01: Session-isolated temporary directory to prevent cross-session spill leakage
        var sessionTempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "autheris_olap_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(sessionTempDir);

        try
        {
            // SEC-OLAP-03: Completely transient in-memory database session
            using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync(cts.Token).ConfigureAwait(false);

            // SEC-OLAP-01 & SEC-OLAP-02 & SG-01: Sandbox isolation, session temp dir, disable external filesystem/network access, limit RAM & threads
            using (var setupCmd = connection.CreateCommand())
            {
                var maxMem = SanitizeMemoryString(_options.MaxMemory);
                var maxTempSize = SanitizeMemoryString(_options.MaxTempDirectorySize);
                int threads = Math.Clamp(_options.MaxThreads, 1, 8);
                var normalizedTempDir = sessionTempDir.Replace('\\', '/').Replace("'", "''");

                setupCmd.CommandText = $"SET temp_directory = '{normalizedTempDir}'; PRAGMA max_temp_directory_size = '{maxTempSize}'; SET enable_external_access = false; PRAGMA max_memory = '{maxMem}'; PRAGMA threads = {threads}; SET lock_configuration = true;";
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

            var columns = new List<string>();
            var rows = new List<IReadOnlyList<object?>>();

            using (var queryCmd = connection.CreateCommand())
            {
                queryCmd.CommandText = request.Sql;

                // SQL2-9: DuckDB.NET checks the token only before it starts; Cancel() calls duckdb_interrupt and stops a
                // running query when the timeout fires or the caller aborts.
                using var interrupt = cts.Token.Register(static state => ((System.Data.Common.DbCommand)state!).Cancel(), queryCmd);
                using var reader = await queryCmd.ExecuteReaderAsync(cts.Token).ConfigureAwait(false);

                int fieldCount = reader.FieldCount;
                for (int i = 0; i < fieldCount; i++)
                {
                    columns.Add(reader.GetName(i));
                }

                // Gateway:DuckDbOlap:MaxResultRows (default 50 000) bounds every OLAP result.
                int maxResultRows = _options.MaxResultRows > 0 ? _options.MaxResultRows : 50000;
                int rowLimit = request.Limit.HasValue && request.Limit.Value > 0
                    ? Math.Min(request.Limit.Value, maxResultRows)
                    : maxResultRows;

                long maxResultBytes = _options.MaxResultBytes > 0 ? _options.MaxResultBytes : 32 * 1024 * 1024;
                long currentResultBytes = 0;

                while (await reader.ReadAsync(cts.Token).ConfigureAwait(false))
                {
                    if (rows.Count >= rowLimit)
                    {
                        break;
                    }

                    var row = new object?[fieldCount];
                    for (int i = 0; i < fieldCount; i++)
                    {
                        if (reader.IsDBNull(i))
                        {
                            row[i] = null;
                            currentResultBytes += 4;
                        }
                        else
                        {
                            var val = reader.GetValue(i);
                            row[i] = val;
                            if (val is string str)
                            {
                                currentResultBytes += (long)str.Length * sizeof(char);
                            }
                            else if (val is byte[] bytes)
                            {
                                currentResultBytes += bytes.Length;
                            }
                            else
                            {
                                currentResultBytes += 16;
                            }
                        }
                    }

                    if (currentResultBytes > maxResultBytes)
                    {
                        throw new System.Security.SecurityException(
                            $"OLAP query result exceeded maximum allowed memory size of {maxResultBytes} bytes.");
                    }

                    rows.Add(row);
                }
            }

            sw.Stop();
            return new OlapQueryResult(columns, rows, rows.Count, sw.Elapsed);
        }
        finally
        {
            _concurrencySemaphore.Release();
            try
            {
                if (System.IO.Directory.Exists(sessionTempDir))
                {
                    System.IO.Directory.Delete(sessionTempDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete temporary OLAP spill directory {Path}", sessionTempDir);
            }
        }
    }

    private static async Task StageTableAsync(
        DuckDBConnection connection,
        OlapTableSource source,
        CancellationToken ct)
    {
        var cleanTableName = SanitizeIdentifier(!string.IsNullOrWhiteSpace(source.StagingTableName) ? source.StagingTableName : source.Table.TableName);
        var columns = source.Metadata?.Columns != null && source.Metadata.Columns.Count > 0
            ? source.Metadata.Columns
            : InferColumns(source.GovernedRows);

        if (columns.Count == 0)
        {
            // Default dummy column if no columns known
            columns = [new TableColumn { ColumnName = "id", DataType = "int" }];
        }

        var colDefs = columns.Select(c =>
        {
            bool isMasked = source.MaskedColumns != null && source.MaskedColumns.Contains(c.ColumnName);
            var duckType = isMasked ? "VARCHAR" : MapDuckDbType(c);
            return $"\"{SanitizeIdentifier(c.ColumnName)}\" {duckType}";
        });
        var createTableSql = $"CREATE TABLE \"{cleanTableName}\" ({string.Join(", ", colDefs)});";

        using (var createCmd = connection.CreateCommand())
        {
            createCmd.CommandText = createTableSql;
            await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Also create domain-prefixed view if distinct and not using an explicit staging table name
            if (string.IsNullOrWhiteSpace(source.StagingTableName) && !string.IsNullOrWhiteSpace(source.Table.Domain))
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

        // SG-24: Prohibit dollar quoting ($$...$$) and dollar variables to eliminate lexer and quote bypasses
        if (sql.Contains('$'))
        {
            throw new System.Security.SecurityException("Dollar-quoted strings and parameters are prohibited in OLAP queries.");
        }

        var stripped = StripSqlComments(sql);
        var trimmed = stripped.Trim();

        // 1. Single statement check: reject semicolons outside quotes
        bool inQuotes = false;
        char quoteChar = '\0';
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '\'' || c == '"') // DuckDB: backslash is no escape; doubled quotes toggle twice
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
            "PRAGMA", "SET", "CALL", "CHECKPOINT",
            // SQL2-9: recursive CTEs generate unbounded rows
            "RECURSIVE"
        };

        var tokens = ExtractTokens(trimmed);
        foreach (var token in tokens)
        {
            if (dangerousKeywords.Contains(token))
            {
                throw new System.Security.SecurityException($"Cannot execute query: configuration is locked and command or keyword '{token}' is prohibited in OLAP queries.");
            }

            // SG-01: Prohibit file and internal system functions
            if (token.Equals("glob", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("read_", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("sniff_", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("parquet_", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("duckdb_", StringComparison.OrdinalIgnoreCase))
            {
                throw new System.Security.SecurityException($"Function or table function '{token}' is prohibited in OLAP queries.");
            }
        }

        // 4. SQL2-9 & SG-01: unbounded generator and file reading functions are prohibited
        var match = DisallowedTableFunctionsRegex.Match(trimmed);
        if (match.Success)
        {
            throw new System.Security.SecurityException($"Table generator or file function '{match.Groups[1].Value}' is not permitted in OLAP queries.");
        }
    }

    // Function name followed by optional whitespace and '(' (e.g. "range (1, 1000000000)").
    private static readonly System.Text.RegularExpressions.Regex DisallowedTableFunctionsRegex = new(
        @"\b(range|generate_series|repeat|read_\w+|glob|sniff_\w+|parquet_\w+|duckdb_\w+)\s*\(",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

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
            if (c == '\'' || c == '"') // DuckDB: backslash is no escape; doubled quotes toggle twice
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

    private static string StripSqlComments(string sql)
    {
        var sb = new System.Text.StringBuilder(sql.Length);
        bool inQuotes = false;
        char quoteChar = '\0';
        int i = 0;

        while (i < sql.Length)
        {
            char c = sql[i];
            if (c == '\'' || c == '"')
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
                sb.Append(c);
                i++;
            }
            else if (!inQuotes && c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                // Line comment: skip until newline or end
                i += 2;
                while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r')
                {
                    i++;
                }
                sb.Append(' ');
            }
            else if (!inQuotes && c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                // Block comment: skip until */ or end
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    i++;
                }
                if (i + 1 < sql.Length)
                {
                    i += 2; // skip */
                }
                else
                {
                    i = sql.Length;
                }
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        _concurrencySemaphore.Dispose();
    }
}

