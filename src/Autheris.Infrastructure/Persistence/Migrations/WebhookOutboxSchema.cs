namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>
/// Governance migration 0002 (WP-ARC-A-02): the durable webhook outbox. All times are Unix milliseconds (BIGINT) on every dialect.
/// Rows hold no target URL, secret or custom headers (resolved from the subscription at delivery time).
/// </summary>
internal static class WebhookOutboxSchema
{
    public const string PostgreSql = @"
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
";

    public const string Sqlite = @"
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
";

    public const string SqlServer = @"
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
";
}
