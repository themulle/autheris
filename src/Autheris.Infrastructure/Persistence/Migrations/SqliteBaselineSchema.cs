using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>
/// Baseline governance schema for SQLite (migration 0001). Moved verbatim from the former repository constructor DDL;
/// every statement is idempotent so the baseline runs unchanged on fresh and on pre-existing databases.
/// </summary>
internal sealed class SqliteBaselineSchema
{
    private readonly SqliteConnection _connection;
    private readonly ILogger? _logger;

    internal SqliteBaselineSchema(SqliteConnection connection, ILogger? logger)
    {
        _connection = connection;
        _logger = logger;
    }

    internal void Apply()
    {
        DeduplicateDataOwnersBeforeIndex(_connection);
        EnsureAccessProfilesTableMigration(_connection);

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
                access_level INTEGER NOT NULL,
                applicable_scopes TEXT NOT NULL DEFAULT ''
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

            -- AU-05: Dead-letter queue for failed audit batches
            CREATE TABLE IF NOT EXISTS AUDIT_DEAD_LETTER (
                id TEXT PRIMARY KEY,
                batch_json TEXT NOT NULL,
                error_message TEXT NOT NULL,
                failed_at TEXT NOT NULL,
                tenant_id TEXT NOT NULL
            );

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

            CREATE TABLE IF NOT EXISTS VIRTUAL_FILTER_ACCESS_PROFILES (
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

            CREATE TABLE IF NOT EXISTS ACCESS_PROFILES (
                profile_id TEXT NOT NULL,
                tenant_id TEXT NOT NULL,
                name TEXT NOT NULL,
                masking_mode TEXT NOT NULL DEFAULT 'Default',
                target_tables TEXT NOT NULL DEFAULT '[""*.*""]',
                row_filter_predicate TEXT,
                justification TEXT,
                created_by TEXT NOT NULL,
                created_at TEXT NOT NULL,
                valid_to TEXT,
                PRIMARY KEY (tenant_id, profile_id)
            );

            CREATE TABLE IF NOT EXISTS ACCESS_PROFILE_ASSIGNMENTS (
                profile_id TEXT NOT NULL,
                tenant_id TEXT NOT NULL,
                subject TEXT NOT NULL,
                subject_type TEXT NOT NULL DEFAULT 'User',
                assigned_at TEXT NOT NULL,
                expires_at TEXT,
                PRIMARY KEY (tenant_id, profile_id, subject),
                FOREIGN KEY (tenant_id, profile_id) REFERENCES ACCESS_PROFILES (tenant_id, profile_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_ACCESS_PROFILE_ASSIGNMENTS_SUBJECT 
                ON ACCESS_PROFILE_ASSIGNMENTS(tenant_id, subject);

            CREATE TABLE IF NOT EXISTS VIRTUAL_FILTER_GENERATION (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                generation BIGINT NOT NULL
            );

            INSERT INTO VIRTUAL_FILTER_GENERATION (id, generation) VALUES (1, 0) ON CONFLICT (id) DO NOTHING;

            -- CR-ADG-31: tenant registry (grouped by NFKC + upper-case key; collided = 1 denies every spelling of the group)
            CREATE TABLE IF NOT EXISTS TENANT_REGISTRY (
                normalized_key TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                collided INTEGER NOT NULL DEFAULT 0,
                first_seen_at TEXT NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();

        EnsureConsentRowFilterColumns();
        EnsureConsentColumnRuleColumns();
        EnsureTableColumns();
        EnsureTableColumnFields();
        EnsureConsentRequestColumns();
        EnsureConsentColumns();
        EnsureAuditLogColumns();
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

    private void EnsureConsentColumnRuleColumns()
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(CONSENT_COLUMN_RULES);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        if (!existingCols.Contains("applicable_scopes"))
        {
            try
            {
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE CONSENT_COLUMN_RULES ADD COLUMN applicable_scopes TEXT NOT NULL DEFAULT '';";
                alterCmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
            }
        }
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

    private static void EnsureAccessProfilesTableMigration(SqliteConnection conn)
    {
        try
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(ACCESS_PROFILES);";
            using var reader = pragma.ExecuteReader();
            bool hasProfileId = false;
            bool hasId = false;
            while (reader.Read())
            {
                var col = reader.GetString(1);
                if (string.Equals(col, "profile_id", StringComparison.OrdinalIgnoreCase)) hasProfileId = true;
                if (string.Equals(col, "id", StringComparison.OrdinalIgnoreCase)) hasId = true;
            }
            reader.Close();
            if (hasId && !hasProfileId)
            {
                using var migrate = conn.CreateCommand();
                migrate.CommandText = "ALTER TABLE ACCESS_PROFILES RENAME TO VIRTUAL_FILTER_ACCESS_PROFILES;";
                migrate.ExecuteNonQuery();
            }
        }
        catch
        {
            // Table might not exist yet; ignore
        }
    }
}
