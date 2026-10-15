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
                tenant_id TEXT NOT NULL DEFAULT 'legacy-single-tenant',
                seq INTEGER
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_audit_seq ON AUDIT_LOG_ENTRIES (seq);
            CREATE INDEX IF NOT EXISTS idx_audit_tenant_id ON AUDIT_LOG_ENTRIES (tenant_id, occurred_at);
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

            -- WP-ARC-A-02: durable webhook outbox (all times are Unix milliseconds)
            CREATE TABLE IF NOT EXISTS WEBHOOK_OUTBOX (
                id TEXT PRIMARY KEY,
                source INTEGER NOT NULL,
                subscription_id TEXT NOT NULL,
                owner_tenant_id TEXT NULL,
                event_tenant_id TEXT NULL,
                event_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                event_source TEXT NOT NULL,
                event_time_ms INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                attempt INTEGER NOT NULL,
                max_attempts INTEGER NOT NULL,
                state INTEGER NOT NULL,
                next_attempt_ms INTEGER NOT NULL,
                lease_until_ms INTEGER NULL,
                lease_owner TEXT NULL,
                last_status_code INTEGER NULL,
                last_error_code TEXT NULL,
                created_ms INTEGER NOT NULL,
                updated_ms INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_webhook_outbox_dedupe ON WEBHOOK_OUTBOX (source, subscription_id, event_id);
            CREATE INDEX IF NOT EXISTS ix_webhook_outbox_due ON WEBHOOK_OUTBOX (state, next_attempt_ms);
            CREATE INDEX IF NOT EXISTS ix_webhook_outbox_lease ON WEBHOOK_OUTBOX (state, lease_until_ms);
            CREATE INDEX IF NOT EXISTS ix_webhook_outbox_owner ON WEBHOOK_OUTBOX (owner_tenant_id, state);

            -- WP-ARC-A-11: durable, fenced CDC watermarks
            CREATE TABLE IF NOT EXISTS autheris_cdc_watermarks (
                source TEXT NOT NULL,
                domain_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                watermark INTEGER NOT NULL,
                fencing_token INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL,
                PRIMARY KEY (source, domain_name, schema_name, table_name)
            );

            -- Issue #99 (I1): governed CDC stream (durable, encrypted, fenced append; see PLAN-CDC-99)

            CREATE TABLE IF NOT EXISTS autheris_cdc_streams (
                stream_id TEXT NOT NULL,
                source TEXT NOT NULL,
                domain_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                status INTEGER NOT NULL,
                activation_code TEXT NULL,
                head_offset INTEGER NOT NULL,
                tail_offset INTEGER NOT NULL,
                producer_fencing_token INTEGER NOT NULL,
                retention_fencing_token INTEGER NOT NULL,
                last_polled_ms INTEGER NULL,
                created_ms INTEGER NOT NULL,
                updated_ms INTEGER NOT NULL,
                PRIMARY KEY (stream_id)
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_cdc_streams_table ON autheris_cdc_streams (source, domain_name, schema_name, table_name);

            CREATE TABLE IF NOT EXISTS autheris_cdc_stream_events (
                stream_id TEXT NOT NULL,
                event_offset INTEGER NOT NULL,
                change_version INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                operation INTEGER NULL,
                tenant_scope INTEGER NOT NULL,
                tenant_id TEXT NULL,
                prior_tenant_id TEXT NULL,
                dek_id TEXT NOT NULL,
                payload BLOB NOT NULL,
                produced_ms INTEGER NOT NULL,
                PRIMARY KEY (stream_id, event_offset)
            );

            CREATE INDEX IF NOT EXISTS ix_cdc_stream_events_produced ON autheris_cdc_stream_events (stream_id, produced_ms);

            CREATE INDEX IF NOT EXISTS ix_cdc_stream_events_dek ON autheris_cdc_stream_events (stream_id, dek_id);

            CREATE TABLE IF NOT EXISTS autheris_cdc_row_images (
                stream_id TEXT NOT NULL,
                key_fingerprint BLOB NOT NULL,
                tenant_scope INTEGER NOT NULL,
                tenant_id TEXT NULL,
                dek_id TEXT NOT NULL,
                image BLOB NOT NULL,
                event_offset INTEGER NOT NULL,
                updated_ms INTEGER NOT NULL,
                PRIMARY KEY (stream_id, key_fingerprint)
            );

            CREATE TABLE IF NOT EXISTS autheris_cdc_stream_keys (
                stream_id TEXT NOT NULL,
                dek_id TEXT NOT NULL,
                purpose INTEGER NOT NULL,
                kek_id TEXT NOT NULL,
                wrapped_key BLOB NOT NULL,
                created_ms INTEGER NOT NULL,
                retired_ms INTEGER NULL,
                PRIMARY KEY (stream_id, dek_id)
            );

            CREATE INDEX IF NOT EXISTS ix_cdc_stream_keys_kek ON autheris_cdc_stream_keys (kek_id);

            -- WP-ARC-A-03: durable CDC webhook (CloudEvent) subscriptions
            CREATE TABLE IF NOT EXISTS CLOUDEVENT_SUBSCRIPTIONS (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                target_url TEXT NOT NULL,
                filter_table TEXT NOT NULL,
                filter_operations TEXT NULL,
                hmac_secret_protected TEXT NULL,
                is_enabled INTEGER NOT NULL,
                custom_headers_json TEXT NULL,
                created_by_sid TEXT NULL,
                created_by_groups_json TEXT NULL,
                created_by_roles_json TEXT NULL,
                created_ms INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_cloudevent_subs_tenant ON CLOUDEVENT_SUBSCRIPTIONS (tenant_id, is_enabled);

            -- Issue #94: declared primary keys and indexes (composite, ordered, unique flag); columns referenced by TABLE_COLUMNS.id
            CREATE TABLE IF NOT EXISTS TABLE_PRIMARY_KEY_COLUMNS (
                table_id TEXT NOT NULL,
                key_ordinal INTEGER NOT NULL CHECK (key_ordinal >= 1),
                table_column_id TEXT NOT NULL,
                PRIMARY KEY (table_id, key_ordinal)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_PK_COLUMNS_COLUMN ON TABLE_PRIMARY_KEY_COLUMNS (table_id, table_column_id);

            CREATE TABLE IF NOT EXISTS TABLE_INDEXES (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                index_name TEXT NOT NULL,
                is_unique INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_INDEXES_NAME ON TABLE_INDEXES (table_id, index_name);

            CREATE TABLE IF NOT EXISTS TABLE_INDEX_COLUMNS (
                index_id TEXT NOT NULL,
                column_ordinal INTEGER NOT NULL CHECK (column_ordinal >= 1),
                table_column_id TEXT NOT NULL,
                PRIMARY KEY (index_id, column_ordinal)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_INDEX_COLUMNS_COLUMN ON TABLE_INDEX_COLUMNS (index_id, table_column_id);
        ";
        cmd.ExecuteNonQuery();

    }

}
