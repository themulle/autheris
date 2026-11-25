-- Governance schema step 0048 (PostgreSQL): externally sourced deputy relations (issue #1126). An external system (absence, HR) states
-- "person A deputizes for person B from X to Y"; Autheris reconciles these person-to-person relations per source. Inside the window A is
-- an eligible decider wherever B is a container owner-group member; outside it the row grants nothing. The *_lookup columns hold the
-- normalized, realm-qualified lookup forms of the resolved identities.

CREATE TABLE IF NOT EXISTS governance_deputy_relations (
    id VARCHAR(36) NOT NULL PRIMARY KEY,
    tenant_id VARCHAR(128) NOT NULL,
    source VARCHAR(64) NOT NULL,
    external_id VARCHAR(256) NOT NULL,
    principal_ref VARCHAR(256) NOT NULL,
    principal_lookup VARCHAR(256) NOT NULL,
    principal_alt_lookup VARCHAR(256) NULL,
    deputy_ref VARCHAR(256) NOT NULL,
    deputy_lookup VARCHAR(256) NOT NULL,
    deputy_alt_lookup VARCHAR(256) NULL,
    valid_from TIMESTAMPTZ NOT NULL,
    valid_to TIMESTAMPTZ NOT NULL,
    created_by VARCHAR(256) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_by VARCHAR(256) NULL,
    updated_at TIMESTAMPTZ NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_governance_deputy_relations_external
    ON governance_deputy_relations (tenant_id, source, external_id);

CREATE INDEX IF NOT EXISTS ix_governance_deputy_relations_deputy
    ON governance_deputy_relations (tenant_id, deputy_lookup, valid_to);

CREATE INDEX IF NOT EXISTS ix_governance_deputy_relations_deputy_alt
    ON governance_deputy_relations (tenant_id, deputy_alt_lookup, valid_to);

CREATE INDEX IF NOT EXISTS ix_governance_deputy_relations_principal
    ON governance_deputy_relations (tenant_id, principal_lookup);
