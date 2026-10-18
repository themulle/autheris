namespace Autheris.Application.Sql.Federation.Parameters;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Sql;
using Autheris.Application.Sql.ListParameters;
using Autheris.Domain.Common;
using TrinoSqlEngine.Ast.Emit;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Represents SQL semantic column data type classes")]
public enum ColumnTypeClass
{
    Integer,
    Decimal,
    Boolean,
    String,
    Temporal,
    Guid,
    Float,
    Binary,
    Spatial,
    Json,
    Array,
    Unknown
}

public sealed class FederatedParameterContract
{
    private static readonly Regex ValidNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    public static FederatedParameterContract Empty { get; } = new(
        FrozenDictionary<string, object?>.Empty,
        FrozenDictionary<string, int>.Empty,
        ClientListShapes.Empty,
        hasValues: false);

    private readonly FrozenDictionary<string, object?> _clientValues;
    private readonly FrozenDictionary<string, int> _listShapes;
    private readonly ClientListShapes _astShapes;
    private readonly string[] _names;

    public bool HasValues { get; }
    public IReadOnlyDictionary<string, object?> ClientValues => _clientValues;
    public IReadOnlyDictionary<string, int> ListShapeCounts => _listShapes;
    public ClientListShapes ListShapes => _astShapes;
    public IReadOnlyList<string> Names => _names;

    private FederatedParameterContract(
        FrozenDictionary<string, object?> clientValues,
        FrozenDictionary<string, int> listShapes,
        ClientListShapes astShapes,
        bool hasValues)
    {
        _clientValues = clientValues;
        _listShapes = listShapes;
        _astShapes = astShapes;
        HasValues = hasValues;
        _names = clientValues.Keys
            .Where(k => ValidNamePattern.IsMatch(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
    }

    public static FederatedParameterContract Create(
        IReadOnlyDictionary<string, object?>? rawParameters,
        int maxListElements = 1000) =>
        Create(rawParameters, new ListParameterLimits(maxListElements, 64));

    public static FederatedParameterContract Create(
        IReadOnlyDictionary<string, object?>? rawParameters,
        ListParameterLimits limits)
    {
        if (rawParameters == null || rawParameters.Count == 0)
        {
            return Empty;
        }

        // 1. Reserved prefix validation (fail closed SEC-DATA131-06)
        foreach (var key in rawParameters.Keys)
        {
            ValidateParameterName(key);
        }

        var normalizedValues = new Dictionary<string, object?>(rawParameters.Count, StringComparer.OrdinalIgnoreCase);
        var listShapes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int maxListElements = limits.ConfiguredMaxValues > 0 ? limits.ConfiguredMaxValues : 1000;

        foreach (var (rawKey, rawValue) in rawParameters)
        {
            var key = rawKey.TrimStart('@');
            if (normalizedValues.ContainsKey(key))
            {
                continue; // First spelling wins
            }

            // Embedded NUL check on raw strings
            if (rawValue is string rawStr && rawStr.Contains('\0'))
            {
                throw new WebSqlSecurityException("Parameter value contains forbidden control character.");
            }

            if (rawValue is JsonElement jsonElem && jsonElem.ValueKind == JsonValueKind.String)
            {
                var s = jsonElem.GetString();
                if (s != null && s.Contains('\0'))
                {
                    throw new WebSqlSecurityException("Parameter value contains forbidden control character.");
                }
            }

            if (rawValue is JsonElement jsonObject && jsonObject.ValueKind == JsonValueKind.Object)
            {
                throw new ArgumentException($"SQL parameter '{key}' cannot be an object.");
            }

            // Check if list
            if (ListParameterNormalizer.IsListCandidate(rawValue))
            {
                var normalizedList = ListParameterNormalizer.Normalize(rawValue!, limits, null);
                if (normalizedList.Count > maxListElements)
                {
                    throw new WebSqlPolicyException($"List parameter '{key}' exceeds maximum element limit of {maxListElements}.");
                }

                // Check embedded NUL in list elements
                for (int i = 0; i < normalizedList.Count; i++)
                {
                    if (normalizedList[i] is string elemStr && elemStr.Contains('\0'))
                    {
                        throw new WebSqlSecurityException("Parameter value contains forbidden control character.");
                    }
                }

                listShapes[key] = normalizedList.Count;
                normalizedValues[key] = normalizedList;
            }
            else
            {
                var normalizedScalar = WebSqlParameterValues.Normalize(rawValue);
                if (normalizedScalar is string s && s.Contains('\0'))
                {
                    throw new WebSqlSecurityException("Parameter value contains forbidden control character.");
                }

                normalizedValues[key] = normalizedScalar;
            }
        }

        var astShapes = listShapes.Count > 0 ? ClientListShapes.Create(listShapes) : ClientListShapes.Empty;

        return new FederatedParameterContract(
            normalizedValues.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            listShapes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            astShapes,
            hasValues: true);
    }

    public static FederatedParameterContract ForAnalysis(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var norm = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var key = n.TrimStart('@');
            if (key.StartsWith("__param_", StringComparison.OrdinalIgnoreCase))
            {
                key = key["__param_".Length..];
            }
            norm[key] = null;
        }

        return new FederatedParameterContract(
            norm.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            FrozenDictionary<string, int>.Empty,
            ClientListShapes.Empty,
            hasValues: false);
    }

    public static void ValidateParameterName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.TrimStart('@');
        if (trimmed.StartsWith("__gql_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("p_rls_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("rls_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("__param_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("p_pd_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("__rls_ks", StringComparison.OrdinalIgnoreCase))
        {
            throw new WebSqlPolicyException("A request parameter uses a reserved gateway parameter name.");
        }
    }

    public bool TryGetScalar(string name, ColumnTypeClass columnClass, out object value)
    {
        value = default!;
        string cleanName = name.TrimStart('@');
        if (cleanName.StartsWith("__param_", StringComparison.OrdinalIgnoreCase))
        {
            cleanName = cleanName["__param_".Length..];
        }

        if (!_clientValues.TryGetValue(cleanName, out var rawVal) || rawVal == null)
        {
            return false;
        }

        if (rawVal is ClientListValue)
        {
            return false;
        }

        if (!IsLosslessTypeMatch(rawVal, columnClass))
        {
            return false;
        }

        value = rawVal;
        return true;
    }

    public bool TryGetList(string name, ColumnTypeClass columnClass, int maxElements, out IReadOnlyList<object> values)
    {
        values = default!;
        string cleanName = name.TrimStart('@');
        if (cleanName.StartsWith("__param_", StringComparison.OrdinalIgnoreCase))
        {
            cleanName = cleanName["__param_".Length..];
        }

        if (!_clientValues.TryGetValue(cleanName, out var rawVal) || rawVal is not ClientListValue list)
        {
            return false;
        }

        if (list.Count == 0 || list.Count > maxElements)
        {
            return false;
        }

        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (item == null || !IsLosslessTypeMatch(item, columnClass))
            {
                return false;
            }
        }

        var copy = new object[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            copy[i] = list[i];
        }
        values = copy;
        return true;
    }

    private static bool IsLosslessTypeMatch(object value, ColumnTypeClass columnClass)
    {
        return columnClass switch
        {
            ColumnTypeClass.Integer => value is sbyte or byte or short or ushort or int or uint or long,
            ColumnTypeClass.Decimal => value is sbyte or byte or short or ushort or int or uint or long or decimal,
            ColumnTypeClass.Boolean => value is bool,
            ColumnTypeClass.String => value is string,
            _ => false
        };
    }

    public IReadOnlyDictionary<string, object?> ToRequestParameters(IEnumerable<string> referencedNames)
    {
        ArgumentNullException.ThrowIfNull(referencedNames);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in referencedNames)
        {
            var clean = r.TrimStart('@');
            if (clean.StartsWith("__param_", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean["__param_".Length..];
            }

            if (_clientValues.TryGetValue(clean, out var val))
            {
                result[clean] = val;
            }
        }

        return result;
    }

    public FederatedParameterAudit ToAudit()
    {
        var entries = new List<FederatedParameterAuditEntry>(_clientValues.Count);
        int invalidNames = 0;

        foreach (var (key, val) in _clientValues)
        {
            if (!ValidNamePattern.IsMatch(key))
            {
                invalidNames++;
                continue;
            }

            if (val is ClientListValue list)
            {
                entries.Add(new FederatedParameterAuditEntry(key, "list", list.Count));
            }
            else
            {
                entries.Add(new FederatedParameterAuditEntry(key, "scalar", null));
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new FederatedParameterAudit(entries, invalidNames);
    }
}

public sealed record FederatedParameterAudit(
    [property: System.Text.Json.Serialization.JsonPropertyName("parameters")] IReadOnlyList<FederatedParameterAuditEntry> Parameters,
    [property: System.Text.Json.Serialization.JsonPropertyName("invalidParameterNames")] int InvalidParameterNames);

public sealed record FederatedParameterAuditEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("kind")] string Kind,
    [property: System.Text.Json.Serialization.JsonPropertyName("count")] int? Count);

public sealed record PushedParameterAudit(
    [property: System.Text.Json.Serialization.JsonPropertyName("parameterNames")] IReadOnlyList<string> ParameterNames,
    [property: System.Text.Json.Serialization.JsonPropertyName("listParameterCounts")] IReadOnlyDictionary<string, int> ListParameterCounts);
