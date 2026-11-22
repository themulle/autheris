using System.Data;
using System.Data.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Dialect-neutral persistence store for governance domains, table assignments, and object protection metadata.</summary>
public sealed class GovernanceDomainStore(IGovernanceSqlDialect dialect, TimeProvider? timeProvider = null) : IGovernanceDomainRepository
{
    private const string UnassignedName = "Unassigned";
    private const string UnassignedCreatedBy = "system:governance-domain-store";
    private static readonly string UnassignedId = Guid.Empty.ToString("D");

    internal const string ListPgSql = "SELECT id, name, description, contact, owner_group_sid, default_class, visibility, is_system, version, created_at, updated_at FROM GOVERNANCE_DOMAINS WHERE tenant_id = @tenantId ORDER BY name, id LIMIT @limit OFFSET @offset";
    internal const string ListSsSql = "SELECT id, name, description, contact, owner_group_sid, default_class, visibility, is_system, version, created_at, updated_at FROM GOVERNANCE_DOMAINS WHERE tenant_id = @tenantId ORDER BY name, id OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";
    internal const string GetSql = "SELECT id, name, description, contact, owner_group_sid, default_class, visibility, is_system, version, created_at, updated_at FROM GOVERNANCE_DOMAINS WHERE tenant_id = @tenantId AND id = @id";
    internal const string ExistsDomainSql = "SELECT 1 FROM GOVERNANCE_DOMAINS WHERE tenant_id = @tenantId AND id = @id";
    internal const string InsertDomainSql = "INSERT INTO GOVERNANCE_DOMAINS (tenant_id, id, name, description, contact, owner_group_sid, default_class, visibility, is_system, version, created_by, created_at, updated_at) VALUES (@tenantId, @id, @name, @description, @contact, @ownerGroupSid, @defaultClass, @visibility, @isSystem, 1, @createdBy, @createdAt, @updatedAt)";
    internal const string UpdateDomainSql = "UPDATE GOVERNANCE_DOMAINS SET name = @name, description = @description, contact = @contact, owner_group_sid = @ownerGroupSid, default_class = @defaultClass, visibility = @visibility, version = @newVersion, updated_at = @updatedAt WHERE tenant_id = @tenantId AND id = @id AND version = @expectedVersion";
    internal const string DeleteDomainTablesByDomainSql = "DELETE FROM GOVERNANCE_DOMAIN_TABLES WHERE tenant_id = @tenantId AND domain_id = @id";
    internal const string DeleteDomainSql = "DELETE FROM GOVERNANCE_DOMAINS WHERE tenant_id = @tenantId AND id = @id AND version = @expectedVersion AND is_system = @isSystemFalse";
    internal const string EnsureUnassignedPgSql = "INSERT INTO GOVERNANCE_DOMAINS (tenant_id, id, name, description, contact, owner_group_sid, default_class, visibility, is_system, version, created_by, created_at, updated_at) VALUES (@tenantId, @id, @name, NULL, NULL, NULL, @defaultClass, @visibility, @isSystem, 1, @createdBy, @createdAt, @updatedAt) ON CONFLICT (tenant_id, id) DO NOTHING";
    internal const string EnsureUnassignedSsSql = "INSERT INTO GOVERNANCE_DOMAINS (tenant_id, id, name, description, contact, owner_group_sid, default_class, visibility, is_system, version, created_by, created_at, updated_at) SELECT @tenantId, @id, @name, NULL, NULL, NULL, @defaultClass, @visibility, @isSystem, 1, @createdBy, @createdAt, @updatedAt WHERE NOT EXISTS (SELECT 1 FROM GOVERNANCE_DOMAINS WHERE tenant_id = @tenantId AND id = @id)";
    internal const string UpsertDomainTablePgSql = "INSERT INTO GOVERNANCE_DOMAIN_TABLES (tenant_id, table_id, domain_id, assigned_by, assigned_at) VALUES (@tenantId, @tableId, @domainId, @assignedBy, @assignedAt) ON CONFLICT (tenant_id, table_id) DO UPDATE SET domain_id = EXCLUDED.domain_id, assigned_by = EXCLUDED.assigned_by, assigned_at = EXCLUDED.assigned_at";
    internal const string UpsertDomainTableSsSql = "MERGE GOVERNANCE_DOMAIN_TABLES WITH (HOLDLOCK) AS target USING (VALUES (@tenantId, @tableId)) AS source (tenant_id, table_id) ON target.tenant_id = source.tenant_id AND target.table_id = source.table_id WHEN MATCHED THEN UPDATE SET domain_id = @domainId, assigned_by = @assignedBy, assigned_at = @assignedAt WHEN NOT MATCHED THEN INSERT (tenant_id, table_id, domain_id, assigned_by, assigned_at) VALUES (@tenantId, @tableId, @domainId, @assignedBy, @assignedAt);";
    internal const string GetProtectionSql = "SELECT column_name, protection_class, mask_category FROM OBJECT_PROTECTION WHERE tenant_id = @tenantId AND table_id = @tableId ORDER BY column_name";
    internal const string UpsertProtectionPgSql = "INSERT INTO OBJECT_PROTECTION (tenant_id, table_id, column_name, protection_class, mask_category, updated_by, updated_at) VALUES (@tenantId, @tableId, @columnName, @protectionClass, @maskCategory, @updatedBy, @updatedAt) ON CONFLICT (tenant_id, table_id, column_name) DO UPDATE SET protection_class = EXCLUDED.protection_class, mask_category = EXCLUDED.mask_category, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at";
    internal const string UpsertProtectionSsSql = "MERGE OBJECT_PROTECTION WITH (HOLDLOCK) AS target USING (VALUES (@tenantId, @tableId, @columnName)) AS source (tenant_id, table_id, column_name) ON target.tenant_id = source.tenant_id AND target.table_id = source.table_id AND target.column_name = source.column_name WHEN MATCHED THEN UPDATE SET protection_class = @protectionClass, mask_category = @maskCategory, updated_by = @updatedBy, updated_at = @updatedAt WHEN NOT MATCHED THEN INSERT (tenant_id, table_id, column_name, protection_class, mask_category, updated_by, updated_at) VALUES (@tenantId, @tableId, @columnName, @protectionClass, @maskCategory, @updatedBy, @updatedAt);";
    internal const string DeleteProtectionSql = "DELETE FROM OBJECT_PROTECTION WHERE tenant_id = @tenantId AND table_id = @tableId AND column_name = @columnName";
    internal const string ClearColumnClassSql = "UPDATE OBJECT_PROTECTION SET protection_class = NULL, updated_by = @updatedBy, updated_at = @updatedAt WHERE tenant_id = @tenantId AND table_id = @tableId AND column_name = @columnName AND mask_category IS NOT NULL";
    internal const string DeleteEmptyColumnProtectionSql = "DELETE FROM OBJECT_PROTECTION WHERE tenant_id = @tenantId AND table_id = @tableId AND column_name = @columnName AND mask_category IS NULL";
    internal const string GetContainerForTableSql = "SELECT d.id, d.owner_group_sid, d.default_class, d.visibility FROM GOVERNANCE_DOMAIN_TABLES dt INNER JOIN GOVERNANCE_DOMAINS d ON d.tenant_id = dt.tenant_id AND d.id = dt.domain_id WHERE dt.tenant_id = @tenantId AND dt.table_id = @tableId UNION ALL SELECT d.id, d.owner_group_sid, d.default_class, d.visibility FROM GOVERNANCE_DOMAINS d WHERE d.tenant_id = @tenantId AND d.id = @unassignedId AND NOT EXISTS (SELECT 1 FROM GOVERNANCE_DOMAIN_TABLES dt WHERE dt.tenant_id = @tenantId AND dt.table_id = @tableId)";

    internal static IReadOnlyList<string> AllSqlTemplates => [ListPgSql, ListSsSql, GetSql, ExistsDomainSql, InsertDomainSql, UpdateDomainSql, DeleteDomainTablesByDomainSql, DeleteDomainSql, EnsureUnassignedPgSql, EnsureUnassignedSsSql, UpsertDomainTablePgSql, UpsertDomainTableSsSql, GetProtectionSql, UpsertProtectionPgSql, UpsertProtectionSsSql, DeleteProtectionSql, ClearColumnClassSql, DeleteEmptyColumnProtectionSql, GetContainerForTableSql];

    private readonly IGovernanceSqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private bool IsSqlServer => _dialect is SqlServerGovernanceDialect;

    public async Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        var tenantId = RequireTenant(tenant, GovernanceTable.GovernanceDomains);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Cmd(conn, null, IsSqlServer ? ListSsSql : ListPgSql, tenantId, ("@limit", Math.Min(limit, IGovernanceDomainRepository.MaxPageSize), DbType.Int32), ("@offset", offset, DbType.Int32));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<GovernanceDomain>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(ReadDomain(reader, tenant));
        return items;
    }

    public async Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant(tenant, GovernanceTable.GovernanceDomains);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await ReadByIdAsync(conn, null, tenantId, tenant, domainId, ct).ConfigureAwait(false);
    }

    public async Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var tenantId = RequireTenant(domain.Tenant, GovernanceTable.GovernanceDomains);
        var actorValue = RequireBounded(actor.Value, 256, nameof(actor));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        var norm = NormalizeAndValidateDomain(domain, expectedVersion);
        var now = _time.GetUtcNow().ToUniversalTime();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            if (expectedVersion == 0)
            {
                await using var ins = Cmd(conn, null, InsertDomainSql, tenantId, DomainParams(norm, now));
                ins.AddBound("@isSystem", norm.IsSystemUnassigned, DbType.Boolean);
                ins.AddBound("@createdBy", actorValue);
                ins.AddBound("@createdAt", now, DbType.DateTimeOffset);
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return new(GovernanceDomainWriteStatus.Created, norm with { Version = 1, CreatedAt = now, UpdatedAt = now });
            }
            await using var upd = Cmd(conn, null, UpdateDomainSql, tenantId, DomainParams(norm, now));
            upd.AddBound("@newVersion", expectedVersion + 1, DbType.Int32);
            upd.AddBound("@expectedVersion", expectedVersion, DbType.Int32);
            if (await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
                return new(GovernanceDomainWriteStatus.Updated, await ReadByIdAsync(conn, null, tenantId, domain.Tenant, domain.Id, ct).ConfigureAwait(false) ?? norm with { Version = expectedVersion + 1, UpdatedAt = now });
        }
        catch (DbException ex) when (_dialect.IsUniqueViolation(ex)) { return new(GovernanceDomainWriteStatus.Conflict); }
        return new(await DomainExistsAsync(conn, null, tenantId, domain.Id, ct).ConfigureAwait(false) ? GovernanceDomainWriteStatus.Conflict : GovernanceDomainWriteStatus.NotFound);
    }

    public Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default)
    {
        var tenantId = RequireTenant(tenant, GovernanceTable.GovernanceDomains);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        if (domainId == Guid.Empty) throw new InvalidOperationException("System Unassigned domain cannot be deleted.");
        return RunInTxAsync(async (conn, tx) =>
        {
            await using (var unassign = Cmd(conn, tx, DeleteDomainTablesByDomainSql, tenantId, ("@id", domainId.ToString("D"), null)))
                await unassign.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await using var del = Cmd(conn, tx, DeleteDomainSql, tenantId, ("@id", domainId.ToString("D"), null), ("@expectedVersion", expectedVersion, DbType.Int32), ("@isSystemFalse", false, DbType.Boolean));
            var deleted = await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
            return (deleted, deleted);
        }, ct);
    }

    public async Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default)
    {
        var tenantId = RequireTenant(tenant, GovernanceTable.GovernanceDomains);
        var now = _time.GetUtcNow().ToUniversalTime();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Cmd(conn, null, IsSqlServer ? EnsureUnassignedSsSql : EnsureUnassignedPgSql, tenantId,
            ("@id", UnassignedId, null), ("@name", UnassignedName, null), ("@defaultClass", ProtectionClass.Confidential.ToCanonicalName(), null),
            ("@visibility", "ON_REQUEST", null), ("@isSystem", true, DbType.Boolean), ("@createdBy", UnassignedCreatedBy, null),
            ("@createdAt", now, DbType.DateTimeOffset), ("@updatedAt", now, DbType.DateTimeOffset));
        try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        catch (DbException ex) when (_dialect.IsUniqueViolation(ex)) { }
        return await ReadByIdAsync(conn, null, tenantId, tenant, Guid.Empty, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("System Unassigned domain could not be loaded.");
    }

    public Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default)
    {
        var tenantId = RequireTenant(tenant, GovernanceTable.GovernanceDomainTables);
        ArgumentNullException.ThrowIfNull(tableIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tableIds.Count, IGovernanceDomainRepository.MaxBatchAssignCount);
        var actorValue = RequireBounded(actor.Value, 256, nameof(actor));
        var ordered = tableIds.Distinct().ToList();
        if (ordered.Any(id => id == Guid.Empty)) throw new ArgumentException("TableId cannot be empty.", nameof(tableIds));
        var now = _time.GetUtcNow().ToUniversalTime();
        return RunInTxAsync(async (conn, tx) =>
        {
            if (!await DomainExistsAsync(conn, tx, tenantId, domainId, ct).ConfigureAwait(false))
                throw new KeyNotFoundException($"Governance domain '{domainId}' was not found for tenant.");
            if (ordered.Count > 0)
            {
                await using var cmd = Cmd(conn, tx, IsSqlServer ? UpsertDomainTableSsSql : UpsertDomainTablePgSql, tenantId,
                    ("@domainId", domainId.ToString("D"), null), ("@assignedBy", actorValue, null), ("@assignedAt", now, DbType.DateTimeOffset));
                var tableIdParam = cmd.AddBound("@tableId", string.Empty);
                foreach (var tableId in ordered) { tableIdParam.Value = tableId.ToString("D"); await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            }
            return (true, true);
        }, ct);
    }

    public async Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(TenantId tenant, Guid tableId, CancellationToken ct = default)
    {
        var tenantId = RequireTableScope(tenant, tableId, GovernanceTable.ObjectProtection);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Cmd(conn, null, GetProtectionSql, tenantId, ("@tableId", tableId.ToString("D"), null));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        ObjectProtection? tableProtection = null;
        var columns = new Dictionary<string, ObjectProtection>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var col = reader.GetString(0);
            ProtectionClass? cls = reader.IsDBNull(1) ? null : ProtectionClassExtensions.ParseOrConfidential(reader.GetString(1));
            var mask = reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2)) || string.Equals(reader.GetString(2).Trim(), "PUBLIC", StringComparison.OrdinalIgnoreCase) ? null : reader.GetString(2).Trim();
            var prot = new ObjectProtection(cls, mask);
            if (col.Length == 0) tableProtection = prot;
            else columns[col] = columns.TryGetValue(col, out var ex) ? new(ex.Class is { } a && cls is { } b ? ProtectionClassExtensions.Max(a, b) : ex.Class ?? cls, ex.MaskCategory ?? mask) : prot;
        }
        return (tableProtection, columns.AsReadOnly());
    }

    public async Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default)
    {
        var (tenantId, normCol, normProt, actorValue) = ValidateProtectionWrite(tenant, tableId, column, protection, actor);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await WriteProtectionAsync(conn, null, tenantId, tableId, normCol, normProt, actorValue, _time.GetUtcNow().ToUniversalTime(), ct).ConfigureAwait(false);
    }

    public Task SetTableProtectionAsync(TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default)
    {
        var (tenantId, _, normProt, actorValue) = ValidateProtectionWrite(tenant, tableId, string.Empty, protection, actor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(clearColumns?.Count ?? 0, IGovernanceDomainRepository.MaxBatchAssignCount);
        var clear = (clearColumns ?? []).Select(c => RequireBounded(c, 256, nameof(clearColumns))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var now = _time.GetUtcNow().ToUniversalTime();
        return RunInTxAsync(async (conn, tx) =>
        {
            await WriteProtectionAsync(conn, tx, tenantId, tableId, string.Empty, normProt, actorValue, now, ct).ConfigureAwait(false);
            if (clear.Count > 0)
            {
                await using var upd = Cmd(conn, tx, ClearColumnClassSql, tenantId, ("@tableId", tableId.ToString("D"), null), ("@updatedBy", actorValue, null), ("@updatedAt", now, DbType.DateTimeOffset));
                var updCol = upd.AddBound("@columnName", string.Empty);
                await using var del = Cmd(conn, tx, DeleteEmptyColumnProtectionSql, tenantId, ("@tableId", tableId.ToString("D"), null));
                var delCol = del.AddBound("@columnName", string.Empty);
                foreach (var col in clear) { updCol.Value = col; await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); delCol.Value = col; await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            }
            return (true, true);
        }, ct);
    }

    public async Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default)
    {
        var tenantId = RequireTableScope(tenant, tableId, GovernanceTable.GovernanceDomainTables);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Cmd(conn, null, GetContainerForTableSql, tenantId, ("@tableId", tableId.ToString("D"), null), ("@unassignedId", UnassignedId, null));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return ContainerDefaults.Unassigned;
        var ownerSid = reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1)) ? (Sid?)null : new Sid(reader.GetString(1));
        ProtectionClass? defaultClass = reader.IsDBNull(2) ? null : ProtectionClassExtensions.ParseOrConfidential(reader.GetString(2));
        return new ContainerDefaults(new(ContainerKind.Domain, Guid.Parse(reader.GetString(0))), ownerSid, defaultClass, CatalogVisibilityModeExtensions.ParseOrOnRequest(reader.IsDBNull(3) ? null : reader.GetString(3)), IsValid: true);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = _dialect.CreateConnection();
        try { await conn.OpenAsync(ct).ConfigureAwait(false); return conn; }
        catch { await conn.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<T> RunInTxAsync<T>(Func<DbConnection, DbTransaction, Task<(bool Commit, T Result)>> work, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try { var (commit, result) = await work(conn, tx).ConfigureAwait(false); if (commit) await tx.CommitAsync(ct).ConfigureAwait(false); else await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false); return result; }
        catch { await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false); throw; }
    }

    private static DbCommand Cmd(DbConnection conn, DbTransaction? tx, string sql, string tenantId, params (string Name, object? Value, DbType? Type)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.AddBound("@tenantId", tenantId);
        foreach (var (name, value, type) in parameters) cmd.AddBound(name, value, type);
        return cmd;
    }

    private static async Task<bool> DomainExistsAsync(DbConnection conn, DbTransaction? tx, string tenantId, Guid domainId, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, ExistsDomainSql, tenantId, ("@id", domainId.ToString("D"), null));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null and not DBNull;
    }

    private static async Task<GovernanceDomain?> ReadByIdAsync(DbConnection conn, DbTransaction? tx, string tenantId, TenantId tenant, Guid domainId, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, GetSql, tenantId, ("@id", domainId.ToString("D"), null));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadDomain(reader, tenant) : null;
    }

    private static GovernanceDomain ReadDomain(DbDataReader r, TenantId tenant) => new(
        Guid.Parse(r.GetString(0)), tenant, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) || string.IsNullOrWhiteSpace(r.GetString(4)) ? (Sid?)null : new Sid(r.GetString(4)),
        r.IsDBNull(5) ? null : ProtectionClassExtensions.ParseOrConfidential(r.GetString(5)),
        CatalogVisibilityModeExtensions.ParseOrOnRequest(r.IsDBNull(6) ? null : r.GetString(6)),
        r.GetBoolean(7), r.GetInt32(8), r.GetFieldValue<DateTimeOffset>(9), r.GetFieldValue<DateTimeOffset>(10));

    private static (string, object?, DbType?)[] DomainParams(GovernanceDomain d, DateTimeOffset updatedAt) =>
        [("@id", d.Id.ToString("D"), null), ("@name", d.Name, null), ("@description", (object?)d.Description ?? DBNull.Value, DbType.String), ("@contact", (object?)d.Contact ?? DBNull.Value, DbType.String), ("@ownerGroupSid", (object?)d.OwnerGroupSid?.Value ?? DBNull.Value, DbType.String), ("@defaultClass", (object?)d.DefaultClass?.NormalizeOrConfidential().ToCanonicalName() ?? DBNull.Value, DbType.String), ("@visibility", d.Visibility == CatalogVisibilityMode.Open ? "OPEN" : "ON_REQUEST", null), ("@updatedAt", updatedAt, DbType.DateTimeOffset)];

    private async Task WriteProtectionAsync(DbConnection conn, DbTransaction? tx, string tenantId, Guid tableId, string col, ObjectProtection p, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (p.Class is null && p.MaskCategory is null) { await using var del = Cmd(conn, tx, DeleteProtectionSql, tenantId, ("@tableId", tableId.ToString("D"), null), ("@columnName", col, null)); await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false); return; }
        await using var cmd = Cmd(conn, tx, IsSqlServer ? UpsertProtectionSsSql : UpsertProtectionPgSql, tenantId, ("@tableId", tableId.ToString("D"), null), ("@columnName", col, null), ("@protectionClass", (object?)p.Class?.ToCanonicalName() ?? DBNull.Value, DbType.String), ("@maskCategory", (object?)p.MaskCategory ?? DBNull.Value, DbType.String), ("@updatedBy", actor, null), ("@updatedAt", now, DbType.DateTimeOffset));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string RequireTenant(TenantId tenant, GovernanceTable table) =>
        TenantId.TryParse(TenantScope.RequireTenant(tenant, table.ToSqlName()).Value, out var valid) ? valid.Value : throw new ArgumentException("A valid non-reserved TenantId is required.", nameof(tenant));

    private static string RequireTableScope(TenantId tenant, Guid tableId, GovernanceTable table) =>
        tableId == Guid.Empty ? throw new ArgumentException("TableId cannot be empty.", nameof(tableId)) : RequireTenant(tenant, table);

    private static string RequireBounded(string? raw, int maxLen, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw, paramName);
        var trimmed = raw.Trim();
        return trimmed.Length > maxLen || trimmed.Any(char.IsControl) ? throw new ArgumentException($"Value for '{paramName}' exceeds {maxLen} characters or contains control characters.", paramName) : trimmed;
    }

    private static GovernanceDomain NormalizeAndValidateDomain(GovernanceDomain d, int expectedVersion)
    {
        var name = RequireBounded(d.Name, 128, nameof(d.Name));
        var defaultClass = d.DefaultClass?.NormalizeOrConfidential();
        var visibility = d.Visibility.NormalizeOrOnRequest();
        if (d.Id == Guid.Empty)
        {
            if (expectedVersion == 0 || !d.IsSystemUnassigned || !string.Equals(name, UnassignedName, StringComparison.Ordinal) || defaultClass != ProtectionClass.Confidential || visibility != CatalogVisibilityMode.OnRequest)
                throw new InvalidOperationException("System Unassigned domain invariants cannot be altered.");
        }
        else if (d.IsSystemUnassigned || string.Equals(name, UnassignedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Non-system domains cannot use the reserved Unassigned identity or name.");

        var ownerSid = string.IsNullOrWhiteSpace(d.OwnerGroupSid?.Value) ? null : RequireBounded(d.OwnerGroupSid.Value.Value, 184, nameof(d.OwnerGroupSid));
        return d with { Name = name, Description = string.IsNullOrWhiteSpace(d.Description) ? null : RequireBounded(d.Description, 1024, nameof(d.Description)), Contact = string.IsNullOrWhiteSpace(d.Contact) ? null : RequireBounded(d.Contact, 256, nameof(d.Contact)), OwnerGroupSid = ownerSid is null ? (Sid?)null : new Sid(ownerSid), DefaultClass = defaultClass, Visibility = visibility };
    }

    private static (string TenantId, string Column, ObjectProtection Protection, string Actor) ValidateProtectionWrite(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor)
    {
        var tenantId = RequireTableScope(tenant, tableId, GovernanceTable.ObjectProtection);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(protection);
        var maskResult = ProtectionWriteValidator.ValidateMaskCategory(protection.MaskCategory);
        if (!maskResult.IsValid) throw new ArgumentException(maskResult.ErrorMessage, nameof(protection));
        return (tenantId, column.Length == 0 ? string.Empty : RequireBounded(column, 256, nameof(column)), new ObjectProtection(protection.Class?.NormalizeOrConfidential(), maskResult.NormalizedProtection?.MaskCategory), RequireBounded(actor.Value, 256, nameof(actor)));
    }
}
