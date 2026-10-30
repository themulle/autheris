# F-DBT-05: Streaming dbt Metadata Ingestion & Automated Model Sync (B-01..B-06, R-50..R-51)

## Executive Summary

Autheris bridges modern Analytics Engineering workflows with zero-trust runtime access control through its **Streaming dbt Metadata Ingestion Engine**.

Large-scale enterprise dbt projects often produce gigabyte-sized `manifest.json` and `catalog.json` build artifacts. Autheris streams these artifacts with linear memory overhead, automatically synchronizing model schemas, column descriptions, source relationships, sensitivity tags, and declarative SQL endpoints.

---

## Key Features & PoC Findings Addressed

### 1. Zero-Allocation Streaming Parser (`DbtArtifactStreamingParser`)
- Streams JSON tokens via `Utf8JsonReader` directly from disk or HTTP upload buffers without deserializing the full DOM tree into memory.
- Safely processes massive corporate repositories with tens of thousands of models and hundreds of thousands of columns in seconds.

### 2. Full `replace` Mode & Orphaned Model Purge (B-02)
- Supports non-destructive incremental updates (`merge`) and full synchronization (`replace`).
- In `replace` mode, endpoints and metadata records associated with models removed from the dbt repository are automatically purged, preventing stale or phantom endpoints from lingering in production.

### 3. Automated Source Resolution (B-04)
- Resolves dbt `source('source_name', 'table_name')` macro references into fully qualified canonical database identifiers (`source_name.table_name`).

### 4. Deterministic Sensitivity Hierarchy (B-03)
- Harmonizes dbt meta sensitivity tags (`public`, `internal`, `confidential`, `strictly_confidential`) into Autheris's 1-based sensitivity ranking (`1: Public` to `4: Strictly Confidential`).

### 5. Header Directive Injection Protection (H-20)
- When generating declarative `.sql` query files from dbt models, inputs are sanitized against newline injections (`\r\n`, `\u2028`, `\u2029`) to prevent malicious injection of `-- @...` directives.
- Overwriting non-dbt files or following symbolic links is strictly blocked.

---

## Relations and keys on models and sources

The sync imports relations from `relationships` tests, from `relationships_combination` tests and from `foreign_key` constraints (model and column level). Models, seeds, snapshots and sources are indexed by unique id; `ref('m')`, `ref('pkg', 'm')`, `ref('m', v=N)` and `source('s', 't')` resolve to exactly one node:

- Candidates in the test's `depends_on` are used first, then all manifest nodes. An explicit package in `ref` is a filter. There is no preference for the owning package.
- A reference that matches no node is skipped with `UNKNOWN_MODEL`; one that matches more than one is skipped with `AMBIGUOUS_REFERENCE`. Nothing is picked by order.
- Without `attached_node`, the child is the single `depends_on` node other than the parent. A self relation is inferred only when `depends_on` holds exactly the parent; any other shape is `AMBIGUOUS_REFERENCE`. With `attached_node`, the node must be in the manifest and in `depends_on`.
- The importer resolves tables by unique id (database, schema, name); sources are registered under their `name`. A relation without unique ids resolves by name only when exactly one model has that name, otherwise `AMBIGUOUS_MODEL`.
- `unique` and `unique_combination_of_columns` tests on a source declare a key. Without `attached_node` the test attaches only to its single `model.`, `seed.`, `snapshot.` or `source.` dependency; otherwise the key is skipped with `AMBIGUOUS_ATTACHMENT`.

A multi-column relation is declared with the table-level generic test `relationships_combination` (no namespace, or the project's own package):

```yaml
data_tests:
  - relationships_combination:
      columns: [customer_id, tenant_id]      # child columns, ordered
      to: source('crm', 'customers')
      fields: [customer_id, tenant_id]       # parent columns, same order and length
      relation_name: customer_tenant         # optional
```

`columns` and `fields` must be arrays of equal length within the key width limit; other forms are skipped with `UNSUPPORTED_FORM`, `COUNT_MISMATCH` or `TOO_WIDE`. Without `relation_name` the default name (the parent table name) applies, so a combination test and a single-column test on the same pair collide and both are skipped with `DUPLICATE_RELATION`. A relation that touches a table outside the caller's tenant is dropped without naming that table.

---

## CLI & API Usage

```bash
# Ingest dbt artifacts via CLI
curl -X POST "http://localhost:5000/api/extensions/dbt/sync?mode=replace" \
  -H "Authorization: Bearer <DbtAdmin-Token>" \
  -F "manifest=@target/manifest.json" \
  -F "catalog=@target/catalog.json"
```

## Data Owners in the Governance Import (issue #393)

`POST /api/extensions/dbt/governance` can assign table data owners (the approvers of consent requests). It is off unless `Gateway:Dbt:ImportDataOwners` is `true` (see the configuration guide). The manifest sync never writes owners; `meta.owner` stays a suggested owner team.

**Payload.** A table item carries `owner` (object) or `owners` (array, at most 10 per table):

```json
{ "classifications": [ {
    "database": "sales", "schema": "dbo", "table": "orders",
    "owners": [
      { "sid": "S-1-5-21-123", "display_name": "Ada Owner", "email": "ada@corp.example", "account": "CORP\\ada", "role": "primary" },
      "S-1-5-21-456"
    ] } ] }
```

- Object fields: `sid` (required), `account` (defaults to `sid`), `display_name` (required when the owner is created), `email`, `role` (`primary` default or `delegate`). Unknown keys reject. `group` / `groups` are not supported.
- A bare string is a lookup key (sid, account or e-mail) that must match exactly one active existing owner; it never creates an owner.
- Format rules: identifiers have no whitespace or control characters and no `system:` or `S-1-5-21-FORWARD-GRP-` prefix; display names have no control, bidi or zero-width characters (200 characters at most); e-mail addresses must be plain addresses.

**Rules.**
- The whole import is rejected (nothing is written) on any owner error: flag off, caller not a global governance admin, unparsable or invalid owner, an owner that matches the caller itself (sid, account, e-mail or any identifier alias; trimmed, Unicode-normalised, case-insensitive), a table that is unknown or outside the caller tenant, more than 5,000 assignments or 1,000 new owners in one import.
- Existing `DATA_OWNERS` rows are never changed: a payload attribute that differs from the stored row (`dbt_owner_conflict`), an inactive row, or a new owner whose sid, account or e-mail matches any identifier of another row (`dbt_owner_identifier_collision`) reject.
- When an identity realm is configured, every owner that is created or newly linked must exist and be active in the SCIM identity store; a lookup failure rejects. Without a realm the audit records `identity_verified=false`.
- New `TABLE_OWNERS` rows carry the id prefix `dbt:`. Merge mode only adds missing assignments. In replace mode, for each listed table the `dbt:` rows that are not in the payload are removed; rows without the prefix and tables that are not listed are never touched.
- `dryRun=true` runs every check and reports `CreatedDataOwnersCount`, `AssignedTableOwnersCount` and `RemovedTableOwnersCount`; it writes nothing.
- Audit: before anything is written the change set is recorded (`DATA_OWNER_CREATED`, `TABLE_OWNER_ASSIGNED`, `TABLE_OWNER_REMOVED`; a failure aborts the import), then one `DBT_OWNER_IMPORT_OUTCOME` event. Details carry owner id, role, table and source only, never an e-mail, account or display name.
- Error messages name the table and field and a governed `dbt_owner_*` code, never an identifier value.

Imported owners show up in `describe_dataset` and can approve consent requests of their tables like any other owner (the self-approval and consent-tenant checks are unchanged).
