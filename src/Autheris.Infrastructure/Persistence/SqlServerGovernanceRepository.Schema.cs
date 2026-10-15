using Autheris.Domain.Model;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

public partial class SqlServerGovernanceRepository
{
    private void SeedInitialCatalog()
    {
        var domains = new[]
        {
            "finance", "hr", "sales", "inventory", "compliance",
            "marketing", "logistics", "crm", "procurement", "billing"
        };

        using var conn = OpenConnectionCore();
        using var trans = conn.BeginTransaction();
        try
        {
            // Idempotent on the natural keys (ad_sid, role_name, ...): a restart must not abort the seed on a unique violation.
            string EnsureOwner(string sid, string account, string name, string email)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    SELECT @id, @sid, @account, @name, @email, 1
                                    WHERE NOT EXISTS (SELECT 1 FROM DATA_OWNERS WHERE ad_sid = @sid);";
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@sid", sid);
                cmd.Parameters.AddWithValue("@account", account);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@email", email);
                cmd.ExecuteNonQuery();
                return ResolveOwnerId(conn, trans, sid, string.Empty);
            }

            var ownerId = EnsureOwner("S-1-5-21-DATAOWNER-1", "CORP\\dataowner", "Chief Finance Data Owner", "dataowner@corp.local");
            var salesOwnerId = EnsureOwner("S-1-5-21-DATAOWNER-APPROVER", "CORP\\salesowner", "Chief Sales Data Owner", "salesowner@corp.local");
            var approverId = EnsureOwner("S-1-5-21-APPROVER", "CORP\\approver", "Finance Approver", "approver@corp.local");
            var approverAId = EnsureOwner("S-1-5-21-APPROVER-A", "CORP\\approver-a", "Finance Approver A", "approver-a@corp.local");
            var approverBId = EnsureOwner("S-1-5-21-APPROVER-B", "CORP\\approver-b", "Finance Approver B", "approver-b@corp.local");

            foreach (var domain in domains)
            {
                for (int t = 1; t <= 10; t++)
                {
                    var tableId = Guid.NewGuid().ToString();
                    var tableName = $"{domain}_table_{t}";
                    var schema = "dbo";

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = trans;
                        cmd.CommandText = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active)
                                            SELECT @id, 'SqlServer', @domain, @schema, @name, @disp, @sens, @four, 1
                                            WHERE NOT EXISTS (SELECT 1 FROM TABLES WHERE source_name = @domain AND schema_name = @schema AND table_name = @name);";
                        cmd.Parameters.AddWithValue("@id", tableId);
                        cmd.Parameters.AddWithValue("@domain", domain);
                        cmd.Parameters.AddWithValue("@schema", schema);
                        cmd.Parameters.AddWithValue("@name", tableName);
                        cmd.Parameters.AddWithValue("@disp", $"{domain.ToUpperInvariant()} Table {t}");
                        cmd.Parameters.AddWithValue("@sens", (t % 5 == 0) ? "HIGH" : "NORMAL");
                        cmd.Parameters.AddWithValue("@four", (t % 5 == 0) ? 1 : 0);
                        cmd.ExecuteNonQuery();
                    }

                    using (var idCmd = conn.CreateCommand())
                    {
                        idCmd.Transaction = trans;
                        idCmd.CommandText = "SELECT TOP (1) id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@name)";
                        idCmd.Parameters.AddWithValue("@domain", domain);
                        idCmd.Parameters.AddWithValue("@schema", schema);
                        idCmd.Parameters.AddWithValue("@name", tableName);
                        var actualId = idCmd.ExecuteScalar()?.ToString();
                        if (!string.IsNullOrEmpty(actualId))
                        {
                            tableId = actualId;
                        }
                    }

                    if (domain == "finance" && t == 1)
                    {
                        EnsureTableOwner(conn, trans, tableId, ownerId, "PRIMARY");
                        EnsureTableOwner(conn, trans, tableId, approverId, "DELEGATE");
                    }
                    else if (domain == "finance" && t == 5)
                    {
                        EnsureTableOwner(conn, trans, tableId, ownerId, "PRIMARY");
                        EnsureTableOwner(conn, trans, tableId, approverAId, "DELEGATE");
                        EnsureTableOwner(conn, trans, tableId, approverBId, "DELEGATE");
                    }
                    else if (domain == "sales" && (t == 1 || t == 5))
                    {
                        EnsureTableOwner(conn, trans, tableId, salesOwnerId, "PRIMARY");
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = trans;
                        cmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
                                            SELECT @id, @domain, @schema, @name, 1, @now
                                            WHERE NOT EXISTS (SELECT 1 FROM POLICY_EPOCHS WHERE table_id = @id)
                                              AND NOT EXISTS (SELECT 1 FROM POLICY_EPOCHS WHERE domain = @domain AND schema_name = @schema AND table_name = @name);";
                        cmd.Parameters.AddWithValue("@id", tableId);
                        cmd.Parameters.AddWithValue("@domain", domain);
                        cmd.Parameters.AddWithValue("@schema", schema);
                        cmd.Parameters.AddWithValue("@name", tableName);
                        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                        cmd.ExecuteNonQuery();
                    }

                    var columns = new[]
                    {
                        ("id", "int", false),
                        ("name", "varchar", false),
                        ("amount", "decimal", false),
                        ("email", "varchar", true),
                        ("created_at", "datetime", false)
                    };

                    foreach (var (cName, cType, cSens) in columns)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = trans;
                            cmd.CommandText = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive)
                                                SELECT @id, @tid, @name, @type, @sens
                                                WHERE NOT EXISTS (SELECT 1 FROM TABLE_COLUMNS WHERE table_id = @tid AND column_name = @name);";
                            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                            cmd.Parameters.AddWithValue("@tid", tableId);
                            cmd.Parameters.AddWithValue("@name", cName);
                            cmd.Parameters.AddWithValue("@type", cType);
                            cmd.Parameters.AddWithValue("@sens", cSens ? 1 : 0);
                            cmd.ExecuteNonQuery();
                        }

                        string? colId;
                        using (var colIdCmd = conn.CreateCommand())
                        {
                            colIdCmd.Transaction = trans;
                            colIdCmd.CommandText = "SELECT TOP (1) id FROM TABLE_COLUMNS WHERE table_id = @tid AND LOWER(column_name) = LOWER(@name)";
                            colIdCmd.Parameters.AddWithValue("@tid", tableId);
                            colIdCmd.Parameters.AddWithValue("@name", cName);
                            colId = colIdCmd.ExecuteScalar()?.ToString();
                        }

                        if (cSens && !string.IsNullOrEmpty(colId))
                        {
                            using var maskCmd = conn.CreateCommand();
                            maskCmd.Transaction = trans;
                            maskCmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement)
                                                   SELECT @id, @cid, 'EMAIL', NULL, '***@***.***'
                                                   WHERE NOT EXISTS (SELECT 1 FROM COLUMN_MASKING_RULES WHERE table_column_id = @cid);";
                            maskCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                            maskCmd.Parameters.AddWithValue("@cid", colId);
                            maskCmd.ExecuteNonQuery();
                        }
                    }
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO ROLES (id, role_name, description)
                                    SELECT @id, 'GovernanceAdmin', 'Platform governance administrators'
                                    WHERE NOT EXISTS (SELECT 1 FROM ROLES WHERE role_name = 'GovernanceAdmin');";
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                cmd.ExecuteNonQuery();
            }

            string? adminRoleId;
            using (var idCmd = conn.CreateCommand())
            {
                idCmd.Transaction = trans;
                idCmd.CommandText = "SELECT TOP (1) id FROM ROLES WHERE role_name = 'GovernanceAdmin'";
                adminRoleId = idCmd.ExecuteScalar()?.ToString();
            }

            if (!string.IsNullOrEmpty(adminRoleId))
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                                    SELECT @id, @rid, 'User', 'S-1-5-21-GOVERNANCEADMIN'
                                    WHERE NOT EXISTS (SELECT 1 FROM ROLE_MEMBERS WHERE role_id = @rid AND member_sid = 'S-1-5-21-GOVERNANCEADMIN');";
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@rid", adminRoleId);
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
        catch (Exception ex)
        {
            trans.Rollback();
            _logger?.LogWarning(ex, "Failed to seed demo data into SQL Server Governance DB; skipping.");
        }
    }

    private static void EnsureTableOwner(SqlConnection conn, SqlTransaction trans, string tableId, string ownerId, string role)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = trans;
        cmd.CommandText = @"INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                            SELECT @id, @tid, @oid, @role
                            WHERE NOT EXISTS (SELECT 1 FROM TABLE_OWNERS WHERE table_id = @tid AND data_owner_id = @oid AND owner_role = @role);";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@tid", tableId);
        cmd.Parameters.AddWithValue("@oid", ownerId);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.ExecuteNonQuery();
    }

    private static string ResolveOwnerId(SqlConnection conn, SqlTransaction trans, string sid, string fallbackId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = trans;
        cmd.CommandText = "SELECT TOP (1) id FROM DATA_OWNERS WHERE ad_sid = @sid";
        cmd.Parameters.AddWithValue("@sid", sid);
        var existing = cmd.ExecuteScalar()?.ToString();
        return !string.IsNullOrEmpty(existing) ? existing : fallbackId;
    }
}
