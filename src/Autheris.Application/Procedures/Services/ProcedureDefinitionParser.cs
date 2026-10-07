namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Domain.Model;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// F-SQL-02: Parses the header comments of a <c>*.proc.sql</c> declaration. Only the header is evaluated; the gateway
/// never executes any SQL text from the file, it calls the procedure via <c>CommandType.StoredProcedure</c>.
/// </summary>
public static class ProcedureDefinitionParser
{
    private static readonly Regex HeaderRegex = new(
        @"^[ \t]*--[ \t]*@([a-zA-Z0-9_-]+)(?:[:\s][ \t]*(.*))?$",
        RegexOptions.Compiled | RegexOptions.Multiline,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex EndpointNameRegex = new(
        @"\A[A-Za-z][A-Za-z0-9_-]{0,63}\z", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex IdentifierRegex = new(
        @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex ProcedureNameRegex = new(
        @"\A([A-Za-z_][A-Za-z0-9_]{0,127})\.([A-Za-z_][A-Za-z0-9_]{0,127})\z", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex ParamRegex = new(
        @"\A@?([A-Za-z_][A-Za-z0-9_]{0,127})[ \t]+([A-Za-z0-9]+(?:\([ \t]*(?:\d+|max)(?:[ \t]*,[ \t]*\d+)?[ \t]*\))?)[ \t]+(required|optional)(?:[ \t]+(.*))?\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ContextRegex = new(
        @"\A(tenant_id|user_sid|purpose)[ \t]*->[ \t]*@?([A-Za-z_][A-Za-z0-9_]{0,127})\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ResultColumnRegex = new(
        @"\A([A-Za-z_][A-Za-z0-9_]{0,127})[ \t]+clear\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ResultTableRegex = new(
        @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\.[A-Za-z_][A-Za-z0-9_]{0,127}\z",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Parses a declaration. Throws <see cref="FormatException"/> for anything that is not understood (fail-closed).
    /// </summary>
    /// <param name="content">File content.</param>
    /// <param name="defaultName">Endpoint name if <c>@name</c> is missing.</param>
    /// <param name="allowRlsNone">Whether <c>@rls none</c> is permitted (Development only).</param>
    /// <param name="maxTimeoutSeconds">Upper bound for <c>@timeout</c>.</param>
    public static ProcedureDefinition Parse(string content, string defaultName, bool allowRlsNone, int maxTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        string name = defaultName;
        string summary = string.Empty;
        string? procedure = null;
        var mode = ProcedureMode.Read;
        string? dataSource = null;
        var rls = ProcedureRlsMode.SessionContext;
        string? resultTable = null;
        bool allowDynamicSql = false;
        int timeout = 30;
        var parameters = new List<ProcedureParameter>();
        var contexts = new List<ProcedureContextBinding>();
        var cleared = new List<string>();
        var roles = new List<string>();
        var validationMode = ProcedureValidationMode.Catalog;
        var declaredOutputs = new List<string>();
        var kind = ProcedureKind.Procedure;
        var argumentOrder = new List<string>();
        var rowScopeKey = new List<string>();

        foreach (Match match in HeaderRegex.Matches(content))
        {
            string key = match.Groups[1].Value.ToLowerInvariant();
            string val = StripTrailingComment(match.Groups[2].Success ? match.Groups[2].Value : string.Empty);

            switch (key)
            {
                case "kind" or "type":
                    kind = val.ToLowerInvariant() switch
                    {
                        "tvf" or "function" or "table-valued-function" => ProcedureKind.TableValuedFunction,
                        "procedure" or "proc" or "sp" => ProcedureKind.Procedure,
                        _ => throw new FormatException($"@kind must be 'procedure' or 'tvf', got '{val}'.")
                    };
                    break;
                case "validation":
                    validationMode = val.ToLowerInvariant() switch
                    {
                        "declared" => ProcedureValidationMode.Declared,
                        "catalog" => ProcedureValidationMode.Catalog,
                        _ => throw new FormatException($"@validation must be 'declared' or 'catalog', got '{val}'.")
                    };
                    break;
                case "output" or "outputs":
                    foreach (string output in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        declaredOutputs.Add(RequireIdentifier(output, "@output column"));
                    }

                    break;
                case "name":
                    name = val;
                    break;
                case "summary" or "description":
                    summary = Sanitize(val);
                    break;
                case "procedure":
                    procedure = val;
                    break;
                case "mode":
                    mode = val.ToLowerInvariant() switch
                    {
                        "read" => ProcedureMode.Read,
                        "write" => ProcedureMode.Write,
                        _ => throw new FormatException($"@mode must be 'read' or 'write', got '{val}'.")
                    };
                    break;
                case "datasource" or "data_source":
                    dataSource = val;
                    break;
                case "rls":
                    rls = val.ToLowerInvariant() switch
                    {
                        "session-context" => ProcedureRlsMode.SessionContext,
                        "none" when allowRlsNone => ProcedureRlsMode.None,
                        "none" => throw new FormatException("@rls none is only permitted in the Development environment."),
                        _ => throw new FormatException($"@rls must be 'session-context' or 'none', got '{val}'.")
                    };
                    break;
                case "result-table":
                    if (!ResultTableRegex.IsMatch(val))
                    {
                        throw new FormatException("@result-table must have the form schema.table.");
                    }

                    resultTable = val;
                    break;
                case "row-scope-key":
                    foreach (string keyColumn in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        rowScopeKey.Add(keyColumn);
                    }

                    break;
                case "result-column":
                    var rc = ResultColumnRegex.Match(val);
                    if (!rc.Success)
                    {
                        throw new FormatException("@result-column must have the form '<column> clear'.");
                    }

                    cleared.Add(rc.Groups[1].Value);
                    break;
                case "param":
                    var parsedParameter = ParseParameter(val);
                    parameters.Add(parsedParameter);
                    argumentOrder.Add(parsedParameter.Name);
                    break;
                case "context":
                    var cm = ContextRegex.Match(val);
                    if (!cm.Success)
                    {
                        throw new FormatException("@context must have the form 'tenant_id|user_sid|purpose -> @Parameter'.");
                    }

                    contexts.Add(new ProcedureContextBinding(
                        cm.Groups[1].Value.ToLowerInvariant() switch
                        {
                            "tenant_id" => ProcedureContextKey.TenantId,
                            "user_sid" => ProcedureContextKey.UserSid,
                            _ => ProcedureContextKey.Purpose
                        },
                        cm.Groups[2].Value));
                    argumentOrder.Add(cm.Groups[2].Value);
                    break;
                case "roles":
                    var headerRoles = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (headerRoles.Length == 0)
                    {
                        // Review R4-7: an empty @roles directive must not open the endpoint.
                        throw new FormatException("@roles needs at least one role.");
                    }

                    roles.AddRange(headerRoles);
                    break;
                case "allow-dynamic-sql":
                    allowDynamicSql = true;
                    break;
                case "approval":
                    if (!string.Equals(val, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new FormatException("@approval four-eyes/hitl is not supported yet (write procedures are planned for phase 2).");
                    }

                    break;
                case "timeout":
                    if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout) || timeout < 1)
                    {
                        throw new FormatException("@timeout must be a positive integer (seconds).");
                    }

                    break;
                default:
                    throw new FormatException($"Unknown header '@{key}'.");
            }
        }

        if (!EndpointNameRegex.IsMatch(name))
        {
            throw new FormatException("Endpoint name must match [A-Za-z][A-Za-z0-9_-]{0,63}.");
        }

        if (procedure == null || !ProcedureNameRegex.IsMatch(procedure))
        {
            throw new FormatException("@procedure is required and must have the form schema.name (no database, linked server or quoting).");
        }

        var duplicate = parameters.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new FormatException($"Parameter '{duplicate.Key}' is declared more than once.");
        }

        var bound = contexts.Select(c => c.ParameterName).ToList();
        if (bound.Distinct(StringComparer.OrdinalIgnoreCase).Count() != bound.Count ||
            contexts.Select(c => c.Key).Distinct().Count() != contexts.Count)
        {
            throw new FormatException("Each context value and each procedure parameter can be bound only once.");
        }

        if (parameters.Any(p => bound.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
        {
            throw new FormatException("A parameter bound via @context must not also be declared as client @param.");
        }

        return new ProcedureDefinition(
            Name: name,
            Summary: summary,
            ProcedureName: procedure,
            Mode: mode,
            DataSource: string.IsNullOrWhiteSpace(dataSource) ? null : dataSource,
            Parameters: parameters,
            ContextBindings: contexts,
            RlsMode: rls,
            ResultTable: resultTable,
            ClearedResultColumns: cleared,
            RequiredRoles: roles,
            AllowDynamicSql: allowDynamicSql,
            TimeoutSeconds: Math.Min(timeout, Math.Max(1, maxTimeoutSeconds)),
            ValidationMode: validationMode,
            DeclaredOutputs: declaredOutputs,
            Kind: kind)
        {
            ArgumentOrder = argumentOrder,
            RowScopeKey = ValidateRowScopeKey(SplitRowScopeKey(rowScopeKey, out var scopeTableColumns), resultTable, validationMode, declaredOutputs),
            RowScopeKeyTable = scopeTableColumns
        };
    }

    private static ProcedureParameter ParseParameter(string text)
    {
        var m = ParamRegex.Match(text);
        if (!m.Success)
        {
            throw new FormatException("@param must have the form '<name> <sqltype> required|optional [description]'.");
        }

        string name = m.Groups[1].Value;
        string sqlType = Regex.Replace(m.Groups[2].Value.ToLowerInvariant(), @"\s+", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        bool required = string.Equals(m.Groups[3].Value, "required", StringComparison.OrdinalIgnoreCase);
        string? desc = m.Groups[4].Success ? Sanitize(m.Groups[4].Value) : null;

        string baseType = sqlType.Contains('(') ? sqlType[..sqlType.IndexOf('(')] : sqlType;
        int? maxLength = null;
        if (sqlType.Contains('('))
        {
            string inner = sqlType[(sqlType.IndexOf('(') + 1)..^1];
            string first = inner.Split(',')[0];
            if (!string.Equals(first, "max", StringComparison.Ordinal))
            {
                maxLength = int.Parse(first, CultureInfo.InvariantCulture);
            }
        }

        Type? clr = baseType switch
        {
            "int" => typeof(int),
            "bigint" => typeof(long),
            "smallint" => typeof(short),
            "tinyint" => typeof(byte),
            "bit" => typeof(bool),
            "decimal" or "numeric" => typeof(decimal),
            "float" => typeof(double),
            "real" => typeof(float),
            "nvarchar" or "varchar" or "nchar" or "char" => typeof(string),
            "uniqueidentifier" => typeof(Guid),
            "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
            "datetimeoffset" => typeof(DateTimeOffset),
            _ => null
        };

        if (clr == null)
        {
            throw new FormatException($"SQL type '{baseType}' is not supported for procedure parameters.");
        }

        return new ProcedureParameter(name, sqlType, clr, required, maxLength, desc);
    }

    private static string StripTrailingComment(string value)
    {
        int idx = value.IndexOf(" --", StringComparison.Ordinal);
        if (idx >= 0)
        {
            value = value[..idx];
        }

        return value.Trim();
    }

    private static string Sanitize(string value)
    {
        var sb = new System.Text.StringBuilder(Math.Min(value.Length, 512));
        foreach (char c in value)
        {
            if (sb.Length >= 512)
            {
                break;
            }

            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Contract-First: Parses a YAML declaration (*.proc.yaml / *.proc.yml). Like the header format it is fail-closed:
    /// unknown keys, unknown context values and invalid identifiers raise <see cref="FormatException"/> (review P-4/P-5).
    /// </summary>
    public static ProcedureDefinition ParseYaml(string yamlContent, string defaultName, bool allowRlsNone, int maxTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlContent);

        // Review R4-7: duplicate mapping keys are rejected instead of "last one wins".
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        ProcedureYamlModel? model;
        try
        {
            model = deserializer.Deserialize<ProcedureYamlModel>(yamlContent);
        }
        catch (YamlException ex)
        {
            // Syntax errors, unknown keys and type errors (review P-3/P-5); never let them escape as YamlException.
            throw new FormatException($"Invalid YAML procedure declaration: {ex.Message}", ex);
        }

        if (model == null)
        {
            throw new FormatException("Empty YAML procedure declaration.");
        }

        string name = string.IsNullOrWhiteSpace(model.Name) ? defaultName : model.Name.Trim();
        if (!EndpointNameRegex.IsMatch(name))
        {
            throw new FormatException("Endpoint name must match [A-Za-z][A-Za-z0-9_-]{0,63}.");
        }

        if (string.IsNullOrWhiteSpace(model.Procedure) || !ProcedureNameRegex.IsMatch(model.Procedure.Trim()))
        {
            throw new FormatException("Procedure name is required and must have the form schema.name.");
        }

        string procedure = model.Procedure.Trim();
        string summary = Sanitize(model.Summary ?? model.Description ?? string.Empty);

        var mode = (model.Mode?.Trim().ToLowerInvariant() ?? "read") switch
        {
            "read" => ProcedureMode.Read,
            "write" => ProcedureMode.Write,
            var other => throw new FormatException($"mode must be 'read' or 'write', got '{other}'.")
        };

        var validationMode = (model.Validation?.Trim().ToLowerInvariant() ?? "catalog") switch
        {
            "catalog" => ProcedureValidationMode.Catalog,
            "declared" => ProcedureValidationMode.Declared,
            var other => throw new FormatException($"validation must be 'declared' or 'catalog', got '{other}'.")
        };

        var rls = (model.Rls?.Trim().ToLowerInvariant() ?? "session-context") switch
        {
            "session-context" or "session_context" => ProcedureRlsMode.SessionContext,
            "none" when allowRlsNone => ProcedureRlsMode.None,
            "none" => throw new FormatException("rls: none is only permitted in Development."),
            var other => throw new FormatException($"rls must be 'session-context' or 'none', got '{other}'.")
        };

        var kind = (model.Kind?.Trim().ToLowerInvariant() ?? "procedure") switch
        {
            "tvf" or "function" or "table_valued_function" or "table-valued-function" => ProcedureKind.TableValuedFunction,
            "procedure" or "proc" or "sp" => ProcedureKind.Procedure,
            var other => throw new FormatException($"kind must be 'procedure' or 'tvf', got '{other}'.")
        };

        int timeout = model.TimeoutSeconds ?? model.Timeout ?? 30;
        if (timeout < 1)
        {
            throw new FormatException("timeout must be a positive integer (seconds).");
        }

        var parameters = new List<ProcedureParameter>();
        var contexts = new List<ProcedureContextBinding>();
        var argumentOrder = new List<string>();

        if (model.Parameters != null)
        {
            foreach (var p in model.Parameters)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Name))
                {
                    throw new FormatException("Every entry in 'parameters' needs a name.");
                }

                string pName = RequireIdentifier(p.Name.Trim().TrimStart('@'), "parameter name");
                if (!string.IsNullOrWhiteSpace(p.Context))
                {
                    contexts.Add(new ProcedureContextBinding(MapContextKey(p.Context), pName));
                }
                else
                {
                    string typeStr = string.IsNullOrWhiteSpace(p.Type) ? "string" : p.Type.Trim();
                    bool isReq = p.Required && (p.IsRequired ?? true);
                    parameters.Add(ParseParameter($"{pName} {typeStr} {(isReq ? "required" : "optional")} {p.Description}"));
                }

                argumentOrder.Add(pName);
            }
        }

        bool hasSeparateContext = model.Context is { Count: > 0 } || model.ContextBindings is { Count: > 0 };
        if (kind == ProcedureKind.TableValuedFunction && hasSeparateContext)
        {
            // Review P-2: function arguments are positional; their order must be explicit.
            throw new FormatException("For kind: tvf declare context values inline in 'parameters' (with 'context:'), in the order of the function signature.");
        }

        if (model.Context != null)
        {
            foreach (var kvp in model.Context)
            {
                if (string.IsNullOrWhiteSpace(kvp.Value))
                {
                    throw new FormatException($"context '{kvp.Key}' needs a parameter name.");
                }

                string pName = RequireIdentifier(kvp.Value.Trim().TrimStart('@'), "context parameter name");
                contexts.Add(new ProcedureContextBinding(MapContextKey(kvp.Key), pName));
                argumentOrder.Add(pName);
            }
        }

        if (model.ContextBindings != null)
        {
            foreach (var cb in model.ContextBindings)
            {
                if (cb == null || string.IsNullOrWhiteSpace(cb.Key) || string.IsNullOrWhiteSpace(cb.Parameter))
                {
                    throw new FormatException("Every entry in 'context_bindings' needs 'key' and 'parameter'.");
                }

                string pName = RequireIdentifier(cb.Parameter.Trim().TrimStart('@'), "context parameter name");
                contexts.Add(new ProcedureContextBinding(MapContextKey(cb.Key), pName));
                argumentOrder.Add(pName);
            }
        }

        var rawRoles = model.RequiredRoles ?? model.Roles;
        var roles = (rawRoles ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList();
        // Review R4-7: an explicitly given but empty/blank role list must not silently open the endpoint to every
        // authenticated user (omit the key for "any authenticated user").
        if (rawRoles != null && roles.Count == 0)
        {
            throw new FormatException("'required_roles' is present but contains no role; remove the key or list at least one role.");
        }

        var outputs = ParseYamlOutputs(model.Outputs, out var outputTypes, out var outputSources);
        var cleared = (model.ClearedColumns ?? []).Select(c => RequireIdentifier(c?.Trim(), "cleared column")).ToList();

        string? resultTable = string.IsNullOrWhiteSpace(model.ResultTable) ? null : model.ResultTable.Trim();
        if (resultTable != null && !ResultTableRegex.IsMatch(resultTable))
        {
            throw new FormatException("result_table must have the form schema.table.");
        }

        var referencedTables = (model.ReferencedTables ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList();

        foreach (var rt in referencedTables)
        {
            if (!ResultTableRegex.IsMatch(rt))
            {
                throw new FormatException($"referenced_tables entry '{rt}' must have the form schema.table.");
            }
        }

        var duplicate = parameters.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new FormatException($"Parameter '{duplicate.Key}' is declared more than once.");
        }

        var bound = contexts.Select(c => c.ParameterName).ToList();
        if (bound.Distinct(StringComparer.OrdinalIgnoreCase).Count() != bound.Count ||
            contexts.Select(c => c.Key).Distinct().Count() != contexts.Count)
        {
            throw new FormatException("Each context value and each procedure parameter can be bound only once.");
        }

        if (parameters.Any(p => bound.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
        {
            throw new FormatException("A parameter bound via context must not also be declared as client parameter.");
        }

        return new ProcedureDefinition(
            Name: name,
            Summary: summary,
            ProcedureName: procedure,
            Mode: mode,
            DataSource: string.IsNullOrWhiteSpace(model.DataSource) ? null : model.DataSource.Trim(),
            Parameters: parameters,
            ContextBindings: contexts,
            RlsMode: rls,
            ResultTable: resultTable,
            ClearedResultColumns: cleared,
            RequiredRoles: roles,
            AllowDynamicSql: model.AllowDynamicSql,
            TimeoutSeconds: Math.Min(timeout, Math.Max(1, maxTimeoutSeconds)),
            ValidationMode: validationMode,
            DeclaredOutputs: outputs,
            Kind: kind)
        {
            ArgumentOrder = argumentOrder,
            DeclaredOutputTypes = outputTypes,
            DeclaredOutputSources = outputSources,
            ReferencedTables = referencedTables,
            DdlHash = model.Integrity?.DdlHash?.Trim(),
            RowScopeKey = ValidateRowScopeKey(SplitRowScopeKey(ParseYamlRowScopeKey(model.RowScopeKey), out var scopeTableColumns), resultTable, validationMode, outputs),
            RowScopeKeyTable = scopeTableColumns
        };
    }

    /// <summary>
    /// Entries are <c>column</c> or <c>result_column=table_column</c> (the procedure names the key differently than the
    /// result table). Returns the result columns; the table columns come back in <paramref name="tableColumns"/>.
    /// </summary>
    private static List<string> SplitRowScopeKey(List<string> entries, out List<string> tableColumns)
    {
        var result = new List<string>(entries.Count);
        tableColumns = new List<string>(entries.Count);
        foreach (string entry in entries)
        {
            var parts = entry.Split('=', StringSplitOptions.TrimEntries);
            if (parts.Length > 2)
            {
                throw new FormatException($"Invalid row_scope_key entry '{entry}'. Use 'column' or 'result_column=table_column'.");
            }

            result.Add(RequireIdentifier(parts[0], "row_scope_key column"));
            tableColumns.Add(RequireIdentifier(parts.Length == 2 ? parts[1] : parts[0], "row_scope_key table column"));
        }

        return result;
    }

    /// <summary><c>row_scope_key</c> is a column name or a list of column names (composite key).</summary>
    private static List<string> ParseYamlRowScopeKey(object? raw)
    {
        var keys = new List<string>();
        switch (raw)
        {
            case null:
                break;
            case string s:
                keys.Add(s.Trim());
                break;
            case IEnumerable<object> list:
                foreach (var item in list)
                {
                    keys.Add(item?.ToString()?.Trim() ?? string.Empty);
                }

                break;
            default:
                throw new FormatException("row_scope_key must be a column name or a list of column names.");
        }

        return keys;
    }

    /// <summary>
    /// The scope key identifies rows of the result table: it needs a result table, no duplicates, and in declared mode the
    /// key columns must be part of the declared outputs (they are read from the result set).
    /// </summary>
    private static List<string> ValidateRowScopeKey(
        List<string> keys,
        string? resultTable,
        ProcedureValidationMode validationMode,
        IReadOnlyList<string> declaredOutputs)
    {
        if (keys.Count == 0)
        {
            return keys;
        }

        if (resultTable == null)
        {
            throw new FormatException("row_scope_key requires a result_table.");
        }

        if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count)
        {
            throw new FormatException("row_scope_key must not list a column more than once.");
        }

        if (validationMode == ProcedureValidationMode.Declared)
        {
            var missing = keys.FirstOrDefault(k => !declaredOutputs.Contains(k, StringComparer.OrdinalIgnoreCase));
            if (missing != null)
            {
                throw new FormatException($"row_scope_key column '{missing}' must be one of the declared outputs.");
            }
        }

        return keys;
    }

    /// <summary>
    /// Outputs may be plain column names or typed: <c>- name</c>, <c>- {name: gps_latitude, type: float}</c> or the short
    /// form <c>- gps_latitude: float</c>. Can also declare <c>source_table</c> and <c>source_column</c> (or <c>source: schema.table.column</c>)
    /// for offline/pure-YAML column-level governance. Types only document the contract (OpenAPI).
    /// </summary>
    private static List<string> ParseYamlOutputs(
        List<object>? raw,
        out Dictionary<string, string> types,
        out Dictionary<string, ResultColumnSource> sources)
    {
        types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        sources = new Dictionary<string, ResultColumnSource>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var item in raw ?? [])
        {
            string? name;
            string? type = null;
            string? sourceTable = null;
            string? sourceColumn = null;
            switch (item)
            {
                case string s:
                    name = s;
                    break;
                case IDictionary<object, object> map when map.TryGetValue("name", out var n):
                    name = n?.ToString();
                    type = map.TryGetValue("type", out var t) ? t?.ToString() : null;
                    if (map.TryGetValue("source", out var srcVal) && srcVal != null)
                    {
                        var parts = srcVal.ToString()!.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length == 3)
                        {
                            sourceTable = $"{parts[0]}.{parts[1]}";
                            sourceColumn = parts[2];
                        }
                        else if (parts.Length == 2)
                        {
                            sourceTable = parts[0];
                            sourceColumn = parts[1];
                        }
                        else
                        {
                            throw new FormatException($"Invalid source '{srcVal}'. Expected 'table.column' or 'schema.table.column'.");
                        }
                    }
                    if (map.TryGetValue("source_table", out var stVal) && stVal != null)
                    {
                        sourceTable = stVal.ToString()?.Trim();
                    }
                    if (map.TryGetValue("source_column", out var scVal) && scVal != null)
                    {
                        sourceColumn = scVal.ToString()?.Trim();
                    }
                    foreach (var key in map.Keys)
                    {
                        if (key?.ToString() is not ("name" or "type" or "source" or "source_table" or "source_column"))
                        {
                            throw new FormatException($"Unknown key '{key}' in output declaration (allowed: name, type, source, source_table, source_column).");
                        }
                    }

                    break;
                case IDictionary<object, object> map when map.Count == 1:
                    var only = map.First();
                    name = only.Key?.ToString();
                    type = only.Value?.ToString();
                    break;
                default:
                    throw new FormatException("Every entry in 'outputs' must be a column name, '{name, type}' or '<name>: <type>'.");
            }

            string column = RequireIdentifier(name?.Trim(), "output column");
            names.Add(column);
            if (type != null)
            {
                types[column] = NormalizeOutputSqlType(type);
            }
            if (!string.IsNullOrWhiteSpace(sourceTable) && !string.IsNullOrWhiteSpace(sourceColumn))
            {
                var tableParts = sourceTable.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string? schema = tableParts.Length == 2 ? tableParts[0] : null;
                string table = tableParts.Length == 2 ? tableParts[1] : tableParts[0];
                sources[column] = new ResultColumnSource(schema, table, sourceColumn);
            }
        }

        return names;
    }

    private static readonly HashSet<string> OutputSqlBaseTypes = new(StringComparer.Ordinal)
    {
        "int", "bigint", "smallint", "tinyint", "bit", "decimal", "numeric", "float", "real", "money", "smallmoney",
        "nvarchar", "varchar", "nchar", "char", "uniqueidentifier", "date", "datetime", "datetime2", "smalldatetime",
        "datetimeoffset", "time"
    };

    private static string NormalizeOutputSqlType(string text)
    {
        string t = Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        if (!Regex.IsMatch(t, @"\A[a-z0-9]+(\((\d+|max)(,\d+)?\))?\z", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
        {
            throw new FormatException($"Invalid output type '{text}'.");
        }

        string baseType = t.Contains('(') ? t[..t.IndexOf('(')] : t;
        if (!OutputSqlBaseTypes.Contains(baseType))
        {
            throw new FormatException($"SQL type '{baseType}' is not supported for procedure outputs.");
        }

        return t;
    }

    /// <summary>Review P-5: unknown context keys are rejected instead of silently binding the purpose value.</summary>
    private static ProcedureContextKey MapContextKey(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "tenant_id" or "tenant" => ProcedureContextKey.TenantId,
        "user_sid" or "sid" or "user" => ProcedureContextKey.UserSid,
        "purpose" => ProcedureContextKey.Purpose,
        _ => throw new FormatException($"Unknown context value '{key}'. Allowed: tenant_id, user_sid, purpose.")
    };

    /// <summary>Review P-4: identifiers end up in SQL text (TVF/CALL); only [A-Za-z_][A-Za-z0-9_]* is accepted.</summary>
    private static string RequireIdentifier(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierRegex.IsMatch(value))
        {
            throw new FormatException($"Invalid {what} '{value}'. Only [A-Za-z_][A-Za-z0-9_]{{0,127}} is allowed.");
        }

        return value;
    }
}

public sealed class ProcedureYamlModel
{
    public string? Name { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Procedure { get; set; }
    public string? Kind { get; set; }
    public string? Mode { get; set; }
    public string? DataSource { get; set; }
    public string? Validation { get; set; }
    public string? Rls { get; set; }
    public string? ResultTable { get; set; }
    public int? Timeout { get; set; }
    public int? TimeoutSeconds { get; set; }
    public bool AllowDynamicSql { get; set; }
    public List<string>? Roles { get; set; }
    public List<string>? RequiredRoles { get; set; }
    public List<ProcedureParameterYamlModel>? Parameters { get; set; }
    public Dictionary<string, string>? Context { get; set; }
    public List<ProcedureContextYamlModel>? ContextBindings { get; set; }
    public List<object>? Outputs { get; set; }
    public List<string>? ClearedColumns { get; set; }
    /// <summary>A column name or a list of column names.</summary>
    public object? RowScopeKey { get; set; }

    /// <summary>Explicit list of physical tables referenced by the procedure.</summary>
    public List<string>? ReferencedTables { get; set; }

    /// <summary>Optional integrity metadata (e.g. ddl_hash) for drift prevention.</summary>
    public ProcedureIntegrityYamlModel? Integrity { get; set; }
}

public sealed class ProcedureIntegrityYamlModel
{
    public string? DdlHash { get; set; }
}

public sealed class ProcedureParameterYamlModel
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; } = true;
    public bool? IsRequired { get; set; }
    public string? Description { get; set; }
    public string? Context { get; set; }
}

public sealed class ProcedureContextYamlModel
{
    public string Key { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
}
