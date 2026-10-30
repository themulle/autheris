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
