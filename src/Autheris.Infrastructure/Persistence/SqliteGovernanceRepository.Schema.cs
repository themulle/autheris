using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository
{
    private void SeedInitialCatalog(SqliteConnection conn)
    {
        // 10 domains with sample tables
        var domains = new[]
        {
            "finance", "hr", "sales", "inventory", "compliance",
            "marketing", "logistics", "crm", "procurement", "billing"
        };

        using var trans = conn.BeginTransaction();
        try
        {
            // Seed sample DataOwners
            var ownerSid = "S-1-5-21-DATAOWNER-1";
            var ownerId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT OR IGNORE INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1);";
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
                cmd.CommandText = @"INSERT OR IGNORE INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1);";
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
                cmd.CommandText = @"INSERT OR IGNORE INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1);";
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
                cmd.CommandText = @"INSERT OR IGNORE INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1);";
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
                cmd.CommandText = @"INSERT OR IGNORE INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1);";
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

                    // Insert Table
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = trans;
                        cmd.CommandText = @"INSERT OR IGNORE INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active)
                                            VALUES (@id, 'SqlServer', @domain, @schema, @name, @disp, @sens, @four, 1);";
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
                        idCmd.CommandText = "SELECT id FROM TABLES WHERE source_name = @domain AND schema_name = @schema AND table_name = @name LIMIT 1";
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
                        towCmd.CommandText = @"INSERT OR IGNORE INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                                              VALUES (@id1, @tid, @oid1, 'PRIMARY'),
                                                     (@id2, @tid, @oid2, 'DELEGATE');";
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
                        towCmd.CommandText = @"INSERT OR IGNORE INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                                              VALUES (@id1, @tid, @oid1, 'PRIMARY'),
                                                     (@id2, @tid, @oid2, 'DELEGATE'),
                                                     (@id3, @tid, @oid3, 'DELEGATE');";
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
                        towCmd.CommandText = @"INSERT OR IGNORE INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                                              VALUES (@id, @tid, @oid, 'PRIMARY');";
                        towCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                        towCmd.Parameters.AddWithValue("@tid", tableId);
                        towCmd.Parameters.AddWithValue("@oid", salesOwnerId);
                        towCmd.ExecuteNonQuery();
                    }

                    // Insert Policy Epoch
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = trans;
                        cmd.CommandText = @"INSERT OR IGNORE INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
                                            VALUES (@id, @domain, @schema, @name, 1, @now);";
                        cmd.Parameters.AddWithValue("@id", tableId);
                        cmd.Parameters.AddWithValue("@domain", domain);
                        cmd.Parameters.AddWithValue("@schema", schema);
                        cmd.Parameters.AddWithValue("@name", tableName);
                        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                        cmd.ExecuteNonQuery();
                    }

                    // Insert 5 columns for each table
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
                            cmd.CommandText = @"INSERT OR IGNORE INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive)
                                                VALUES (@id, @tid, @name, @type, @sens);";
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
                            colIdCmd.CommandText = "SELECT id FROM TABLE_COLUMNS WHERE table_id = @tid AND column_name = @name LIMIT 1";
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
                            using var cmd = conn.CreateCommand();
                            cmd.Transaction = trans;
                            cmd.CommandText = @"INSERT OR IGNORE INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                                                VALUES (@id, @cid, 'REGEX', '', '', 'key-2026-q1');";
                            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                            cmd.Parameters.AddWithValue("@cid", colId);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
            }

            // Seed Child Table: finance_items for finance_table_1
            var childTableId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT OR IGNORE INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active)
                                    VALUES (@id, 'SqlServer', 'finance', 'dbo', 'finance_items', 'Finance Items', 'NORMAL', 0, 1);";
                cmd.Parameters.AddWithValue("@id", childTableId);
                cmd.ExecuteNonQuery();
            }

            using (var idCmd = conn.CreateCommand())
            {
                idCmd.Transaction = trans;
                idCmd.CommandText = "SELECT id FROM TABLES WHERE source_name = 'finance' AND schema_name = 'dbo' AND table_name = 'finance_items' LIMIT 1";
                var actualChildId = idCmd.ExecuteScalar()?.ToString();
                if (!string.IsNullOrEmpty(actualChildId))
                {
                    childTableId = actualChildId;
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT OR IGNORE INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
                                    VALUES (@id, 'finance', 'dbo', 'finance_items', 1, @now);";
                cmd.Parameters.AddWithValue("@id", childTableId);
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            var childCols = new[]
            {
                ("id", "varchar", false),
                ("parent_id", "varchar", false),
                ("product_name", "varchar", false),
                ("price", "decimal", false),
                ("sensitive_note", "varchar", true)
            };
            foreach (var (cName, cType, cSens) in childCols)
            {
                var colId = Guid.NewGuid().ToString();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT OR IGNORE INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive)
                                    VALUES (@id, @tid, @name, @type, @sens);";
                cmd.Parameters.AddWithValue("@id", colId);
                cmd.Parameters.AddWithValue("@tid", childTableId);
                cmd.Parameters.AddWithValue("@name", cName);
                cmd.Parameters.AddWithValue("@type", cType);
                cmd.Parameters.AddWithValue("@sens", cSens ? 1 : 0);
                cmd.ExecuteNonQuery();

                using (var colIdCmd = conn.CreateCommand())
                {
                    colIdCmd.Transaction = trans;
                    colIdCmd.CommandText = "SELECT id FROM TABLE_COLUMNS WHERE table_id = @tid AND column_name = @name LIMIT 1";
                    colIdCmd.Parameters.AddWithValue("@tid", childTableId);
                    colIdCmd.Parameters.AddWithValue("@name", cName);
                    var actualColId = colIdCmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrEmpty(actualColId))
                    {
                        colId = actualColId;
                    }
                }

                if (cSens)
                {
                    using var mcmd = conn.CreateCommand();
                    mcmd.Transaction = trans;
                    mcmd.CommandText = @"INSERT OR IGNORE INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                                        VALUES (@id, @cid, 'REDACT', '', '[CONFIDENTIAL NOTE]', 'key-2026-q1');";
                    mcmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                    mcmd.Parameters.AddWithValue("@cid", colId);
                    mcmd.ExecuteNonQuery();
                }
            }

            // Relate finance_table_1 -> finance_items (issue #151: ordered key pairs live in TABLE_RELATION_KEYS)
            var relId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT OR IGNORE INTO TABLE_RELATIONS (id, parent_table_id, child_table_id, relation_name, cardinality)
                                    SELECT @relId, id, @childId, 'items', 'OneToMany'
                                    FROM TABLES
                                    WHERE schema_name = 'dbo' AND table_name = 'finance_table_1';";
                cmd.Parameters.AddWithValue("@relId", relId);
                cmd.Parameters.AddWithValue("@childId", childTableId);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT OR IGNORE INTO TABLE_RELATION_KEYS (relation_id, key_ordinal, parent_column, child_column)
                                    SELECT r.id, 1, 'id', 'parent_id'
                                    FROM TABLE_RELATIONS r
                                    JOIN TABLES tp ON tp.id = r.parent_table_id
                                    WHERE r.relation_name = 'items' AND tp.schema_name = 'dbo' AND tp.table_name = 'finance_table_1';";
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
        catch
        {
            trans.Rollback();
            throw;
        }
    }

    private static string ResolveOwnerId(SqliteConnection connection, SqliteTransaction trans, string sid, string fallbackId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = trans;
        cmd.CommandText = "SELECT id FROM DATA_OWNERS WHERE ad_sid = @sid LIMIT 1";
        cmd.Parameters.AddWithValue("@sid", sid);
        var existing = cmd.ExecuteScalar()?.ToString();
        return !string.IsNullOrEmpty(existing) ? existing : fallbackId;
    }
}
