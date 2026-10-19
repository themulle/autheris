using Microsoft.Data.SqlClient;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>Baseline governance schema for SQL Server (migration 0001), moved verbatim from the former repository constructor DDL. All statements are idempotent.</summary>
internal static class SqlServerBaselineSchema
{
    private static void ExecuteSchemaCommand(SqlConnection conn, string sql)
    {
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static void Apply(SqlConnection conn)
    {
            // Column sizing: NVARCHAR(128) ids, NVARCHAR(256) natural-key / lookup columns, NVARCHAR(64) ISO-8601 timestamps
            // (stored as text like the other providers), NVARCHAR(MAX) for free text and JSON. Index keys stay below 1700 bytes.
            ExecuteSchemaCommand(conn, @"
IF OBJECT_ID(N'dbo.TABLES', N'U') IS NULL
CREATE TABLE dbo.TABLES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    source_type NVARCHAR(128) NOT NULL,
    source_name NVARCHAR(256) NOT NULL,
    schema_name NVARCHAR(256) NOT NULL,
    table_name NVARCHAR(256) NOT NULL,
    display_name NVARCHAR(512) NOT NULL,
    description NVARCHAR(MAX) NULL,
    long_description NVARCHAR(MAX) NULL,
    doc_source NVARCHAR(MAX) NULL,
    sensitivity NVARCHAR(64) NOT NULL,
    requires_four_eyes INT NOT NULL,
    is_active INT NOT NULL,
    data_source_type INT NOT NULL DEFAULT 0,
    http_endpoint_json NVARCHAR(MAX) NULL,
    plugin_name NVARCHAR(256) NULL,
    required_filter_columns_json NVARCHAR(MAX) NULL,
    source_name_lc AS LOWER(source_name) PERSISTED,
    schema_name_lc AS LOWER(schema_name) PERSISTED,
    table_name_lc AS LOWER(table_name) PERSISTED
);

IF OBJECT_ID(N'dbo.TABLE_COLUMNS', N'U') IS NULL
CREATE TABLE dbo.TABLE_COLUMNS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    table_id NVARCHAR(128) NOT NULL,
    column_name NVARCHAR(256) NOT NULL,
    data_type NVARCHAR(256) NOT NULL,
    is_sensitive INT NOT NULL,
    description NVARCHAR(MAX) NULL,
    long_description NVARCHAR(MAX) NULL,
    doc_source NVARCHAR(MAX) NULL,
    meta_json NVARCHAR(MAX) NULL
);

IF OBJECT_ID(N'dbo.COLUMN_MASKING_RULES', N'U') IS NULL
CREATE TABLE dbo.COLUMN_MASKING_RULES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    table_column_id NVARCHAR(128) NOT NULL,
    rule_type NVARCHAR(64) NOT NULL,
    pattern_or_format NVARCHAR(MAX) NULL,
    replacement NVARCHAR(MAX) NULL,
    hmac_key_id NVARCHAR(256) NULL
);

IF OBJECT_ID(N'dbo.DATA_OWNERS', N'U') IS NULL
CREATE TABLE dbo.DATA_OWNERS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    ad_sid NVARCHAR(256) NOT NULL,
    ad_account NVARCHAR(256) NOT NULL,
    display_name NVARCHAR(512) NOT NULL,
    email NVARCHAR(512) NOT NULL,
    is_active INT NOT NULL
);

IF OBJECT_ID(N'dbo.TABLE_OWNERS', N'U') IS NULL
CREATE TABLE dbo.TABLE_OWNERS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    table_id NVARCHAR(128) NOT NULL,
    data_owner_id NVARCHAR(128) NOT NULL,
    owner_role NVARCHAR(64) NOT NULL
);

IF OBJECT_ID(N'dbo.DATA_OWNER_DELEGATIONS', N'U') IS NULL
CREATE TABLE dbo.DATA_OWNER_DELEGATIONS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    data_owner_id NVARCHAR(128) NOT NULL,
    delegate_sid NVARCHAR(256) NOT NULL,
    valid_from NVARCHAR(64) NOT NULL,
    valid_to NVARCHAR(64) NOT NULL,
    reason NVARCHAR(MAX) NOT NULL
);

IF OBJECT_ID(N'dbo.ROLES', N'U') IS NULL
CREATE TABLE dbo.ROLES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    role_name NVARCHAR(256) NOT NULL UNIQUE,
    description NVARCHAR(MAX) NOT NULL
);

IF OBJECT_ID(N'dbo.ROLE_MEMBERS', N'U') IS NULL
CREATE TABLE dbo.ROLE_MEMBERS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    role_id NVARCHAR(128) NOT NULL,
    member_type NVARCHAR(64) NOT NULL,
    member_sid NVARCHAR(256) NOT NULL
);

IF OBJECT_ID(N'dbo.CONSENTS', N'U') IS NULL
CREATE TABLE dbo.CONSENTS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    table_id NVARCHAR(128) NOT NULL,
    consent_request_id NVARCHAR(128) NULL,
    effect NVARCHAR(64) NOT NULL,
    grantee_type NVARCHAR(64) NOT NULL,
    grantee_sid NVARCHAR(256) NULL,
    grantee_sid_norm NVARCHAR(256) NULL,
    role_id NVARCHAR(128) NULL,
    role_name NVARCHAR(256) NULL,
    valid_from NVARCHAR(64) NOT NULL,
    valid_to NVARCHAR(64) NOT NULL,
    is_revoked INT NOT NULL,
    revoked_by_sid NVARCHAR(256) NULL,
    revoked_at NVARCHAR(64) NULL,
    revoke_reason NVARCHAR(MAX) NULL,
    tenant_id NVARCHAR(128) NOT NULL DEFAULT 'legacy-single-tenant',
    renewal_count INT NOT NULL CONSTRAINT DF_CONSENTS_renewals DEFAULT 0,
    description NVARCHAR(2000) NULL
);

IF OBJECT_ID(N'dbo.CONSENT_COLUMN_RULES', N'U') IS NULL
CREATE TABLE dbo.CONSENT_COLUMN_RULES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    consent_id NVARCHAR(128) NOT NULL,
    table_column_id NVARCHAR(128) NOT NULL,
    column_name NVARCHAR(256) NOT NULL,
    access_level INT NOT NULL,
    applicable_scopes NVARCHAR(MAX) NOT NULL DEFAULT ''
);

IF OBJECT_ID(N'dbo.CONSENT_ROW_FILTERS', N'U') IS NULL
CREATE TABLE dbo.CONSENT_ROW_FILTERS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    consent_id NVARCHAR(128) NOT NULL,
    filter_group INT NOT NULL,
    table_column_id NVARCHAR(128) NOT NULL,
    column_name NVARCHAR(256) NOT NULL,
    operator NVARCHAR(64) NOT NULL,
    value_type NVARCHAR(64) NOT NULL,
    value_json NVARCHAR(MAX) NOT NULL,
    value_source NVARCHAR(64) NOT NULL,
    user_attribute NVARCHAR(256) NULL,
    filter_type INT NOT NULL DEFAULT 0,
    dependent_table NVARCHAR(512) NULL,
    dependent_table_alias NVARCHAR(256) NULL,
    foreign_key_column NVARCHAR(256) NULL,
    primary_key_column NVARCHAR(256) NULL,
    subquery_predicate_json NVARCHAR(MAX) NULL,
    target_temporal_column NVARCHAR(256) NULL,
    dependent_valid_from_column NVARCHAR(256) NULL,
    dependent_valid_to_column NVARCHAR(256) NULL,
    target_table_alias NVARCHAR(256) NULL,
    additional_hops_json NVARCHAR(MAX) NULL,
    key_set_strategy INT NULL,
    key_set_duration_seconds INT NULL,
    key_set_max_keys INT NULL
);

IF OBJECT_ID(N'dbo.POLICY_EPOCHS', N'U') IS NULL
CREATE TABLE dbo.POLICY_EPOCHS (
    table_id NVARCHAR(128) NOT NULL PRIMARY KEY,
    domain NVARCHAR(256) NOT NULL,
    schema_name NVARCHAR(256) NOT NULL,
    table_name NVARCHAR(256) NOT NULL,
    epoch INT NOT NULL,
    updated_at NVARCHAR(64) NOT NULL
);

-- rowid mirrors the SQLite rowid / PostgreSQL BIGSERIAL column: it defines the physical append order of the audit chain.
IF OBJECT_ID(N'dbo.AUDIT_LOG_ENTRIES', N'U') IS NULL
CREATE TABLE dbo.AUDIT_LOG_ENTRIES (
    rowid BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    id NVARCHAR(128) NOT NULL,
    occurred_at NVARCHAR(64) NOT NULL,
    event_type NVARCHAR(128) NOT NULL,
    actor_sid NVARCHAR(256) NOT NULL,
    target_table NVARCHAR(512) NOT NULL,
    target_column NVARCHAR(256) NULL,
    decision NVARCHAR(64) NOT NULL,
    trace_id NVARCHAR(128) NOT NULL,
    details_json NVARCHAR(MAX) NOT NULL,
    prev_hash NVARCHAR(128) NOT NULL,
    entry_hash NVARCHAR(128) NOT NULL,
    tenant_id NVARCHAR(128) NOT NULL DEFAULT 'legacy-single-tenant',
    seq BIGINT NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_audit_target_table' AND object_id = OBJECT_ID(N'dbo.AUDIT_LOG_ENTRIES'))
    CREATE INDEX idx_audit_target_table ON dbo.AUDIT_LOG_ENTRIES (target_table, occurred_at);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_audit_actor_sid' AND object_id = OBJECT_ID(N'dbo.AUDIT_LOG_ENTRIES'))
    CREATE INDEX idx_audit_actor_sid ON dbo.AUDIT_LOG_ENTRIES (actor_sid, occurred_at);

-- Second line of defence against forks / duplicate sequence numbers.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_audit_seq' AND object_id = OBJECT_ID(N'dbo.AUDIT_LOG_ENTRIES'))
    CREATE UNIQUE INDEX idx_audit_seq ON dbo.AUDIT_LOG_ENTRIES (seq);

-- AU-05: Dead-letter queue for failed audit batches
IF OBJECT_ID(N'dbo.AUDIT_DEAD_LETTER', N'U') IS NULL
CREATE TABLE dbo.AUDIT_DEAD_LETTER (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    batch_json NVARCHAR(MAX) NOT NULL,
    error_message NVARCHAR(MAX) NOT NULL,
    failed_at NVARCHAR(64) NOT NULL,
    tenant_id NVARCHAR(128) NOT NULL
);

IF OBJECT_ID(N'dbo.AUDIT_PURGE_CHECKPOINTS', N'U') IS NULL
CREATE TABLE dbo.AUDIT_PURGE_CHECKPOINTS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    first_surviving_seq BIGINT NOT NULL,
    surviving_prev_hash NVARCHAR(128) NOT NULL,
    purged_count BIGINT NOT NULL,
    purged_through_utc NVARCHAR(64) NOT NULL,
    created_at NVARCHAR(64) NOT NULL,
    signature NVARCHAR(128) NOT NULL,
    external_signature NVARCHAR(MAX) NULL
);

IF OBJECT_ID(N'dbo.CONSENT_REQUESTS', N'U') IS NULL
CREATE TABLE dbo.CONSENT_REQUESTS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    table_id NVARCHAR(128) NOT NULL,
    requester_sid NVARCHAR(256) NOT NULL,
    requested_grantee_type NVARCHAR(64) NOT NULL,
    requested_grantee_ref NVARCHAR(512) NOT NULL,
    business_justification NVARCHAR(MAX) NOT NULL,
    status NVARCHAR(64) NOT NULL,
    requested_at NVARCHAR(64) NOT NULL,
    requested_valid_to NVARCHAR(64) NOT NULL,
    itsm_ticket_id NVARCHAR(256) NULL,
    tenant_id NVARCHAR(128) NULL,
    requester_identifiers_json NVARCHAR(MAX) NULL,
    context_json NVARCHAR(MAX) NULL
);

IF OBJECT_ID(N'dbo.APPROVAL_STEPS', N'U') IS NULL
CREATE TABLE dbo.APPROVAL_STEPS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    consent_request_id NVARCHAR(128) NOT NULL,
    step_number INT NOT NULL,
    approver_sid NVARCHAR(256) NOT NULL,
    decision NVARCHAR(64) NOT NULL,
    rejection_reason NVARCHAR(MAX) NULL,
    decided_at NVARCHAR(64) NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_approval_steps_req_step' AND object_id = OBJECT_ID(N'dbo.APPROVAL_STEPS'))
    CREATE UNIQUE INDEX ux_approval_steps_req_step ON dbo.APPROVAL_STEPS (consent_request_id, step_number);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_tables_natural' AND object_id = OBJECT_ID(N'dbo.TABLES'))
    CREATE UNIQUE INDEX ux_tables_natural ON dbo.TABLES (source_name, schema_name, table_name);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_table_columns_natural' AND object_id = OBJECT_ID(N'dbo.TABLE_COLUMNS'))
    CREATE UNIQUE INDEX ux_table_columns_natural ON dbo.TABLE_COLUMNS (table_id, column_name);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_data_owners_sid' AND object_id = OBJECT_ID(N'dbo.DATA_OWNERS'))
    CREATE UNIQUE INDEX ux_data_owners_sid ON dbo.DATA_OWNERS (ad_sid);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_table_owners_natural' AND object_id = OBJECT_ID(N'dbo.TABLE_OWNERS'))
    CREATE UNIQUE INDEX ux_table_owners_natural ON dbo.TABLE_OWNERS (table_id, data_owner_id, owner_role);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_policy_epochs_natural' AND object_id = OBJECT_ID(N'dbo.POLICY_EPOCHS'))
    CREATE UNIQUE INDEX ux_policy_epochs_natural ON dbo.POLICY_EPOCHS (domain, schema_name, table_name);

-- WP-SC-01: indexes of the consent query. grantee_sid_norm is the upper-invariant grantee SID written by the repository; rows without it
-- (NULL) are always candidates and the C# filter stays authoritative. The lower-case TABLES columns make the natural key sargable.

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_consents_tenant_table' AND object_id = OBJECT_ID(N'dbo.CONSENTS'))
    CREATE INDEX ix_consents_tenant_table ON dbo.CONSENTS (tenant_id, table_id, is_revoked);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_consents_tenant_grantee' AND object_id = OBJECT_ID(N'dbo.CONSENTS'))
    CREATE INDEX ix_consents_tenant_grantee ON dbo.CONSENTS (tenant_id, grantee_sid_norm, is_revoked);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_consents_tenant_role' AND object_id = OBJECT_ID(N'dbo.CONSENTS'))
    CREATE INDEX ix_consents_tenant_role ON dbo.CONSENTS (tenant_id, role_name);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_consent_column_rules_consent' AND object_id = OBJECT_ID(N'dbo.CONSENT_COLUMN_RULES'))
    CREATE INDEX ix_consent_column_rules_consent ON dbo.CONSENT_COLUMN_RULES (consent_id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_consent_row_filters_consent' AND object_id = OBJECT_ID(N'dbo.CONSENT_ROW_FILTERS'))
    CREATE INDEX ix_consent_row_filters_consent ON dbo.CONSENT_ROW_FILTERS (consent_id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_column_masking_rules_column' AND object_id = OBJECT_ID(N'dbo.COLUMN_MASKING_RULES'))
    CREATE INDEX ix_column_masking_rules_column ON dbo.COLUMN_MASKING_RULES (table_column_id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_role_members_member' AND object_id = OBJECT_ID(N'dbo.ROLE_MEMBERS'))
    CREATE INDEX ix_role_members_member ON dbo.ROLE_MEMBERS (member_sid);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_table_owners_owner' AND object_id = OBJECT_ID(N'dbo.TABLE_OWNERS'))
    CREATE INDEX ix_table_owners_owner ON dbo.TABLE_OWNERS (data_owner_id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tables_natural_ci' AND object_id = OBJECT_ID(N'dbo.TABLES'))
    CREATE INDEX ix_tables_natural_ci ON dbo.TABLES (source_name_lc, schema_name_lc, table_name_lc);

IF OBJECT_ID(N'dbo.TABLE_RELATIONS', N'U') IS NULL
CREATE TABLE dbo.TABLE_RELATIONS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    parent_table_id NVARCHAR(128) NOT NULL,
    child_table_id NVARCHAR(128) NOT NULL,
    relation_name NVARCHAR(256) NOT NULL,
    cardinality NVARCHAR(64) NOT NULL
);

-- Issue #151: composite-key relations. One row per ordered key pair; column names are validated on write.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TABLE_RELATIONS_NATURAL' AND object_id = OBJECT_ID(N'dbo.TABLE_RELATIONS'))
    CREATE UNIQUE INDEX UX_TABLE_RELATIONS_NATURAL ON dbo.TABLE_RELATIONS (parent_table_id, child_table_id, relation_name);

IF OBJECT_ID(N'dbo.TABLE_RELATION_KEYS', N'U') IS NULL
CREATE TABLE dbo.TABLE_RELATION_KEYS (
    relation_id NVARCHAR(128) NOT NULL,
    key_ordinal INT NOT NULL CONSTRAINT CK_TABLE_RELATION_KEYS_ordinal CHECK (key_ordinal >= 1),
    parent_column NVARCHAR(256) NOT NULL,
    child_column NVARCHAR(256) NOT NULL,
    CONSTRAINT PK_TABLE_RELATION_KEYS PRIMARY KEY (relation_id, key_ordinal)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TABLE_RELATION_KEYS_PARENT' AND object_id = OBJECT_ID(N'dbo.TABLE_RELATION_KEYS'))
    CREATE UNIQUE INDEX UX_TABLE_RELATION_KEYS_PARENT ON dbo.TABLE_RELATION_KEYS (relation_id, parent_column);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TABLE_RELATION_KEYS_CHILD' AND object_id = OBJECT_ID(N'dbo.TABLE_RELATION_KEYS'))
    CREATE UNIQUE INDEX UX_TABLE_RELATION_KEYS_CHILD ON dbo.TABLE_RELATION_KEYS (relation_id, child_column);

IF OBJECT_ID(N'dbo.ITSM_OUTBOX', N'U') IS NULL
CREATE TABLE dbo.ITSM_OUTBOX (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    request_id NVARCHAR(128) NOT NULL,
    tenant_id NVARCHAR(128) NOT NULL,
    event_type NVARCHAR(128) NOT NULL,
    payload_json NVARCHAR(MAX) NOT NULL,
    preferred_system NVARCHAR(128) NOT NULL,
    status INT NOT NULL,
    retry_count INT NOT NULL,
    max_retries INT NOT NULL,
    created_at NVARCHAR(64) NOT NULL,
    next_retry_at NVARCHAR(64) NULL,
    last_error NVARCHAR(MAX) NULL
);

-- A consent request is activated at most once.
-- (The OpenMetadata sync marker is shared by many sync consents and is therefore excluded.)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_consents_request' AND object_id = OBJECT_ID(N'dbo.CONSENTS'))
    CREATE UNIQUE INDEX ux_consents_request ON dbo.CONSENTS (consent_request_id)
        WHERE consent_request_id IS NOT NULL AND consent_request_id <> '0e3d5c1a-7b2f-4c8e-9a61-5f0d2b7c4e19';

-- A ticket id identifies at most one consent request per tenant (webhooks look requests up by ticket).
-- SQL Server unique indexes treat NULL tenant ids as equal, which matches COALESCE(tenant_id, '') on PostgreSQL.
-- Existing duplicates must be cleaned up before this index can be created (see operations runbook).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_consent_requests_ticket' AND object_id = OBJECT_ID(N'dbo.CONSENT_REQUESTS'))
    CREATE UNIQUE INDEX ux_consent_requests_ticket ON dbo.CONSENT_REQUESTS (tenant_id, itsm_ticket_id)
        WHERE itsm_ticket_id IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_itsm_outbox_status_retry' AND object_id = OBJECT_ID(N'dbo.ITSM_OUTBOX'))
    CREATE INDEX idx_itsm_outbox_status_retry ON dbo.ITSM_OUTBOX (status, next_retry_at);

-- Virtual filters (docs/plans/2026-10-08-umsetzungsplan-virtuelle-filter.md, phase 2)
IF OBJECT_ID(N'dbo.VIRTUAL_FILTERS', N'U') IS NULL
CREATE TABLE dbo.VIRTUAL_FILTERS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(128) NOT NULL,
    name NVARCHAR(256) NOT NULL,
    source NVARCHAR(64) NOT NULL,
    definition_json NVARCHAR(MAX) NOT NULL,
    definition_hash NVARCHAR(128) NOT NULL,
    managed_path NVARCHAR(MAX) NULL,
    managed_commit NVARCHAR(256) NULL,
    updated_by NVARCHAR(256) NULL,
    updated_at NVARCHAR(64) NOT NULL,
    CONSTRAINT uq_virtual_filters_tenant_name UNIQUE (tenant_id, name)
);

IF OBJECT_ID(N'dbo.VIRTUAL_FILTER_ACCESS_PROFILES', N'U') IS NULL
CREATE TABLE dbo.VIRTUAL_FILTER_ACCESS_PROFILES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(128) NOT NULL,
    name NVARCHAR(256) NOT NULL,
    definition_json NVARCHAR(MAX) NOT NULL,
    definition_hash NVARCHAR(128) NOT NULL,
    managed_path NVARCHAR(MAX) NULL,
    managed_commit NVARCHAR(256) NULL,
    updated_by NVARCHAR(256) NULL,
    updated_at NVARCHAR(64) NOT NULL,
    CONSTRAINT uq_virtual_filter_access_profiles_tenant_name UNIQUE (tenant_id, name)
);

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ACCESS_PROFILES')
BEGIN
    CREATE TABLE ACCESS_PROFILES (
        profile_id NVARCHAR(128) NOT NULL,
        tenant_id NVARCHAR(64) NOT NULL,
        name NVARCHAR(256) NOT NULL,
        masking_mode NVARCHAR(32) NOT NULL CONSTRAINT DF_ACCESS_PROFILES_mode DEFAULT 'Default',
        target_tables NVARCHAR(MAX) NOT NULL CONSTRAINT DF_ACCESS_PROFILES_tables DEFAULT '[""*.*""]',
        row_filter_predicate NVARCHAR(MAX) NULL,
        justification NVARCHAR(MAX) NULL,
        created_by NVARCHAR(256) NOT NULL,
        created_at DATETIMEOFFSET NOT NULL CONSTRAINT DF_ACCESS_PROFILES_created DEFAULT SYSDATETIMEOFFSET(),
        valid_to DATETIMEOFFSET NULL,
        CONSTRAINT PK_ACCESS_PROFILES PRIMARY KEY (tenant_id, profile_id)
    );
END;

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ACCESS_PROFILE_ASSIGNMENTS')
BEGIN
    CREATE TABLE ACCESS_PROFILE_ASSIGNMENTS (
        profile_id NVARCHAR(128) NOT NULL,
        tenant_id NVARCHAR(64) NOT NULL,
        subject NVARCHAR(256) NOT NULL,
        subject_type NVARCHAR(32) NOT NULL CONSTRAINT DF_ACCESS_PROFILE_ASSIGNMENTS_type DEFAULT 'User',
        assigned_at DATETIMEOFFSET NOT NULL CONSTRAINT DF_ACCESS_PROFILE_ASSIGNMENTS_assigned DEFAULT SYSDATETIMEOFFSET(),
        expires_at DATETIMEOFFSET NULL,
        subject_norm NVARCHAR(256) NULL,
        created_by NVARCHAR(256) NULL,
        approved_by NVARCHAR(256) NULL,
        approved_at DATETIMEOFFSET NULL,
        justification NVARCHAR(MAX) NULL,
        kind NVARCHAR(32) NOT NULL CONSTRAINT DF_ACCESS_PROFILE_ASSIGNMENTS_kind DEFAULT 'Standard',
        renewal_count INT NOT NULL CONSTRAINT DF_ACCESS_PROFILE_ASSIGNMENTS_renewals DEFAULT 0,
        CONSTRAINT PK_ACCESS_PROFILE_ASSIGNMENTS PRIMARY KEY NONCLUSTERED (tenant_id, profile_id, subject_type, subject),
        CONSTRAINT FK_ACCESS_PROFILE_ASSIGNMENTS_PROFILE FOREIGN KEY (tenant_id, profile_id) 
            REFERENCES ACCESS_PROFILES (tenant_id, profile_id) ON DELETE CASCADE
    );

    CREATE NONCLUSTERED INDEX IX_ACCESS_PROFILE_ASSIGNMENTS_SUBJECT 
        ON ACCESS_PROFILE_ASSIGNMENTS(tenant_id, subject_type, subject_norm);
END;

IF OBJECT_ID(N'dbo.VIRTUAL_FILTER_GENERATION', N'U') IS NULL
CREATE TABLE dbo.VIRTUAL_FILTER_GENERATION (
    id INT NOT NULL PRIMARY KEY CHECK (id = 1),
    generation BIGINT NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM dbo.VIRTUAL_FILTER_GENERATION WHERE id = 1)
    INSERT INTO dbo.VIRTUAL_FILTER_GENERATION (id, generation) VALUES (1, 0);

-- Issue #129: caller attribute registry (four-eyes, draft beside the active row), service sets and one generation counter
IF OBJECT_ID(N'dbo.CALLER_ATTRIBUTES', N'U') IS NULL
CREATE TABLE dbo.CALLER_ATTRIBUTES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(128) NOT NULL,
    name NVARCHAR(128) NOT NULL,
    definition_json NVARCHAR(MAX) NOT NULL,
    definition_hash NVARCHAR(128) NOT NULL,
    status INT NOT NULL,
    created_by NVARCHAR(256) NOT NULL,
    approved_by NVARCHAR(256) NULL,
    approved_at NVARCHAR(64) NULL,
    draft_definition_json NVARCHAR(MAX) NULL,
    draft_definition_hash NVARCHAR(128) NULL,
    draft_created_by NVARCHAR(256) NULL,
    pending_deletion INT NOT NULL CONSTRAINT DF_CALLER_ATTRIBUTES_pending_deletion DEFAULT 0,
    deletion_requested_by NVARCHAR(256) NULL,
    updated_by NVARCHAR(256) NULL,
    updated_at NVARCHAR(64) NOT NULL,
    CONSTRAINT uq_caller_attributes_tenant_name UNIQUE (tenant_id, name)
);

IF OBJECT_ID(N'dbo.CALLER_ATTRIBUTE_SERVICE_SETS', N'U') IS NULL
CREATE TABLE dbo.CALLER_ATTRIBUTE_SERVICE_SETS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(128) NOT NULL,
    application_id NVARCHAR(256) NOT NULL,
    attribute_name NVARCHAR(128) NOT NULL,
    values_json NVARCHAR(MAX) NOT NULL,
    values_hash NVARCHAR(128) NOT NULL,
    status INT NOT NULL,
    created_by NVARCHAR(256) NOT NULL,
    approved_by NVARCHAR(256) NULL,
    approved_at NVARCHAR(64) NULL,
    draft_values_json NVARCHAR(MAX) NULL,
    draft_created_by NVARCHAR(256) NULL,
    updated_at NVARCHAR(64) NOT NULL,
    CONSTRAINT uq_caller_attribute_service_sets UNIQUE (tenant_id, application_id, attribute_name)
);

IF OBJECT_ID(N'dbo.CALLER_ATTRIBUTE_GENERATION', N'U') IS NULL
CREATE TABLE dbo.CALLER_ATTRIBUTE_GENERATION (
    id INT NOT NULL PRIMARY KEY CHECK (id = 1),
    generation BIGINT NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM dbo.CALLER_ATTRIBUTE_GENERATION WHERE id = 1)
    INSERT INTO dbo.CALLER_ATTRIBUTE_GENERATION (id, generation) VALUES (1, 0);

-- CR-ADG-31: tenant registry (grouped by NFKC + upper-case key; collided = 1 denies every spelling of the group)
IF OBJECT_ID(N'dbo.TENANT_REGISTRY', N'U') IS NULL
CREATE TABLE dbo.TENANT_REGISTRY (
    normalized_key NVARCHAR(256) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(256) NOT NULL,
    collided INT NOT NULL DEFAULT 0,
    first_seen_at NVARCHAR(64) NOT NULL
);

-- WP-ARC-A-02: durable webhook outbox (all times are Unix milliseconds)
IF OBJECT_ID(N'dbo.WEBHOOK_OUTBOX', N'U') IS NULL
CREATE TABLE dbo.WEBHOOK_OUTBOX (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    source INT NOT NULL,
    subscription_id NVARCHAR(128) NOT NULL,
    owner_tenant_id NVARCHAR(128) NULL,
    event_tenant_id NVARCHAR(128) NULL,
    event_id NVARCHAR(128) NOT NULL,
    event_type NVARCHAR(256) NOT NULL,
    event_source NVARCHAR(512) NOT NULL,
    event_time_ms BIGINT NOT NULL,
    payload_json NVARCHAR(MAX) NOT NULL,
    attempt INT NOT NULL,
    max_attempts INT NOT NULL,
    state INT NOT NULL,
    next_attempt_ms BIGINT NOT NULL,
    lease_until_ms BIGINT NULL,
    lease_owner NVARCHAR(128) NULL,
    last_status_code INT NULL,
    last_error_code NVARCHAR(64) NULL,
    created_ms BIGINT NOT NULL,
    updated_ms BIGINT NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_webhook_outbox_dedupe' AND object_id = OBJECT_ID(N'dbo.WEBHOOK_OUTBOX'))
    CREATE UNIQUE INDEX ux_webhook_outbox_dedupe ON dbo.WEBHOOK_OUTBOX (source, subscription_id, event_id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_webhook_outbox_due' AND object_id = OBJECT_ID(N'dbo.WEBHOOK_OUTBOX'))
    CREATE INDEX ix_webhook_outbox_due ON dbo.WEBHOOK_OUTBOX (state, next_attempt_ms);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_webhook_outbox_lease' AND object_id = OBJECT_ID(N'dbo.WEBHOOK_OUTBOX'))
    CREATE INDEX ix_webhook_outbox_lease ON dbo.WEBHOOK_OUTBOX (state, lease_until_ms);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_webhook_outbox_owner' AND object_id = OBJECT_ID(N'dbo.WEBHOOK_OUTBOX'))
    CREATE INDEX ix_webhook_outbox_owner ON dbo.WEBHOOK_OUTBOX (owner_tenant_id, state);

-- WP-ARC-A-11: durable, fenced CDC watermarks
IF OBJECT_ID(N'dbo.autheris_cdc_watermarks', N'U') IS NULL
CREATE TABLE dbo.autheris_cdc_watermarks (
    source NVARCHAR(32) NOT NULL,
    domain_name NVARCHAR(128) NOT NULL,
    schema_name NVARCHAR(128) NOT NULL,
    table_name NVARCHAR(128) NOT NULL,
    watermark BIGINT NOT NULL,
    fencing_token BIGINT NOT NULL,
    updated_at NVARCHAR(64) NOT NULL,
    updated_by NVARCHAR(256) NOT NULL,
    watermark_state NVARCHAR(MAX) NULL,
    CONSTRAINT PK_autheris_cdc_watermarks PRIMARY KEY (source, domain_name, schema_name, table_name)
);

-- Issue #99 (I1): governed CDC stream (durable, encrypted, fenced append; see PLAN-CDC-99)

IF OBJECT_ID(N'dbo.autheris_cdc_streams', N'U') IS NULL
CREATE TABLE dbo.autheris_cdc_streams (
    stream_id NVARCHAR(32) NOT NULL,
    source NVARCHAR(32) NOT NULL,
    domain_name NVARCHAR(128) NOT NULL,
    schema_name NVARCHAR(128) NOT NULL,
    table_name NVARCHAR(128) NOT NULL,
    status INT NOT NULL,
    activation_code NVARCHAR(64) NULL,
    head_offset BIGINT NOT NULL,
    tail_offset BIGINT NOT NULL,
    producer_fencing_token BIGINT NOT NULL,
    retention_fencing_token BIGINT NOT NULL,
    last_polled_ms BIGINT NULL,
    created_ms BIGINT NOT NULL,
    updated_ms BIGINT NOT NULL,
    CONSTRAINT PK_autheris_cdc_streams PRIMARY KEY (stream_id)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_cdc_streams_table' AND object_id = OBJECT_ID(N'dbo.autheris_cdc_streams'))
    CREATE UNIQUE INDEX ux_cdc_streams_table ON dbo.autheris_cdc_streams (source, domain_name, schema_name, table_name);

IF OBJECT_ID(N'dbo.autheris_cdc_stream_events', N'U') IS NULL
CREATE TABLE dbo.autheris_cdc_stream_events (
    stream_id NVARCHAR(32) NOT NULL,
    event_offset BIGINT NOT NULL,
    change_version BIGINT NOT NULL,
    kind INT NOT NULL,
    operation INT NULL,
    tenant_scope INT NOT NULL,
    tenant_id NVARCHAR(128) NULL,
    prior_tenant_id NVARCHAR(128) NULL,
    dek_id NVARCHAR(32) NOT NULL,
    payload VARBINARY(MAX) NOT NULL,
    produced_ms BIGINT NOT NULL,
    CONSTRAINT PK_autheris_cdc_stream_events PRIMARY KEY (stream_id, event_offset)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_cdc_stream_events_produced' AND object_id = OBJECT_ID(N'dbo.autheris_cdc_stream_events'))
    CREATE INDEX ix_cdc_stream_events_produced ON dbo.autheris_cdc_stream_events (stream_id, produced_ms);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_cdc_stream_events_dek' AND object_id = OBJECT_ID(N'dbo.autheris_cdc_stream_events'))
    CREATE INDEX ix_cdc_stream_events_dek ON dbo.autheris_cdc_stream_events (stream_id, dek_id);

IF OBJECT_ID(N'dbo.autheris_cdc_row_images', N'U') IS NULL
CREATE TABLE dbo.autheris_cdc_row_images (
    stream_id NVARCHAR(32) NOT NULL,
    key_fingerprint VARBINARY(32) NOT NULL,
    tenant_scope INT NOT NULL,
    tenant_id NVARCHAR(128) NULL,
    dek_id NVARCHAR(32) NOT NULL,
    image VARBINARY(MAX) NOT NULL,
    event_offset BIGINT NOT NULL,
    updated_ms BIGINT NOT NULL,
    CONSTRAINT PK_autheris_cdc_row_images PRIMARY KEY (stream_id, key_fingerprint)
);

IF OBJECT_ID(N'dbo.autheris_cdc_stream_keys', N'U') IS NULL
CREATE TABLE dbo.autheris_cdc_stream_keys (
    stream_id NVARCHAR(32) NOT NULL,
    dek_id NVARCHAR(32) NOT NULL,
    purpose INT NOT NULL,
    kek_id NVARCHAR(64) NOT NULL,
    wrapped_key VARBINARY(256) NOT NULL,
    created_ms BIGINT NOT NULL,
    retired_ms BIGINT NULL,
    CONSTRAINT PK_autheris_cdc_stream_keys PRIMARY KEY (stream_id, dek_id)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_cdc_stream_keys_kek' AND object_id = OBJECT_ID(N'dbo.autheris_cdc_stream_keys'))
    CREATE INDEX ix_cdc_stream_keys_kek ON dbo.autheris_cdc_stream_keys (kek_id);

-- WP-ARC-A-03: durable CDC webhook (CloudEvent) subscriptions
IF OBJECT_ID(N'dbo.CLOUDEVENT_SUBSCRIPTIONS', N'U') IS NULL
CREATE TABLE dbo.CLOUDEVENT_SUBSCRIPTIONS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(128) NOT NULL,
    target_url NVARCHAR(2048) NOT NULL,
    filter_table NVARCHAR(512) NOT NULL,
    filter_operations NVARCHAR(MAX) NULL,
    hmac_secret_protected NVARCHAR(MAX) NULL,
    is_enabled BIT NOT NULL,
    custom_headers_json NVARCHAR(MAX) NULL,
    created_by_sid NVARCHAR(256) NULL,
    created_by_groups_json NVARCHAR(MAX) NULL,
    created_by_roles_json NVARCHAR(MAX) NULL,
    created_ms BIGINT NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_cloudevent_subs_tenant' AND object_id = OBJECT_ID(N'dbo.CLOUDEVENT_SUBSCRIPTIONS'))
    CREATE INDEX ix_cloudevent_subs_tenant ON dbo.CLOUDEVENT_SUBSCRIPTIONS (tenant_id, is_enabled);

-- SQL Server cannot attach a trigger to TRUNCATE TABLE. A table that is referenced by a foreign key cannot be truncated,
-- so this (always empty) guard table blocks TRUNCATE on the audit table, mirroring the PostgreSQL TRUNCATE trigger.
IF OBJECT_ID(N'dbo.AUDIT_LOG_TRUNCATE_GUARD', N'U') IS NULL
CREATE TABLE dbo.AUDIT_LOG_TRUNCATE_GUARD (
    id INT NOT NULL PRIMARY KEY,
    seq BIGINT NULL,
    CONSTRAINT fk_audit_truncate_guard FOREIGN KEY (seq) REFERENCES dbo.AUDIT_LOG_ENTRIES (seq)
);

-- Issue #94: declared primary keys and indexes (composite, ordered, unique flag); columns referenced by TABLE_COLUMNS.id
IF OBJECT_ID(N'dbo.TABLE_PRIMARY_KEY_COLUMNS', N'U') IS NULL
CREATE TABLE dbo.TABLE_PRIMARY_KEY_COLUMNS (
    table_id NVARCHAR(128) NOT NULL,
    key_ordinal INT NOT NULL CONSTRAINT CK_TABLE_PK_COLUMNS_ordinal CHECK (key_ordinal >= 1),
    table_column_id NVARCHAR(128) NOT NULL,
    CONSTRAINT PK_TABLE_PRIMARY_KEY_COLUMNS PRIMARY KEY (table_id, key_ordinal)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TABLE_PK_COLUMNS_COLUMN' AND object_id = OBJECT_ID(N'dbo.TABLE_PRIMARY_KEY_COLUMNS'))
    CREATE UNIQUE INDEX UX_TABLE_PK_COLUMNS_COLUMN ON dbo.TABLE_PRIMARY_KEY_COLUMNS (table_id, table_column_id);

IF OBJECT_ID(N'dbo.TABLE_INDEXES', N'U') IS NULL
CREATE TABLE dbo.TABLE_INDEXES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    table_id NVARCHAR(128) NOT NULL,
    index_name NVARCHAR(128) NOT NULL,
    is_unique BIT NOT NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TABLE_INDEXES_NAME' AND object_id = OBJECT_ID(N'dbo.TABLE_INDEXES'))
    CREATE UNIQUE INDEX UX_TABLE_INDEXES_NAME ON dbo.TABLE_INDEXES (table_id, index_name);

IF OBJECT_ID(N'dbo.TABLE_INDEX_COLUMNS', N'U') IS NULL
CREATE TABLE dbo.TABLE_INDEX_COLUMNS (
    index_id NVARCHAR(128) NOT NULL,
    column_ordinal INT NOT NULL CONSTRAINT CK_TABLE_INDEX_COLUMNS_ordinal CHECK (column_ordinal >= 1),
    table_column_id NVARCHAR(128) NOT NULL,
    CONSTRAINT PK_TABLE_INDEX_COLUMNS PRIMARY KEY (index_id, column_ordinal)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TABLE_INDEX_COLUMNS_COLUMN' AND object_id = OBJECT_ID(N'dbo.TABLE_INDEX_COLUMNS'))
    CREATE UNIQUE INDEX UX_TABLE_INDEX_COLUMNS_COLUMN ON dbo.TABLE_INDEX_COLUMNS (index_id, table_column_id);

-- Issue #107: object-level row filter evaluation strategy (key-set cache); separate from TABLES because ingestion upserts rewrite TABLES
IF OBJECT_ID(N'dbo.TABLE_ROW_FILTER_STRATEGIES', N'U') IS NULL
CREATE TABLE dbo.TABLE_ROW_FILTER_STRATEGIES (
    table_id NVARCHAR(128) NOT NULL PRIMARY KEY,
    strategy INT NULL,
    duration_seconds INT NULL,
    max_keys INT NULL,
    updated_by NVARCHAR(256) NOT NULL,
    updated_at NVARCHAR(64) NOT NULL
);

-- Issue #111: dataset catalog tags (admin / dbt source) and visibility tier; separate from TABLES because ingestion upserts rewrite TABLES.
-- No foreign keys and no CHECK constraints (as TABLE_ROW_FILTER_STRATEGIES): validity is enforced on write and re-validated on read.
IF OBJECT_ID(N'dbo.TABLE_TAGS', N'U') IS NULL
CREATE TABLE dbo.TABLE_TAGS (
    table_id NVARCHAR(128) NOT NULL,
    tag NVARCHAR(64) NOT NULL,
    source NVARCHAR(16) NOT NULL,
    created_by NVARCHAR(256) NOT NULL,
    created_at NVARCHAR(64) NOT NULL,
    CONSTRAINT PK_TABLE_TAGS PRIMARY KEY (table_id, tag)
);
IF OBJECT_ID(N'dbo.TABLE_CATALOG_SETTINGS', N'U') IS NULL
CREATE TABLE dbo.TABLE_CATALOG_SETTINGS (
    table_id NVARCHAR(128) NOT NULL PRIMARY KEY,
    visibility INT NOT NULL,
    updated_by NVARCHAR(256) NOT NULL,
    updated_at NVARCHAR(64) NOT NULL
);

-- Issue #112: one validated semantic (dbt) manifest per tenant (latest version only); a save replaces the row atomically.
-- No foreign key (tenant registry rows are optional in single-tenant installs). content_hash is the SHA-256 hex of manifest_json.
IF OBJECT_ID(N'dbo.SEMANTIC_MANIFESTS', N'U') IS NULL
CREATE TABLE dbo.SEMANTIC_MANIFESTS (
    tenant_id NVARCHAR(128) NOT NULL PRIMARY KEY,
    manifest_version NVARCHAR(128) NOT NULL,
    content_hash CHAR(64) NOT NULL,
    format_version INT NOT NULL,
    models_count INT NOT NULL,
    metrics_count INT NOT NULL,
    manifest_json NVARCHAR(MAX) NOT NULL,
    imported_by NVARCHAR(256) NOT NULL,
    imported_at NVARCHAR(64) NOT NULL
);
");

            // Append-only audit table (a table owner can still drop the trigger; the runtime login should only
            // hold INSERT/SELECT, the schema is applied with a separate migration login). CREATE TRIGGER must be alone in its batch.
            ExecuteSchemaCommand(conn, @"CREATE OR ALTER TRIGGER dbo.trg_audit_append_only ON dbo.AUDIT_LOG_ENTRIES
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF CAST(SESSION_CONTEXT(N'audit_purge_authorized') AS bit) = 1
    BEGIN
        DELETE e
        FROM dbo.AUDIT_LOG_ENTRIES e
        INNER JOIN deleted d ON e.id = d.id;
        RETURN;
    END;
    THROW 50042, 'AUDIT_LOG_ENTRIES is append-only', 1;
END");
    }
}
