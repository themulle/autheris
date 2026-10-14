using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository
{
    private void SeedInitialCatalog()
    {
        var domains = new[]
        {
            "finance", "hr", "sales", "inventory", "compliance",
            "marketing", "logistics", "crm", "procurement", "billing"
        };

        using var conn = _dataSource.OpenConnection();
        using var trans = conn.BeginTransaction();
        try
        {
            var ownerSid = "S-1-5-21-DATAOWNER-1";
            var ownerId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1)
                                    ON CONFLICT (id) DO NOTHING;";
                cmd.Parameters.AddWithValue("@id", ownerId);
                cmd.Parameters.AddWithValue("@sid", ownerSid);
                cmd.Parameters.AddWithValue("@account", "CORP\\dataowner");
                cmd.Parameters.AddWithValue("@name", "Chief Finance Data Owner");
                cmd.Parameters.AddWithValue("@email", "dataowner@corp.local");
                cmd.ExecuteNonQuery();
            }

            var salesOwnerSid = "S-1-5-21-DATAOWNER-APPROVER";
            var salesOwnerId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1)
                                    ON CONFLICT (id) DO NOTHING;";
                cmd.Parameters.AddWithValue("@id", salesOwnerId);
                cmd.Parameters.AddWithValue("@sid", salesOwnerSid);
                cmd.Parameters.AddWithValue("@account", "CORP\\salesowner");
                cmd.Parameters.AddWithValue("@name", "Chief Sales Data Owner");
                cmd.Parameters.AddWithValue("@email", "salesowner@corp.local");
                cmd.ExecuteNonQuery();
            }

            var approverSid = "S-1-5-21-APPROVER";
            var approverId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1)
                                    ON CONFLICT (id) DO NOTHING;";
                cmd.Parameters.AddWithValue("@id", approverId);
                cmd.Parameters.AddWithValue("@sid", approverSid);
                cmd.Parameters.AddWithValue("@account", "CORP\\approver");
                cmd.Parameters.AddWithValue("@name", "Finance Approver");
                cmd.Parameters.AddWithValue("@email", "approver@corp.local");
                cmd.ExecuteNonQuery();
            }

            var approverASid = "S-1-5-21-APPROVER-A";
            var approverAId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1)
                                    ON CONFLICT (id) DO NOTHING;";
                cmd.Parameters.AddWithValue("@id", approverAId);
                cmd.Parameters.AddWithValue("@sid", approverASid);
                cmd.Parameters.AddWithValue("@account", "CORP\\approver-a");
                cmd.Parameters.AddWithValue("@name", "Finance Approver A");
                cmd.Parameters.AddWithValue("@email", "approver-a@corp.local");
                cmd.ExecuteNonQuery();
            }

            var approverBSid = "S-1-5-21-APPROVER-B";
            var approverBId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1)
                                    ON CONFLICT (id) DO NOTHING;";
                cmd.Parameters.AddWithValue("@id", approverBId);
                cmd.Parameters.AddWithValue("@sid", approverBSid);
                cmd.Parameters.AddWithValue("@account", "CORP\\approver-b");
                cmd.Parameters.AddWithValue("@name", "Finance Approver B");
                cmd.Parameters.AddWithValue("@email", "approver-b@corp.local");
                cmd.ExecuteNonQuery();
            }

            ownerId = ResolveOwnerId(conn, trans, ownerSid, ownerId);
            salesOwnerId = ResolveOwnerId(conn, trans, salesOwnerSid, salesOwnerId);
            approverId = ResolveOwnerId(conn, trans, approverSid, approverId);
            approverAId = ResolveOwnerId(conn, trans, approverASid, approverAId);
            approverBId = ResolveOwnerId(conn, trans, approverBSid, approverBId);

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
                                            VALUES (@id, 'SqlServer', @domain, @schema, @name, @disp, @sens, @four, 1)
                                            ON CONFLICT (id) DO NOTHING;";
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
                        idCmd.CommandText = "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@name) LIMIT 1";
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
                        using var towCmd = conn.CreateCommand();
                        towCmd.Transaction = trans;
                        towCmd.CommandText = @"INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                                              VALUES (@id1, @tid, @oid1, 'PRIMARY'),
                                                     (@id2, @tid, @oid2, 'DELEGATE')
                                              ON CONFLICT (id) DO NOTHING;";
                        towCmd.Parameters.AddWithValue("@id1", Guid.NewGuid().ToString());
                        towCmd.Parameters.AddWithValue("@oid1", ownerId);
                        towCmd.Parameters.AddWithValue("@id2", Guid.NewGuid().ToString());
                        towCmd.Parameters.AddWithValue("@oid2", approverId);
                        towCmd.Parameters.AddWithValue("@tid", tableId);
                        towCmd.ExecuteNonQuery();
                    }
                    else if (domain == "finance" && t == 5)
                    {
                        using var towCmd = conn.CreateCommand();
                        towCmd.Transaction = trans;
                        towCmd.CommandText = @"INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                                              VALUES (@id1, @tid, @oid1, 'PRIMARY'),
                                                     (@id2, @tid, @oid2, 'DELEGATE'),
                                                     (@id3, @tid, @oid3, 'DELEGATE')
                                              ON CONFLICT (id) DO NOTHING;";
                        towCmd.Parameters.AddWithValue("@id1", Guid.NewGuid().ToString());
                        towCmd.Parameters.AddWithValue("@oid1", ownerId);
                        towCmd.Parameters.AddWithValue("@id2", Guid.NewGuid().ToString());
                        towCmd.Parameters.AddWithValue("@oid2", approverAId);
                        towCmd.Parameters.AddWithValue("@id3", Guid.NewGuid().ToString());
                        towCmd.Parameters.AddWithValue("@oid3", approverBId);
                        towCmd.Parameters.AddWithValue("@tid", tableId);
                        towCmd.ExecuteNonQuery();
                    }
                    else if (domain == "sales" && (t == 1 || t == 5))
                    {
                        using var towCmd = conn.CreateCommand();
                        towCmd.Transaction = trans;
                        towCmd.CommandText = @"INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                                              VALUES (@id, @tid, @oid, 'PRIMARY')
                                              ON CONFLICT (id) DO NOTHING;";
                        towCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                        towCmd.Parameters.AddWithValue("@tid", tableId);
                        towCmd.Parameters.AddWithValue("@oid", salesOwnerId);
                        towCmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = trans;
                        cmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
                                            VALUES (@id, @domain, @schema, @name, 1, @now)
                                            ON CONFLICT (table_id) DO NOTHING;";
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
                        var colId = Guid.NewGuid().ToString();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = trans;
                            cmd.CommandText = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive)
                                                VALUES (@id, @tid, @name, @type, @sens)
                                                ON CONFLICT (id) DO NOTHING;";
                            cmd.Parameters.AddWithValue("@id", colId);
                            cmd.Parameters.AddWithValue("@tid", tableId);
                            cmd.Parameters.AddWithValue("@name", cName);
                            cmd.Parameters.AddWithValue("@type", cType);
                            cmd.Parameters.AddWithValue("@sens", cSens ? 1 : 0);
                            cmd.ExecuteNonQuery();
                        }

                        using (var colIdCmd = conn.CreateCommand())
                        {
                            colIdCmd.Transaction = trans;
                            colIdCmd.CommandText = "SELECT id FROM TABLE_COLUMNS WHERE table_id = @tid AND LOWER(column_name) = LOWER(@name) LIMIT 1";
                            colIdCmd.Parameters.AddWithValue("@tid", tableId);
                            colIdCmd.Parameters.AddWithValue("@name", cName);
                            var actualColId = colIdCmd.ExecuteScalar()?.ToString();
                            if (!string.IsNullOrEmpty(actualColId))
                            {
                                colId = actualColId;
                            }
                        }

                        if (cSens)
                        {
                            using var maskCmd = conn.CreateCommand();
                            maskCmd.Transaction = trans;
                            maskCmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement)
                                                   VALUES (@id, @cid, 'EMAIL', NULL, '***@***.***')
                                                   ON CONFLICT (id) DO NOTHING;";
                            maskCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                            maskCmd.Parameters.AddWithValue("@cid", colId);
                            maskCmd.ExecuteNonQuery();
                        }
                    }
                }
            }

            var adminRoleId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO ROLES (id, role_name, description)
                                    VALUES (@id, 'GovernanceAdmin', 'Platform governance administrators')
                                    ON CONFLICT (role_name) DO NOTHING;";
                cmd.Parameters.AddWithValue("@id", adminRoleId);
                cmd.ExecuteNonQuery();
            }

            using (var idCmd = conn.CreateCommand())
            {
                idCmd.Transaction = trans;
                idCmd.CommandText = "SELECT id FROM ROLES WHERE role_name = 'GovernanceAdmin' LIMIT 1";
                var actualRoleId = idCmd.ExecuteScalar()?.ToString();
                if (!string.IsNullOrEmpty(actualRoleId))
                {
                    adminRoleId = actualRoleId;
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                                    VALUES (@id, @rid, 'User', 'S-1-5-21-GOVERNANCEADMIN')
                                    ON CONFLICT (id) DO NOTHING;";
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@rid", adminRoleId);
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
        catch (Exception ex)
        {
            trans.Rollback();
            _logger?.LogWarning(ex, "Failed to seed demo data into PostgreSQL Governance DB; skipping.");
        }
    }

    private static string ResolveOwnerId(NpgsqlConnection conn, NpgsqlTransaction trans, string sid, string fallbackId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = trans;
        cmd.CommandText = "SELECT id FROM DATA_OWNERS WHERE ad_sid = @sid LIMIT 1";
        cmd.Parameters.AddWithValue("@sid", sid);
        var existing = cmd.ExecuteScalar()?.ToString();
        return !string.IsNullOrEmpty(existing) ? existing : fallbackId;
    }

}
