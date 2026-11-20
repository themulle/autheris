namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

/// <summary>Validates and binds typed <c>:name</c> row-policy parameters and KeySet cross-source / max-age rules.</summary>
public static partial class RowPolicyParameterBinder
{
    internal const string SentinelPrefix = "__autheris_param__";

    [GeneratedRegex("^:[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex StructuredPlaceholderRegex();

    [GeneratedRegex("(?<!:):([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex SqlNamedParameterRegex();

    [GeneratedRegex("'([^']|'')*'", RegexOptions.CultureInvariant)]
    private static partial Regex SqlStringLiteralRegex();

    internal static bool TryExtractStructuredPlaceholder(string? value, out string name)
    {
        if (value != null && StructuredPlaceholderRegex().IsMatch(value)) { name = value[1..]; return true; }
        name = string.Empty;
        return false;
    }

    internal static bool ContainsSqlParameters(string? sql) =>
        !string.IsNullOrEmpty(sql) && SqlNamedParameterRegex().IsMatch(SqlStringLiteralRegex().Replace(sql, "''"));

    internal static bool TryParseSentinel(string? value, out string name)
    {
        if (value != null && value.StartsWith(SentinelPrefix, StringComparison.Ordinal)) { name = value[SentinelPrefix.Length..]; return name.Length > 0; }
        name = string.Empty;
        return false;
    }

    public static void ValidateFilterDeclarations(VirtualFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var declared = filter.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        if (filter.Structured != null)
        {
            foreach (var c in filter.Structured.Where)
            {
                if (!TryExtractStructuredPlaceholder(c.Value, out var name)) continue;
                if (c.Operator is not (FilterConditionOperator.Eq or FilterConditionOperator.NotEq))
                    throw Invalid($"Parameter ':{name}' on '{c.Column}' is only valid with equality/inequality operators.");
                if (!declared.ContainsKey(name))
                    throw Invalid($"The virtual filter '{filter.Name}' references unknown parameter ':{name}'.");
            }
        }
        if (filter.Sql != null) RewriteSqlParameters(filter, out _);
    }

    internal static string RewriteSqlParameters(VirtualFilter filter, out IReadOnlyList<string> referencedNames)
    {
        var sql = filter.Sql ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no sql definition.");
        var declared = filter.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var sb = new StringBuilder(sql.Length);
        int cursor = 0;
        foreach (Match lit in SqlStringLiteralRegex().Matches(sql))
        {
            AppendSegment(sql, cursor, lit.Index - cursor, declared, filter.Name, names, sb);
            sb.Append(sql, lit.Index, lit.Length);
            cursor = lit.Index + lit.Length;
        }
        if (cursor < sql.Length) AppendSegment(sql, cursor, sql.Length - cursor, declared, filter.Name, names, sb);
        referencedNames = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return sb.ToString();
    }

    private static void AppendSegment(string sql, int start, int length, Dictionary<string, RowPolicyParameter> declared, string filterName, List<string> seen, StringBuilder sb)
    {
        if (length <= 0) return;
        var rewritten = SqlNamedParameterRegex().Replace(sql.Substring(start, length), match =>
        {
            var paramName = match.Groups[1].Value;
            if (!declared.ContainsKey(paramName))
                throw Invalid($"The sql definition of the virtual filter '{filterName}' references unknown parameter ':{paramName}'.");
            seen.Add(paramName);
            return $"'{SentinelPrefix}{paramName}'";
        });
        if (rewritten.Contains('?', StringComparison.Ordinal) || rewritten.Contains('@', StringComparison.Ordinal) ||
            rewritten.Replace("::", string.Empty, StringComparison.Ordinal).Contains(':', StringComparison.Ordinal))
            throw Invalid($"The sql definition of the virtual filter '{filterName}' contains unsupported parameter syntax.");
        sb.Append(rewritten);
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<PolicyValue>> BindValues(VirtualFilter filter, IReadOnlyDictionary<string, JsonElement>? values)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var declared = filter.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        if (values != null)
        {
            foreach (var key in values.Keys)
                if (!declared.ContainsKey(key)) throw Invalid($"Unknown row policy parameter '{key}' for filter '{filter.Name}'.");
        }
        var bound = new Dictionary<string, IReadOnlyList<PolicyValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (var param in filter.Parameters)
        {
            if (values == null || !TryGetCaseInsensitive(values, param.Name, out var element) ||
                element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw Invalid($"Missing required row policy parameter '{param.Name}' for filter '{filter.Name}'.");
            if (param.IsList)
            {
                if (element.ValueKind != JsonValueKind.Array)
                    throw Invalid($"Row policy parameter '{param.Name}' expects a list of {param.Type} values.");
                int count = element.GetArrayLength();
                if (count == 0 || count > param.MaxValues)
                    throw Invalid($"Row policy parameter '{param.Name}' expects 1..{param.MaxValues} values, received {count}.");
                var items = new List<PolicyValue>(count);
                foreach (var item in element.EnumerateArray()) items.Add(ParseSingle(param, item));
                bound[param.Name] = items;
            }
            else
            {
                if (element.ValueKind == JsonValueKind.Array) throw Invalid($"Scalar row policy parameter '{param.Name}' cannot accept a list.");
                bound[param.Name] = [ParseSingle(param, element)];
            }
        }
        return bound;
    }

    public static void EnsureCrossSourceAllowed(VirtualFilter filter, string targetSource)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!string.Equals(filter.Source, targetSource, StringComparison.OrdinalIgnoreCase) && filter.EvaluationMode != RowPolicyEvaluationMode.KeySet)
            throw new VirtualFilterBindingException(VirtualFilterErrorCodes.RowPolicyCrossSourceRequiresKeySet,
                $"Virtual filter '{filter.Name}' (source '{filter.Source}') requires KeySet evaluation mode to target '{targetSource}'.");
    }

    public static TimeSpan ComputeEffectiveKeySetMaxAge(VirtualFilter filter, GatewayOptions? options)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var cacheMax = TimeSpan.FromHours(1);
        var requested = filter.KeySetMaxAge ?? TimeSpan.FromSeconds((options?.VirtualFilters?.Federation ?? new VirtualFilterFederationOptions()).KeyCacheTtlSeconds);
        return requested <= TimeSpan.Zero ? throw Invalid($"key_set_max_age of '{filter.Name}' must be positive.") : (requested <= cacheMax ? requested : cacheMax);
    }

    private static PolicyValue ParseSingle(RowPolicyParameter param, JsonElement element) => param.Type switch
    {
        RowPolicyParameterType.String when element.ValueKind == JsonValueKind.String && element.GetString() is { } s => new(s, SqlParameterType.String),
        RowPolicyParameterType.Int64 when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var n) => new(n, SqlParameterType.Int64),
        RowPolicyParameterType.Decimal when element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var d) => new(d, SqlParameterType.Decimal),
        RowPolicyParameterType.Guid when element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var g) => new(g.ToString("D"), SqlParameterType.String),
        RowPolicyParameterType.Date when element.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) => new(dt, SqlParameterType.Date),
        _ => throw Invalid($"Row policy parameter '{param.Name}' does not match declared type {param.Type}.")
    };

    private static bool TryGetCaseInsensitive(IReadOnlyDictionary<string, JsonElement> values, string name, out JsonElement value)
    {
        if (values.TryGetValue(name, out value)) return true;
        foreach (var kv in values)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        value = default;
        return false;
    }

    private static VirtualFilterBindingException Invalid(string message) => new(VirtualFilterErrorCodes.RowPolicyParameterInvalid, message);
}
