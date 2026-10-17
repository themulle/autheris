namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Globalization;
using Autheris.Domain.Common;

/// <summary>The fixed lookup an attribute-table provider performs (PLAN-GOV-129 A-5.2). Identifiers only; no user text.</summary>
public sealed record AttributeTableSpec(
    string DataSource,
    string? Schema,
    string Table,
    string SubjectKeyColumn,
    SubjectKeyKind KeyKind,
    string ValueColumn,
    string? TenantColumn,
    string? IssuerColumn)
{
    public const string DataSourceKey = "dataSource";
    public const string SchemaKey = "schema";
    public const string TableKey = "table";
    public const string KeyColumnKey = "subjectKeyColumn";
    public const string KeyKindKey = "subjectKeyKind";
    public const string ValueColumnKey = "valueColumn";
    public const string TenantColumnKey = "tenantColumn";
    public const string IssuerColumnKey = "issuerColumn";

    /// <summary><c>schema.table</c> (or just <c>table</c>), the form used by the catalog exposure check and the exempt-table list.</summary>
    public string QualifiedName => string.IsNullOrEmpty(Schema) ? Table : $"{Schema}.{Table}";

    /// <summary>Parses and validates the provider configuration; false with a stable reason code never containing a configured value.</summary>
    public static bool TryParse(IReadOnlyDictionary<string, string> config, out AttributeTableSpec? spec, out string error)
    {
        spec = null;
        error = string.Empty;
        string? Get(string key) => config.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        var dataSource = Get(DataSourceKey);
        var table = Get(TableKey);
        var key = Get(KeyColumnKey);
        var value = Get(ValueColumnKey);
        var schema = Get(SchemaKey);
        var tenant = Get(TenantColumnKey);
        var issuer = Get(IssuerColumnKey);
        if (dataSource is null || table is null || key is null || value is null)
        {
            error = "MissingConfiguration";
            return false;
        }

        if (!Enum.TryParse<SubjectKeyKind>(Get(KeyKindKey), ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            error = "InvalidKeyKind";
            return false;
        }

        foreach (var identifier in new[] { schema, table, key, value, tenant, issuer })
        {
            if (identifier is not null && !IsSafeIdentifier(identifier))
            {
                error = "UnsafeIdentifier";
                return false;
            }
        }

        spec = new AttributeTableSpec(dataSource, schema, table, key, kind, value, tenant, issuer);
        return true;
    }

    /// <summary>
    /// <c>^[A-Za-z_][A-Za-z0-9_]{0,127}$</c>: no <c>#</c> (SQL Server temp tables), no <c>$</c>, no dot, quote, bracket, comment marker or unicode (BC-129-11).
    /// </summary>
    public static bool IsSafeIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier.Length > 128)
        {
            return false;
        }

        for (int i = 0; i < identifier.Length; i++)
        {
            char c = identifier[i];
            bool letter = (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || c == '_';
            bool digit = c is >= '0' and <= '9';
            if (!(letter || (digit && i > 0)))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The statement and its bound parameters.</summary>
public sealed record AttributeTableStatement(string Sql, IReadOnlyList<KeyValuePair<string, object>> Parameters, int ValueOrdinal, int? TenantOrdinal, int? IssuerOrdinal);

/// <summary>
/// Builds <c>SELECT value[, tenant][, issuer] FROM schema.table WHERE key = @subject [AND tenant = @tenant] [AND issuer = @issuer] AND value IS NOT NULL</c>
/// with <c>Max + 1</c> rows (BC-129-09): the gateway builds it from validated identifiers, the subject, tenant and issuer are bound.
/// </summary>
public static class AttributeTableQuery
{
    public const string SubjectParameter = "ca_subject";
    public const string TenantParameter = "ca_tenant";
    public const string IssuerParameter = "ca_issuer";

    public static AttributeTableStatement Build(AttributeTableSpec spec, DatabaseDialect dialect, string tenant, string subjectKey, string? issuer, int maxValues)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrEmpty(tenant);
        ArgumentException.ThrowIfNullOrEmpty(subjectKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValues, 1);
        if (dialect is not (DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite))
        {
            throw new NotSupportedException("Attribute tables support SQL Server, PostgreSQL and SQLite.");
        }

        string Q(string id) => dialect.QuoteIdentifier(id);
        string Marker(string name) => dialect.FormatParameterMarker(name);
        // Tenant and issuer match ordinally in the database (BC-129-11), so the Max+1 limit counts only exactly matching rows.
        string Ordinal() => dialect switch
        {
            DatabaseDialect.SqlServer => " COLLATE Latin1_General_100_BIN2",
            DatabaseDialect.PostgreSql => " COLLATE \"C\"",
            _ => " COLLATE BINARY",
        };

        var select = new List<string> { Q(spec.ValueColumn) };
        int? tenantOrdinal = null;
        int? issuerOrdinal = null;
        var where = new List<string> { $"{Q(spec.SubjectKeyColumn)} = {Marker(SubjectParameter)}" };
        var parameters = new List<KeyValuePair<string, object>> { new(dialect.FormatParameterName(SubjectParameter), subjectKey) };
        if (spec.TenantColumn is not null)
        {
            tenantOrdinal = select.Count;
            select.Add(Q(spec.TenantColumn));
            where.Add($"{Q(spec.TenantColumn)}{Ordinal()} = {Marker(TenantParameter)}");
            parameters.Add(new(dialect.FormatParameterName(TenantParameter), tenant));
        }

        if (spec.IssuerColumn is not null)
        {
            if (string.IsNullOrEmpty(issuer))
            {
                throw new InvalidOperationException("The attribute table needs the issuer of the subject.");
            }

            issuerOrdinal = select.Count;
            select.Add(Q(spec.IssuerColumn));
            where.Add($"{Q(spec.IssuerColumn)}{Ordinal()} = {Marker(IssuerParameter)}");
            parameters.Add(new(dialect.FormatParameterName(IssuerParameter), issuer));
        }

        where.Add($"{Q(spec.ValueColumn)} IS NOT NULL");
        string from = string.IsNullOrEmpty(spec.Schema) ? Q(spec.Table) : $"{Q(spec.Schema)}.{Q(spec.Table)}";
        string limit = (maxValues + 1).ToString(CultureInfo.InvariantCulture);
        string columns = string.Join(", ", select);
        string conditions = string.Join(" AND ", where);
        string sql = dialect == DatabaseDialect.SqlServer
            ? $"SELECT TOP ({limit}) {columns} FROM {from} WHERE {conditions}"
            : $"SELECT {columns} FROM {from} WHERE {conditions} LIMIT {limit}";
        return new AttributeTableStatement(sql, parameters, 0, tenantOrdinal, issuerOrdinal);
    }
}
