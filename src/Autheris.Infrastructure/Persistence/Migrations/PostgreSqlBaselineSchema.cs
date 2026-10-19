using Microsoft.Extensions.Logging;
using Npgsql;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>Baseline governance schema for PostgreSQL (migration 0001), moved verbatim from the former repository constructor DDL. All statements are idempotent.</summary>
internal static class PostgreSqlBaselineSchema
{
    private static void ExecuteSchemaCommand(NpgsqlConnection conn, string sql)
    {
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static void Apply(NpgsqlConnection conn, ILogger? logger)
    {
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
                plugin_name TEXT,
                required_filter_columns_json TEXT
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
                grantee_sid_norm TEXT,
                role_id TEXT,
                role_name TEXT,
                valid_from TEXT NOT NULL,
                valid_to TEXT NOT NULL,
                is_revoked INTEGER NOT NULL,
                revoked_by_sid TEXT,
                revoked_at TEXT,
                revoke_reason TEXT,
                tenant_id TEXT NOT NULL DEFAULT 'legacy-single-tenant',
                renewal_count INTEGER NOT NULL DEFAULT 0,
                description TEXT CHECK (description IS NULL OR char_length(description) <= 2000)
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
                additional_hops_json TEXT,
                key_set_strategy INTEGER NULL,
                key_set_duration_seconds INTEGER NULL,
                key_set_max_keys INTEGER NULL
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

            -- AU-05: Dead-letter queue for failed audit batches
            CREATE TABLE IF NOT EXISTS AUDIT_DEAD_LETTER (
                id TEXT PRIMARY KEY,
                batch_json TEXT NOT NULL,
                error_message TEXT NOT NULL,
                failed_at TEXT NOT NULL,
                tenant_id TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AUDIT_PURGE_CHECKPOINTS (
                id TEXT PRIMARY KEY,
                first_surviving_seq BIGINT NOT NULL,
                surviving_prev_hash TEXT NOT NULL,
                purged_count BIGINT NOT NULL,
                purged_through_utc TEXT NOT NULL,
                created_at TEXT NOT NULL,
                signature TEXT NOT NULL,
                external_signature TEXT
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
                requester_identifiers_json TEXT,
                context_json TEXT
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

            -- WP-SC-01: indexes of the consent query. grantee_sid_norm is the upper-invariant grantee SID written by the repository
            -- (case-insensitive subject match); rows without it (NULL) are always candidates, the C# filter stays authoritative.
            CREATE INDEX IF NOT EXISTS ix_consents_tenant_table ON CONSENTS (tenant_id, table_id, is_revoked);
            CREATE INDEX IF NOT EXISTS ix_consents_tenant_grantee ON CONSENTS (tenant_id, grantee_sid_norm, is_revoked);
            CREATE INDEX IF NOT EXISTS ix_consents_tenant_role ON CONSENTS (tenant_id, role_name);
            CREATE INDEX IF NOT EXISTS ix_consent_column_rules_consent ON CONSENT_COLUMN_RULES (consent_id);
            CREATE INDEX IF NOT EXISTS ix_consent_row_filters_consent ON CONSENT_ROW_FILTERS (consent_id);
            CREATE INDEX IF NOT EXISTS ix_column_masking_rules_column ON COLUMN_MASKING_RULES (table_column_id);
            CREATE INDEX IF NOT EXISTS ix_role_members_member ON ROLE_MEMBERS (member_sid);
            CREATE INDEX IF NOT EXISTS ix_table_owners_owner ON TABLE_OWNERS (data_owner_id);
            CREATE INDEX IF NOT EXISTS ix_tables_natural_ci
                ON TABLES (LOWER(source_name), LOWER(schema_name), LOWER(table_name));

            CREATE TABLE IF NOT EXISTS TABLE_RELATIONS (
                id TEXT PRIMARY KEY,
                parent_table_id TEXT NOT NULL,
                child_table_id TEXT NOT NULL,
                relation_name TEXT NOT NULL,
                cardinality TEXT NOT NULL
            );

            -- Issue #151: composite-key relations. One row per ordered key pair; column names are validated on write.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_table_relations_natural
                ON TABLE_RELATIONS (parent_table_id, child_table_id, relation_name);
            CREATE TABLE IF NOT EXISTS TABLE_RELATION_KEYS (
                relation_id TEXT NOT NULL,
                key_ordinal INTEGER NOT NULL CHECK (key_ordinal >= 1),
                parent_column TEXT NOT NULL,
                child_column TEXT NOT NULL,
                PRIMARY KEY (relation_id, key_ordinal)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_table_relation_keys_parent ON TABLE_RELATION_KEYS (relation_id, parent_column);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_table_relation_keys_child ON TABLE_RELATION_KEYS (relation_id, child_column);

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

            CREATE TABLE IF NOT EXISTS access_profiles (
                profile_id VARCHAR(128) NOT NULL,
                tenant_id VARCHAR(64) NOT NULL,
                name VARCHAR(256) NOT NULL,
                masking_mode VARCHAR(32) NOT NULL DEFAULT 'Default',
                target_tables TEXT NOT NULL DEFAULT '[""*.*""]',
                row_filter_predicate TEXT NULL,
                justification TEXT NULL,
                created_by VARCHAR(256) NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                valid_to TIMESTAMPTZ NULL,
                CONSTRAINT pk_access_profiles PRIMARY KEY (tenant_id, profile_id)
            );

            CREATE TABLE IF NOT EXISTS access_profile_assignments (
                profile_id VARCHAR(128) NOT NULL,
                tenant_id VARCHAR(64) NOT NULL,
                subject VARCHAR(256) NOT NULL,
                subject_type VARCHAR(32) NOT NULL DEFAULT 'User',
                assigned_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                expires_at TIMESTAMPTZ NULL,
                subject_norm VARCHAR(256) NULL,
                created_by VARCHAR(256) NULL,
                approved_by VARCHAR(256) NULL,
                approved_at TIMESTAMPTZ NULL,
                justification TEXT NULL,
                kind VARCHAR(32) NOT NULL DEFAULT 'Standard',
                renewal_count INTEGER NOT NULL DEFAULT 0,
                CONSTRAINT pk_access_profile_assignments PRIMARY KEY (tenant_id, profile_id, subject_type, subject),
                CONSTRAINT fk_profile_assignments_profile FOREIGN KEY (tenant_id, profile_id) 
                    REFERENCES access_profiles (tenant_id, profile_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_access_profile_assignments_subject 
                ON access_profile_assignments(tenant_id, subject_type, subject_norm);

            CREATE TABLE IF NOT EXISTS VIRTUAL_FILTER_GENERATION (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                generation BIGINT NOT NULL
            );

            INSERT INTO VIRTUAL_FILTER_GENERATION (id, generation) VALUES (1, 0) ON CONFLICT (id) DO NOTHING;

            -- Issue #129: caller attribute registry (four-eyes, draft beside the active row), service sets and one generation counter
            CREATE TABLE IF NOT EXISTS CALLER_ATTRIBUTES (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                name TEXT NOT NULL,
                definition_json TEXT NOT NULL,
                definition_hash TEXT NOT NULL,
                status INTEGER NOT NULL,
                created_by TEXT NOT NULL,
                approved_by TEXT,
                approved_at TEXT,
                draft_definition_json TEXT,
                draft_definition_hash TEXT,
                draft_created_by TEXT,
                pending_deletion INTEGER NOT NULL DEFAULT 0,
                deletion_requested_by TEXT,
                updated_by TEXT,
                updated_at TEXT NOT NULL,
                UNIQUE (tenant_id, name)
            );

            CREATE TABLE IF NOT EXISTS CALLER_ATTRIBUTE_SERVICE_SETS (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                application_id TEXT NOT NULL,
                attribute_name TEXT NOT NULL,
                values_json TEXT NOT NULL,
                values_hash TEXT NOT NULL,
                status INTEGER NOT NULL,
                created_by TEXT NOT NULL,
                approved_by TEXT,
                approved_at TEXT,
                draft_values_json TEXT,
                draft_created_by TEXT,
                updated_at TEXT NOT NULL,
                UNIQUE (tenant_id, application_id, attribute_name)
            );

            CREATE TABLE IF NOT EXISTS CALLER_ATTRIBUTE_GENERATION (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                generation BIGINT NOT NULL
            );

            INSERT INTO CALLER_ATTRIBUTE_GENERATION (id, generation) VALUES (1, 0) ON CONFLICT (id) DO NOTHING;

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
                event_time_ms BIGINT NOT NULL,
                payload_json TEXT NOT NULL,
                attempt INTEGER NOT NULL,
                max_attempts INTEGER NOT NULL,
                state INTEGER NOT NULL,
                next_attempt_ms BIGINT NOT NULL,
                lease_until_ms BIGINT NULL,
                lease_owner TEXT NULL,
                last_status_code INTEGER NULL,
                last_error_code TEXT NULL,
                created_ms BIGINT NOT NULL,
                updated_ms BIGINT NOT NULL
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
                watermark BIGINT NOT NULL,
                fencing_token BIGINT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL,
                PRIMARY KEY (source, domain_name, schema_name, table_name)
            );

            -- Issue #99 (I1): governed CDC stream (durable, encrypted, fenced append; see PLAN-CDC-99)

            CREATE TABLE IF NOT EXISTS autheris_cdc_streams (
                stream_id VARCHAR(32) NOT NULL,
                source VARCHAR(32) NOT NULL,
                domain_name VARCHAR(128) NOT NULL,
                schema_name VARCHAR(128) NOT NULL,
                table_name VARCHAR(128) NOT NULL,
                status INTEGER NOT NULL,
                activation_code VARCHAR(64) NULL,
                head_offset BIGINT NOT NULL,
                tail_offset BIGINT NOT NULL,
                producer_fencing_token BIGINT NOT NULL,
                retention_fencing_token BIGINT NOT NULL,
                last_polled_ms BIGINT NULL,
                created_ms BIGINT NOT NULL,
                updated_ms BIGINT NOT NULL,
                PRIMARY KEY (stream_id)
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_cdc_streams_table ON autheris_cdc_streams (source, domain_name, schema_name, table_name);

            CREATE TABLE IF NOT EXISTS autheris_cdc_stream_events (
                stream_id VARCHAR(32) NOT NULL,
                event_offset BIGINT NOT NULL,
                change_version BIGINT NOT NULL,
                kind INTEGER NOT NULL,
                operation INTEGER NULL,
                tenant_scope INTEGER NOT NULL,
                tenant_id VARCHAR(128) NULL,
                prior_tenant_id VARCHAR(128) NULL,
                dek_id VARCHAR(32) NOT NULL,
                payload BYTEA NOT NULL,
                produced_ms BIGINT NOT NULL,
                PRIMARY KEY (stream_id, event_offset)
            );

            CREATE INDEX IF NOT EXISTS ix_cdc_stream_events_produced ON autheris_cdc_stream_events (stream_id, produced_ms);

            CREATE INDEX IF NOT EXISTS ix_cdc_stream_events_dek ON autheris_cdc_stream_events (stream_id, dek_id);

            CREATE TABLE IF NOT EXISTS autheris_cdc_row_images (
                stream_id VARCHAR(32) NOT NULL,
                key_fingerprint BYTEA NOT NULL,
                tenant_scope INTEGER NOT NULL,
                tenant_id VARCHAR(128) NULL,
                dek_id VARCHAR(32) NOT NULL,
                image BYTEA NOT NULL,
                event_offset BIGINT NOT NULL,
                updated_ms BIGINT NOT NULL,
                PRIMARY KEY (stream_id, key_fingerprint)
            );

            CREATE TABLE IF NOT EXISTS autheris_cdc_stream_keys (
                stream_id VARCHAR(32) NOT NULL,
                dek_id VARCHAR(32) NOT NULL,
                purpose INTEGER NOT NULL,
                kek_id VARCHAR(64) NOT NULL,
                wrapped_key BYTEA NOT NULL,
                created_ms BIGINT NOT NULL,
                retired_ms BIGINT NULL,
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
                is_enabled BOOLEAN NOT NULL,
                custom_headers_json TEXT NULL,
                created_by_sid TEXT NULL,
                created_by_groups_json TEXT NULL,
                created_by_roles_json TEXT NULL,
                created_ms BIGINT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_cloudevent_subs_tenant ON CLOUDEVENT_SUBSCRIPTIONS (tenant_id, is_enabled);

            -- Issue #94: declared primary keys and indexes (composite, ordered, unique flag); columns referenced by TABLE_COLUMNS.id
            CREATE TABLE IF NOT EXISTS TABLE_PRIMARY_KEY_COLUMNS (
                table_id TEXT NOT NULL,
                key_ordinal INTEGER NOT NULL CHECK (key_ordinal >= 1),
                table_column_id TEXT NOT NULL,
                PRIMARY KEY (table_id, key_ordinal)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_table_pk_columns_column ON TABLE_PRIMARY_KEY_COLUMNS (table_id, table_column_id);

            CREATE TABLE IF NOT EXISTS TABLE_INDEXES (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                index_name TEXT NOT NULL,
                is_unique BOOLEAN NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_table_indexes_name ON TABLE_INDEXES (table_id, index_name);

            CREATE TABLE IF NOT EXISTS TABLE_INDEX_COLUMNS (
                index_id TEXT NOT NULL,
                column_ordinal INTEGER NOT NULL CHECK (column_ordinal >= 1),
                table_column_id TEXT NOT NULL,
                PRIMARY KEY (index_id, column_ordinal)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_table_index_columns_column ON TABLE_INDEX_COLUMNS (index_id, table_column_id);

            -- Issue #107: object-level row filter evaluation strategy (key-set cache); separate from TABLES because ingestion upserts rewrite TABLES
            CREATE TABLE IF NOT EXISTS TABLE_ROW_FILTER_STRATEGIES (
                table_id TEXT PRIMARY KEY,
                strategy INTEGER NULL,
                duration_seconds INTEGER NULL,
                max_keys INTEGER NULL,
                updated_by TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            -- Issue #111: dataset catalog tags (admin / dbt source) and visibility tier; separate from TABLES because ingestion upserts rewrite TABLES.
            -- No foreign keys and no CHECK constraints (as TABLE_ROW_FILTER_STRATEGIES): validity is enforced on write and re-validated on read.
            CREATE TABLE IF NOT EXISTS TABLE_TAGS (
                table_id TEXT NOT NULL,
                tag TEXT NOT NULL,
                source TEXT NOT NULL,
                created_by TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (table_id, tag)
            );
            CREATE TABLE IF NOT EXISTS TABLE_CATALOG_SETTINGS (
                table_id TEXT PRIMARY KEY,
                visibility INTEGER NOT NULL,
                updated_by TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            -- Issue #112: one validated semantic (dbt) manifest per tenant (latest version only); a save replaces the row atomically.
            -- No foreign key (tenant registry rows are optional in single-tenant installs). content_hash is the SHA-256 hex of manifest_json.
            CREATE TABLE IF NOT EXISTS SEMANTIC_MANIFESTS (
                tenant_id TEXT NOT NULL PRIMARY KEY,
                manifest_version TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                format_version INTEGER NOT NULL,
                models_count INTEGER NOT NULL,
                metrics_count INTEGER NOT NULL,
                manifest_json TEXT NOT NULL,
                imported_by TEXT NOT NULL,
                imported_at TEXT NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();

        // Review PG-2: append-only audit table (a table owner can still drop the trigger; the runtime role should only
        // hold INSERT/SELECT, the schema is applied with a separate migration role). One command per statement.
        ExecuteSchemaCommand(conn, @"CREATE OR REPLACE FUNCTION autheris_audit_append_only() RETURNS trigger AS $audit$
            BEGIN
                IF TG_OP = 'DELETE' AND current_setting('autheris.audit_purge_authorized', true) = 'true' THEN
                    RETURN OLD;
                END IF;
                RAISE EXCEPTION 'AUDIT_LOG_ENTRIES is append-only (%)', TG_OP USING ERRCODE = '42501';
            END;
            $audit$ LANGUAGE plpgsql");
        ExecuteSchemaCommand(conn, @"CREATE OR REPLACE TRIGGER trg_audit_append_only_row BEFORE UPDATE OR DELETE ON AUDIT_LOG_ENTRIES
            FOR EACH ROW EXECUTE FUNCTION autheris_audit_append_only()");
        ExecuteSchemaCommand(conn, @"CREATE OR REPLACE TRIGGER trg_audit_append_only_trunc BEFORE TRUNCATE ON AUDIT_LOG_ENTRIES
            FOR EACH STATEMENT EXECUTE FUNCTION autheris_audit_append_only()");
    }

}
