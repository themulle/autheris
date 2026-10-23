namespace Autheris.Application.Data;

using System;
using System.Collections.Generic;
using System.Globalization;
using Autheris.Domain.Common;

/// <summary>
/// VF-03: the single in-memory tenant check for rows read from a data source. Defense in depth behind the SQL tenant predicate: a row of a
/// tenanted table without a tenant value (missing column, NULL, DBNull) or with another tenant never passes. The comparison is ordinal.
/// </summary>
internal static class TenantRowGuard
{
    public static bool Matches(IReadOnlyDictionary<string, object?> row, string? tenantColumn, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (tenantColumn == null)
        {
            return true;   // a table without tenant scoping
        }

        if (!TryGetValue(row, tenantColumn, out var value) || value is null or DBNull)
        {
            return false;
        }

        return string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), tenant.Value, StringComparison.Ordinal);
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, object?> row, string column, out object? value)
    {
        if (row.TryGetValue(column, out value))
        {
            return true;
        }

        foreach (var (key, candidate) in row)
        {
            if (string.Equals(key, column, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = null;
        return false;
    }
}
