namespace Autheris.Tests.Integration.Governance;

using System;
using System.Data.Common;
using System.Threading.Tasks;

/// <summary>
/// Raw-row seeding of container ownership for store-level tests that run without the domain service (#1009, D13): one domain of
/// <c>tenant</c> whose owner group is <c>ownerGroupSid</c>, with the table assigned to it. Works on PostgreSQL and SQL Server.
/// </summary>
internal static class ContainerOwnerSeed
{
    private const string InsertDomainSql =
        "INSERT INTO GOVERNANCE_DOMAINS (tenant_id, id, name, description, contact, owner_group_sid, default_class, visibility, is_system, version, created_by, created_at, updated_at) " +
        "VALUES (@cosTenant, @cosDomain, @cosName, NULL, NULL, @cosOwner, 'INTERNAL', 'ON_REQUEST', @cosSystem, 1, 'test-seed', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)";

    private const string AssignTableSql =
        "INSERT INTO GOVERNANCE_DOMAIN_TABLES (tenant_id, table_id, domain_id, assigned_by, assigned_at) " +
        "VALUES (@cosTenant, @cosTable, @cosDomain, 'test-seed', CURRENT_TIMESTAMP)";

    /// <summary>Creates the owner domain and assigns <paramref name="tableId"/>; returns the domain id.</summary>
    public static async Task<Guid> SeedAsync(DbConnection conn, string tenant, Guid tableId, string ownerGroupSid, DbTransaction? tx = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var domainId = Guid.NewGuid();
        await ExecAsync(conn, tx, InsertDomainSql,
            ("@cosTenant", tenant), ("@cosDomain", domainId.ToString("D")), ("@cosName", "owners-" + domainId.ToString("N")[..12]),
            ("@cosOwner", ownerGroupSid), ("@cosSystem", false));
        await ExecAsync(conn, tx, AssignTableSql, ("@cosTenant", tenant), ("@cosTable", tableId.ToString()), ("@cosDomain", domainId.ToString("D")));
        return domainId;
    }

    /// <summary>
    /// Makes <paramref name="ownerGroupSid"/> the owner group of the container of <paramref name="tableId"/> in <paramref name="tenant"/>: the
    /// assigned domain's owner group is replaced (an ownership transfer), or a new owner domain is created when the table is unassigned.
    /// </summary>
    public static async Task SetOwnerAsync(DbConnection conn, string tenant, Guid tableId, string ownerGroupSid)
    {
        ArgumentNullException.ThrowIfNull(conn);
        await using var find = conn.CreateCommand();
        find.CommandText = "SELECT domain_id FROM GOVERNANCE_DOMAIN_TABLES WHERE tenant_id = @cosTenant AND table_id = @cosTable";
        AddParameter(find, "@cosTenant", tenant);
        AddParameter(find, "@cosTable", tableId.ToString());
        var domainId = Convert.ToString(await find.ExecuteScalarAsync());
        if (string.IsNullOrEmpty(domainId))
        {
            await SeedAsync(conn, tenant, tableId, ownerGroupSid);
            return;
        }

        await ExecAsync(conn, null, "UPDATE GOVERNANCE_DOMAINS SET owner_group_sid = @cosOwner WHERE tenant_id = @cosTenant AND id = @cosDomain",
            ("@cosOwner", ownerGroupSid), ("@cosTenant", tenant), ("@cosDomain", domainId));
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static async Task ExecAsync(DbConnection conn, DbTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            AddParameter(cmd, name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }
}
