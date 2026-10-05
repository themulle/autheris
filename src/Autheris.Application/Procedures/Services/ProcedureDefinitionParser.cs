namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Domain.Model;
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
        @"^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex IdentifierRegex = new(
        @"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex ProcedureNameRegex = new(
        @"^([A-Za-z_][A-Za-z0-9_]{0,127})\.([A-Za-z_][A-Za-z0-9_]{0,127})$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex ParamRegex = new(
        @"^@?([A-Za-z_][A-Za-z0-9_]{0,127})[ \t]+([A-Za-z0-9]+(?:\([ \t]*(?:\d+|max)(?:[ \t]*,[ \t]*\d+)?[ \t]*\))?)[ \t]+(required|optional)(?:[ \t]+(.*))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ContextRegex = new(
        @"^(tenant_id|user_sid|purpose)[ \t]*->[ \t]*@?([A-Za-z_][A-Za-z0-9_]{0,127})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ResultColumnRegex = new(
        @"^([A-Za-z_][A-Za-z0-9_]{0,127})[ \t]+clear$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ResultTableRegex = new(
        @"^[A-Za-z_][A-Za-z0-9_]{0,127}\.[A-Za-z_][A-Za-z0-9_]{0,127}$",
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
                    declaredOutputs.AddRange(val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
                case "result-column":
                    var rc = ResultColumnRegex.Match(val);
                    if (!rc.Success)
                    {
                        throw new FormatException("@result-column must have the form '<column> clear'.");
                    }

                    cleared.Add(rc.Groups[1].Value);
                    break;
                case "param":
                    parameters.Add(ParseParameter(val));
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
                    break;
                case "roles":
                    roles.AddRange(val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
            Kind: kind);
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
    /// Contract-First: Parses a YAML declaration (*.proc.yaml / *.proc.yml).
    /// </summary>
    public static ProcedureDefinition ParseYaml(string yamlContent, string defaultName, bool allowRlsNone, int maxTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlContent);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var model = deserializer.Deserialize<ProcedureYamlModel>(yamlContent);
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
        var mode = string.Equals(model.Mode, "write", StringComparison.OrdinalIgnoreCase) ? ProcedureMode.Write : ProcedureMode.Read;

        var validationMode = string.Equals(model.Validation, "declared", StringComparison.OrdinalIgnoreCase)
            ? ProcedureValidationMode.Declared
            : ProcedureValidationMode.Catalog;

        var rls = string.Equals(model.Rls, "none", StringComparison.OrdinalIgnoreCase)
            ? (allowRlsNone ? ProcedureRlsMode.None : throw new FormatException("rls: none is only permitted in Development."))
            : ProcedureRlsMode.SessionContext;

        var kind = string.Equals(model.Kind, "tvf", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(model.Kind, "function", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(model.Kind, "table_valued_function", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(model.Kind, "table-valued-function", StringComparison.OrdinalIgnoreCase)
            ? ProcedureKind.TableValuedFunction
            : ProcedureKind.Procedure;

        int timeout = model.TimeoutSeconds ?? model.Timeout ?? 30;

        var parameters = new List<ProcedureParameter>();
        var contexts = new List<ProcedureContextBinding>();

        if (model.Parameters != null)
        {
            foreach (var p in model.Parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Name))
                {
                    continue;
                }

                string pName = p.Name.Trim().TrimStart('@');
                if (!string.IsNullOrWhiteSpace(p.Context))
                {
                    var contextKey = p.Context.Trim().ToLowerInvariant() switch
                    {
                        "tenant_id" or "tenant" => ProcedureContextKey.TenantId,
                        "user_sid" or "sid" or "user" => ProcedureContextKey.UserSid,
                        _ => ProcedureContextKey.Purpose
                    };
                    contexts.Add(new ProcedureContextBinding(contextKey, pName));
                }
                else
                {
                    string typeStr = string.IsNullOrWhiteSpace(p.Type) ? "string" : p.Type.Trim();
                    bool isReq = p.Required && (p.IsRequired ?? true);
                    parameters.Add(ParseParameter($"{pName} {typeStr} {(isReq ? "required" : "optional")} {p.Description}"));
                }
            }
        }

        if (model.Context != null)
        {
            foreach (var kvp in model.Context)
            {
                var contextKey = kvp.Key.Trim().ToLowerInvariant() switch
                {
                    "tenant_id" or "tenant" => ProcedureContextKey.TenantId,
                    "user_sid" or "sid" or "user" => ProcedureContextKey.UserSid,
                    _ => ProcedureContextKey.Purpose
                };
                contexts.Add(new ProcedureContextBinding(contextKey, kvp.Value.Trim().TrimStart('@')));
            }
        }

        if (model.ContextBindings != null)
        {
            foreach (var cb in model.ContextBindings)
            {
                var contextKey = cb.Key.Trim().ToLowerInvariant() switch
                {
                    "tenant_id" or "tenant" => ProcedureContextKey.TenantId,
                    "user_sid" or "sid" or "user" => ProcedureContextKey.UserSid,
                    _ => ProcedureContextKey.Purpose
                };
                contexts.Add(new ProcedureContextBinding(contextKey, cb.Parameter.Trim().TrimStart('@')));
            }
        }

        var roles = model.RequiredRoles ?? model.Roles ?? [];
        var outputs = model.Outputs ?? [];
        var cleared = model.ClearedColumns ?? [];

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
            ResultTable: model.ResultTable,
            ClearedResultColumns: cleared,
            RequiredRoles: roles,
            AllowDynamicSql: model.AllowDynamicSql,
            TimeoutSeconds: Math.Min(timeout, Math.Max(1, maxTimeoutSeconds)),
            ValidationMode: validationMode,
            DeclaredOutputs: outputs,
            Kind: kind);
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
    public List<string>? Outputs { get; set; }
    public List<string>? ClearedColumns { get; set; }
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
