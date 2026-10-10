namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>The key query of one structured Virtual Filter and its bound parameters (name with <c>@</c> prefix, value).</summary>
internal sealed record VirtualFilterKeyQuery(string Sql, IReadOnlyList<KeyValuePair<string, object>> Parameters);

/// <summary>
/// F-GOV-14 (M4, SEC-4, SEC-5, SEC-6): the query that reads the authorized key tuples of a structured Virtual Filter in its
/// key source:
/// <c>SELECT DISTINCT [TOP (n)] &lt;alias.key&gt; AS &lt;key&gt;, ... FROM ... [INNER JOIN ...] WHERE &lt;conditions&gt;
/// AND &lt;alias.tenant&gt; = @vf_tenant ... AND &lt;alias.key&gt; IS NOT NULL ... [LIMIT n]</c>.
/// The complete definition is evaluated in SQL (the same FROM/JOIN/condition rendering as the SQL-channel predicate);
/// every value and the tenant are bound parameters; the caller asks for <c>MaxAuthorizedKeys + 1</c> rows to detect an
/// overflow instead of truncating.
/// </summary>
internal static class StructuredFilterKeyQueryBuilder
{
    internal const string TenantParameter = "@vf_tenant";

    /// <param name="filter">A structured filter (validated identifiers).</param>
    /// <param name="dialect">Dialect of the key source: SQL Server, PostgreSQL or SQLite.</param>
    /// <param name="tenantColumnsByAlias">Tenant column per table alias of the definition (tables without one are absent).</param>
    /// <param name="tenantId">The caller's tenant (bound as <see cref="TenantParameter"/>).</param>
    /// <param name="maxRows">Row limit of the query (<c>MaxAuthorizedKeys + 1</c>).</param>
    internal static VirtualFilterKeyQuery Build(
        VirtualFilter filter,
        DatabaseDialect dialect,
        IReadOnlyDictionary<string, string> tenantColumnsByAlias,
        string tenantId,
        int maxRows)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(tenantColumnsByAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        if (dialect is not (DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite))
        {
            throw new NotSupportedException($"Virtual Filter key sources support SQL Server, PostgreSQL and SQLite, not {dialect}.");
        }

        var definition = filter.Structured ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no structured definition.");
        if (filter.KeyColumns.Count == 0)
        {
            throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no key columns.");
        }

        var parameters = new List<KeyValuePair<string, object>>();
        string Bind(string value)
        {
            var name = "@vf_p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new KeyValuePair<string, object>(name, ParameterValue(value)));
            return name;
        }

        var where = definition.Where.Select(c => StructuredFilterSqlParts.Condition(c, dialect, Bind)).ToList();

        // WI-19 (SEC-6): every tenanted table of the definition is restricted to the caller's tenant.
        var aliases = new[] { definition.FromAlias }.Concat(definition.Joins.Select(j => j.Alias));
        foreach (var alias in aliases)
        {
            if (tenantColumnsByAlias.TryGetValue(alias, out var tenantColumn))
            {
                where.Add($"{dialect.QuoteIdentifier(alias)}.{dialect.QuoteIdentifier(tenantColumn)} = {TenantParameter}");
            }
        }

        if (where.Any(w => w.Contains(TenantParameter, StringComparison.Ordinal)))
        {
            parameters.Add(new KeyValuePair<string, object>(TenantParameter, tenantId));
        }

        var keys = filter.KeyColumns.Select(k => dialect.QuoteQualifiedColumn(k)).ToList();
        where.AddRange(keys.Select(k => $"{k} IS NOT NULL"));

        var select = string.Join(", ", filter.KeyColumns.Select((k, i) => $"{keys[i]} AS {dialect.QuoteIdentifier(VirtualFilterNames.ColumnOf(k))}"));
        var limit = maxRows.ToString(CultureInfo.InvariantCulture);
        var sql = dialect == DatabaseDialect.SqlServer
            ? $"SELECT DISTINCT TOP ({limit}) {select} {StructuredFilterSqlParts.From(definition, dialect)} WHERE {string.Join(" AND ", where)}"
            : $"SELECT DISTINCT {select} {StructuredFilterSqlParts.From(definition, dialect)} WHERE {string.Join(" AND ", where)} LIMIT {limit}";
        return new VirtualFilterKeyQuery(sql, parameters);
    }

    /// <summary>
    /// The bound value of a condition: numbers like the SQL-channel literal (integral -> <see cref="long"/>, otherwise
    /// <see cref="decimal"/>), everything else as text.
    /// </summary>
    private static object ParameterValue(string value)
    {
        if (!StructuredFilterSqlBuilder.IsNumericLiteral(value))
        {
            return value;
        }

        var number = decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return decimal.Truncate(number) == number && !value.Contains('.', StringComparison.Ordinal) ? (long)number : number;
    }
}
