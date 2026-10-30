-- phase3: I388-SC-06
-- WP-DT-2b (default tenant): the code always writes an explicit tenant, so the dead column defaults go and CONSENT_REQUESTS.tenant_id becomes NOT NULL.
-- Fail closed: SET NOT NULL aborts the step (and the transaction) when a row still has a NULL tenant. There is no backfill; the operator assigns the tenant first.
ALTER TABLE CONSENT_REQUESTS ALTER COLUMN tenant_id SET NOT NULL;
ALTER TABLE CONSENTS ALTER COLUMN tenant_id DROP DEFAULT;
ALTER TABLE AUDIT_LOG_ENTRIES ALTER COLUMN tenant_id DROP DEFAULT;
