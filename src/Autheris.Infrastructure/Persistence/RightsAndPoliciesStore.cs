namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;

internal enum RightsAndPoliciesDialect
{
    Sqlite,
    PostgreSql,
    SqlServer
}

internal sealed class RightsAndPoliciesScope(DbConnection connection, Func<ValueTask> dispose) : IAsyncDisposable
{
    public DbConnection Connection { get; } = connection;
    public ValueTask DisposeAsync() => dispose();
}

internal sealed class RightsAndPoliciesStore(
    RightsAndPoliciesDialect dialect,
    Func<bool, CancellationToken, Task<RightsAndPoliciesScope>> open)
{
    // ---------------------------------------------------- FUNCTIONAL RIGHTS

    public async Task<IReadOnlyList<FunctionalRightMapping>> GetMappingsAsync(TenantId tenantId, string realmId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, realm_id, group_or_role, functional_right, approved_by, approved_at, recert_class, created_by, created_at FROM FUNCTIONAL_RIGHT_MAPPINGS WHERE tenant_id = @tenantId AND realm_id = @realmId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);

        var list = new List<FunctionalRightMapping>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadMapping(reader));
        }
        return list;
    }

    public async Task<IReadOnlyList<FunctionalRightMapping>> GetApprovedMappingsForGroupsAndRolesAsync(TenantId tenantId, string realmId, IReadOnlyCollection<string> groupsAndRoles, CancellationToken ct = default)
    {
        if (groupsAndRoles == null || groupsAndRoles.Count == 0)
        {
            return Array.Empty<FunctionalRightMapping>();
        }

        await using var scope = await open(false, ct).ConfigureAwait(false);
        var list = new List<FunctionalRightMapping>();

        // Query in batches
        var items = new List<string>(groupsAndRoles);
        for (int i = 0; i < items.Count; i += 50)
        {
            var chunk = items.GetRange(i, Math.Min(50, items.Count - i));
            await using var cmd = scope.Connection.CreateCommand();
            var paramNames = new List<string>();
            for (int j = 0; j < chunk.Count; j++)
            {
                var pName = $"@p{j}";
                paramNames.Add(pName);
                AddParam(cmd, pName, chunk[j]);
            }

            cmd.CommandText = $"SELECT tenant_id, realm_id, group_or_role, functional_right, approved_by, approved_at, recert_class, created_by, created_at FROM FUNCTIONAL_RIGHT_MAPPINGS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_or_role IN ({string.Join(",", paramNames)})";
            AddParam(cmd, "@tenantId", tenantId.Value);
            AddParam(cmd, "@realmId", realmId);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadMapping(reader));
            }
        }

        return list;
    }

    public async Task<FunctionalRightMapping?> GetMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, realm_id, group_or_role, functional_right, approved_by, approved_at, recert_class, created_by, created_at FROM FUNCTIONAL_RIGHT_MAPPINGS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_or_role = @groupOrRole AND functional_right = @right";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupOrRole", groupOrRole);
        AddParam(cmd, "@right", functionalRight.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadMapping(reader);
        }
        return null;
    }

    public async Task CreateMappingAsync(FunctionalRightMapping mapping, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        if (dialect == RightsAndPoliciesDialect.Sqlite || dialect == RightsAndPoliciesDialect.PostgreSql)
        {
            cmd.CommandText = @"INSERT INTO FUNCTIONAL_RIGHT_MAPPINGS (tenant_id, realm_id, group_or_role, functional_right, approved_by, approved_at, recert_class, created_by, created_at)
VALUES (@tenantId, @realmId, @groupOrRole, @right, @approvedBy, @approvedAt, @recertClass, @createdBy, @createdAt)
ON CONFLICT (tenant_id, realm_id, group_or_role, functional_right) DO UPDATE SET
  approved_by = EXCLUDED.approved_by,
  approved_at = EXCLUDED.approved_at,
  recert_class = EXCLUDED.recert_class";
        }
        else
        {
            cmd.CommandText = @"MERGE INTO dbo.FUNCTIONAL_RIGHT_MAPPINGS AS target
USING (VALUES (@tenantId, @realmId, @groupOrRole, @right, @approvedBy, @approvedAt, @recertClass, @createdBy, @createdAt))
  AS source (tenant_id, realm_id, group_or_role, functional_right, approved_by, approved_at, recert_class, created_by, created_at)
ON target.tenant_id = source.tenant_id AND target.realm_id = source.realm_id AND target.group_or_role = source.group_or_role AND target.functional_right = source.functional_right
WHEN MATCHED THEN UPDATE SET
  target.approved_by = source.approved_by,
  target.approved_at = source.approved_at,
  target.recert_class = source.recert_class
WHEN NOT MATCHED THEN INSERT (tenant_id, realm_id, group_or_role, functional_right, approved_by, approved_at, recert_class, created_by, created_at)
  VALUES (source.tenant_id, source.realm_id, source.group_or_role, source.functional_right, source.approved_by, source.approved_at, source.recert_class, source.created_by, source.created_at);";
        }

        AddParam(cmd, "@tenantId", mapping.TenantId.Value);
        AddParam(cmd, "@realmId", mapping.RealmId);
        AddParam(cmd, "@groupOrRole", mapping.GroupOrRole);
        AddParam(cmd, "@right", mapping.FunctionalRight.Value);
        AddParam(cmd, "@approvedBy", (object?)mapping.ApprovedBy ?? DBNull.Value);
        AddParam(cmd, "@approvedAt", (object?)mapping.ApprovedAt?.ToString("O") ?? DBNull.Value);
        AddParam(cmd, "@recertClass", mapping.RecertClass);
        AddParam(cmd, "@createdBy", mapping.CreatedBy);
        AddParam(cmd, "@createdAt", mapping.CreatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ApproveMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, string approvedBy, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "UPDATE FUNCTIONAL_RIGHT_MAPPINGS SET approved_by = @approvedBy, approved_at = @approvedAt WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_or_role = @groupOrRole AND functional_right = @right";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupOrRole", groupOrRole);
        AddParam(cmd, "@right", functionalRight.Value);
        AddParam(cmd, "@approvedBy", approvedBy);
        AddParam(cmd, "@approvedAt", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM FUNCTIONAL_RIGHT_MAPPINGS WHERE tenant_id = @tenantId AND realm_id = @realmId AND group_or_role = @groupOrRole AND functional_right = @right";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@realmId", realmId);
        AddParam(cmd, "@groupOrRole", groupOrRole);
        AddParam(cmd, "@right", functionalRight.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static FunctionalRightMapping ReadMapping(DbDataReader reader)
    {
        var tenantId = new TenantId(reader.GetString(0));
        var realmId = reader.GetString(1);
        var groupOrRole = reader.GetString(2);
        var right = new FunctionalRight(reader.GetString(3));
        var approvedBy = reader.IsDBNull(4) ? null : reader.GetString(4);
        var approvedAt = reader.IsDBNull(5) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(5));
        var recertClass = reader.GetString(6);
        var createdBy = reader.GetString(7);
        var createdAt = DateTimeOffset.Parse(reader.GetString(8));

        return new FunctionalRightMapping(tenantId, realmId, groupOrRole, right, approvedBy, approvedAt, recertClass, createdBy, createdAt);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
