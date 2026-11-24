namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using static Autheris.Infrastructure.Persistence.GovernanceRepository.ClassificationsSql;

public partial class GovernanceRepository : IClassificationRepository
{
    public async Task<IReadOnlyList<DataClassification>> GetClassificationsAsync(
        string? tenantScope,
        bool includeInactive = false,
        CancellationToken ct = default)
    {
        var validatedTenant = ValidateOptionalTenantFilter(tenantScope);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ReadClassificationsInternalAsync(conn, null, validatedTenant, includeInactive, ct).ConfigureAwait(false);
    }

    public async Task<DataClassification?> GetClassificationAsync(
        string scope,
        string code,
        CancellationToken ct = default)
    {
        var validScope = ClassificationScopes.ValidateScope(scope, allowGlobal: true);
        ValidateClassificationCodeArg(code, nameof(code));

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ClassificationsSql.SelectClassificationByKeySql;
        cmd.AddBound("@scope", validScope);
        cmd.AddBound("@code", code);

        await using var reader = await ExecuteReaderAsync(cmd, "Classifications.GetOne", ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadClassification(reader) : null;
    }

    public async Task<DataClassification> UpsertClassificationAsync(
        DataClassification classification,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(classification);
        var validScope = ClassificationScopes.ValidateScope(classification.Scope, allowGlobal: true);
        var normalized = classification with { Scope = validScope };

        DataClassification? stored;
        CatalogVersion? newVersion = null;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var scopeFilter = ClassificationScopes.IsGlobal(validScope) ? null : validScope;
                var rows = await ReadClassificationsInternalAsync(conn, tx, scopeFilter, includeInactive: true, ct).ConfigureAwait(false);
                var effective = BuildEffectiveCatalog(rows, validScope);
                ClassificationHierarchyValidator.ValidateDefinition(normalized, effective);

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = IsPostgreSql ? ClassificationsSql.UpsertClassificationSqlPg : ClassificationsSql.UpsertClassificationSqlSs;
                    cmd.AddBound("@scope", normalized.Scope);
                    cmd.AddBound("@code", normalized.Code);
                    cmd.AddBound("@display_name", normalized.DisplayName);
                    cmd.AddBound("@description", normalized.Description);
                    cmd.AddBound("@parent_code", normalized.ParentCode);
                    cmd.AddBound("@restriction_rank", normalized.RestrictionRank);
                    cmd.AddBound("@is_active", normalized.IsActive);
                    cmd.AddBound("@version", Math.Max(1, normalized.Version));
                    cmd.AddBound("@updated_by", normalized.UpdatedBy);
                    cmd.AddBound("@updated_at", normalized.UpdatedAt.ToUniversalTime(), DbType.DateTimeOffset);
                    await ExecuteNonQueryAsync(cmd, "Classifications.Upsert", ct).ConfigureAwait(false);
                }

                stored = await ReadClassificationInTxAsync(conn, tx, normalized.Scope, normalized.Code, ct).ConfigureAwait(false);
                newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        if (newVersion.HasValue)
        {
            await _writeInvalidator.InvalidateWideAsync(TryScopeTenant(normalized.Scope), newVersion.Value, ct: ct).ConfigureAwait(false);
            await FlushScopePendingAsync(ct).ConfigureAwait(false);
        }

        return stored ?? normalized;
    }

    public async Task<bool> IsClassificationReferencedAsync(
        string scope,
        string code,
        CancellationToken ct = default)
    {
        var validScope = ClassificationScopes.ValidateScope(scope, allowGlobal: true);
        ValidateClassificationCodeArg(code, nameof(code));

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        if (ClassificationScopes.IsGlobal(validScope))
        {
            cmd.CommandText = ClassificationsSql.CountClassificationReferencesGlobalSql;
        }
        else
        {
            cmd.CommandText = ClassificationsSql.CountClassificationReferencesScopedSql;
            cmd.AddBound("@scope", validScope);
        }

        cmd.AddBound("@code", code);
        var count = await ExecuteScalarAsync(cmd, "Classifications.CountReferences", ct).ConfigureAwait(false);
        return count is not null and not DBNull && Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public async Task<IReadOnlyList<ClassificationMaskRule>> GetClassificationMaskRulesAsync(
        string? tenantScope,
        CancellationToken ct = default)
    {
        var validatedTenant = ValidateOptionalTenantFilter(tenantScope);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        if (validatedTenant is null)
        {
            cmd.CommandText = ClassificationsSql.SelectMaskRulesGlobalOnlySql;
        }
        else
        {
            cmd.CommandText = ClassificationsSql.SelectMaskRulesScopedSql;
            cmd.AddBound("@scope", validatedTenant);
        }

        var list = new List<ClassificationMaskRule>();
        await using var reader = await ExecuteReaderAsync(cmd, "Classifications.GetMaskRules", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadMaskRule(reader));
        }

        return list;
    }

    public async Task<ClassificationMaskRule?> GetClassificationMaskRuleAsync(
        string scope,
        string code,
        CancellationToken ct = default)
    {
        var validScope = ClassificationScopes.ValidateScope(scope, allowGlobal: true);
        ValidateMaskRuleCodeArg(code, nameof(code));

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ReadMaskRuleInTxAsync(conn, null, validScope, code, ct).ConfigureAwait(false);
    }

    public async Task<ClassificationMaskRule> UpsertClassificationMaskRuleAsync(
        ClassificationMaskRule rule,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var validScope = ClassificationScopes.ValidateScope(rule.Scope, allowGlobal: true);
        ValidateMaskRuleCodeArg(rule.Code, nameof(rule));
        var normalized = rule with { Scope = validScope };

        ClassificationMaskRule? stored;
        CatalogVersion? newVersion;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = IsPostgreSql ? ClassificationsSql.UpsertMaskRuleSqlPg : ClassificationsSql.UpsertMaskRuleSqlSs;
                    cmd.AddBound("@scope", normalized.Scope);
                    cmd.AddBound("@code", normalized.Code);
                    cmd.AddBound("@mask_function", ClassificationMaskFunctions.ToCode(normalized.MaskFunction));
                    cmd.AddBound("@params_json", normalized.ParamsJson);
                    cmd.AddBound("@key_ref", normalized.KeyRef);
                    cmd.AddBound("@catalog_text", ClassificationCatalogTextVisibilities.ToCode(normalized.CatalogText));
                    cmd.AddBound("@version", Math.Max(1, normalized.Version));
                    cmd.AddBound("@updated_by", normalized.UpdatedBy);
                    cmd.AddBound("@updated_at", normalized.UpdatedAt.ToUniversalTime(), DbType.DateTimeOffset);
                    await ExecuteNonQueryAsync(cmd, "Classifications.UpsertMaskRule", ct).ConfigureAwait(false);
                }

                stored = await ReadMaskRuleInTxAsync(conn, tx, normalized.Scope, normalized.Code, ct).ConfigureAwait(false);
                newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        await _writeInvalidator.InvalidateWideAsync(TryScopeTenant(normalized.Scope), newVersion.Value, ct: ct).ConfigureAwait(false);
        await FlushScopePendingAsync(ct).ConfigureAwait(false);
        return stored ?? normalized;
    }

    public async Task<IReadOnlyList<ColumnClassification>> GetColumnClassificationsAsync(
        string tableColumnId, string? tenantScope = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableColumnId);
        return (await GetColumnClassificationsBatchAsync([tableColumnId], tenantScope, ct).ConfigureAwait(false)).TryGetValue(tableColumnId, out var rows) ? rows : [];
    }

    public async Task AssignColumnClassificationAsync(
        ColumnClassification assignment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var validScope = ClassificationScopes.ValidateScope(assignment.Scope, allowGlobal: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignment.TableColumnId);
        ValidateClassificationCodeArg(assignment.Code, nameof(assignment));
        ArgumentException.ThrowIfNullOrWhiteSpace(assignment.Source);

        TableIdentifier? table;
        CatalogVersion newVersion;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = IsPostgreSql ? ClassificationsSql.UpsertColumnClassificationSqlPg : ClassificationsSql.UpsertColumnClassificationSqlSs;
                    cmd.AddBound("@scope", validScope);
                    cmd.AddBound("@table_column_id", assignment.TableColumnId);
                    cmd.AddBound("@code", assignment.Code);
                    cmd.AddBound("@source", assignment.Source);
                    cmd.AddBound("@assigned_by", assignment.AssignedBy);
                    cmd.AddBound("@assigned_at", assignment.AssignedAt.ToUniversalTime(), DbType.DateTimeOffset);
                    await ExecuteNonQueryAsync(cmd, "Classifications.AssignColumn", ct).ConfigureAwait(false);
                }

                (table, newVersion) = await BumpColumnVersionAndEpochInTxAsync(conn, tx, assignment.TableColumnId, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        await PublishAndFlushTableMutationAsync(newVersion, table, validScope, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveColumnClassificationAsync(
        string scope,
        string tableColumnId,
        string code,
        CancellationToken ct = default)
    {
        var validScope = ClassificationScopes.ValidateScope(scope, allowGlobal: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableColumnId);
        ValidateClassificationCodeArg(code, nameof(code));

        int affected;
        TableIdentifier? table = null;
        CatalogVersion? newVersion = null;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = ClassificationsSql.DeleteColumnClassificationSql;
                    cmd.AddBound("@scope", validScope);
                    cmd.AddBound("@table_column_id", tableColumnId);
                    cmd.AddBound("@code", code);
                    affected = await ExecuteNonQueryAsync(cmd, "Classifications.RemoveColumn", ct).ConfigureAwait(false);
                }

                if (affected > 0)
                {
                    (table, newVersion) = await BumpColumnVersionAndEpochInTxAsync(conn, tx, tableColumnId, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        if (affected > 0 && newVersion.HasValue)
        {
            await PublishAndFlushTableMutationAsync(newVersion.Value, table, validScope, ct).ConfigureAwait(false);
        }

        return affected > 0;
    }

    public async Task<IReadOnlyList<ColumnMaskOverride>> GetColumnMaskOverridesAsync(
        string tableColumnId, string? tenantScope = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableColumnId);
        return (await GetColumnMaskOverridesBatchAsync([tableColumnId], tenantScope, ct).ConfigureAwait(false)).TryGetValue(tableColumnId, out var rows) ? rows : [];
    }

    // Issue #1043: the rows of the per-column reads for a set of columns, keyed by table column id (1,000 ids per statement).
    public Task<IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>>> GetColumnClassificationsBatchAsync(
        IReadOnlyCollection<string> tableColumnIds, string? tenantScope = null, CancellationToken ct = default) =>
        ReadColumnBatchAsync(tableColumnIds, tenantScope, SelectColumnClassificationsBatchPrefix, "Classifications.GetColumnClassifications", static r => new ColumnClassification
        {
            Scope = r.GetString(0), TableColumnId = r.GetString(1), Code = r.GetString(2), Source = r.GetString(3), AssignedBy = r.GetString(4), AssignedAt = r.GetFieldValue<DateTimeOffset>(5)
        }, static c => c.TableColumnId, ct);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<ColumnMaskOverride>>> GetColumnMaskOverridesBatchAsync(
        IReadOnlyCollection<string> tableColumnIds, string? tenantScope = null, CancellationToken ct = default) =>
        ReadColumnBatchAsync(tableColumnIds, tenantScope, SelectColumnMaskOverridesBatchPrefix, "Classifications.GetColumnOverrides", ReadColumnMaskOverride, static o => o.TableColumnId, ct);

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<T>>> ReadColumnBatchAsync<T>(
        IReadOnlyCollection<string> tableColumnIds, string? tenantScope, string prefix, string fragmentId, Func<DbDataReader, T> map, Func<T, string> columnIdOf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableColumnIds);
        var scope = ValidateOptionalTenantFilter(tenantScope) ?? ClassificationScopes.Global;
        var byColumn = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
        var ids = tableColumnIds.Distinct(StringComparer.Ordinal).ToArray();
        Array.ForEach(ids, static id => ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(tableColumnIds)));
        if (ids.Length > 0)
        {
            await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
            foreach (var chunk in ConsentSubjectFilter.Chunks(ids))
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = prefix + ConsentSqlCore.BindIdInClause(cmd, "c", chunk) + SelectColumnBatchSuffix;
                cmd.AddBound("@scope", scope);
                await using var reader = await ExecuteReaderAsync(cmd, fragmentId, ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var row = map(reader);
                    (byColumn.TryGetValue(columnIdOf(row), out var list) ? list : byColumn[columnIdOf(row)] = []).Add(row);
                }
            }
        }

        return byColumn.ToDictionary(static kv => kv.Key, static kv => (IReadOnlyList<T>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ColumnMaskOverride> UpsertColumnMaskOverrideAsync(
        ColumnMaskOverride maskOverride,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(maskOverride);
        var validScope = ClassificationScopes.ValidateScope(maskOverride.Scope, allowGlobal: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(maskOverride.TableColumnId);
        if (maskOverride.Code is not null)
        {
            ValidateClassificationCodeArg(maskOverride.Code, nameof(maskOverride));
        }

        var id = maskOverride.Id == Guid.Empty ? Guid.NewGuid() : maskOverride.Id;
        var normalized = maskOverride with { Id = id, Scope = validScope };

        Guid persistedId;
        TableIdentifier? table;
        CatalogVersion newVersion;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = (IsPostgreSql, normalized.Code is null) switch
                    {
                        (true, false) => ClassificationsSql.UpsertColumnMaskOverrideWithCodeSqlPg,
                        (true, true) => ClassificationsSql.UpsertColumnMaskOverrideAllSqlPg,
                        (false, false) => ClassificationsSql.UpsertColumnMaskOverrideWithCodeSqlSs,
                        (false, true) => ClassificationsSql.UpsertColumnMaskOverrideAllSqlSs
                    };
                    cmd.AddBound("@id", normalized.Id);
                    cmd.AddBound("@scope", normalized.Scope);
                    cmd.AddBound("@table_column_id", normalized.TableColumnId);
                    if (normalized.Code is not null)
                    {
                        cmd.AddBound("@code", normalized.Code);
                    }

                    cmd.AddBound("@mask_function", ClassificationMaskFunctions.ToCode(normalized.MaskFunction));
                    cmd.AddBound("@params_json", normalized.ParamsJson);
                    cmd.AddBound("@key_ref", normalized.KeyRef);
                    cmd.AddBound("@catalog_text", ClassificationCatalogTextVisibilities.ToCode(normalized.CatalogText));
                    cmd.AddBound("@updated_by", normalized.UpdatedBy);
                    cmd.AddBound("@updated_at", normalized.UpdatedAt.ToUniversalTime(), DbType.DateTimeOffset);

                    var rawId = await ExecuteScalarAsync(cmd, "Classifications.UpsertColumnOverride", ct).ConfigureAwait(false);
                    persistedId = rawId is Guid g ? g : Guid.Parse(rawId?.ToString() ?? id.ToString());
                }

                (table, newVersion) = await BumpColumnVersionAndEpochInTxAsync(conn, tx, normalized.TableColumnId, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        await PublishAndFlushTableMutationAsync(newVersion, table, validScope, ct).ConfigureAwait(false);
        return normalized with { Id = persistedId };
    }

    public async Task<bool> DeleteColumnMaskOverrideAsync(
        string scope,
        string tableColumnId,
        string? code,
        CancellationToken ct = default)
    {
        var validScope = ClassificationScopes.ValidateScope(scope, allowGlobal: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableColumnId);
        if (code is not null)
        {
            ValidateClassificationCodeArg(code, nameof(code));
        }

        int affected;
        TableIdentifier? table = null;
        CatalogVersion? newVersion = null;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = code is null
                        ? ClassificationsSql.DeleteColumnMaskOverrideAllSql
                        : ClassificationsSql.DeleteColumnMaskOverrideWithCodeSql;
                    cmd.AddBound("@scope", validScope);
                    cmd.AddBound("@table_column_id", tableColumnId);
                    if (code is not null)
                    {
                        cmd.AddBound("@code", code);
                    }

                    affected = await ExecuteNonQueryAsync(cmd, "Classifications.DeleteColumnOverride", ct).ConfigureAwait(false);
                }

                if (affected > 0)
                {
                    (table, newVersion) = await BumpColumnVersionAndEpochInTxAsync(conn, tx, tableColumnId, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        if (affected > 0 && newVersion.HasValue)
        {
            await PublishAndFlushTableMutationAsync(newVersion.Value, table, validScope, ct).ConfigureAwait(false);
        }

        return affected > 0;
    }

    private async Task<(TableIdentifier? Table, CatalogVersion Version)> BumpColumnVersionAndEpochInTxAsync(
        DbConnection conn,
        DbTransaction tx,
        string tableColumnId,
        CancellationToken ct)
    {
        var table = await TryReadTableIdentifierByColumnIdAsync(conn, tx, tableColumnId, ct).ConfigureAwait(false);
        var version = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
        if (table.HasValue)
        {
            var changeVersion = version.StructureVersion + version.ContentVersion;
            await IncrementTableEpochInternalAsync(conn, tx, table.Value, ct, changeVersion).ConfigureAwait(false);
        }

        return (table, version);
    }

    internal static Autheris.Domain.Common.TenantId? TryScopeTenant(string scope) =>
        !ClassificationScopes.IsGlobal(scope) && Autheris.Domain.Common.TenantId.TryParse(scope, out var t) ? t : (Autheris.Domain.Common.TenantId?)null;

    private async Task PublishAndFlushTableMutationAsync(
        CatalogVersion version,
        TableIdentifier? table,
        string scope,
        CancellationToken ct)
    {
        var tenant = TryScopeTenant(scope);
        await (table.HasValue
            ? _writeInvalidator.InvalidateTablesAsync(tenant, [table.Value], version, InvalidateTableMetadata, ct)
            : _writeInvalidator.InvalidateWideAsync(tenant, version, ct: ct)).ConfigureAwait(false);
        await FlushScopePendingAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DataClassification>> ReadClassificationsInternalAsync(
        DbConnection conn,
        DbTransaction? tx,
        string? validatedTenant,
        bool includeInactive,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        if (validatedTenant is null)
        {
            cmd.CommandText = includeInactive
                ? ClassificationsSql.SelectClassificationsAllGlobalOnlySql
                : ClassificationsSql.SelectClassificationsActiveGlobalOnlySql;
        }
        else
        {
            cmd.CommandText = includeInactive
                ? ClassificationsSql.SelectClassificationsAllScopedSql
                : ClassificationsSql.SelectClassificationsActiveScopedSql;
            cmd.AddBound("@scope", validatedTenant);
        }

        if (!includeInactive)
        {
            // Bound as a boolean: PostgreSQL has no boolean = integer operator, so the SQL must not compare is_active with a literal 1.
            cmd.AddBound("@active", true);
        }

        var list = new List<DataClassification>();
        await using var reader = await ExecuteReaderAsync(cmd, "Classifications.List", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadClassification(reader));
        }

        return list;
    }

    private async Task<DataClassification?> ReadClassificationInTxAsync(
        DbConnection conn,
        DbTransaction? tx,
        string scope,
        string code,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = ClassificationsSql.SelectClassificationByKeySql;
        cmd.AddBound("@scope", scope);
        cmd.AddBound("@code", code);

        await using var reader = await ExecuteReaderAsync(cmd, "Classifications.GetInTx", ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadClassification(reader) : null;
    }

    private async Task<ClassificationMaskRule?> ReadMaskRuleInTxAsync(
        DbConnection conn,
        DbTransaction? tx,
        string scope,
        string code,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = ClassificationsSql.SelectMaskRuleByKeySql;
        cmd.AddBound("@scope", scope);
        cmd.AddBound("@code", code);

        await using var reader = await ExecuteReaderAsync(cmd, "Classifications.GetMaskRule", ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadMaskRule(reader) : null;
    }

    public async Task<TableIdentifier?> GetTableIdentifierByColumnIdAsync(string tableColumnId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableColumnId);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TryReadTableIdentifierByColumnIdAsync(conn, null, tableColumnId, ct).ConfigureAwait(false);
    }

    private async Task<TableIdentifier?> TryReadTableIdentifierByColumnIdAsync(
        DbConnection conn,
        DbTransaction? tx,
        string tableColumnId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = ClassificationsSql.SelectTableIdentifierByColumnIdSql;
        cmd.AddBound("@table_column_id", tableColumnId);

        await using var reader = await ExecuteReaderAsync(cmd, "Classifications.ResolveTable", ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }
}
