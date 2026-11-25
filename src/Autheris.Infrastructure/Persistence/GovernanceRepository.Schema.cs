namespace Autheris.Infrastructure.Persistence;

using System;
using System.Data.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Catalog;
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
                        cmd.CommandText = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, is_active)
                                            VALUES (@id, 'SqlServer', @domain, @schema, @name, @disp, 1)
                                            ON CONFLICT DO NOTHING;";
                        cmd.AddBound("@id", tableId);
                        cmd.AddBound("@domain", domain);
                        cmd.AddBound("@schema", schema);
                        cmd.AddBound("@name", tableName);
                        cmd.AddBound("@disp", $"{domain.ToUpperInvariant()} Table {t}");
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

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = trans;
                        cmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, change_version, updated_at)
                                            VALUES (@id, @domain, @schema, @name, 1, 1, @now)
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
                            cmd.CommandText = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, ordinal)
                                                VALUES (@id, @tid, @name, @type, (SELECT COALESCE(MAX(ordinal), 0) + 1 FROM TABLE_COLUMNS WHERE table_id = @tid))
                                                ON CONFLICT DO NOTHING;";
                            cmd.AddBound("@id", colId);
                            cmd.AddBound("@tid", tableId);
                            cmd.AddBound("@name", cName);
                            cmd.AddBound("@type", cType);
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
                            CatalogRowHydrator.SeedSensitiveColumn(conn, trans, colId, "REGEX", string.Empty);
                        }
                    }
                }
            }

            // Child table finance_items for finance_table_1.
            var childTableId = Guid.NewGuid().ToString();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, is_active)
                                    VALUES (@id, 'SqlServer', 'finance', 'dbo', 'finance_items', 'Finance Items', 1)
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
                cmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, change_version, updated_at)
                                    VALUES (@id, 'finance', 'dbo', 'finance_items', 1, 1, @now)
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
                    cmd.CommandText = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, ordinal)
                                        VALUES (@id, @tid, @name, @type, (SELECT COALESCE(MAX(ordinal), 0) + 1 FROM TABLE_COLUMNS WHERE table_id = @tid))
                                        ON CONFLICT DO NOTHING;";
                    cmd.AddBound("@id", colId);
                    cmd.AddBound("@tid", childTableId);
                    cmd.AddBound("@name", cName);
                    cmd.AddBound("@type", cType);
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
                    CatalogRowHydrator.SeedSensitiveColumn(conn, trans, colId, "REDACT", "[CONFIDENTIAL NOTE]");
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

            trans.Commit();
        }
        catch (Exception)
        {
            trans.Rollback();
            LogCatalogNotice("Failed to seed demo data into the governance database; skipping.");
        }
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
                        cmd.CommandText = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, is_active)
                                            SELECT @id, 'SqlServer', @domain, @schema, @name, @disp, 1
                                            WHERE NOT EXISTS (SELECT 1 FROM TABLES WHERE source_name = @domain AND schema_name = @schema AND table_name = @name);";
                        cmd.AddBound("@id", tableId);
                        cmd.AddBound("@domain", domain);
                        cmd.AddBound("@schema", schema);
                        cmd.AddBound("@name", tableName);
                        cmd.AddBound("@disp", $"{domain.ToUpperInvariant()} Table {t}");
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

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = trans;
                        cmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, change_version, updated_at)
                                            SELECT @id, @domain, @schema, @name, 1, 1, @now
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
                            cmd.CommandText = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, ordinal)
                                                SELECT @id, @tid, @name, @type, (SELECT COALESCE(MAX(ordinal), 0) + 1 FROM TABLE_COLUMNS WHERE table_id = @tid)
                                                WHERE NOT EXISTS (SELECT 1 FROM TABLE_COLUMNS WHERE table_id = @tid AND column_name = @name);";
                            cmd.AddBound("@id", Guid.NewGuid().ToString());
                            cmd.AddBound("@tid", tableId);
                            cmd.AddBound("@name", cName);
                            cmd.AddBound("@type", cType);
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
                            CatalogRowHydrator.SeedSensitiveColumn(conn, trans, colId, "EMAIL", "***@***.***");
                        }
                    }
                }
            }

            trans.Commit();
        }
        catch (Exception)
        {
            trans.Rollback();
            LogCatalogNotice("Failed to seed demo data into the governance database; skipping.");
        }
    }
}
