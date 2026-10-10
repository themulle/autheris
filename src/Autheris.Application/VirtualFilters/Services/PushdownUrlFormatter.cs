namespace Autheris.Application.VirtualFilters.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autheris.Domain.Model;

/// <summary>
/// Formats authorized key parameters for outbound Web-API pushdown requests (Tier 2).
/// </summary>
public static class PushdownUrlFormatter
{
    public static string FormatQueryString(
        string paramName,
        IReadOnlyList<string> keys,
        PushdownParameterFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paramName);
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return string.Empty;
        }

        switch (format)
        {
            case PushdownParameterFormat.CommaSeparated:
                return $"{paramName}={string.Join(",", keys.Select(Uri.EscapeDataString))}";

            case PushdownParameterFormat.RepeatedParam:
                return string.Join("&", keys.Select(k => $"{paramName}={Uri.EscapeDataString(k)}"));

            case PushdownParameterFormat.ODataIn:
                var escapedKeys = string.Join(", ", keys.Select(k => long.TryParse(k, out _) ? k : $"'{k.Replace("'", "''")}'"));
                return $"$filter={paramName} in ({escapedKeys})";

            case PushdownParameterFormat.PostBatch:
                return string.Empty;

            default:
                return $"{paramName}={string.Join(",", keys.Select(Uri.EscapeDataString))}";
        }
    }

    public static string FormatJsonBatchBody(string arrayPropertyName, IReadOnlyList<string> keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arrayPropertyName);
        ArgumentNullException.ThrowIfNull(keys);

        var elements = string.Join(",", keys.Select(k => $"\"{k.Replace("\"", "\\\"")}\""));
        return $"{{\"{arrayPropertyName}\":[{elements}]}}";
    }
}
