namespace Autheris.Infrastructure.Persistence;

using System;
using System.Data.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Logging;

public partial class GovernanceRepository
{
    /// <summary>Seeds the development demo catalog; failures are logged and skipped.</summary>
    internal void SeedInitialCatalog()
    {
        if (IsPostgreSql)
        {
            SeedInitialCatalogPostgreSql();
        }
        else
        {
            SeedInitialCatalogSqlServer();
        }
    }

    private void SeedInitialCatalogPostgreSql()
    {
        var domains = new[]
        {
            "finance", "hr", "sales", "inventory", "compliance",
            "marketing", "logistics", "crm", "procurement", "billing"
        };

        using var conn = OpenUncheckedConnection();
        using var trans = conn.BeginTransaction();
        try
        {
            var ownerSid = "S-1-5-21-1000-1000-1000-1001";
            var ownerId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1)
                                    ON CONFLICT DO NOTHING;";
                cmd.AddBound("@id", ownerId);
                cmd.AddBound("@sid", ownerSid);
                cmd.AddBound("@account", "CORP\\dataowner");
                cmd.AddBound("@name", "Chief Finance Data Owner");
                cmd.AddBound("@email", "dataowner@corp.local");
                cmd.ExecuteNonQuery();
            }

            var salesOwnerSid = "S-1-5-21-DATAOWNER-APPROVER";
            var salesOwnerId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1)
                                    ON CONFLICT DO NOTHING;";
                cmd.AddBound("@id", salesOwnerId);
                cmd.AddBound("@sid", salesOwnerSid);
                cmd.AddBound("@account", "CORP\\salesowner");
                cmd.AddBound("@name", "Chief Sales Data Owner");
                cmd.AddBound("@email", "salesowner@corp.local");
                cmd.ExecuteNonQuery();
            }

            var approverSid = "S-1-5-21-APPROVER";
            var approverId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1)
                                    ON CONFLICT DO NOTHING;";
                cmd.AddBound("@id", approverId);
                cmd.AddBound("@sid", approverSid);
                cmd.AddBound("@account", "CORP\\approver");
                cmd.AddBound("@name", "Finance Approver");
                cmd.AddBound("@email", "approver@corp.local");
                cmd.ExecuteNonQuery();
            }

            var approverASid = "S-1-5-21-1000-1000-1000-1002";
            var approverAId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1)
                                    ON CONFLICT DO NOTHING;";
                cmd.AddBound("@id", approverAId);
                cmd.AddBound("@sid", approverASid);
                cmd.AddBound("@account", "CORP\\approver-a");
                cmd.AddBound("@name", "Finance Approver A");
                cmd.AddBound("@email", "approver-a@corp.local");
                cmd.ExecuteNonQuery();
            }

            var approverBSid = "S-1-5-21-1000-1000-1000-1003";
            var approverBId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                    VALUES (@id, @sid, @account, @name, @email, 1)
                                    ON CONFLICT DO NOTHING;";
                cmd.AddBound("@id", approverBId);
                cmd.AddBound("@sid", approverBSid);
                cmd.AddBound("@account", "CORP\\approver-b");
                cmd.AddBound("@name", "Finance Approver B");
                cmd.AddBound("@email", "approver-b@corp.local");
                cmd.ExecuteNonQuery();
            }

            ownerId = ResolveOwnerIdPostgreSql(conn, trans, ownerSid, ownerId);
            salesOwnerId = ResolveOwnerIdPostgreSql(conn, trans, salesOwnerSid, salesOwnerId);
            approverId = ResolveOwnerIdPostgreSql(conn, trans, approverSid, approverId);
            approverAId = ResolveOwnerIdPostgreSql(conn, trans, approverASid, approverAId);
            approverBId = ResolveOwnerIdPostgreSql(conn, trans, approverBSid, approverBId);

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
                                            ON CONFLICT DO NOTHING;";
                        cmd.AddBound("@id", tableId);
                        cmd.AddBound("@domain", domain);
                        cmd.AddBound("@schema", schema);
                        cmd.AddBound("@name", tableName);
                        cmd.AddBound("@disp", $"{domain.ToUpperInvariant()} Table {t}");
                        cmd.AddBound("@sens", (t % 5 == 0) ? "HIGH" : "NORMAL");
                        cmd.AddBound("@four", (t % 5 == 0) ? 1 : 0);
                        cmd.ExecuteNonQuery();
                    }

                    using (var idCmd = conn.CreateCommand())
                    {
                        idCmd.Transaction = trans;
                        idCmd.CommandText = "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@name) LIMIT 1";
                        idCmd.AddBound("@domain", domain);
                        idCmd.AddBound("@schema", schema);
                        idCmd.AddBound("@name", tableName);
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
                                              ON CONFLICT DO NOTHING;";
                        towCmd.AddBound("@id1", Guid.NewGuid().ToString());
                        towCmd.AddBound("@oid1", ownerId);
                        towCmd.AddBound("@id2", Guid.NewGuid().ToString());
                        towCmd.AddBound("@oid2", approverId);
                        towCmd.AddBound("@tid", tableId);
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
                                              ON CONFLICT DO NOTHING;";
                        towCmd.AddBound("@id1", Guid.NewGuid().ToString());
                        towCmd.AddBound("@oid1", ownerId);
                        towCmd.AddBound("@id2", Guid.NewGuid().ToString());
                        towCmd.AddBound("@oid2", approverAId);
                        towCmd.AddBound("@id3", Guid.NewGuid().ToString());
                        towCmd.AddBound("@oid3", approverBId);
                        towCmd.AddBound("@tid", tableId);
                        towCmd.ExecuteNonQuery();
                    }
                    else if (domain == "sales" && (t == 1 || t == 5))
                    {
                        using var towCmd = conn.CreateCommand();
                        towCmd.Transaction = trans;
                        towCmd.CommandText = @"INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                                              VALUES (@id, @tid, @oid, 'PRIMARY')
                                              ON CONFLICT DO NOTHING;";
                        towCmd.AddBound("@id", Guid.NewGuid().ToString());
                        towCmd.AddBound("@tid", tableId);
                        towCmd.AddBound("@oid", salesOwnerId);
                        towCmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = trans;
                        cmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
                                            VALUES (@id, @domain, @schema, @name, 1, @now)
                                            ON CONFLICT (table_id) DO NOTHING;";
                        cmd.AddBound("@id", tableId);
                        cmd.AddBound("@domain", domain);
                        cmd.AddBound("@schema", schema);
                        cmd.AddBound("@name", tableName);
                        cmd.AddBound("@now", TimeProvider.System.GetUtcNow().ToString("O"));
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
                                                ON CONFLICT DO NOTHING;";
                            cmd.AddBound("@id", colId);
                            cmd.AddBound("@tid", tableId);
                            cmd.AddBound("@name", cName);
                            cmd.AddBound("@type", cType);
                            cmd.AddBound("@sens", cSens ? 1 : 0);
                            cmd.ExecuteNonQuery();
                        }

                        using (var colIdCmd = conn.CreateCommand())
                        {
                            colIdCmd.Transaction = trans;
                            colIdCmd.CommandText = "SELECT id FROM TABLE_COLUMNS WHERE table_id = @tid AND LOWER(column_name) = LOWER(@name) LIMIT 1";
                            colIdCmd.AddBound("@tid", tableId);
                            colIdCmd.AddBound("@name", cName);
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
                            maskCmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                                                   SELECT @id, @cid, 'REGEX', '', '', 'key-2026-q1'
                                                   WHERE NOT EXISTS (SELECT 1 FROM COLUMN_MASKING_RULES WHERE table_column_id = @cid)
                                                   ON CONFLICT DO NOTHING;";
                            maskCmd.AddBound("@id", Guid.NewGuid().ToString());
                            maskCmd.AddBound("@cid", colId);
                            maskCmd.ExecuteNonQuery();
                        }
                    }
                }
            }

            // Child table finance_items for finance_table_1 (same demo content as the SQLite seed that the governance tests were written against).
            var childTableId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active)
                                    VALUES (@id, 'SqlServer', 'finance', 'dbo', 'finance_items', 'Finance Items', 'NORMAL', 0, 1)
                                    ON CONFLICT DO NOTHING;";
                cmd.AddBound("@id", childTableId);
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
                cmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
                                    VALUES (@id, 'finance', 'dbo', 'finance_items', 1, @now)
                                    ON CONFLICT DO NOTHING;";
                cmd.AddBound("@id", childTableId);
                cmd.AddBound("@now", TimeProvider.System.GetUtcNow().ToString("O"));
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
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = trans;
                    cmd.CommandText = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive)
                                        VALUES (@id, @tid, @name, @type, @sens)
                                        ON CONFLICT DO NOTHING;";
                    cmd.AddBound("@id", colId);
                    cmd.AddBound("@tid", childTableId);
                    cmd.AddBound("@name", cName);
                    cmd.AddBound("@type", cType);
                    cmd.AddBound("@sens", cSens ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }

                using (var colIdCmd = conn.CreateCommand())
                {
                    colIdCmd.Transaction = trans;
                    colIdCmd.CommandText = "SELECT id FROM TABLE_COLUMNS WHERE table_id = @tid AND LOWER(column_name) = LOWER(@name) LIMIT 1";
                    colIdCmd.AddBound("@tid", childTableId);
                    colIdCmd.AddBound("@name", cName);
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
                    mcmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                                         SELECT @id, @cid, 'REDACT', '', '[CONFIDENTIAL NOTE]', 'key-2026-q1'
                                         WHERE NOT EXISTS (SELECT 1 FROM COLUMN_MASKING_RULES WHERE table_column_id = @cid)
                                         ON CONFLICT DO NOTHING;";
                    mcmd.AddBound("@id", Guid.NewGuid().ToString());
                    mcmd.AddBound("@cid", colId);
                    mcmd.ExecuteNonQuery();
                }
            }

            // Relate finance_table_1 -> finance_items (issue #151: ordered key pairs live in TABLE_RELATION_KEYS).
            var relId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO TABLE_RELATIONS (id, parent_table_id, child_table_id, relation_name, cardinality)
                                    SELECT @relId, id, @childId, 'items', 'OneToMany'
                                    FROM TABLES
                                    WHERE schema_name = 'dbo' AND table_name = 'finance_table_1'
                                    ON CONFLICT DO NOTHING;";
                cmd.AddBound("@relId", relId);
                cmd.AddBound("@childId", childTableId);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO TABLE_RELATION_KEYS (relation_id, key_ordinal, parent_column, child_column)
                                    SELECT r.id, 1, 'id', 'parent_id'
                                    FROM TABLE_RELATIONS r
                                    JOIN TABLES tp ON tp.id = r.parent_table_id
                                    WHERE r.relation_name = 'items' AND tp.schema_name = 'dbo' AND tp.table_name = 'finance_table_1'
                                    ON CONFLICT DO NOTHING;";
                cmd.ExecuteNonQuery();
            }

            var adminRoleId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO ROLES (id, role_name, description)
                                    VALUES (@id, 'GovernanceAdmin', 'Platform governance administrators')
                                    ON CONFLICT (role_name) DO NOTHING;";
                cmd.AddBound("@id", adminRoleId);
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
                                    ON CONFLICT DO NOTHING;";
                cmd.AddBound("@id", Guid.NewGuid().ToString());
                cmd.AddBound("@rid", adminRoleId);
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
        catch (Exception)
        {
            trans.Rollback();
            LogCatalogNotice("Failed to seed demo data into the governance database; skipping.");
        }
    }

    private static string ResolveOwnerIdPostgreSql(DbConnection conn, DbTransaction trans, string sid, string fallbackId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = trans;
        cmd.CommandText = "SELECT id FROM DATA_OWNERS WHERE ad_sid = @sid LIMIT 1";
        cmd.AddBound("@sid", sid);
        var existing = cmd.ExecuteScalar()?.ToString();
        return !string.IsNullOrEmpty(existing) ? existing : fallbackId;
    }

    private void SeedInitialCatalogSqlServer()
    {
        var domains = new[]
        {
            "finance", "hr", "sales", "inventory", "compliance",
            "marketing", "logistics", "crm", "procurement", "billing"
        };

        using var conn = OpenUncheckedConnection();
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
                cmd.AddBound("@id", Guid.NewGuid().ToString());
                cmd.AddBound("@sid", sid);
                cmd.AddBound("@account", account);
                cmd.AddBound("@name", name);
                cmd.AddBound("@email", email);
                cmd.ExecuteNonQuery();
                return ResolveOwnerIdSqlServer(conn, trans, sid, string.Empty);
            }

            var ownerId = EnsureOwner("S-1-5-21-1000-1000-1000-1001", "CORP\\dataowner", "Chief Finance Data Owner", "dataowner@corp.local");
            var salesOwnerId = EnsureOwner("S-1-5-21-DATAOWNER-APPROVER", "CORP\\salesowner", "Chief Sales Data Owner", "salesowner@corp.local");
            var approverId = EnsureOwner("S-1-5-21-APPROVER", "CORP\\approver", "Finance Approver", "approver@corp.local");
            var approverAId = EnsureOwner("S-1-5-21-1000-1000-1000-1002", "CORP\\approver-a", "Finance Approver A", "approver-a@corp.local");
            var approverBId = EnsureOwner("S-1-5-21-1000-1000-1000-1003", "CORP\\approver-b", "Finance Approver B", "approver-b@corp.local");

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
                        cmd.AddBound("@id", tableId);
                        cmd.AddBound("@domain", domain);
                        cmd.AddBound("@schema", schema);
                        cmd.AddBound("@name", tableName);
                        cmd.AddBound("@disp", $"{domain.ToUpperInvariant()} Table {t}");
                        cmd.AddBound("@sens", (t % 5 == 0) ? "HIGH" : "NORMAL");
                        cmd.AddBound("@four", (t % 5 == 0) ? 1 : 0);
                        cmd.ExecuteNonQuery();
                    }

                    using (var idCmd = conn.CreateCommand())
                    {
                        idCmd.Transaction = trans;
                        idCmd.CommandText = "SELECT TOP (1) id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@name)";
                        idCmd.AddBound("@domain", domain);
                        idCmd.AddBound("@schema", schema);
                        idCmd.AddBound("@name", tableName);
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
                        cmd.AddBound("@id", tableId);
                        cmd.AddBound("@domain", domain);
                        cmd.AddBound("@schema", schema);
                        cmd.AddBound("@name", tableName);
                        cmd.AddBound("@now", TimeProvider.System.GetUtcNow().ToString("O"));
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
                            cmd.AddBound("@id", Guid.NewGuid().ToString());
                            cmd.AddBound("@tid", tableId);
                            cmd.AddBound("@name", cName);
                            cmd.AddBound("@type", cType);
                            cmd.AddBound("@sens", cSens ? 1 : 0);
                            cmd.ExecuteNonQuery();
                        }

                        string? colId;
                        using (var colIdCmd = conn.CreateCommand())
                        {
                            colIdCmd.Transaction = trans;
                            colIdCmd.CommandText = "SELECT TOP (1) id FROM TABLE_COLUMNS WHERE table_id = @tid AND LOWER(column_name) = LOWER(@name)";
                            colIdCmd.AddBound("@tid", tableId);
                            colIdCmd.AddBound("@name", cName);
                            colId = colIdCmd.ExecuteScalar()?.ToString();
                        }

                        if (cSens && !string.IsNullOrEmpty(colId))
                        {
                            using var maskCmd = conn.CreateCommand();
                            maskCmd.Transaction = trans;
                            maskCmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement)
                                                   SELECT @id, @cid, 'EMAIL', NULL, '***@***.***'
                                                   WHERE NOT EXISTS (SELECT 1 FROM COLUMN_MASKING_RULES WHERE table_column_id = @cid);";
                            maskCmd.AddBound("@id", Guid.NewGuid().ToString());
                            maskCmd.AddBound("@cid", colId);
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
                cmd.AddBound("@id", Guid.NewGuid().ToString());
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
                cmd.AddBound("@id", Guid.NewGuid().ToString());
                cmd.AddBound("@rid", adminRoleId);
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
        catch (Exception)
        {
            trans.Rollback();
            LogCatalogNotice("Failed to seed demo data into the governance database; skipping.");
        }
    }

    private static void EnsureTableOwner(DbConnection conn, DbTransaction trans, string tableId, string ownerId, string role)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = trans;
        cmd.CommandText = @"INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                            SELECT @id, @tid, @oid, @role
                            WHERE NOT EXISTS (SELECT 1 FROM TABLE_OWNERS WHERE table_id = @tid AND data_owner_id = @oid AND owner_role = @role);";
        cmd.AddBound("@id", Guid.NewGuid().ToString());
        cmd.AddBound("@tid", tableId);
        cmd.AddBound("@oid", ownerId);
        cmd.AddBound("@role", role);
        cmd.ExecuteNonQuery();
    }

    private static string ResolveOwnerIdSqlServer(DbConnection conn, DbTransaction trans, string sid, string fallbackId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = trans;
        cmd.CommandText = "SELECT TOP (1) id FROM DATA_OWNERS WHERE ad_sid = @sid";
        cmd.AddBound("@sid", sid);
        var existing = cmd.ExecuteScalar()?.ToString();
        return !string.IsNullOrEmpty(existing) ? existing : fallbackId;
    }
}
