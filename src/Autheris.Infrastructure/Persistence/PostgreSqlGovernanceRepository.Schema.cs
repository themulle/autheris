using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository
{
    private int _isInitialized;

    /// <summary>Session-level advisory lock that serialises the schema DDL of concurrently starting replicas ("AUTH" "SCHE").</summary>
    private const long SchemaAdvisoryLockKey = 0x41555448_53434845L;

    private static void ExecuteSchemaCommand(NpgsqlConnection conn, string sql)
    {
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void InitializeDatabase()
    {
        if (Interlocked.Exchange(ref _isInitialized, 1) == 1) return;

        // Review PG-7: DDL with a separate migration role when configured (the runtime role then needs no DDL rights).
        using NpgsqlDataSource? migrationDataSource = string.IsNullOrWhiteSpace(_migrationConnectionString)
            ? null
            : NpgsqlDataSource.Create(_migrationConnectionString);
        using var conn = (migrationDataSource ?? _dataSource).OpenConnection();
        DeduplicateDataOwnersBeforeIndex(conn);
        using var cmd = conn.CreateCommand();
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
                rowid BIGSERIAL PRIMARY KEY,
                id TEXT NOT NULL,
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
                tenant_id TEXT NOT NULL DEFAULT 'legacy-single-tenant',
                seq BIGINT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_audit_target_table ON AUDIT_LOG_ENTRIES (target_table, occurred_at);
            CREATE INDEX IF NOT EXISTS idx_audit_actor_sid ON AUDIT_LOG_ENTRIES (actor_sid, occurred_at);

            -- Review PG-1: second line of defence against forks / duplicate sequence numbers.
            CREATE UNIQUE INDEX IF NOT EXISTS idx_audit_seq ON AUDIT_LOG_ENTRIES (seq);

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

            CREATE UNIQUE INDEX IF NOT EXISTS ux_approval_steps_req_step
                ON APPROVAL_STEPS (consent_request_id, step_number);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_tables_natural
                ON TABLES (source_name, schema_name, table_name);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_table_columns_natural
                ON TABLE_COLUMNS (table_id, column_name);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_data_owners_sid
                ON DATA_OWNERS (ad_sid);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_table_owners_natural
                ON TABLE_OWNERS (table_id, data_owner_id, owner_role);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_policy_epochs_natural
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

            -- Review PG-4 (SEC N-2): a consent request is activated at most once.
            -- (The OpenMetadata sync marker is shared by many sync consents and is therefore excluded.)
            CREATE UNIQUE INDEX IF NOT EXISTS ux_consents_request
                ON CONSENTS (consent_request_id)
                WHERE consent_request_id IS NOT NULL AND consent_request_id <> '0e3d5c1a-7b2f-4c8e-9a61-5f0d2b7c4e19';

            -- Review PG-12: a ticket id identifies at most one consent request per tenant (webhooks look requests up by ticket).
            -- Existing duplicates must be cleaned up before this index can be created (see operations runbook).
            CREATE UNIQUE INDEX IF NOT EXISTS ux_consent_requests_ticket
                ON CONSENT_REQUESTS (COALESCE(tenant_id, ''), itsm_ticket_id)
                WHERE itsm_ticket_id IS NOT NULL;

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

        // Review PG-1: concurrent replica starts must not run the DDL at the same time ("tuple concurrently updated").
        ExecuteSchemaCommand(conn, "SELECT pg_advisory_lock(" + SchemaAdvisoryLockKey + ")");
        try
        {
            cmd.ExecuteNonQuery();

            // Review PG-2: append-only audit table (a table owner can still drop the trigger; the runtime role should only
            // hold INSERT/SELECT, the schema is applied with a separate migration role). One command per statement.
            ExecuteSchemaCommand(conn, @"CREATE OR REPLACE FUNCTION autheris_audit_append_only() RETURNS trigger AS $audit$
                BEGIN
                    RAISE EXCEPTION 'AUDIT_LOG_ENTRIES is append-only (%)', TG_OP USING ERRCODE = '42501';
                END;
                $audit$ LANGUAGE plpgsql");
            ExecuteSchemaCommand(conn, @"CREATE OR REPLACE TRIGGER trg_audit_append_only_row BEFORE UPDATE OR DELETE ON AUDIT_LOG_ENTRIES
                FOR EACH ROW EXECUTE FUNCTION autheris_audit_append_only()");
            ExecuteSchemaCommand(conn, @"CREATE OR REPLACE TRIGGER trg_audit_append_only_trunc BEFORE TRUNCATE ON AUDIT_LOG_ENTRIES
                FOR EACH STATEMENT EXECUTE FUNCTION autheris_audit_append_only()");
        }
        finally
        {
            ExecuteSchemaCommand(conn, "SELECT pg_advisory_unlock(" + SchemaAdvisoryLockKey + ")");
        }

        var (tailHash, tailSeq) = ReadAuditTail(null);
        if (tailHash != null)
        {
            _lastAuditHash = tailHash;
            _lastAuditSeq = tailSeq;
        }
    }

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

    private void DeduplicateDataOwnersBeforeIndex(NpgsqlConnection conn)
    {
        try
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_name = 'data_owners' LIMIT 1;";
            if (checkCmd.ExecuteScalar() == null) return;

            using var dupCmd = conn.CreateCommand();
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
                using var fetchCmd = conn.CreateCommand();
                fetchCmd.CommandText = "SELECT id, is_active FROM DATA_OWNERS WHERE ad_sid = @sid ORDER BY is_active DESC, id ASC;";
                fetchCmd.Parameters.AddWithValue("@sid", sid);
                var rows = new List<(string id, int active)>();
                using (var reader = fetchCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add((reader.GetString(0), Convert.ToInt32(reader.GetValue(1))));
                    }
                }

                if (rows.Count <= 1) continue;

                var primaryId = rows[0].id;
                for (int i = 1; i < rows.Count; i++)
                {
                    var secondaryId = rows[i].id;
                    _logger?.LogInformation("Merging duplicate DATA_OWNERS id '{SecondaryId}' into primary id '{PrimaryId}' for ad_sid '{Sid}'.", secondaryId, primaryId, sid);

                    using var updateTableOwnersCmd = conn.CreateCommand();
                    updateTableOwnersCmd.CommandText = @"
                        UPDATE TABLE_OWNERS SET data_owner_id = @primaryId WHERE data_owner_id = @secondaryId
                        AND NOT EXISTS (SELECT 1 FROM TABLE_OWNERS WHERE data_owner_id = @primaryId);
                        DELETE FROM TABLE_OWNERS WHERE data_owner_id = @secondaryId;
                    ";
                    updateTableOwnersCmd.Parameters.AddWithValue("@primaryId", primaryId);
                    updateTableOwnersCmd.Parameters.AddWithValue("@secondaryId", secondaryId);
                    updateTableOwnersCmd.ExecuteNonQuery();

                    using var deleteCmd = conn.CreateCommand();
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
