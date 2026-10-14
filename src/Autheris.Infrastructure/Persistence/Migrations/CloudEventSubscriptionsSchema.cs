namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>
/// Governance migration 0004 (WP-ARC-A-03): durable CDC webhook (CloudEvent) subscriptions. Times are Unix milliseconds.
/// <c>hmac_secret_protected</c> holds the KEK-protected secret (<c>enc:v1:...</c>); the plaintext secret is never stored.
/// </summary>
internal static class CloudEventSubscriptionsSchema
{
    public const string PostgreSql = @"
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
";

    public const string Sqlite = @"
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
";

    public const string SqlServer = @"
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
";
}
