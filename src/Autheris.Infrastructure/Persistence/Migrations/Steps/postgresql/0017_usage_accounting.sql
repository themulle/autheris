-- Usage accounting schema: daily aggregates, grant attribution, group sets, tariffs, and retirement of legacy consumer daily tables.

CREATE TABLE IF NOT EXISTS USAGE_DAILY (
    day DATE NOT NULL,
    series_hash BYTEA NOT NULL,
    tenant_id VARCHAR(128) NOT NULL,
    user_key VARCHAR(184) NOT NULL,
    group_set_id CHAR(32) NOT NULL,
    billing_group VARCHAR(184) NOT NULL,
    transport SMALLINT NOT NULL,
    application VARCHAR(64) NOT NULL,
    client_id VARCHAR(64) NOT NULL,
    data_source VARCHAR(128) NOT NULL,
    table_id VARCHAR(128) NOT NULL,
    requests BIGINT NOT NULL DEFAULT 0,
    queries BIGINT NOT NULL DEFAULT 0,
    rows_returned BIGINT NOT NULL DEFAULT 0,
    rows_measured BIGINT NOT NULL DEFAULT 0,
    bytes BIGINT NOT NULL DEFAULT 0,
    duration_ms BIGINT NOT NULL DEFAULT 0,
    analysis_cost BIGINT NOT NULL DEFAULT 0,
    source_compute_ms BIGINT NOT NULL DEFAULT 0,
    source_compute_measured BIGINT NOT NULL DEFAULT 0,
    source_cost_micros BIGINT NOT NULL DEFAULT 0,
    source_cost_measured BIGINT NOT NULL DEFAULT 0,
    allow_count BIGINT NOT NULL DEFAULT 0,
    deny_access BIGINT NOT NULL DEFAULT 0,
    deny_rate_limit BIGINT NOT NULL DEFAULT 0,
    deny_cost BIGINT NOT NULL DEFAULT 0,
    deny_quota BIGINT NOT NULL DEFAULT 0,
    deny_other BIGINT NOT NULL DEFAULT 0,
    errors BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (day, series_hash)
);

CREATE INDEX IF NOT EXISTS ix_usage_daily_tenant_day
    ON USAGE_DAILY (tenant_id, day);

CREATE INDEX IF NOT EXISTS ix_usage_daily_tenant_user_day
    ON USAGE_DAILY (tenant_id, user_key, day);

CREATE INDEX IF NOT EXISTS ix_usage_daily_tenant_table_day
    ON USAGE_DAILY (tenant_id, table_id, day);

CREATE INDEX IF NOT EXISTS ix_usage_daily_tenant_source_day
    ON USAGE_DAILY (tenant_id, data_source, day);

CREATE INDEX IF NOT EXISTS ix_usage_daily_tenant_group_set_day
    ON USAGE_DAILY (tenant_id, group_set_id, day);

CREATE TABLE IF NOT EXISTS USAGE_GRANT_DAILY (
    day DATE NOT NULL,
    tenant_id VARCHAR(128) NOT NULL,
    grant_id VARCHAR(128) NOT NULL,
    data_source VARCHAR(128) NOT NULL,
    table_id VARCHAR(128) NOT NULL,
    accesses BIGINT NOT NULL DEFAULT 0,
    queries BIGINT NOT NULL DEFAULT 0,
    rows_returned BIGINT NOT NULL DEFAULT 0,
    bytes BIGINT NOT NULL DEFAULT 0,
    analysis_cost BIGINT NOT NULL DEFAULT 0,
    source_compute_ms BIGINT NOT NULL DEFAULT 0,
    source_cost_micros BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (day, tenant_id, grant_id, data_source, table_id)
);

CREATE INDEX IF NOT EXISTS ix_usage_grant_daily_tenant_grant_day
    ON USAGE_GRANT_DAILY (tenant_id, grant_id, day);

CREATE TABLE IF NOT EXISTS USAGE_GROUP_SET (
    tenant_id VARCHAR(128) NOT NULL,
    group_set_id CHAR(32) NOT NULL,
    group_sid VARCHAR(184) NOT NULL,
    first_seen DATE NOT NULL,
    PRIMARY KEY (tenant_id, group_set_id, group_sid)
);

CREATE INDEX IF NOT EXISTS ix_usage_group_set_tenant_sid
    ON USAGE_GROUP_SET (tenant_id, group_sid);

CREATE TABLE IF NOT EXISTS USAGE_GRANT_REF (
    tenant_id VARCHAR(128) NOT NULL,
    grant_id VARCHAR(128) NOT NULL,
    valid_from TIMESTAMPTZ NULL,
    valid_until TIMESTAMPTZ NULL,
    first_seen DATE NOT NULL,
    last_seen DATE NOT NULL,
    PRIMARY KEY (tenant_id, grant_id)
);

CREATE TABLE IF NOT EXISTS USAGE_TARIFF (
    tenant_id VARCHAR(128) NOT NULL,
    data_source VARCHAR(128) NOT NULL,
    version INT NOT NULL,
    valid_from DATE NOT NULL,
    valid_until DATE NULL,
    base_per_query DECIMAL(19,9) NOT NULL DEFAULT 0,
    per_row DECIMAL(19,9) NOT NULL DEFAULT 0,
    per_mib DECIMAL(19,9) NOT NULL DEFAULT 0,
    per_compute_second DECIMAL(19,9) NOT NULL DEFAULT 0,
    per_analysis_cost DECIMAL(19,9) NOT NULL DEFAULT 0,
    source_cost_factor DECIMAL(19,9) NOT NULL DEFAULT 0,
    source_cost_unit VARCHAR(32) NULL,
    unit_label VARCHAR(32) NOT NULL,
    compute_basis SMALLINT NOT NULL DEFAULT 0,
    created_by VARCHAR(184) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    reason VARCHAR(512) NOT NULL,
    PRIMARY KEY (tenant_id, data_source, version)
);

DROP TABLE IF EXISTS CONSUMER_APP_DAILY;
DROP TABLE IF EXISTS CONSUMER_APP_ACTOR_DAILY;
