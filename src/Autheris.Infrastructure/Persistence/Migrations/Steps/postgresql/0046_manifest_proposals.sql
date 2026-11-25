-- Governance schema step 0046 (PostgreSQL): declarative manifest proposals (Amendment 6). IT administration submits a consent manifest
-- as a pending proposal; it has no effect until one Data Governance member confirms it. The row keeps the manifest, the computed diff and
-- the hash of the grant state the diff was computed against.

CREATE TABLE IF NOT EXISTS governance_manifest_proposals (
    id VARCHAR(36) NOT NULL PRIMARY KEY,
    tenant_id VARCHAR(128) NOT NULL,
    scope_name VARCHAR(128) NOT NULL,
    submitted_by VARCHAR(256) NOT NULL,
    submitted_at TIMESTAMPTZ NOT NULL,
    manifest_json TEXT NOT NULL,
    diff_json TEXT NOT NULL,
    state_hash VARCHAR(64) NOT NULL,
    status VARCHAR(16) NOT NULL,
    decided_by VARCHAR(256) NULL,
    decided_at TIMESTAMPTZ NULL,
    reject_reason VARCHAR(512) NULL,
    expires_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_governance_manifest_proposals_scope
    ON governance_manifest_proposals (tenant_id, scope_name, status);

CREATE INDEX IF NOT EXISTS ix_governance_manifest_proposals_expiry
    ON governance_manifest_proposals (status, expires_at);
