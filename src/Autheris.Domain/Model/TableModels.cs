namespace Autheris.Domain.Model;

public enum ConsentEffect
{
    Allow = 1,
    Deny = 2
}

public enum GranteeType
{
    User = 1,
    Group = 2,
    Role = 3,
    ServicePrincipal = 4
}

public sealed class Table
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SourceType { get; init; } = "PostgreSQL";
    public string SourceName { get; init; } = string.Empty;
    public string SchemaName { get; init; } = string.Empty;
    public string TableName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? LongDescription { get; init; }
    public string? DocumentationSource { get; init; }
    public string Sensitivity { get; init; } = "NORMAL";
    public bool RequiresFourEyes { get; init; }
    public bool IsActive { get; init; } = true;

    public DataSourceType DataSourceType { get; init; } = DataSourceType.Sql;
    public string? Location { get; init; }
    public HttpEndpointDescriptor? HttpEndpoint { get; init; }
    public string? PluginName { get; init; }

    public bool IsHighlySensitive =>
        string.Equals(Sensitivity, "HIGH", StringComparison.OrdinalIgnoreCase) || RequiresFourEyes;

    public DatabaseDialect Dialect => DatabaseDialectExtensions.ParseDialect(SourceType);

    public TableIdentifier ToIdentifier(string domain) =>
        new(domain, SchemaName, TableName);
}

public sealed class TableColumn
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TableId { get; init; }
    public string ColumnName { get; init; } = string.Empty;
    public string DataType { get; init; } = "varchar";
    public bool IsSensitive { get; init; }
    public string? Description { get; init; }
    public string? LongDescription { get; init; }
    public string? DocumentationSource { get; init; }
    public IReadOnlyDictionary<string, string> Meta { get; init; } = new Dictionary<string, string>();
}

public sealed class MaskingRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TableColumnId { get; init; }
    public string RuleType { get; init; } = "REDACT"; // REGEX, HMAC, REDACT, NULLIFY
    public string? PatternOrFormat { get; init; }
    public string? Replacement { get; init; }
    public string? HmacKeyId { get; init; }
}

public sealed class TableMetadata
{
    public Table Table { get; init; } = new();
    public TableIdentifier Identifier { get; init; }
    public IReadOnlyList<TableColumn> Columns { get; init; } = Array.Empty<TableColumn>();
    public IReadOnlyDictionary<string, MaskingRule> ColumnMaskingRules { get; init; } = new Dictionary<string, MaskingRule>();
    public IReadOnlyList<string> PrimaryKeyColumns { get; init; } = new[] { "id" };

    public bool IsCompositePrimaryKey => PrimaryKeyColumns.Count > 1;

    public DataSourceType DataSourceType => Table.DataSourceType;
    public HttpEndpointDescriptor? HttpEndpoint => Table.HttpEndpoint;
    public string? PluginName => Table.PluginName;

    public DatabaseDialect Dialect => Table.Dialect;

    public bool HasColumn(string columnName) =>
        Columns.Any(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Review E-5: the column that carries the tenant, whatever the source system calls it (<c>tenant_id</c>, <c>TenantId</c>,
    /// <c>tenantId</c>, <c>TENANT-ID</c>). An exact <c>tenant_id</c> wins. Null when the table has no tenant column.
    /// </summary>
    public string? TenantColumnName
    {
        get
        {
            var exact = Columns.FirstOrDefault(c => string.Equals(c.ColumnName, "tenant_id", StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact.ColumnName;
            }

            return Columns.FirstOrDefault(c =>
                string.Equals(c.ColumnName.Replace("_", string.Empty).Replace("-", string.Empty), "tenantid", StringComparison.OrdinalIgnoreCase))?.ColumnName;
        }
    }

    /// <summary>
    /// Review E-5: fail-closed guard for deployments that require every table to be tenant scoped. Returns normally when a tenant
    /// column exists, when the requirement is off or when the table is exempt; throws otherwise.
    /// </summary>
    public static string? RequireTenantColumnOrThrow(TableMetadata metadata, bool required, IEnumerable<string>? exemptTables)
    {
        var column = metadata.TenantColumnName;
        if (column != null || !required)
        {
            return column;
        }

        var full = $"{metadata.Identifier.Schema}.{metadata.Identifier.TableName}";
        if (exemptTables != null && exemptTables.Any(e =>
                string.Equals(e, full, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e, metadata.Identifier.TableName, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        throw new System.Security.SecurityException(
            $"Table '{full}' has no tenant column and is not listed in DataSources:TenantColumnExemptTables; access is refused (fail-closed).");
    }

    public TableColumn? GetColumn(string columnName) =>
        Columns.FirstOrDefault(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
}

public enum RelationCardinality
{
    OneToMany = 1,
    ManyToOne = 2
}

public sealed class TableRelation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ParentTableId { get; init; }
    public TableIdentifier ParentTableIdentifier { get; init; }
    public Guid ChildTableId { get; init; }
    public TableIdentifier ChildTableIdentifier { get; init; }
    public string RelationName { get; init; } = string.Empty;
    public IReadOnlyList<string> JoinKeysParent { get; init; } = new[] { "id" };
    public IReadOnlyList<string> JoinKeysChild { get; init; } = Array.Empty<string>();
    public bool IsComposite => JoinKeysParent.Count > 1;

    public string JoinKeyParent
    {
        get => JoinKeysParent.Count > 0 ? JoinKeysParent[0] : "id";
        init => JoinKeysParent = new[] { value };
    }

    public string JoinKeyChild
    {
        get => JoinKeysChild.Count > 0 ? JoinKeysChild[0] : string.Empty;
        init => JoinKeysChild = new[] { value };
    }

    public RelationCardinality Cardinality { get; init; } = RelationCardinality.OneToMany;
}
