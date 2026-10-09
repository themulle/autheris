using Autheris.Domain.Model;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

public partial class SqlServerGovernanceRepository
{
    private int _isInitialized;

    /// <summary>Application lock resource that serialises the schema DDL of concurrently starting replicas.</summary>
    private const string SchemaAppLockResource = "autheris:governance:schema";

    private static void ExecuteSchemaCommand(SqlConnection conn, string sql)
    {
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void InitializeDatabase()
    {
        if (Interlocked.Exchange(ref _isInitialized, 1) == 1) return;

        // DDL with a separate migration login when configured (the runtime login then needs no DDL rights).
        using var conn = string.IsNullOrWhiteSpace(_migrationConnectionString)
            ? OpenConnection()
            : OpenMigrationConnection(_migrationConnectionString);

        // Concurrent replica starts must not run the DDL at the same time (session-scoped application lock).
        using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 60000; SELECT @r;";
            lockCmd.Parameters.AddWithValue("@res", SchemaAppLockResource);
            var rc = Convert.ToInt32(lockCmd.ExecuteScalar());
            if (rc < 0)
            {
                throw new InvalidOperationException($"Could not acquire the SQL Server schema application lock (sp_getapplock returned {rc}).");
            }
        }

        try
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
    plugin_name NVARCHAR(256) NULL
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
    role_id NVARCHAR(128) NULL,
    role_name NVARCHAR(256) NULL,
    valid_from NVARCHAR(64) NOT NULL,
    valid_to NVARCHAR(64) NOT NULL,
    is_revoked INT NOT NULL,
    revoked_by_sid NVARCHAR(256) NULL,
    revoked_at NVARCHAR(64) NULL,
    revoke_reason NVARCHAR(MAX) NULL,
    tenant_id NVARCHAR(128) NOT NULL DEFAULT 'legacy-single-tenant'
);

IF OBJECT_ID(N'dbo.CONSENT_COLUMN_RULES', N'U') IS NULL
CREATE TABLE dbo.CONSENT_COLUMN_RULES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    consent_id NVARCHAR(128) NOT NULL,
    table_column_id NVARCHAR(128) NOT NULL,
    column_name NVARCHAR(256) NOT NULL,
    access_level INT NOT NULL
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
    additional_hops_json NVARCHAR(MAX) NULL
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
    requester_identifiers_json NVARCHAR(MAX) NULL
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
BEGIN
    WITH DupOwners AS (
        SELECT id, ad_sid, ROW_NUMBER() OVER(PARTITION BY ad_sid ORDER BY is_active DESC, id ASC) as rn
        FROM dbo.DATA_OWNERS
        WHERE ad_sid IS NOT NULL AND ad_sid <> ''
    )
    DELETE FROM dbo.DATA_OWNERS WHERE id IN (SELECT id FROM DupOwners WHERE rn > 1);

    CREATE UNIQUE INDEX ux_data_owners_sid ON dbo.DATA_OWNERS (ad_sid);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_table_owners_natural' AND object_id = OBJECT_ID(N'dbo.TABLE_OWNERS'))
    CREATE UNIQUE INDEX ux_table_owners_natural ON dbo.TABLE_OWNERS (table_id, data_owner_id, owner_role);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_policy_epochs_natural' AND object_id = OBJECT_ID(N'dbo.POLICY_EPOCHS'))
    CREATE UNIQUE INDEX ux_policy_epochs_natural ON dbo.POLICY_EPOCHS (domain, schema_name, table_name);

IF OBJECT_ID(N'dbo.TABLE_RELATIONS', N'U') IS NULL
CREATE TABLE dbo.TABLE_RELATIONS (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    parent_table_id NVARCHAR(128) NOT NULL,
    child_table_id NVARCHAR(128) NOT NULL,
    relation_name NVARCHAR(256) NOT NULL,
    join_key_parent NVARCHAR(256) NOT NULL,
    join_key_child NVARCHAR(256) NOT NULL,
    cardinality NVARCHAR(64) NOT NULL
);

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
        CONSTRAINT PK_ACCESS_PROFILE_ASSIGNMENTS PRIMARY KEY (tenant_id, profile_id, subject),
        CONSTRAINT FK_ACCESS_PROFILE_ASSIGNMENTS_PROFILE FOREIGN KEY (tenant_id, profile_id) 
            REFERENCES ACCESS_PROFILES (tenant_id, profile_id) ON DELETE CASCADE
    );

    CREATE NONCLUSTERED INDEX IX_ACCESS_PROFILE_ASSIGNMENTS_SUBJECT 
        ON ACCESS_PROFILE_ASSIGNMENTS(tenant_id, subject);
END;

IF OBJECT_ID(N'dbo.VIRTUAL_FILTER_GENERATION', N'U') IS NULL
CREATE TABLE dbo.VIRTUAL_FILTER_GENERATION (
    id INT NOT NULL PRIMARY KEY CHECK (id = 1),
    generation BIGINT NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM dbo.VIRTUAL_FILTER_GENERATION WHERE id = 1)
    INSERT INTO dbo.VIRTUAL_FILTER_GENERATION (id, generation) VALUES (1, 0);

-- SQL Server cannot attach a trigger to TRUNCATE TABLE. A table that is referenced by a foreign key cannot be truncated,
-- so this (always empty) guard table blocks TRUNCATE on the audit table, mirroring the PostgreSQL TRUNCATE trigger.
IF OBJECT_ID(N'dbo.AUDIT_LOG_TRUNCATE_GUARD', N'U') IS NULL
CREATE TABLE dbo.AUDIT_LOG_TRUNCATE_GUARD (
    id INT NOT NULL PRIMARY KEY,
    seq BIGINT NULL,
    CONSTRAINT fk_audit_truncate_guard FOREIGN KEY (seq) REFERENCES dbo.AUDIT_LOG_ENTRIES (seq)
);
");

            // Append-only audit table (a table owner can still drop the trigger; the runtime login should only
            // hold INSERT/SELECT, the schema is applied with a separate migration login). CREATE TRIGGER must be alone in its batch.
            ExecuteSchemaCommand(conn, @"CREATE OR ALTER TRIGGER dbo.trg_audit_append_only ON dbo.AUDIT_LOG_ENTRIES
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50042, 'AUDIT_LOG_ENTRIES is append-only', 1;
END");
        }
        finally
        {
            using var unlockCmd = conn.CreateCommand();
            unlockCmd.CommandText = "EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session';";
            unlockCmd.Parameters.AddWithValue("@res", SchemaAppLockResource);
            unlockCmd.ExecuteNonQuery();
        }

        var (tailHash, tailSeq) = ReadAuditTail(null);
        if (tailHash != null)
        {
            _lastAuditHash = tailHash;
            _lastAuditSeq = tailSeq;
        }
    }

    private static SqlConnection OpenMigrationConnection(string connectionString)
    {
        var conn = new SqlConnection(connectionString);
        try
        {
            conn.Open();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private void SeedInitialCatalog()
    {
        var domains = new[]
        {
            "finance", "hr", "sales", "inventory", "compliance",
            "marketing", "logistics", "crm", "procurement", "billing"
        };

        using var conn = OpenConnection();
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
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@sid", sid);
                cmd.Parameters.AddWithValue("@account", account);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@email", email);
                cmd.ExecuteNonQuery();
                return ResolveOwnerId(conn, trans, sid, string.Empty);
            }

            var ownerId = EnsureOwner("S-1-5-21-DATAOWNER-1", "CORP\\dataowner", "Chief Finance Data Owner", "dataowner@corp.local");
            var salesOwnerId = EnsureOwner("S-1-5-21-DATAOWNER-APPROVER", "CORP\\salesowner", "Chief Sales Data Owner", "salesowner@corp.local");
            var approverId = EnsureOwner("S-1-5-21-APPROVER", "CORP\\approver", "Finance Approver", "approver@corp.local");
            var approverAId = EnsureOwner("S-1-5-21-APPROVER-A", "CORP\\approver-a", "Finance Approver A", "approver-a@corp.local");
            var approverBId = EnsureOwner("S-1-5-21-APPROVER-B", "CORP\\approver-b", "Finance Approver B", "approver-b@corp.local");

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
                        idCmd.CommandText = "SELECT TOP (1) id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@name)";
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
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = trans;
                            cmd.CommandText = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive)
                                                SELECT @id, @tid, @name, @type, @sens
                                                WHERE NOT EXISTS (SELECT 1 FROM TABLE_COLUMNS WHERE table_id = @tid AND column_name = @name);";
                            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                            cmd.Parameters.AddWithValue("@tid", tableId);
                            cmd.Parameters.AddWithValue("@name", cName);
                            cmd.Parameters.AddWithValue("@type", cType);
                            cmd.Parameters.AddWithValue("@sens", cSens ? 1 : 0);
                            cmd.ExecuteNonQuery();
                        }

                        string? colId;
                        using (var colIdCmd = conn.CreateCommand())
                        {
                            colIdCmd.Transaction = trans;
                            colIdCmd.CommandText = "SELECT TOP (1) id FROM TABLE_COLUMNS WHERE table_id = @tid AND LOWER(column_name) = LOWER(@name)";
                            colIdCmd.Parameters.AddWithValue("@tid", tableId);
                            colIdCmd.Parameters.AddWithValue("@name", cName);
                            colId = colIdCmd.ExecuteScalar()?.ToString();
                        }

                        if (cSens && !string.IsNullOrEmpty(colId))
                        {
                            using var maskCmd = conn.CreateCommand();
                            maskCmd.Transaction = trans;
                            maskCmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement)
                                                   SELECT @id, @cid, 'EMAIL', NULL, '***@***.***'
                                                   WHERE NOT EXISTS (SELECT 1 FROM COLUMN_MASKING_RULES WHERE table_column_id = @cid);";
                            maskCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                            maskCmd.Parameters.AddWithValue("@cid", colId);
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
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
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
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@rid", adminRoleId);
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
        catch (Exception ex)
        {
            trans.Rollback();
            _logger?.LogWarning(ex, "Failed to seed demo data into SQL Server Governance DB; skipping.");
        }
    }

    private static void EnsureTableOwner(SqlConnection conn, SqlTransaction trans, string tableId, string ownerId, string role)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = trans;
        cmd.CommandText = @"INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                            SELECT @id, @tid, @oid, @role
                            WHERE NOT EXISTS (SELECT 1 FROM TABLE_OWNERS WHERE table_id = @tid AND data_owner_id = @oid AND owner_role = @role);";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@tid", tableId);
        cmd.Parameters.AddWithValue("@oid", ownerId);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.ExecuteNonQuery();
    }

    private static string ResolveOwnerId(SqlConnection conn, SqlTransaction trans, string sid, string fallbackId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = trans;
        cmd.CommandText = "SELECT TOP (1) id FROM DATA_OWNERS WHERE ad_sid = @sid";
        cmd.Parameters.AddWithValue("@sid", sid);
        var existing = cmd.ExecuteScalar()?.ToString();
        return !string.IsNullOrEmpty(existing) ? existing : fallbackId;
    }
}
