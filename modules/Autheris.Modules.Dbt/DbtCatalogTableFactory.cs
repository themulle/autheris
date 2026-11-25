namespace Autheris.Modules.Dbt;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Issue #282: builds the catalog entry for a dbt model or source that has none yet. Pure data mapping: no connection is opened,
/// no SQL is run and no manifest text is interpolated into SQL. Every failure is a fixed reason code (fail-closed).
/// </summary>
internal static class DbtCatalogTableFactory
{
    /// <summary>columns per registered table; a larger manifest entry is skipped as a whole.</summary>
    internal const int MaxColumnsPerTable = 1000;

    internal static bool IsValidCatalogIdentifier(string? value)
        => SqlIdentifierGuard.IsValidIdentifier(value);

    internal static bool TryBuild(
        DbtModelDefinition model,
        GatewayOptions options,
        out TableMetadata? metadata,
        out string? reason,
        bool applyObjectKind = true,
        ICollection<string>? warnings = null)
    {
        metadata = null;
        reason = null;

        if (!TryResolveConnection(model.Database, options, out var connectionName, out var provider))
        {
            reason = DbtSyncSkipReasons.NoDataSourceMapping;
            return false;
        }

        var sourceType = DatabaseDialectExtensions.ResolveCatalogSourceType(provider, null, out var supported);
        if (!supported)
        {
            reason = DbtSyncSkipReasons.DataSourceNotSql;
            return false;
        }

        if (string.Equals(model.Materialization, "ephemeral", StringComparison.OrdinalIgnoreCase) ||
            model.Columns.Count > MaxColumnsPerTable)
        {
            reason = DbtSyncSkipReasons.Ineligible;
            return false;
        }

        if (!IsValidCatalogIdentifier(model.Database) ||
            !IsValidCatalogIdentifier(model.Schema) ||
            !IsValidCatalogIdentifier(model.Name) ||
            !IsValidCatalogIdentifier(model.PhysicalName))
        {
            reason = DbtSyncSkipReasons.InvalidIdentifier;
            return false;
        }

        foreach (var (key, column) in model.Columns)
        {
            if (!IsValidCatalogIdentifier(key) || !IsValidCatalogIdentifier(column.Name))
            {
                reason = DbtSyncSkipReasons.InvalidIdentifier;
                return false;
            }
        }

        var objectKind = DiscoveryObjectKind.Table;
        string? countMeasure = null;
        if (applyObjectKind && !DbtObjectKindResolver.TryResolveForIngestion(model, warnings, out objectKind, out countMeasure))
        {
            reason = DbtSyncSkipReasons.Ineligible;
            return false;
        }

        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceType = sourceType,
            SourceName = connectionName,
            SchemaName = model.Schema,
            TableName = model.PhysicalName,
            DisplayName = model.Name,
            DbtUniqueId = model.UniqueId,
            Description = model.Description,
            IsActive = options.Dbt.ActivateNewTables,
            DataSourceType = DataSourceType.Sql,
            ObjectKind = objectKind,
            MetricViewCountMeasure = countMeasure
        };

        var columns = new List<TableColumn>(model.Columns.Count);
        foreach (var (_, column) in model.Columns)
        {
            if (!DbtObjectKindResolver.TryResolveColumnRole(column, objectKind, model.Name, warnings, out var role))
            {
                reason = DbtSyncSkipReasons.Ineligible;
                return false;
            }

            columns.Add(new TableColumn
            {
                Id = Guid.NewGuid(),
                TableId = table.Id,
                ColumnName = column.Name,
                DataType = string.IsNullOrWhiteSpace(column.DataType) ? "string" : column.DataType.Trim(),
                Description = column.Description,
                DiscoveryHidden = objectKind == DiscoveryObjectKind.MetricView && role is null,
                Role = role,
                Meta = column.Meta.Count > 0 ? new Dictionary<string, string>(column.Meta, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>()
            });
        }

        metadata = new TableMetadata
        {
            Identifier = model.ToTableIdentifier(),
            Table = table,
            Columns = columns
        };
        return true;
    }

    /// <summary>mapping entry or the database name itself, looked up in the configured connections; never a fallback.</summary>
    internal static bool TryResolveConnection(string database, GatewayOptions options, out string connectionName, out string provider)
    {
        connectionName = string.Empty;
        provider = string.Empty;

        if (string.IsNullOrWhiteSpace(database) || database == "*")
        {
            return false;
        }

        var target = options.Dbt.DatabaseMappings.TryGetValue(database, out var mapped) ? mapped : database;
        if (string.IsNullOrWhiteSpace(target) || target == "*")
        {
            return false;
        }

        foreach (var (key, connection) in options.DataSources.Connections)
        {
            if (string.Equals(key, target, StringComparison.OrdinalIgnoreCase))
            {
                connectionName = key;
                provider = connection.Provider;
                return true;
            }
        }

        return false;
    }
}
