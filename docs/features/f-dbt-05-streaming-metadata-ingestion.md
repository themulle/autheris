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

## CLI & API Usage

```bash
# Ingest dbt artifacts via CLI
curl -X POST "http://localhost:5000/api/extensions/dbt/sync?mode=replace" \
  -H "Authorization: Bearer <DbtAdmin-Token>" \
  -F "manifest=@target/manifest.json" \
  -F "catalog=@target/catalog.json"
```
