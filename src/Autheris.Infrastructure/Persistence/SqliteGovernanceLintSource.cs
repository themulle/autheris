namespace Autheris.Infrastructure.Persistence;

using System.Globalization;
using System.Text.Json;
using Autheris.Application.Policy.Lint;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;

/// <summary>
/// Reads the stored row predicates of a SQLite governance database for <c>tools/PolicyPredicateLint</c>: the catalog, access profile row
/// filters, virtual filters and correlated row filters of active consents (Casbin). The database is opened read-only and never migrated.
/// </summary>
public static class SqliteGovernanceLintSource
{
    public static async Task<PolicyLintInput> LoadAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var tables = await LoadCatalogAsync(connection, ct).ConfigureAwait(false);
        var profiles = await LoadAccessProfilesAsync(connection, ct).ConfigureAwait(false);
        var snapshot = await VirtualFilterStore.LoadSnapshotAsync(connection, ct).ConfigureAwait(false);
        var casbin = await LoadCorrelatedFiltersAsync(connection, tables, ct).ConfigureAwait(false);
        return new PolicyLintInput(tables.Values.ToList(), profiles, snapshot.Filters, casbin);
    }

    private static async Task<Dictionary<string, TableMetadata>> LoadCatalogAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = new Dictionary<string, List<TableColumn>>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT table_id, column_name, data_type FROM TABLE_COLUMNS";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                if (!columns.TryGetValue(id, out var list))
                {
                    columns[id] = list = [];
                }

                list.Add(new TableColumn { ColumnName = reader.GetString(1), DataType = reader.GetString(2) });
            }
        }

        var result = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, source_type, source_name, schema_name, table_name, data_source_type FROM TABLES WHERE is_active = 1";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                var table = new Table
                {
                    Id = Guid.TryParse(id, out var guid) ? guid : Guid.Empty,
                    SourceType = reader.GetString(1),
                    SourceName = reader.GetString(2),
                    SchemaName = reader.GetString(3),
                    TableName = reader.GetString(4),
                    DataSourceType = (DataSourceType)reader.GetInt32(5)
                };
                if (!TableIdentifier.TryParse($"{table.SourceName}.{table.SchemaName}.{table.TableName}", out var identifier))
                {
                    continue;
                }

                result[id] = new TableMetadata
                {
                    Table = table,
                    Identifier = identifier,
                    Columns = columns.TryGetValue(id, out var cols) ? cols : []
                };
            }
        }

        return result;
    }

    private static async Task<List<AccessProfile>> LoadAccessProfilesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var profiles = new List<AccessProfile>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT profile_id, tenant_id, name, target_tables, row_filter_predicate FROM ACCESS_PROFILES";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var targets = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [];
            profiles.Add(new AccessProfile
            {
                ProfileId = reader.GetString(0),
                TenantId = StoredTenant.Parse(reader.IsDBNull(1) ? null : reader.GetString(1)),
                Name = reader.GetString(2),
                TargetTables = targets,
                RowFilterPredicate = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }

        return profiles;
    }

    private static async Task<List<CasbinLintRule>> LoadCorrelatedFiltersAsync(
        SqliteConnection connection, Dictionary<string, TableMetadata> tables, CancellationToken ct)
    {
        var rules = new List<CasbinLintRule>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT f.id, f.consent_id, c.table_id, f.filter_group, f.table_column_id, f.column_name,
                                   f.operator, f.value_type, f.value_json, f.value_source, f.user_attribute,
                                   f.dependent_table, f.dependent_table_alias, f.foreign_key_column, f.primary_key_column,
                                   f.subquery_predicate_json, f.target_temporal_column, f.dependent_valid_from_column,
                                   f.dependent_valid_to_column, f.target_table_alias, f.additional_hops_json
                            FROM CONSENT_ROW_FILTERS f
                            JOIN CONSENTS c ON c.id = f.consent_id
                            WHERE f.filter_type = 1 AND c.is_revoked = 0";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            TableIdentifier? dependent = null;
            if (!reader.IsDBNull(11) && TableIdentifier.TryParse(reader.GetString(11), out var parsed))
            {
                dependent = parsed;
            }

            IReadOnlyList<SubqueryJoinHop>? hops = null;
            bool malformedHops = false;
            if (!reader.IsDBNull(20) && !string.IsNullOrWhiteSpace(reader.GetString(20)))
            {
                try
                {
                    hops = JsonSerializer.Deserialize<List<SubqueryJoinHop>>(reader.GetString(20));
                }
                catch (JsonException)
                {
                    // Production fails closed on malformed hops; an incomplete filter makes the lint report it.
                    malformedHops = true;
                }
            }

            var filter = new ConsentRowFilter
            {
                Id = Guid.TryParse(reader.GetString(0), out var id) ? id : Guid.Empty,
                ConsentId = Guid.TryParse(reader.GetString(1), out var consentId) ? consentId : Guid.Empty,
                FilterGroup = reader.GetInt32(3),
                ColumnName = reader.GetString(5),
                Operator = reader.GetString(6),
                ValueType = reader.GetString(7),
                ValueJson = reader.GetString(8),
                ValueSource = reader.GetString(9),
                UserAttribute = reader.IsDBNull(10) ? null : reader.GetString(10),
                FilterType = RowFilterType.SubqueryCorrelated,
                DependentTable = dependent,
                DependentTableAlias = reader.IsDBNull(12) ? null : reader.GetString(12),
                ForeignKeyColumn = reader.IsDBNull(13) ? null : reader.GetString(13),
                PrimaryKeyColumn = reader.IsDBNull(14) ? null : reader.GetString(14),
                SubqueryFilterPredicateJson = reader.IsDBNull(15) ? null : malformedHops ? "{" : reader.GetString(15),
                TargetTemporalColumn = reader.IsDBNull(16) ? null : reader.GetString(16),
                DependentValidFromColumn = reader.IsDBNull(17) ? null : reader.GetString(17),
                DependentValidToColumn = reader.IsDBNull(18) ? null : reader.GetString(18),
                TargetTableAlias = reader.IsDBNull(19) ? null : reader.GetString(19),
                AdditionalHops = hops
            };

            var tableId = tables.TryGetValue(reader.GetString(2), out var target) ? target.Identifier.ToString() : "-";
            rules.Add(new CasbinLintRule($"consent:{filter.ConsentId}/filter:{filter.Id}", tableId, null, filter));
        }

        return rules;
    }
}
