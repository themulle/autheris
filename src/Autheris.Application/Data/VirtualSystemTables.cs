namespace Autheris.Application.Data;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Registers and defines the canonical virtual system tables (governance.system.*)
/// accessible through the Governed REST Data API and WebSQL.
/// </summary>
public static class VirtualSystemTables
{
    public const string Domain = "governance";
    public const string Schema = "system";

    public static readonly TableIdentifier DataSourcesId = new(Domain, Schema, "datasources");
    public static readonly TableIdentifier PoliciesId = new(Domain, Schema, "policies");
    public static readonly TableIdentifier RebacTuplesId = new(Domain, Schema, "rebac_tuples");
    public static readonly TableIdentifier VirtualFiltersId = new(Domain, Schema, "virtual_filters");
    public static readonly TableIdentifier AuditTrailId = new(Domain, Schema, "audit_trail");

    public static readonly TableMetadata DataSourcesTable = new()
    {
        Identifier = DataSourcesId,
        Table = new Table
        {
            SourceName = Domain,
            SchemaName = Schema,
            TableName = "datasources",
            DisplayName = "Data Sources",
            Description = "Configured and active data sources in the Autheris gateway.",
            Sensitivity = "INTERNAL",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = new TableColumn[]
        {
            new() { ColumnName = "id", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "name", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "domain", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "type", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "base_url", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "is_configured", DataType = "boolean", IsSensitive = false },
            new() { ColumnName = "status", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "created_at", DataType = "timestamp", IsSensitive = false },
            new() { ColumnName = "updated_at", DataType = "timestamp", IsSensitive = false }
        },
        PrimaryKeyColumns = new[] { "id" }
    };

    public static readonly TableMetadata PoliciesTable = new()
    {
        Identifier = PoliciesId,
        Table = new Table
        {
            SourceName = Domain,
            SchemaName = Schema,
            TableName = "policies",
            DisplayName = "Governance Policies",
            Description = "Column-level masking and sensitivity classification policies.",
            Sensitivity = "INTERNAL",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = new TableColumn[]
        {
            new() { ColumnName = "id", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "table_id", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "column_name", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "sensitivity", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "masking_type", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "classification_tags", DataType = "varchar", IsSensitive = false }
        },
        PrimaryKeyColumns = new[] { "id" }
    };

    public static readonly TableMetadata RebacTuplesTable = new()
    {
        Identifier = RebacTuplesId,
        Table = new Table
        {
            SourceName = Domain,
            SchemaName = Schema,
            TableName = "rebac_tuples",
            DisplayName = "ReBAC Tuples",
            Description = "Relationship tuples (Google Zanzibar / OpenFGA) controlling authorization.",
            Sensitivity = "CONFIDENTIAL",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = new TableColumn[]
        {
            new() { ColumnName = "user", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "relation", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "object", DataType = "varchar", IsSensitive = false }
        },
        PrimaryKeyColumns = new[] { "user", "relation", "object" }
    };

    public static readonly TableMetadata VirtualFiltersTable = new()
    {
        Identifier = VirtualFiltersId,
        Table = new Table
        {
            SourceName = Domain,
            SchemaName = Schema,
            TableName = "virtual_filters",
            DisplayName = "Virtual Row Filters",
            Description = "Active mandatory row-level security filters applied at query time.",
            Sensitivity = "INTERNAL",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = new TableColumn[]
        {
            new() { ColumnName = "id", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "table_id", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "principal", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "filter_expression", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "valid_until", DataType = "timestamp", IsSensitive = false }
        },
        PrimaryKeyColumns = new[] { "id" }
    };

    public static readonly TableMetadata AuditTrailTable = new()
    {
        Identifier = AuditTrailId,
        Table = new Table
        {
            SourceName = Domain,
            SchemaName = Schema,
            TableName = "audit_trail",
            DisplayName = "Audit Trail",
            Description = "WORM-signed security and access audit logs.",
            Sensitivity = "CONFIDENTIAL",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = new TableColumn[]
        {
            new() { ColumnName = "id", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "timestamp", DataType = "timestamp", IsSensitive = false },
            new() { ColumnName = "actor_sid", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "channel", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "action", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "target", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "correlation_id", DataType = "varchar", IsSensitive = false },
            new() { ColumnName = "worm_signature", DataType = "varchar", IsSensitive = false }
        },
        PrimaryKeyColumns = new[] { "id" }
    };

    public static readonly IReadOnlyList<TableMetadata> All = new[]
    {
        DataSourcesTable,
        PoliciesTable,
        RebacTuplesTable,
        VirtualFiltersTable,
        AuditTrailTable
    };

    private static readonly Dictionary<TableIdentifier, TableMetadata> Lookup =
        All.ToDictionary(t => t.Identifier);

    public static bool TryGet(TableIdentifier identifier, out TableMetadata? metadata) =>
        Lookup.TryGetValue(identifier, out metadata);

    public static TableMetadata? Get(TableIdentifier identifier) =>
        Lookup.GetValueOrDefault(identifier);

    public static bool IsSystemTable(TableIdentifier identifier) =>
        string.Equals(identifier.Domain, Domain, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(identifier.Schema, Schema, StringComparison.OrdinalIgnoreCase);

    public static async Task RegisterSystemTablesAsync(ITableMetadataRepository repository, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        foreach (var table in All)
        {
            await repository.UpsertTableMetadataAsync(table, ct).ConfigureAwait(false);
        }
    }
}
