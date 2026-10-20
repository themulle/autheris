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

    // ---------------------------------------------------- ACCESS POLICIES

    public async Task<IReadOnlyList<AccessPolicy>> GetPoliciesAsync(TenantId tenantId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, policy_id, name, description, is_active, priority, decision, created_by, approved_by, created_at, approved_at FROM ACCESS_POLICIES WHERE tenant_id = @tenantId";
        AddParam(cmd, "@tenantId", tenantId.Value);

        var policies = new List<AccessPolicy>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                policies.Add(ReadPolicy(reader));
            }
        }

        foreach (var p in policies)
        {
            await LoadGranteesAndSelectorsAsync(scope.Connection, p, ct).ConfigureAwait(false);
        }

        return policies;
    }

    public async Task<AccessPolicy?> GetPolicyByIdAsync(TenantId tenantId, string policyId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, policy_id, name, description, is_active, priority, decision, created_by, approved_by, created_at, approved_at FROM ACCESS_POLICIES WHERE tenant_id = @tenantId AND policy_id = @policyId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@policyId", policyId);

        AccessPolicy? policy = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                policy = ReadPolicy(reader);
            }
        }

        if (policy != null)
        {
            await LoadGranteesAndSelectorsAsync(scope.Connection, policy, ct).ConfigureAwait(false);
        }

        return policy;
    }

    public async Task CreatePolicyAsync(AccessPolicy policy, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO ACCESS_POLICIES (tenant_id, policy_id, name, description, is_active, priority, decision, created_by, approved_by, created_at, approved_at)
VALUES (@tenantId, @policyId, @name, @description, @isActive, @priority, @decision, @createdBy, @approvedBy, @createdAt, @approvedAt)";

        AddParam(cmd, "@tenantId", policy.TenantId);
        AddParam(cmd, "@policyId", policy.PolicyId);
        AddParam(cmd, "@name", policy.Name);
        AddParam(cmd, "@description", (object?)policy.Description ?? DBNull.Value);
        AddParam(cmd, "@isActive", policy.IsActive ? 1 : 0);
        AddParam(cmd, "@priority", policy.Priority);
        AddParam(cmd, "@decision", policy.Decision.ToString());
        AddParam(cmd, "@createdBy", policy.CreatedBy);
        AddParam(cmd, "@approvedBy", (object?)policy.ApprovedBy ?? DBNull.Value);
        AddParam(cmd, "@createdAt", policy.CreatedAt.ToString("O"));
        AddParam(cmd, "@approvedAt", (object?)policy.ApprovedAt?.ToString("O") ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await SaveGranteesAndSelectorsAsync(scope.Connection, policy, ct).ConfigureAwait(false);
    }

    public async Task UpdatePolicyAsync(AccessPolicy policy, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = @"UPDATE ACCESS_POLICIES SET
  name = @name, description = @description, is_active = @isActive, priority = @priority, decision = @decision, approved_by = @approvedBy, approved_at = @approvedAt
WHERE tenant_id = @tenantId AND policy_id = @policyId";

        AddParam(cmd, "@tenantId", policy.TenantId);
        AddParam(cmd, "@policyId", policy.PolicyId);
        AddParam(cmd, "@name", policy.Name);
        AddParam(cmd, "@description", (object?)policy.Description ?? DBNull.Value);
        AddParam(cmd, "@isActive", policy.IsActive ? 1 : 0);
        AddParam(cmd, "@priority", policy.Priority);
        AddParam(cmd, "@decision", policy.Decision.ToString());
        AddParam(cmd, "@approvedBy", (object?)policy.ApprovedBy ?? DBNull.Value);
        AddParam(cmd, "@approvedAt", (object?)policy.ApprovedAt?.ToString("O") ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await SaveGranteesAndSelectorsAsync(scope.Connection, policy, ct).ConfigureAwait(false);
    }

    public async Task DeletePolicyAsync(TenantId tenantId, string policyId, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);

        await using var cmd1 = scope.Connection.CreateCommand();
        cmd1.CommandText = "DELETE FROM ACCESS_POLICY_SELECTORS WHERE tenant_id = @tenantId AND policy_id = @policyId";
        AddParam(cmd1, "@tenantId", tenantId.Value);
        AddParam(cmd1, "@policyId", policyId);
        await cmd1.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var cmd2 = scope.Connection.CreateCommand();
        cmd2.CommandText = "DELETE FROM ACCESS_POLICY_GRANTEES WHERE tenant_id = @tenantId AND policy_id = @policyId";
        AddParam(cmd2, "@tenantId", tenantId.Value);
        AddParam(cmd2, "@policyId", policyId);
        await cmd2.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var cmd3 = scope.Connection.CreateCommand();
        cmd3.CommandText = "DELETE FROM ACCESS_POLICIES WHERE tenant_id = @tenantId AND policy_id = @policyId";
        AddParam(cmd3, "@tenantId", tenantId.Value);
        AddParam(cmd3, "@policyId", policyId);
        await cmd3.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccessPolicy>> GetActivePoliciesForGranteesAsync(TenantId tenantId, string realmId, IReadOnlyCollection<string> groupIds, IReadOnlyCollection<string> roles, CancellationToken ct = default)
    {
        var allPolicies = await GetPoliciesAsync(tenantId, ct).ConfigureAwait(false);
        var active = new List<AccessPolicy>();

        var groupSet = new HashSet<string>(groupIds ?? [], StringComparer.OrdinalIgnoreCase);
        var roleSet = new HashSet<string>(roles ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (var p in allPolicies)
        {
            if (!p.IsActive) continue;

            bool matched = false;
            foreach (var g in p.Grantees)
            {
                if (g.GranteeType == AccessPolicyGranteeType.Group && groupSet.Contains(g.GranteeId))
                {
                    matched = true;
                    break;
                }
                if (g.GranteeType == AccessPolicyGranteeType.Role && roleSet.Contains(g.GranteeId))
                {
                    matched = true;
                    break;
                }
            }

            if (matched)
            {
                active.Add(p);
            }
        }

        return active;
    }

    private static async Task LoadGranteesAndSelectorsAsync(DbConnection conn, AccessPolicy policy, CancellationToken ct)
    {
        policy.Grantees.Clear();
        policy.Selectors.Clear();

        await using var cmdG = conn.CreateCommand();
        cmdG.CommandText = "SELECT tenant_id, policy_id, realm_id, grantee_type, grantee_id FROM ACCESS_POLICY_GRANTEES WHERE tenant_id = @tenantId AND policy_id = @policyId";
        AddParam(cmdG, "@tenantId", policy.TenantId);
        AddParam(cmdG, "@policyId", policy.PolicyId);
        await using (var r = await cmdG.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var typeStr = r.GetString(3);
                var gType = Enum.TryParse<AccessPolicyGranteeType>(typeStr, true, out var parsed) ? parsed : AccessPolicyGranteeType.Group;
                policy.Grantees.Add(new AccessPolicyGrantee(r.GetString(0), r.GetString(1), r.GetString(2), gType, r.GetString(4)));
            }
        }

        await using var cmdS = conn.CreateCommand();
        cmdS.CommandText = "SELECT tenant_id, policy_id, selector_type, selector_value FROM ACCESS_POLICY_SELECTORS WHERE tenant_id = @tenantId AND policy_id = @policyId";
        AddParam(cmdS, "@tenantId", policy.TenantId);
        AddParam(cmdS, "@policyId", policy.PolicyId);
        await using (var r = await cmdS.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var typeStr = r.GetString(2);
                var sType = Enum.TryParse<AccessPolicySelectorType>(typeStr, true, out var parsed) ? parsed : AccessPolicySelectorType.TablePattern;
                policy.Selectors.Add(new AccessPolicySelector(r.GetString(0), r.GetString(1), sType, r.GetString(3)));
            }
        }
    }

    private static async Task SaveGranteesAndSelectorsAsync(DbConnection conn, AccessPolicy policy, CancellationToken ct)
    {
        // Delete existing
        await using var delG = conn.CreateCommand();
        delG.CommandText = "DELETE FROM ACCESS_POLICY_GRANTEES WHERE tenant_id = @tenantId AND policy_id = @policyId";
        AddParam(delG, "@tenantId", policy.TenantId);
        AddParam(delG, "@policyId", policy.PolicyId);
        await delG.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var delS = conn.CreateCommand();
        delS.CommandText = "DELETE FROM ACCESS_POLICY_SELECTORS WHERE tenant_id = @tenantId AND policy_id = @policyId";
        AddParam(delS, "@tenantId", policy.TenantId);
        AddParam(delS, "@policyId", policy.PolicyId);
        await delS.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Insert grantees
        foreach (var g in policy.Grantees)
        {
            await using var insG = conn.CreateCommand();
            insG.CommandText = "INSERT INTO ACCESS_POLICY_GRANTEES (tenant_id, policy_id, realm_id, grantee_type, grantee_id) VALUES (@tenantId, @policyId, @realmId, @type, @id)";
            AddParam(insG, "@tenantId", g.TenantId);
            AddParam(insG, "@policyId", g.PolicyId);
            AddParam(insG, "@realmId", g.RealmId);
            AddParam(insG, "@type", g.GranteeType.ToString());
            AddParam(insG, "@id", g.GranteeId);
            await insG.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Insert selectors
        foreach (var s in policy.Selectors)
        {
            await using var insS = conn.CreateCommand();
            insS.CommandText = "INSERT INTO ACCESS_POLICY_SELECTORS (tenant_id, policy_id, selector_type, selector_value) VALUES (@tenantId, @policyId, @type, @val)";
            AddParam(insS, "@tenantId", s.TenantId);
            AddParam(insS, "@policyId", s.PolicyId);
            AddParam(insS, "@type", s.SelectorType.ToString());
            AddParam(insS, "@val", s.SelectorValue);
            await insS.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
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

    private static AccessPolicy ReadPolicy(DbDataReader reader)
    {
        var tenantId = reader.GetString(0);
        var policyId = reader.GetString(1);
        var name = reader.GetString(2);
        var description = reader.IsDBNull(3) ? null : reader.GetString(3);
        var isActive = reader.GetInt32(4) != 0;
        var priority = reader.GetInt32(5);
        var decisionStr = reader.GetString(6);
        var decision = Enum.TryParse<AccessPolicyDecision>(decisionStr, true, out var d) ? d : AccessPolicyDecision.Permit;
        var createdBy = reader.GetString(7);
        var approvedBy = reader.IsDBNull(8) ? null : reader.GetString(8);
        var createdAt = DateTimeOffset.Parse(reader.GetString(9));
        var approvedAt = reader.IsDBNull(10) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(10));

        return new AccessPolicy
        {
            TenantId = tenantId,
            PolicyId = policyId,
            Name = name,
            Description = description,
            IsActive = isActive,
            Priority = priority,
            Decision = decision,
            CreatedBy = createdBy,
            ApprovedBy = approvedBy,
            CreatedAt = createdAt,
            ApprovedAt = approvedAt
        };
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
