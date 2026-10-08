namespace Autheris.Application.SqlEndpoints.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

public sealed class SqlEndpointLoader : IDisposable
{
    private static readonly Regex HeaderRegex = new(
        @"^\s*--\s*@([a-zA-Z0-9_]+)(?::|\s)\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex LeadingCommentRegex = new(
        @"^\s*--\s*(?!@)(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ParamHeaderRegex = new(
        @"\A([a-zA-Z0-9_]+)\s*:\s*([a-zA-Z0-9_]+)(\!)?(?:\s*=\s*(.+))?\z",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly ISqlEndpointRegistry _registry;
    private readonly ISqlEngine _sqlEngine;
    private readonly ILogger<SqlEndpointLoader>? _logger;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public SqlEndpointLoader(
        ISqlEndpointRegistry registry,
        ILogger<SqlEndpointLoader>? logger = null,
        ISqlEngine? sqlEngine = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger;
        _sqlEngine = sqlEngine ?? FastSqlEngine.Default;
    }

    /// <summary>
    /// Loads all .sql files from the specified directory and optionally starts hot-reload monitoring.
    /// </summary>
    public int LoadFromDirectory(string directoryPath, bool enableHotReload = true)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Directory path cannot be empty.", nameof(directoryPath));
        }

        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
            _logger?.LogInformation("Created SQL endpoints directory at '{DirectoryPath}'.", directoryPath);
            return 0;
        }

        var sqlFiles = Directory.GetFiles(directoryPath, "*.sql", SearchOption.AllDirectories);
        int loadedCount = 0;

        foreach (var file in sqlFiles)
        {
            try
            {
                var def = LoadFile(file);
                if (def != null)
                {
                    loadedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load SQL endpoint from file '{FilePath}'.", file);
            }
        }

        if (enableHotReload && _watcher == null)
        {
            SetupWatcher(directoryPath);
        }

        _logger?.LogInformation("Successfully loaded {LoadedCount} declarative SQL endpoints from '{DirectoryPath}'.", loadedCount, directoryPath);
        return loadedCount;
    }

    /// <summary>
    /// Parses a single .sql file into a SqlEndpointDefinition and registers it.
    /// </summary>
    public SqlEndpointDefinition? LoadFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            return null;
        }

        string content = File.ReadAllText(filePath);
        string defaultName = Path.GetFileNameWithoutExtension(filePath);

        var definition = ParseSqlContent(content, defaultName);
        if (definition != null)
        {
            _registry.Register(definition);
        }

        return definition;
    }

    /// <summary>
    /// Parses raw SQL content with annotations into a SqlEndpointDefinition.
    /// </summary>
    public SqlEndpointDefinition ParseSqlContent(string content, string defaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        string name = defaultName;
        string summary = string.Empty;
        string? dataSource = null;
        string httpMethod = "GET";
        int timeoutSeconds = 30;
        var headerParams = new Dictionary<string, (Type Type, bool IsRequired, object? DefaultVal, string? Desc)>(StringComparer.OrdinalIgnoreCase);

        var matches = HeaderRegex.Matches(content);
        foreach (Match match in matches)
        {
            string key = match.Groups[1].Value.ToLowerInvariant();
            string val = match.Groups[2].Value.Trim();

            switch (key)
            {
                case "name":
                    if (!string.IsNullOrWhiteSpace(val)) name = val;
                    break;
                case "summary" or "description":
                    summary = val;
                    break;
                case "datasource" or "data_source":
                    dataSource = val;
                    break;
                case "method" or "http_method":
                    httpMethod = val.ToUpperInvariant();
                    break;
                case "timeout" when int.TryParse(val, out int t):
                    timeoutSeconds = t;
                    break;
                case "param":
                    ParseParamHeader(val, headerParams);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            var commentMatches = LeadingCommentRegex.Matches(content);
            if (commentMatches.Count > 0)
            {
                var commentLines = new List<string>();
                foreach (Match cm in commentMatches)
                {
                    string line = cm.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        commentLines.Add(line);
                    }
                }
                if (commentLines.Count > 0)
                {
                    summary = string.Join(" ", commentLines);
                }
            }
        }

        // Extract and AST analyze
        var extractedTokens = SqlParameterExtractor.ExtractTokens(content);
        string normalizedSql = SqlParameterExtractor.NormalizeForAst(content);

        var (tree, _) = _sqlEngine.Parse(normalizedSql.AsMemory());
        var enrichedAstParams = SqlParameterExtractor.AnalyzeAstParameters(tree, extractedTokens);

        var analyzer = new SqlQueryAnalyzer();
        var metadata = analyzer.Analyze(tree);

        // Build parameters
        var finalParams = new List<SqlEndpointParameter>();
        var seenParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in enrichedAstParams)
        {
            if (seenParams.Add(p.Name))
            {
                Type paramType = typeof(string);
                bool isRequired = true;
                object? defaultVal = null;
                string? desc = null;

                if (headerParams.TryGetValue(p.Name, out var hp))
                {
                    paramType = hp.Type;
                    isRequired = hp.IsRequired;
                    defaultVal = hp.DefaultVal;
                    desc = hp.Desc;
                }

                finalParams.Add(new SqlEndpointParameter(
                    Name: p.Name,
                    Token: p.Token,
                    ClrType: paramType,
                    IsRequired: isRequired,
                    DefaultValue: defaultVal,
                    Description: desc,
                    TargetColumn: p.TargetColumn,
                    TargetTable: p.TargetTable,
                    ComparisonOperator: p.ComparisonOperator));
            }
        }

        // Build projections
        var projections = new List<SqlEndpointProjection>();
        foreach (var col in metadata.ProjectedColumns)
        {
            string cleanCol = col;
            int dotIdx = col.LastIndexOf('.');
            if (dotIdx >= 0 && dotIdx + 1 < col.Length)
            {
                cleanCol = col[(dotIdx + 1)..];
            }

            cleanCol = SqlIdentifierHelper.NormalizeIdentifier(cleanCol);

            projections.Add(new SqlEndpointProjection(
                ColumnName: cleanCol,
                Alias: cleanCol,
                InferredClrType: typeof(object)));
        }

        var referencedTableNames = metadata.ReferencedTables
            .Select(t => t.FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SqlEndpointDefinition(
            Name: name,
            Summary: summary,
            RawSql: content,
            DataSource: dataSource,
            Parameters: finalParams,
            Projections: projections,
            ReferencedTables: referencedTableNames,
            HttpMethod: httpMethod,
            TimeoutSeconds: timeoutSeconds);
    }

    /// <summary>
    /// SEC H-20: Builds the SELECT for a dbt model from validated, quoted identifiers and writes it via <see cref="SyncDbtModelToFile"/>.
    /// </summary>
    public string SyncDbtModelDefinitionToFile(
        string directoryPath,
        string name,
        string schema,
        IEnumerable<string>? columns,
        string? summary = null,
        string? dataSource = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        string quotedName = QuoteDbtIdentifier(name, nameof(name));
        string quotedSchema = QuoteDbtIdentifier(schema, nameof(schema));

        var quotedColumns = new List<string>();
        if (columns != null)
        {
            foreach (var column in columns)
            {
                quotedColumns.Add(QuoteDbtIdentifier(column, nameof(columns)));
            }
        }

        string projection = quotedColumns.Count > 0 ? string.Join(", ", quotedColumns) : "*";
        // No trailing ';': GovernedSqlExecutionService parses RawSql as a single statement (singleStatement: statement EOF).
        string sql = $"SELECT {projection}\nFROM {quotedSchema}.{quotedName}";

        return SyncDbtModelToFile(directoryPath, name, sql, summary, dataSource);
    }

    /// <summary>
    /// Writes a dbt model or query definition into the configured queries directory.
    /// Option B writes directly into the directory so Option A can hot-reload it.
    /// SEC H-20: strict name validation, directory containment, header sanitizing and no overwrite of non-dbt files.
    /// </summary>
    public string SyncDbtModelToFile(
        string directoryPath,
        string name,
        string sql,
        string? summary = null,
        string? dataSource = null,
        IReadOnlyList<SqlEndpointParameter>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        if (!DbtNameRegex.IsMatch(name))
        {
            throw new ArgumentException($"Invalid dbt endpoint name '{SanitizeHeaderValue(name)}'. Allowed: ^[A-Za-z0-9_]{{1,128}}$.", nameof(name));
        }

        // Directive injection: the SQL body must not carry its own '-- @...' header directives.
        if (HeaderRegex.IsMatch(sql))
        {
            throw new ArgumentException("Generated SQL must not contain '-- @' header directives.", nameof(sql));
        }

        string fullDirectory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDirectory))
        {
            Directory.CreateDirectory(fullDirectory);
        }

        string normalizedDirectory = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;

        string filePath = Path.GetFullPath(Path.Combine(fullDirectory, $"{name}.sql"));
        if (!filePath.StartsWith(normalizedDirectory, StringComparison.Ordinal))
        {
            throw new System.Security.SecurityException($"dbt endpoint file for '{name}' would be written outside of '{fullDirectory}'.");
        }

        if (File.Exists(filePath))
        {
            var existingInfo = new FileInfo(filePath);
            if (existingInfo.LinkTarget != null)
            {
                throw new System.Security.SecurityException($"Refusing to write dbt endpoint through symbolic link '{filePath}'.");
            }

            if (!IsDbtGeneratedFile(filePath))
            {
                throw new InvalidOperationException($"Refusing to overwrite non-dbt-generated SQL endpoint file '{filePath}'.");
            }
        }
        else if (_registry.TryGet(name, out var existingDefinition) &&
                 existingDefinition != null &&
                 !existingDefinition.RawSql.Contains(DbtGeneratedMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to shadow existing non-dbt SQL endpoint '{name}'.");
        }

        string? safeSummary = SanitizeHeaderValue(summary);
        string? safeDataSource = SanitizeHeaderValue(dataSource);
        if (!string.IsNullOrWhiteSpace(safeDataSource) && !DbtDataSourceRegex.IsMatch(safeDataSource))
        {
            throw new ArgumentException($"Invalid data source name '{safeDataSource}' for dbt endpoint '{name}'.", nameof(dataSource));
        }

        using var sw = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
        sw.WriteLine(DbtGeneratedMarker);
        sw.WriteLine($"-- @name: {name}");
        if (!string.IsNullOrWhiteSpace(safeSummary))
        {
            sw.WriteLine($"-- @summary: {safeSummary}");
        }
        if (!string.IsNullOrWhiteSpace(safeDataSource))
        {
            sw.WriteLine($"-- @datasource: {safeDataSource}");
        }
        if (parameters != null)
        {
            foreach (var p in parameters)
            {
                if (!DbtNameRegex.IsMatch(p.Name))
                {
                    throw new ArgumentException($"Invalid parameter name '{SanitizeHeaderValue(p.Name)}'.", nameof(parameters));
                }

                string req = p.IsRequired ? "!" : "";
                string? safeDefault = p.DefaultValue != null
                    ? SanitizeHeaderValue(Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture))?.Replace("\"", string.Empty, StringComparison.Ordinal)
                    : null;
                string def = safeDefault != null ? $" = \"{safeDefault}\"" : "";
                sw.WriteLine($"-- @param {p.Name}: {MapClrTypeToName(p.ClrType)}{req}{def}");
            }
        }
        sw.WriteLine();
        sw.WriteLine(sql.Trim());

        _logger?.LogInformation("Synchronized dbt model to SQL file at '{FilePath}'.", filePath);
        return filePath;
    }

    /// <summary>
    /// Marker written as the first line of every dbt-generated endpoint file.
    /// </summary>
    public const string DbtGeneratedMarker = "-- @generated_by: autheris-dbt-sync";

    private static readonly Regex DbtNameRegex = new(
        @"\A[A-Za-z0-9_]{1,128}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex DbtDataSourceRegex = new(
        @"\A[A-Za-z0-9_.\-]{1,128}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static bool IsDbtGeneratedFile(string filePath)
    {
        using var reader = new StreamReader(filePath, System.Text.Encoding.UTF8);
        string? firstLine = reader.ReadLine();
        return firstLine != null && string.Equals(firstLine.Trim(), DbtGeneratedMarker, StringComparison.Ordinal);
    }

    private static string QuoteDbtIdentifier(string? identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !DbtNameRegex.IsMatch(identifier))
        {
            throw new ArgumentException($"Invalid dbt identifier '{SanitizeHeaderValue(identifier)}'. Allowed: ^[A-Za-z0-9_]{{1,128}}$.", paramName);
        }

        return $"\"{identifier}\"";
    }

    /// <summary>
    /// Removes line breaks and all other control / separator characters that could inject additional header directives.
    /// </summary>
    internal static string? SanitizeHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var sb = new System.Text.StringBuilder(Math.Min(value.Length, 512));
        foreach (char c in value)
        {
            if (sb.Length >= 512) break;

            var category = char.GetUnicodeCategory(c);
            if (char.IsControl(c) ||
                category == UnicodeCategory.LineSeparator ||
                category == UnicodeCategory.ParagraphSeparator ||
                category == UnicodeCategory.Format)
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    private static void ParseParamHeader(
        string text,
        Dictionary<string, (Type Type, bool IsRequired, object? DefaultVal, string? Desc)> headerParams)
    {
        var match = ParamHeaderRegex.Match(text);
        if (!match.Success) return;

        string name = match.Groups[1].Value;
        string typeStr = match.Groups[2].Value.ToLowerInvariant();
        bool isRequired = match.Groups[3].Success;
        string? defaultRaw = match.Groups[4].Success ? match.Groups[4].Value.Trim(' ', '"', '\'') : null;

        Type clrType = typeStr switch
        {
            "int" or "integer" or "int32" => typeof(int),
            "long" or "bigint" or "int64" => typeof(long),
            "decimal" or "numeric" or "money" => typeof(decimal),
            "float" or "double" or "real" => typeof(double),
            "bool" or "boolean" => typeof(bool),
            "date" or "datetime" or "timestamp" => typeof(DateTimeOffset),
            "guid" or "uuid" => typeof(Guid),
            _ => typeof(string)
        };

        object? defaultVal = null;
        if (defaultRaw != null)
        {
            try
            {
                if (clrType == typeof(int) && int.TryParse(defaultRaw, out int iv)) defaultVal = iv;
                else if (clrType == typeof(long) && long.TryParse(defaultRaw, out long lv)) defaultVal = lv;
                else if (clrType == typeof(decimal) && decimal.TryParse(defaultRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal dv)) defaultVal = dv;
                else if (clrType == typeof(bool) && bool.TryParse(defaultRaw, out bool bv)) defaultVal = bv;
                else defaultVal = defaultRaw;
            }
            catch
            {
                defaultVal = defaultRaw;
            }
        }

        headerParams[name] = (clrType, isRequired, defaultVal, null);
    }

    private static string MapClrTypeToName(Type type)
    {
        if (type == typeof(int) || type == typeof(short)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return "decimal";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "date";
        if (type == typeof(Guid)) return "guid";
        return "string";
    }

    private void SetupWatcher(string directoryPath)
    {
        try
        {
            _watcher = new FileSystemWatcher(directoryPath, "*.sql")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _watcher.Created += (_, e) => LoadFile(e.FullPath);
            _watcher.Changed += (_, e) => LoadFile(e.FullPath);
            _watcher.Deleted += (_, e) =>
            {
                string name = Path.GetFileNameWithoutExtension(e.FullPath);
                _registry.Unregister(name);
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to start FileSystemWatcher on '{DirectoryPath}'. Hot-reload disabled.", directoryPath);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
    }
}
