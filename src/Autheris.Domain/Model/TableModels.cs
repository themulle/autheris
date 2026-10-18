namespace Autheris.Domain.Model;

using System.Collections.Immutable;
using Autheris.Domain.Exceptions;

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

/// <summary>
/// Issue #111: which restriction produced a catalog row. <see cref="Unmarked"/> (0) is the default and the most restrictive value:
/// consumers check positively (<c>== Full</c>, <c>== Granted</c> with a consent) and never treat <see cref="Unmarked"/> as granted.
/// </summary>
public enum CatalogView
{
    Unmarked = 0,
    Granted = 1,
    Summary = 2,
    Full = 3
}

public enum DatasetVisibility
{
    Hidden = 0,
    Summary = 1,
    Full = 2
}

/// <summary>
/// Strategy applied when an aggregation group has fewer rows than the minimum group size threshold (#37).
/// </summary>
public enum SmallGroupStrategy
{
    Suppress = 0,
    MergeIntoOther = 1,
    MergeToOther = 2
}

public sealed record Table
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
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public DatasetVisibility Visibility { get; init; } = DatasetVisibility.Hidden;
    public string? RlsExpression { get; init; }
    public int? MinGroupSize { get; init; }
    public SmallGroupStrategy? SmallGroupStrategy { get; init; }
    /// <summary>Issue #163: Table-level required filter columns enforced across every read path.</summary>
    public ImmutableArray<string> RequiredFilterColumns { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Rank of a sensitivity class; unknown classes rank like HIGH (fail-closed). Empty means NORMAL.</summary>
    public static int SensitivityRank(string? sensitivity)
    {
        var s = (sensitivity ?? "NORMAL").Trim().ToUpperInvariant();
        if (s.Length > 2 && char.IsAsciiDigit(s[0]) && s[1] == '_')
        {
            s = s[2..];
        }
        return s switch
        {
            "LOW" or "PUBLIC" => 1,
            "" or "NORMAL" or "INTERNAL" or "MEDIUM" or "UNKNOWN_CUSTOM" => 2,
            "CONFIDENTIAL" or "RESTRICTED" => 3,
            "HIGH" or "SECRET" or "STRICTLY_CONFIDENTIAL" => 4,
            _ => 4
        };
    }

    /// <summary>
    /// D-4 (ADR-010): CONFIDENTIAL and above count as highly sensitive (four eyes, shorter consent TTL, degraded mode,
    /// Backstage): CONFIDENTIAL, HIGH, RESTRICTED, SECRET and every unknown value (e.g. PII). PUBLIC, LOW, INTERNAL,
    /// NORMAL and MEDIUM do not. Same ranking as the catalog governance ratchet.
    /// </summary>
    public static bool IsSensitivityHigh(string? sensitivity) =>
        !string.IsNullOrWhiteSpace(sensitivity) && SensitivityRank(sensitivity) >= 3;

    public bool IsHighlySensitive =>
        RequiresFourEyes || IsSensitivityHigh(Sensitivity);

    /// <summary>
    /// D-1: SQL tables fail closed on an unknown source type. Other sources (HTTP, plugins, lakehouse) never send SQL to a
    /// database; their dialect only shapes the in-memory row filter, so they keep the neutral PostgreSQL default.
    /// </summary>
    public DatabaseDialect Dialect => DataSourceType == DataSourceType.Sql
        ? DatabaseDialectExtensions.ParseDialect(SourceType)
        : DatabaseDialectExtensions.TryParseDialect(SourceType, out var dialect) ? dialect : DatabaseDialect.PostgreSql;

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

    public ColumnSemantics Semantics => new(
        Role: Meta.TryGetValue("role", out var r) ? r : null,
        Unit: Meta.TryGetValue("unit", out var u) ? u : null,
        BusinessName: Meta.TryGetValue("business_name", out var bn) ? bn : null,
        RawMeta: Meta
    );
}


/// <summary>A declared index: ordered columns and the unique flag.</summary>
public sealed record TableIndexDefinition(string Name, IReadOnlyList<string> Columns, bool IsUnique);

/// <summary>The declared key definition of a table: ordered primary key and indexes (identifiers only, no data).</summary>
public sealed record TableKeyDefinition(IReadOnlyList<string> PrimaryKey, IReadOnlyList<TableIndexDefinition> Indexes);

/// <summary>Result of a narrow key definition replacement (audit before/after). <c>BeforeUnavailable</c>: the stored definition was invalid and is not reported.</summary>
public sealed record TableKeyDefinitionChange(TableKeyDefinition Before, TableKeyDefinition After, bool BeforeUnavailable = false);

/// <summary>Issue #80: Controls how much of a column's value profile is published when active row filters apply to a table.</summary>
public enum DatasetProfilePublicationPolicy
{
    /// <summary>Default when row filters apply: exposes only FormatPattern, TypicalLength and coarsened EmptyShare.</summary>
    FormatAndEmptyShareOnly = 0,

    /// <summary>Data owner opt-in: exposes the full coarsened value profile even when row filters apply.</summary>
    FullCoarsenedProfile = 1
}

public sealed record TableMetadata
{
    public Table Table { get; init; } = new();
    public TableIdentifier Identifier { get; init; }
    public string? OwnerTenantId { get; init; }
    public IReadOnlyList<TableColumn> Columns { get; init; } = Array.Empty<TableColumn>();
    public IReadOnlyDictionary<string, MaskingRule> ColumnMaskingRules { get; init; } = new Dictionary<string, MaskingRule>();
    /// <summary>Declared primary key (ordered). Empty when none was declared; no key is ever inferred.</summary>
    public IReadOnlyList<string> PrimaryKeyColumns { get; init; } = Array.Empty<string>();

    /// <summary>Declared secondary and unique indexes (used for default ordering). Empty when none was declared.</summary>
    public IReadOnlyList<TableIndexDefinition> Indexes { get; init; } = Array.Empty<TableIndexDefinition>();

    /// <summary>Issue #107: object-level row filter evaluation settings (loaded from TABLE_ROW_FILTER_STRATEGIES, never written by the metadata upsert). Null = inherit global.</summary>
    public RowFilterEvaluationSettings? RowFilterEvaluation { get; init; }

    /// <summary>Issue #111: which restriction applied to this row; set explicitly by the visibility filter, default <see cref="Model.CatalogView.Unmarked"/> (most restrictive).</summary>
    public CatalogView CatalogView { get; init; } = CatalogView.Unmarked;

    /// <summary>Issue #111: persisted dataset tags (admin and dbt source), overlaid by the repositories. <see cref="Table.Tags"/> is never written by repositories; tags are not policy inputs.</summary>
    public IReadOnlyList<string> CatalogTags { get; init; } = Array.Empty<string>();

    /// <summary>Write intent: the upsert replaces the stored key/index definition only when true; otherwise it keeps it.</summary>
    public bool ReplaceKeyDefinition { get; init; }

    /// <summary>Issue #80: When true, callers with Full catalog visibility may view coarsened value profiles and request synthetic sample rows without an active table consent.</summary>
    public bool ReleaseProfileToCatalogViewers { get; init; }

    /// <summary>Issue #80: Controls whether full coarsened ranges/distinct counts or only format + empty share are published when row filters apply.</summary>
    public DatasetProfilePublicationPolicy ProfilePublicationPolicy { get; init; } = DatasetProfilePublicationPolicy.FormatAndEmptyShareOnly;

    /// <summary>Issue #80: Optional estimated row count of the table used for small-table privacy coarsening.</summary>
    public long? EstimatedRowCount { get; init; }

    /// <summary>Per-table minimum group size override for governed aggregations (#37). Falls back to <see cref="Table.MinGroupSize"/>.</summary>
    public int? MinGroupSize { get; init; }

    /// <summary>Per-table small group handling strategy override (#37). Falls back to <see cref="Table.SmallGroupStrategy"/>.</summary>
    public SmallGroupStrategy? SmallGroupStrategy { get; init; }

    public int? EffectiveMinGroupSize => MinGroupSize ?? Table.MinGroupSize;
    public SmallGroupStrategy? EffectiveSmallGroupStrategy => SmallGroupStrategy ?? Table.SmallGroupStrategy;

    public bool IsCompositePrimaryKey => PrimaryKeyColumns.Count > 1;
    public IReadOnlyList<string> RequiredFilterColumns => Table.RequiredFilterColumns;

    /// <summary>
    /// D-5: the record's generated ToString() printed every property, including masking rules with their HMAC key ids.
    /// Only the identifier is printed, so logging a metadata object cannot disclose masking configuration.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Identifier = ").Append(Identifier.ToString());
        return true;
    }

    public DataSourceType DataSourceType => Table.DataSourceType;
    public HttpEndpointDescriptor? HttpEndpoint => Table.HttpEndpoint;
    public string? PluginName => Table.PluginName;
    /// <summary>Issue #163: Table-level required filter columns enforced across every read path.</summary>
    public ImmutableArray<string> RequiredFilterColumns =>
        Table.RequiredFilterColumns.IsDefault ? ImmutableArray<string>.Empty : Table.RequiredFilterColumns;

    /// <summary>
    /// Validates required filter columns on catalog ingestion or load (SEC-GOV163-01, fail closed).
    /// </summary>
    public void ValidateRequiredFilterColumns()
    {
        if (RequiredFilterColumns.IsDefaultOrEmpty)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var colName in RequiredFilterColumns)
        {
            if (string.IsNullOrWhiteSpace(colName))
            {
                throw new CatalogValidationException($"Table '{Identifier}' contains an empty required filter column name.");
            }

            var trimmed = colName.Trim();
            if (!HasColumn(trimmed))
            {
                throw new CatalogValidationException($"Required filter column '{trimmed}' does not exist in table '{Identifier}'.");
            }

            if (!seen.Add(trimmed))
            {
                throw new CatalogValidationException($"Table '{Identifier}' lists required filter column '{trimmed}' more than once.");
            }
        }
    }

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

    /// <summary>Issue #77: Controls whether changesSince discloses all deleted keys or only caller-known keys under RLS.</summary>
    public DeletedKeyPolicy DeletedKeyPolicy { get; init; } = DeletedKeyPolicy.KnownKeysOnly;
}

/// <summary>
/// Issue #77: Policy controlling disclosure of physically deleted primary keys in <c>changesSince</c> responses when RLS applies.
/// </summary>
public enum DeletedKeyPolicy
{
    /// <summary>Default (fail-closed): only deleted keys present in the caller's <c>KnownKeys</c> or proven via pre-image RLS evaluation are disclosed.</summary>
    KnownKeysOnly = 0,

    /// <summary>Opt-in: all deleted primary keys are returned when the caller has Clear access to every primary key column.</summary>
    AllDeletedKeys = 1
}

public enum RelationCardinality
{
    OneToMany = 1,
    ManyToOne = 2,
    OneToOne = 3
}

public sealed class TableRelation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ParentTableId { get; init; }
    public TableIdentifier ParentTableIdentifier { get; init; }
    public Guid ChildTableId { get; init; }
    public TableIdentifier ChildTableIdentifier { get; init; }
    public string RelationName { get; init; } = string.Empty;
    public IReadOnlyList<string> JoinKeysParent { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> JoinKeysChild { get; init; } = Array.Empty<string>();
    public bool IsComposite => JoinKeysParent.Count > 1;

    public RelationCardinality Cardinality { get; init; } = RelationCardinality.OneToMany;

    /// <summary>Issue #75: Explicitly enables resolving this relation across distinct data sources via gateway-level batched key lookup.</summary>
    public bool AllowCrossDataSource { get; init; }
}
