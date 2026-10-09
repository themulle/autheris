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
    private void InitializeDatabase()
    {
        var configuredJournalMode = _options?.GovernanceDb?.JournalMode;
        if (string.IsNullOrWhiteSpace(configuredJournalMode))
        {
            configuredJournalMode = "WAL";
        }
        var journalMode = configuredJournalMode.Trim().ToUpperInvariant();
        if (journalMode != "WAL" && journalMode != "DELETE" && journalMode != "TRUNCATE" && journalMode != "MEMORY" && journalMode != "OFF")
        {
            journalMode = "WAL";
        }

        try
        {
            using var pragmaCmd = _connection.CreateCommand();
            pragmaCmd.CommandText = $@"
                PRAGMA journal_mode = {journalMode};
                PRAGMA synchronous = {(journalMode == "WAL" ? "NORMAL" : "FULL")};
                PRAGMA busy_timeout = 5000;
                PRAGMA cache_size = -64000;
                PRAGMA temp_store = MEMORY;
            ";
            pragmaCmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to apply PRAGMA journal_mode={JournalMode}; attempting graceful fallback to DELETE mode (R-42).", journalMode);
            try
            {
                using var fallbackCmd = _connection.CreateCommand();
                fallbackCmd.CommandText = @"
                    PRAGMA journal_mode = DELETE;
                    PRAGMA synchronous = FULL;
                    PRAGMA busy_timeout = 5000;
                ";
                fallbackCmd.ExecuteNonQuery();
            }
            catch (Exception fbEx)
            {
                _logger?.LogError(fbEx, "Graceful fallback to journal_mode=DELETE also failed.");
            }
        }

        DeduplicateDataOwnersBeforeIndex(_connection);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS TABLES (
                id TEXT PRIMARY KEY,
                source_type TEXT NOT NULL,
                source_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                display_name TEXT NOT NULL,
                description TEXT,
                long_description TEXT,
                doc_source TEXT,
                sensitivity TEXT NOT NULL,
                requires_four_eyes INTEGER NOT NULL,
                is_active INTEGER NOT NULL,
                data_source_type INTEGER NOT NULL DEFAULT 0,
                http_endpoint_json TEXT,
                plugin_name TEXT
            );

            CREATE TABLE IF NOT EXISTS TABLE_COLUMNS (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                column_name TEXT NOT NULL,
                data_type TEXT NOT NULL,
                is_sensitive INTEGER NOT NULL,
                description TEXT,
                long_description TEXT,
                doc_source TEXT,
                meta_json TEXT
            );

            CREATE TABLE IF NOT EXISTS COLUMN_MASKING_RULES (
                id TEXT PRIMARY KEY,
                table_column_id TEXT NOT NULL,
                rule_type TEXT NOT NULL,
                pattern_or_format TEXT,
                replacement TEXT,
                hmac_key_id TEXT
            );

            CREATE TABLE IF NOT EXISTS DATA_OWNERS (
                id TEXT PRIMARY KEY,
                ad_sid TEXT NOT NULL,
                ad_account TEXT NOT NULL,
                display_name TEXT NOT NULL,
                email TEXT NOT NULL,
                is_active INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS TABLE_OWNERS (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                data_owner_id TEXT NOT NULL,
                owner_role TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DATA_OWNER_DELEGATIONS (
                id TEXT PRIMARY KEY,
                data_owner_id TEXT NOT NULL,
                delegate_sid TEXT NOT NULL,
                valid_from TEXT NOT NULL,
                valid_to TEXT NOT NULL,
                reason TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ROLES (
                id TEXT PRIMARY KEY,
                role_name TEXT NOT NULL UNIQUE,
                description TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ROLE_MEMBERS (
                id TEXT PRIMARY KEY,
                role_id TEXT NOT NULL,
                member_type TEXT NOT NULL,
                member_sid TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS CONSENTS (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                consent_request_id TEXT,
                effect TEXT NOT NULL,
                grantee_type TEXT NOT NULL,
                grantee_sid TEXT,
                role_id TEXT,
                role_name TEXT,
                valid_from TEXT NOT NULL,
                valid_to TEXT NOT NULL,
                is_revoked INTEGER NOT NULL,
                revoked_by_sid TEXT,
                revoked_at TEXT,
                revoke_reason TEXT,
                tenant_id TEXT NOT NULL DEFAULT 'legacy-single-tenant'
            );

            CREATE TABLE IF NOT EXISTS CONSENT_COLUMN_RULES (
                id TEXT PRIMARY KEY,
                consent_id TEXT NOT NULL,
                table_column_id TEXT NOT NULL,
                column_name TEXT NOT NULL,
                access_level INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS CONSENT_ROW_FILTERS (
                id TEXT PRIMARY KEY,
                consent_id TEXT NOT NULL,
                filter_group INTEGER NOT NULL,
                table_column_id TEXT NOT NULL,
                column_name TEXT NOT NULL,
                operator TEXT NOT NULL,
                value_type TEXT NOT NULL,
                value_json TEXT NOT NULL,
                value_source TEXT NOT NULL,
                user_attribute TEXT,
                filter_type INTEGER NOT NULL DEFAULT 0,
                dependent_table TEXT,
                dependent_table_alias TEXT,
                foreign_key_column TEXT,
                primary_key_column TEXT,
                subquery_predicate_json TEXT,
                target_temporal_column TEXT,
                dependent_valid_from_column TEXT,
                dependent_valid_to_column TEXT,
                target_table_alias TEXT,
                additional_hops_json TEXT
            );

            CREATE TABLE IF NOT EXISTS POLICY_EPOCHS (
                table_id TEXT PRIMARY KEY,
                domain TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                epoch INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AUDIT_LOG_ENTRIES (
                id TEXT PRIMARY KEY,
                occurred_at TEXT NOT NULL,
                event_type TEXT NOT NULL,
                actor_sid TEXT NOT NULL,
                target_table TEXT NOT NULL,
                target_column TEXT,
                decision TEXT NOT NULL,
                trace_id TEXT NOT NULL,
                details_json TEXT NOT NULL,
                prev_hash TEXT NOT NULL,
                entry_hash TEXT NOT NULL,
                tenant_id TEXT NOT NULL DEFAULT 'legacy-single-tenant'
            );

            CREATE INDEX IF NOT EXISTS idx_audit_target_table ON AUDIT_LOG_ENTRIES (target_table, occurred_at);
            CREATE INDEX IF NOT EXISTS idx_audit_actor_sid ON AUDIT_LOG_ENTRIES (actor_sid, occurred_at);

            CREATE TABLE IF NOT EXISTS CONSENT_REQUESTS (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                requester_sid TEXT NOT NULL,
                requested_grantee_type TEXT NOT NULL,
                requested_grantee_ref TEXT NOT NULL,
                business_justification TEXT NOT NULL,
                status TEXT NOT NULL,
                requested_at TEXT NOT NULL,
                requested_valid_to TEXT NOT NULL,
                itsm_ticket_id TEXT,
                tenant_id TEXT,
                requester_identifiers_json TEXT
            );

            CREATE TABLE IF NOT EXISTS APPROVAL_STEPS (
                id TEXT PRIMARY KEY,
                consent_request_id TEXT NOT NULL,
                step_number INTEGER NOT NULL,
                approver_sid TEXT NOT NULL,
                decision TEXT NOT NULL,
                rejection_reason TEXT,
                decided_at TEXT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_APPROVAL_STEPS_REQ_STEP
                ON APPROVAL_STEPS (consent_request_id, step_number);

            CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLES_NATURAL
                ON TABLES (source_name, schema_name, table_name);

            CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_COLUMNS_NATURAL
                ON TABLE_COLUMNS (table_id, column_name);

            CREATE UNIQUE INDEX IF NOT EXISTS UX_DATA_OWNERS_SID
                ON DATA_OWNERS (ad_sid);

            CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_OWNERS_NATURAL
                ON TABLE_OWNERS (table_id, data_owner_id, owner_role);

            CREATE UNIQUE INDEX IF NOT EXISTS UX_POLICY_EPOCHS_NATURAL
                ON POLICY_EPOCHS (domain, schema_name, table_name);

            CREATE TABLE IF NOT EXISTS TABLE_RELATIONS (
                id TEXT PRIMARY KEY,
                parent_table_id TEXT NOT NULL,
                child_table_id TEXT NOT NULL,
                relation_name TEXT NOT NULL,
                join_key_parent TEXT NOT NULL,
                join_key_child TEXT NOT NULL,
                cardinality TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ITSM_OUTBOX (
                id TEXT PRIMARY KEY,
                request_id TEXT NOT NULL,
                tenant_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                preferred_system TEXT NOT NULL,
                status INTEGER NOT NULL,
                retry_count INTEGER NOT NULL,
                max_retries INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                next_retry_at TEXT,
                last_error TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_itsm_outbox_status_retry
                ON ITSM_OUTBOX (status, next_retry_at);

            -- Virtual filters (docs/plans/2026-10-08-umsetzungsplan-virtuelle-filter.md, phase 2)
            CREATE TABLE IF NOT EXISTS VIRTUAL_FILTERS (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                name TEXT NOT NULL,
                source TEXT NOT NULL,
                definition_json TEXT NOT NULL,
                definition_hash TEXT NOT NULL,
                managed_path TEXT,
                managed_commit TEXT,
                updated_by TEXT,
                updated_at TEXT NOT NULL,
                UNIQUE (tenant_id, name)
            );

            CREATE TABLE IF NOT EXISTS ACCESS_PROFILES (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                name TEXT NOT NULL,
                definition_json TEXT NOT NULL,
                definition_hash TEXT NOT NULL,
                managed_path TEXT,
                managed_commit TEXT,
                updated_by TEXT,
                updated_at TEXT NOT NULL,
                UNIQUE (tenant_id, name)
            );

            CREATE TABLE IF NOT EXISTS VIRTUAL_FILTER_GENERATION (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                generation BIGINT NOT NULL
            );

            INSERT INTO VIRTUAL_FILTER_GENERATION (id, generation) VALUES (1, 0) ON CONFLICT (id) DO NOTHING;
        ";
        cmd.ExecuteNonQuery();

        EnsureConsentRowFilterColumns();
        EnsureTableColumns();
        EnsureTableColumnFields();
        EnsureConsentRequestColumns();
        EnsureConsentColumns();
        EnsureAuditLogColumns();

        // Startup reference only; it is cross-checked against the external signed anchor (SEC H-17)
        // in InitializeAuditChainAnchor and never silently re-synchronised from the DB afterwards.
        var (tailHash, tailSeq) = ReadAuditTail(null);
        if (tailHash != null)
        {
            _lastAuditHash = tailHash;
            _lastAuditSeq = tailSeq;
        }
    }

    private void EnsureConsentRequestColumns()
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(CONSENT_REQUESTS);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        string[] requiredCols = {
            "itsm_ticket_id TEXT",
            "tenant_id TEXT",
            "requester_identifiers_json TEXT"
        };

        foreach (var colDef in requiredCols)
        {
            var colName = colDef.Split(' ')[0];
            if (!existingCols.Contains(colName))
            {
                try
                {
                    using var alterCmd = _connection.CreateCommand();
                    alterCmd.CommandText = $"ALTER TABLE CONSENT_REQUESTS ADD COLUMN {colDef};";
                    alterCmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
                {
                }
            }
        }
    }

    private void EnsureConsentColumns()
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(CONSENTS);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        string[] requiredCols = {
            "tenant_id TEXT NOT NULL DEFAULT 'legacy-single-tenant'"
        };

        foreach (var colDef in requiredCols)
        {
            var colName = colDef.Split(' ')[0];
            if (!existingCols.Contains(colName))
            {
                try
                {
                    using var alterCmd = _connection.CreateCommand();
                    alterCmd.CommandText = $"ALTER TABLE CONSENTS ADD COLUMN {colDef};";
                    alterCmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
                {
                }
            }
        }
    }

    private void EnsureTableColumns()
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(TABLES);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        string[] requiredCols = {
            "data_source_type INTEGER NOT NULL DEFAULT 0",
            "http_endpoint_json TEXT",
            "plugin_name TEXT",
            "description TEXT",
            "long_description TEXT",
            "doc_source TEXT"
        };

        foreach (var colDef in requiredCols)
        {
            var colName = colDef.Split(' ')[0];
            if (!existingCols.Contains(colName))
            {
                try
                {
                    using var alterCmd = _connection.CreateCommand();
                    alterCmd.CommandText = $"ALTER TABLE TABLES ADD COLUMN {colDef};";
                    alterCmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
                {
                }
            }
        }
    }

    private void EnsureTableColumnFields()
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(TABLE_COLUMNS);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        string[] requiredCols = {
            "description TEXT",
            "long_description TEXT",
            "doc_source TEXT",
            "meta_json TEXT"
        };

        foreach (var colDef in requiredCols)
        {
            var colName = colDef.Split(' ')[0];
            if (!existingCols.Contains(colName))
            {
                try
                {
                    using var alterCmd = _connection.CreateCommand();
                    alterCmd.CommandText = $"ALTER TABLE TABLE_COLUMNS ADD COLUMN {colDef};";
                    alterCmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
                {
                }
            }
        }
    }

    private void EnsureConsentRowFilterColumns()
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(CONSENT_ROW_FILTERS);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        string[] requiredCols = {
            "filter_type INTEGER NOT NULL DEFAULT 0",
            "dependent_table TEXT",
            "dependent_table_alias TEXT",
            "foreign_key_column TEXT",
            "primary_key_column TEXT",
            "subquery_predicate_json TEXT",
            "target_temporal_column TEXT",
            "dependent_valid_from_column TEXT",
            "dependent_valid_to_column TEXT",
            "target_table_alias TEXT",
            "additional_hops_json TEXT"
        };

        foreach (var colDef in requiredCols)
        {
            var colName = colDef.Split(' ')[0];
            if (!existingCols.Contains(colName))
            {
                try
                {
                    using var alterCmd = _connection.CreateCommand();
                    alterCmd.CommandText = $"ALTER TABLE CONSENT_ROW_FILTERS ADD COLUMN {colDef};";
                    alterCmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
                {
                }
            }
        }
    }

    private void SeedInitialCatalog()
    {
        // 10 domains with sample tables
        var domains = new[]
        {
            "finance", "hr", "sales", "inventory", "compliance",
            "marketing", "logistics", "crm", "procurement", "billing"
        };

        using var trans = _connection.BeginTransaction();
        try
        {
            // Seed sample DataOwners
            var ownerSid = "S-1-5-21-DATAOWNER-1";
            var ownerId = Guid.NewGuid().ToString();
            using (var cmd = _connection.CreateCommand())
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
            using (var cmd = _connection.CreateCommand())
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
            using (var cmd = _connection.CreateCommand())
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
            using (var cmd = _connection.CreateCommand())
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
            using (var cmd = _connection.CreateCommand())
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

            ownerId = ResolveOwnerId(_connection, trans, ownerSid, ownerId);
            salesOwnerId = ResolveOwnerId(_connection, trans, salesOwnerSid, salesOwnerId);
            approverId = ResolveOwnerId(_connection, trans, approverSid, approverId);
            approverAId = ResolveOwnerId(_connection, trans, approverASid, approverAId);
            approverBId = ResolveOwnerId(_connection, trans, approverBSid, approverBId);

            foreach (var domain in domains)
            {
                for (int t = 1; t <= 10; t++)
                {
                    var tableId = Guid.NewGuid().ToString();
                    var tableName = $"{domain}_table_{t}";
                    var schema = "dbo";

                    // Insert Table
                    using (var cmd = _connection.CreateCommand())
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

                    using (var idCmd = _connection.CreateCommand())
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
                        using var towCmd = _connection.CreateCommand();
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
                        using var towCmd = _connection.CreateCommand();
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
                        using var towCmd = _connection.CreateCommand();
                        towCmd.Transaction = trans;
                        towCmd.CommandText = @"INSERT OR IGNORE INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                                              VALUES (@id, @tid, @oid, 'PRIMARY');";
                        towCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                        towCmd.Parameters.AddWithValue("@tid", tableId);
                        towCmd.Parameters.AddWithValue("@oid", salesOwnerId);
                        towCmd.ExecuteNonQuery();
                    }

                    // Insert Policy Epoch
                    using (var cmd = _connection.CreateCommand())
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
                        using (var cmd = _connection.CreateCommand())
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

                        using (var colIdCmd = _connection.CreateCommand())
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
                            using var cmd = _connection.CreateCommand();
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
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT OR IGNORE INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active)
                                    VALUES (@id, 'SqlServer', 'finance', 'dbo', 'finance_items', 'Finance Items', 'NORMAL', 0, 1);";
                cmd.Parameters.AddWithValue("@id", childTableId);
                cmd.ExecuteNonQuery();
            }

            using (var idCmd = _connection.CreateCommand())
            {
                idCmd.Transaction = trans;
                idCmd.CommandText = "SELECT id FROM TABLES WHERE source_name = 'finance' AND schema_name = 'dbo' AND table_name = 'finance_items' LIMIT 1";
                var actualChildId = idCmd.ExecuteScalar()?.ToString();
                if (!string.IsNullOrEmpty(actualChildId))
                {
                    childTableId = actualChildId;
                }
            }

            using (var cmd = _connection.CreateCommand())
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
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT OR IGNORE INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive)
                                    VALUES (@id, @tid, @name, @type, @sens);";
                cmd.Parameters.AddWithValue("@id", colId);
                cmd.Parameters.AddWithValue("@tid", childTableId);
                cmd.Parameters.AddWithValue("@name", cName);
                cmd.Parameters.AddWithValue("@type", cType);
                cmd.Parameters.AddWithValue("@sens", cSens ? 1 : 0);
                cmd.ExecuteNonQuery();

                using (var colIdCmd = _connection.CreateCommand())
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
                    using var mcmd = _connection.CreateCommand();
                    mcmd.Transaction = trans;
                    mcmd.CommandText = @"INSERT OR IGNORE INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                                        VALUES (@id, @cid, 'REDACT', '', '[CONFIDENTIAL NOTE]', 'key-2026-q1');";
                    mcmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                    mcmd.Parameters.AddWithValue("@cid", colId);
                    mcmd.ExecuteNonQuery();
                }
            }

            // Relate finance_table_1 -> finance_items
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"INSERT OR IGNORE INTO TABLE_RELATIONS (id, parent_table_id, child_table_id, relation_name, join_key_parent, join_key_child, cardinality)
                                    SELECT @relId, id, @childId, 'items', 'id', 'parent_id', 'OneToMany'
                                    FROM TABLES
                                    WHERE schema_name = 'dbo' AND table_name = 'finance_table_1';";
                cmd.Parameters.AddWithValue("@relId", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@childId", childTableId);
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

    private void EnsureAuditLogColumns()
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(AUDIT_LOG_ENTRIES);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        if (!existingCols.Contains("tenant_id"))
        {
            try
            {
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE AUDIT_LOG_ENTRIES ADD COLUMN tenant_id TEXT NOT NULL DEFAULT 'legacy-single-tenant';";
                alterCmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
            }
        }

        // SEC H-17: gap-free sequence number (part of the v2 entry hash). Legacy rows keep NULL and are
        // verified with the v1 payload; their sequence is their ordinal position in rowid order.
        if (!existingCols.Contains("seq"))
        {
            try
            {
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE AUDIT_LOG_ENTRIES ADD COLUMN seq INTEGER;";
                alterCmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
            }
        }

        using (var seqIdxCmd = _connection.CreateCommand())
        {
            seqIdxCmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_audit_seq ON AUDIT_LOG_ENTRIES (seq);";
            seqIdxCmd.ExecuteNonQuery();
        }

        using (var idxCmd = _connection.CreateCommand())
        {
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_audit_tenant_id ON AUDIT_LOG_ENTRIES (tenant_id, occurred_at);";
            idxCmd.ExecuteNonQuery();
        }
    }

    private void DeduplicateDataOwnersBeforeIndex(SqliteConnection connection)
    {
        try
        {
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='DATA_OWNERS';";
            if (checkCmd.ExecuteScalar() == null) return;

            using var dupCmd = connection.CreateCommand();
            dupCmd.CommandText = "SELECT ad_sid FROM DATA_OWNERS WHERE ad_sid IS NOT NULL AND ad_sid != '' GROUP BY ad_sid HAVING COUNT(*) > 1;";
            var duplicateSids = new List<string>();
            using (var reader = dupCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    duplicateSids.Add(reader.GetString(0));
                }
            }

            if (duplicateSids.Count == 0) return;

            _logger?.LogWarning("Found {Count} duplicate ad_sid values in DATA_OWNERS; merging duplicates before applying unique constraint (B-05).", duplicateSids.Count);

            foreach (var sid in duplicateSids)
            {
                using var fetchCmd = connection.CreateCommand();
                fetchCmd.CommandText = "SELECT id, is_active FROM DATA_OWNERS WHERE ad_sid = @sid ORDER BY is_active DESC, rowid ASC;";
                fetchCmd.Parameters.AddWithValue("@sid", sid);
                var rows = new List<(string id, long active)>();
                using (var reader = fetchCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add((reader.GetString(0), reader.GetInt64(1)));
                    }
                }

                if (rows.Count <= 1) continue;

                var primaryId = rows[0].id;
                for (int i = 1; i < rows.Count; i++)
                {
                    var secondaryId = rows[i].id;
                    _logger?.LogInformation("Merging duplicate DATA_OWNERS id '{SecondaryId}' into primary id '{PrimaryId}' for ad_sid '{Sid}'.", secondaryId, primaryId, sid);

                    using var updateTableOwnersCmd = connection.CreateCommand();
                    updateTableOwnersCmd.CommandText = @"
                        UPDATE OR IGNORE TABLE_OWNERS SET data_owner_id = @primaryId WHERE data_owner_id = @secondaryId;
                        DELETE FROM TABLE_OWNERS WHERE data_owner_id = @secondaryId;
                    ";
                    updateTableOwnersCmd.Parameters.AddWithValue("@primaryId", primaryId);
                    updateTableOwnersCmd.Parameters.AddWithValue("@secondaryId", secondaryId);
                    updateTableOwnersCmd.ExecuteNonQuery();

                    using var updateDefaultsCmd = connection.CreateCommand();
                    updateDefaultsCmd.CommandText = @"
                        UPDATE OR IGNORE DOMAIN_DEFAULTS SET data_owner_id = @primaryId WHERE data_owner_id = @secondaryId;
                        DELETE FROM DOMAIN_DEFAULTS WHERE data_owner_id = @secondaryId;
                    ";
                    updateDefaultsCmd.Parameters.AddWithValue("@primaryId", primaryId);
                    updateDefaultsCmd.Parameters.AddWithValue("@secondaryId", secondaryId);
                    try { updateDefaultsCmd.ExecuteNonQuery(); } catch { }

                    using var deleteCmd = connection.CreateCommand();
                    deleteCmd.CommandText = "DELETE FROM DATA_OWNERS WHERE id = @secondaryId;";
                    deleteCmd.Parameters.AddWithValue("@secondaryId", secondaryId);
                    deleteCmd.ExecuteNonQuery();
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error while deduplicating DATA_OWNERS table before unique index creation.");
        }
    }
}
