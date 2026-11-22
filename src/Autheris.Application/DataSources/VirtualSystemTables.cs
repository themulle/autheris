namespace Autheris.Application.DataSources;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
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
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = new TableColumn[]
        {
            new() { ColumnName = "id", DataType = "varchar" },
            new() { ColumnName = "name", DataType = "varchar" },
            new() { ColumnName = "domain", DataType = "varchar" },
            new() { ColumnName = "type", DataType = "varchar" },
            new() { ColumnName = "base_url", DataType = "varchar" },
            new() { ColumnName = "is_configured", DataType = "boolean" },
            new() { ColumnName = "status", DataType = "varchar" },
            new() { ColumnName = "created_at", DataType = "timestamp" },
            new() { ColumnName = "updated_at", DataType = "timestamp" }
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
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = new TableColumn[]
        {
            new() { ColumnName = "id", DataType = "varchar" },
            new() { ColumnName = "table_id", DataType = "varchar" },
            new() { ColumnName = "column_name", DataType = "varchar" },
            new() { ColumnName = "sensitivity", DataType = "varchar" },
            new() { ColumnName = "masking_type", DataType = "varchar" },
            new() { ColumnName = "classification_tags", DataType = "varchar" }
        },
        PrimaryKeyColumns = new[] { "id" }
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
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = new TableColumn[]
        {
            new() { ColumnName = "id", DataType = "varchar" },
            new() { ColumnName = "table_id", DataType = "varchar" },
            new() { ColumnName = "principal", DataType = "varchar" },
            new() { ColumnName = "filter_expression", DataType = "varchar" },
            new() { ColumnName = "valid_until", DataType = "timestamp" }
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
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns = new TableColumn[]
        {
            new() { ColumnName = "id", DataType = "varchar" },
            new() { ColumnName = "timestamp", DataType = "timestamp" },
            new() { ColumnName = "actor_sid", DataType = "varchar" },
            new() { ColumnName = "channel", DataType = "varchar" },
            new() { ColumnName = "action", DataType = "varchar" },
            new() { ColumnName = "target", DataType = "varchar" },
            new() { ColumnName = "correlation_id", DataType = "varchar" },
            new() { ColumnName = "worm_signature", DataType = "varchar" }
        },
        PrimaryKeyColumns = new[] { "id" }
    };

    public static readonly IReadOnlyList<TableMetadata> All = new[]
    {
        DataSourcesTable,
        PoliciesTable,
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

    public static Task RegisterSystemTablesAsync(ITableMetadataRepository repository, CancellationToken ct = default) =>
        RegisterSystemTablesAsync(repository, null, ct);

    public static async Task RegisterSystemTablesAsync(
        ITableMetadataRepository repository,
        Autheris.Application.Catalog.ICatalogWriteScopeFactory? catalogWriteScopeFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        await using var catalogWriteScope = Autheris.Application.Catalog.CatalogWriteScopeFactoryExtensions.BeginOrNoop(catalogWriteScopeFactory, "system-tables");
        foreach (var table in All)
        {
            // The in-code keys are the declared definition of the system tables: they are written (replaced) on every registration.
            await repository.UpsertTableMetadataAsync(table with { ReplaceKeyDefinition = true }, ct).ConfigureAwait(false);
        }
    }
}
