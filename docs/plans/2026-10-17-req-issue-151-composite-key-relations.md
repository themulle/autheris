# REQ-REL-151: Relations with Composite Keys - dbt Import Keeps All Key Columns, Storage Persists Ordered Key Pairs

**Document ID:** `REQ-REL-151`
**Date:** 2026-10-17
**Status:** PRD READY (Phase 1 complete); next: Phase 2 plan (small; Phase 3 review short), then Phase 4 TDD fix
**Priority:** P1 correctness bug (a relation silently joins on fewer columns than declared)
**Author:** Enterprise Product Manager (Phase 1)
**Source:** GitHub issue #151 (owner, v1.1.16); related: #94 (composite primary keys), `REQ-FED-01` (cross-source relations), `REQ-CAT-138` (relation loading, join paths in search)
**Branch:** `docs/issue-intake-2026-10-17` (docs only; suggested fix branch `fix/issue-151-composite-relations`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Binding project constraints:** fail closed; no production deployment and **no upgrade migrations**: "a migration that keeps existing relations" becomes an edit of the `0001` baseline DDL of the three engines plus the three shape snapshots (the caller supplies the newest schema, existing single-column relations are re-supplied by re-import); English only; no row values in logs.

---

## 1. Verdict

**Sensible with changes, and mostly correct as written.** The query side is already composite-ready; the bug is entirely in import and persistence. Changes against the issue: (a) no migration, a baseline change; (b) store **ordered pairs** in a child table (one row per column pair) so that unequal key lengths cannot be represented; (c) the "fail closed for composite cross-source relations until tuple key sets exist" consideration is **moot**: the cross-source resolver already handles composite keys; (d) three adjacent defects found during verification are in scope because they share the same code (default column `id`, duplicate relations per sync, SQLite id handling).

| Item | Result |
|---|---|
| Size | S to M (importer S, storage M across three engines) |
| Wave | Correctness bugfix, scheduled as an own small step **before** the federation relation steps I-18/I-19 (they edit the same `TABLE_RELATIONS` DDL and repositories). Wave 2 head if the owner treats it as a bug step, wave 3 at the latest |
| Dependencies | none for the fix; must be merged before I-18 (`WP-FED-07`, persists `AllowCrossDataSource`) and before `REQ-CAT-138` INC-138-4 (bulk relation load) |
| Security class | Not a disclosure: row filters, masks and per-level checks still apply. Wrong data is presented as belonging to a parent. Phase 3: short review of the key-column access rule and the new write validation |

## 2. Verified Current State

### 2.1 Claim verification (code at `main` `b5c1b5dd`)

| # | Claim in #151 | Result | Evidence |
|---|---|---|---|
| C1 | `TableRelation.JoinKeysParent/Child` are lists | **Confirmed** | `Autheris.Domain/Model/TableModels.cs:274-275`; single-key accessors `JoinKeyParent/JoinKeyChild` with `init => ... = new[] { value }` at `:278-288` (this setter is the silent truncation point for every writer that uses it) |
| C2 | `TreeSqlCompiler` joins pairwise | **Confirmed** | `Autheris.Application/Sql/Tree/TreeSqlCompiler.cs:381-395` (count check, every key column must resolve as Clear on both sides) and `:543-548` (`child.col = parent.col` per index) |
| C3 | `CrossSourceRelationResolver` checks equal length | **Confirmed** | `CrossSourceRelationResolver.Helpers.cs:115-121` (non-empty, equal counts, else `CrossSourceRelationKeyNotClear`) and `:123-142` (each key column needs `Clear`) |
| C4 | Composite primary keys exist since #94 | **Confirmed** | `TableMetadata.PrimaryKeyColumns`, `TABLE_PRIMARY_KEY_COLUMNS` with ordinals (`PostgreSqlBaselineSchema.cs:493-500`) |
| C5 | dbt import keeps only the first column of `columns`/`to_columns` | **Confirmed** | `DbtArtifactStreamingParser.cs:172-185`: `foreach (...) { parentCol = ...; break; }` for `to_columns` (`:178`) and `columns` (`:183`), no warning |
| C6 | `DbtRelationshipDefinition` holds one parent and one child column | **Confirmed** | `Autheris.Domain/Model/DbtMetadataModels.cs:65-71` (`ParentColumn`, `ChildColumn`); the importer builds `JoinKeysParent = [rel.ParentColumn]` (`DbtMetadataIngestionService.cs:545-546`) |
| C7 | `TABLE_RELATIONS` stores one `join_key_parent` and one `join_key_child` | **Confirmed** | Baseline DDL `PostgreSqlBaselineSchema.cs:250-258`, `SqliteBaselineSchema.cs:253-`, `SqlServerBaselineSchema.cs:291-299`; repositories write `relation.JoinKeyParent/JoinKeyChild` (PostgreSQL `PostgreSqlGovernanceRepository.Catalog.cs:661-680`, SQL Server `SqlServerGovernanceRepository.Catalog.cs:672-690`, SQLite `SqliteGovernanceRepository.Catalog.cs:1120-1131`) and read them with the single-key setters (`:588-634`, `:1066-1110`). A composite relation cannot be persisted on any path |
| C8 | Effect: wrong rows attached, no leak, no signal | **Confirmed** | The compiler joins exactly the stored keys; row filters and masks apply per level (`TreeSqlCompiler`). Correctness failure, not disclosure |
| C9 | Cross-source composite relation needs tuple key sets, "should fail closed until then" | **Wrong (moot)** | `CrossSourceRelationResolver` already extracts composite keys (`CompositeKey`, `Helpers.cs:23-45` `TryExtractKey`) and builds `(a = @p AND b = @q) OR (...)` predicates for several columns (`Helpers.cs:306-352`, `BuildParentInPredicate`). So composite keys work across sources, at the cost of OR-of-AND predicates for large key sets, which `REQ-FED-01` #124 (`KeyPredicate`, key-set filters, `MaxBatchSize` bounded by `MaxBindParameters / keyColumns`) improves. A single-column truncation is the only way a cross-source relation can currently be wrong |
| C10 | dbt `relationships` test covers one column only | **Confirmed** | `DbtArtifactStreamingParser.cs:57-100` reads one `field`/`column_name` |
| C11 | Describe and search show all key columns of a relation | **Not present today** | `describe_dataset` relations are `McpGraphQlRelation(field, dataset, isList)` without any key column (`McpDatasetCatalog.cs:665-672`); search join paths come from a naming heuristic and print one column (`CatalogSearchEngine.cs:84-100`, `McpToolExecutionHandler.cs:171-175`). This is a new feature, not a fix |

### 2.2 New findings (same code path, in scope)

| # | Finding | Evidence | Severity |
|---|---|---|---|
| N-1 | Missing `columns` or `to_columns` defaults silently to the column name `"id"`, inventing a key | `DbtArtifactStreamingParser.cs:176,181` (`parentCol = "id"`, `childCol = "id"`), column-level constraint `:236-250` same default | Correctness: a constraint without `to_columns` becomes a join on `id` |
| N-2 | Every dbt sync creates a **new** relation row: the importer assigns `Id = Guid.NewGuid()` (`DbtMetadataIngestionService.cs:540`) and all three upserts key on `id` only; there is no unique index on the natural identity | `TABLE_RELATIONS` DDL (primary key `id` only); `ON CONFLICT (id)`, `MERGE ... ON t.id = s.id`, `INSERT OR REPLACE` | Duplicate relations after every sync; GraphQL then exposes `rel`, `rel_2`, ... (`CatalogSchemaModel.cs:268-275`). Confirmed from code, to be confirmed by an integration test |
| N-3 | SQLite `CreateRelationAsync` writes `relation.ParentTableId`/`ChildTableId`, while the dbt importer sets only the identifiers, so the stored ids are the empty GUID; the reader joins `TABLES` by id and then finds nothing | `SqliteGovernanceRepository.Catalog.cs:1120-1128` versus PostgreSQL/SQL Server, which resolve ids from the identifiers (`PostgreSqlGovernanceRepository.Catalog.cs:628-650`) | Probably dbt relations never work on SQLite. Confirmed by reading; verify by test |
| N-4 | The importer does not check that the key columns exist in the catalog tables | `DbtMetadataIngestionService.cs:533-562` | A relation naming an unknown column fails later at query time (compiler `ResolveClearColumn`), not at import |
| N-5 | The importer does not report skipped relations: unknown models are skipped without a warning (`:533-538`) and `CreateRelationAsync` failures are only logged (`:559`) | same | Silent loss |
| N-6 | dbt `foreign_key` constraints in the `expression` form (raw DDL text) and the `dbt_constraints` package test `foreign_key` (`fk_column_names`, `pk_column_names`) are not parsed | parser | Gap, not a defect |

### 2.3 Overlap with existing plans

| Plan | Overlap | Handling |
|---|---|---|
| #94 composite primary keys | Ordered key storage precedent (child table with ordinal and a unique index) | Mirror it (R-151-5) |
| `REQ-FED-01`, `PLAN-FED-01` | Composite cross-source relations already supported in the resolver; the planner pushes only single-column equality edges and keeps further conjuncts in DuckDB (`plan-cross-source-all-consumers` section 4, "composite equalities use the first column" for push-down only, results stay correct) | No change needed for correctness; `REQ-FED-01` #124 handles large composite key sets. Add a test that a composite cross-source relation joins on all columns |
| I-18 `WP-FED-07` | Persists `AllowCrossDataSource` on `TABLE_RELATIONS`, per-table owner model | #151 first; I-18 builds on the child table and the natural-key upsert |
| `REQ-CAT-138` INC-138-4 | `GetAllRelationsAsync` bulk read, relation indexes | The bulk query reads key pairs with one `LEFT JOIN`/second query ordered by `(relation_id, key_ordinal)`; INC-138-4 follows #151 |
| `REQ-CAT-138` R-138-2.5 | Search join paths from stored relations | Needs #151's multi-column representation |
| #153, #143, #146 | none | none |

## 3. Problem Statement

Business tables are keyed by several columns. A dbt model-level `foreign_key` constraint with `columns: [company, order_no]` and `to_columns: [company, order_no]` is imported as a relation on `company` alone, silently. Nested GraphQL selections then attach every order of the company to each line. Storage cannot hold the right relation even if the importer were fixed.

## 4. Benchmark and Rejected Anti-Patterns

| Practice | Use here |
|---|---|
| dbt 1.9 model-level `constraints` with `type: foreign_key`, `columns`, `to`, `to_columns` (and the raw `expression` form for some adapters); constraints are metadata, not enforced on every adapter | Parse `columns`/`to_columns` as ordered lists; treat `expression` as unsupported with a warning; do not assume enforcement |
| Relational design: composite foreign keys as ordered column pairs (`information_schema.key_column_usage` with `ordinal_position` and `position_in_unique_constraint`) | Child table `TABLE_RELATION_KEYS` with `key_ordinal` |
| Catalog tools that import relationships (DataHub, OpenMetadata) preserve all columns of a foreign key and show the constraint as a column list | Preserve and expose all columns (only visible ones, see SEC-151-3) |

Rejected anti-patterns:

1. **Keeping parent and child key columns in two parallel lists** (two JSON arrays or two comma lists in two columns). Two lists can disagree in length; every reader needs a guard. Alternative: one row per **pair**; unequal counts become unrepresentable.
2. **Joining on a prefix of the declared key** (current behaviour) or "best effort" shortening. A relation is imported completely or not at all.
3. **Defaulting missing key columns to `id`.** An absent value is an error to report, not a name to invent.
4. **A repair migration that rewrites old rows.** The project has no production and no upgrade migrations; the baseline changes and the importer re-creates relations.
5. **Silent skips.** Every skipped relation produces a sync warning.
6. **Fail-closed by disabling composite cross-source relations.** Not needed (C9); it would remove a working capability.

## 5. Requirements

### 5.1 dbt import

- **R-151-1** A model-level `foreign_key` constraint is imported with **all** entries of `columns` and `to_columns`, paired by index (`columns[i]` is the child column, `to_columns[i]` the parent column). `DbtRelationshipDefinition` carries `IReadOnlyList<string> ParentColumns` and `ChildColumns` (non-empty, equal length).
- **R-151-2** A constraint is **skipped with a warning** (never shortened, never defaulted) when: `columns` or `to_columns` is missing or empty; the counts differ; a column name is empty or duplicated within one side; `to` is missing or unknown; the form is `expression` only. Column-level `foreign_key` constraints keep their single pair; a missing target column is skipped with a warning instead of defaulting to `id`.
- **R-151-3** After the tables are synced, the importer checks that every key column exists on both tables in the catalog (case-insensitive match, stored with the catalog's canonical column name). A relation with an unknown column on either side is skipped with a warning naming relation, table and column (no row values).
- **R-151-4** Warnings reach `DbtSyncResult.Warnings` (also in dry run) and the sync log: one entry per skipped relation with the reason code (`COUNT_MISMATCH`, `MISSING_COLUMNS`, `UNKNOWN_COLUMN`, `UNKNOWN_MODEL`, `UNSUPPORTED_FORM`, `PERSIST_FAILED`). The parser returns the warnings (a result record replaces the tuple), because it is where the constraint JSON is read.
- **R-151-4a (optional, Q-151-3)** The `dbt_constraints` package test `foreign_key` (`fk_column_names`, `pk_column_names`) is accepted with the same rules. Default: not in this fix.

### 5.2 Storage (baseline DDL, no migration)

- **R-151-5** New table `TABLE_RELATION_KEYS (relation_id TEXT NOT NULL, key_ordinal INTEGER NOT NULL CHECK (key_ordinal >= 1), parent_column TEXT NOT NULL, child_column TEXT NOT NULL, PRIMARY KEY (relation_id, key_ordinal))` plus a unique index on `(relation_id, parent_column, child_column)` in the `0001` baseline of PostgreSQL, SQLite and SQL Server and in the three shape snapshots (the existing `0001` checksum constants are handled as the other baseline edits of waves 2/3 do).
- **R-151-6** `join_key_parent` and `join_key_child` are removed from `TABLE_RELATIONS` (no data to keep). The columns `relation_name`, `cardinality` and the table ids stay.
- **R-151-7** Natural identity: unique index on `TABLE_RELATIONS (parent_table_id, child_table_id, relation_name)`; `CreateRelationAsync` upserts on it in all three engines (PostgreSQL `ON CONFLICT (...)`, SQL Server `MERGE ... ON` natural key, SQLite `ON CONFLICT`), replacing the key rows of that relation in the same transaction. A repeated sync updates in place and creates no duplicate. The importer keeps passing a fresh `Id`; the stored id of an existing relation wins.
- **R-151-8** Write validation in `CreateRelationAsync` on every engine: `JoinKeysParent` and `JoinKeysChild` non-empty and of equal length, no duplicate pair, both tables exist (as today), every key column exists on its table; a violation throws `InvalidRelationException` and nothing is written (all three rows in one transaction).
- **R-151-9** Resolve table ids from the identifiers on **all three** engines, as PostgreSQL and SQL Server do (fixes N-3); the SQLite path no longer trusts `relation.ParentTableId/ChildTableId`.
- **R-151-10** Reads (`GetRelationsForTableAsync`, and `GetAllRelationsAsync` when `REQ-CAT-138` INC-138-4 lands) return `JoinKeysParent` and `JoinKeysChild` ordered by `key_ordinal`. A relation row without key rows, or with a key-count mismatch, is skipped with an error log (fail closed); the catalog load does not fail.
- **R-151-11** The single-key accessors `TableRelation.JoinKeyParent/JoinKeyChild` keep working for readers but their `init` setters are removed or marked so no writer can truncate a key by using them (compile-time guard); all writers use the list properties.
- **AC-151-1** A dbt manifest with the two-column constraint of the issue imports as one relation with both pairs, and still has both after a restart (read back from the governance store) on SQLite, PostgreSQL and SQL Server.
- **AC-151-2** Importing the same manifest twice yields one relation, not two.
- **AC-151-3** Constraints with mismatched counts, missing `to_columns`, an unknown column on either side, an unknown target model or the `expression` form are not imported and each appears in the sync warnings with a reason code; dry run reports the same warnings.
- **AC-151-4** On SQLite a dbt-imported relation is read back (verifies N-3).

### 5.3 Query behaviour

- **R-151-12** A nested GraphQL query over a composite relation returns only matching rows in both directions (orders to lines, line to order), with row filters and masks applied per level. The reverse relation swaps the lists as today (`CatalogSchemaModel.cs:336-337`).
- **R-151-13** Every key column of a relation must be `Clear` for the caller on its table or the relation fails closed (already enforced: `TreeSqlCompiler.cs:385-395`, `CrossSourceRelationResolver.Helpers.cs:123-142`); a composite key does not weaken this. Tests cover a composite relation where the second key column is masked: the relation fails closed with the existing error, the first column alone is never used.
- **R-151-14** `GraphQlTreeBuilder` and `CatalogSchemaModel` need no model change (both carry lists, `CatalogSchemaModel.cs:29-36`); the schema fingerprint already includes the pairs (`:77`). A test fixes this.
- **R-151-15** A composite cross-source relation joins on all columns (test with two sources, both directions); a count mismatch is an error (existing `CrossSourceRelationKeyNotClear` message).

### 5.4 Discovery output (extension, can ship after the fix)

- **R-151-16** `describe_dataset` relations gain `keys: [{ parent, child }, ...]` in order. The pairs are returned **only if every column on both sides is a column the caller may list** (column access not `Deny`) and the target dataset is visible; otherwise the relation is shown without keys or, when a key column is hidden, not shown (decision: not shown, because a relation whose key the caller cannot see cannot be queried by that caller anyway).
- **R-151-17** Search join paths (`REQ-CAT-138` R-138-2.5) use stored relations with the same visibility rule and print all pairs.
- **AC-151-5** `describe_dataset` shows both pairs of the order-lines relation to a caller who may see both tables and all four columns; a caller who cannot see one of the key columns gets no keys and no hint of the column.

## 6. Security Requirements

1. **SEC-151-1 Fail closed.** An invalid, partial or inconsistent relation is never written and never used: write validation (R-151-8), read-side skip (R-151-10), compile-time guard (R-151-11).
2. **SEC-151-2 No wrong-join exposure.** A relation is imported completely or not at all; there is no prefix join and no default column.
3. **SEC-151-3 Column visibility in discovery.** Key pairs are shown only for columns the caller may list; relation and key names of hidden tables or hidden columns never appear in `describe_dataset`, search or GraphQL introspection beyond what the schema already exposes.
4. **SEC-151-4 Key columns stay `Clear`-only** on both sides for every pair, for same-source and cross-source execution (R-151-13).
5. **SEC-151-5 Warnings carry no values.** Sync warnings and logs name relation, table, column and reason, never row data or manifest free text beyond identifiers (identifiers are length-limited and escaped like other dbt warnings).
6. **SEC-151-6 Import bounds.** A constraint with more than 16 key columns is skipped with a warning (bounds the join width and the cross-source predicate size).
7. **SEC-151-7 Tenancy.** Relation writes keep the existing tenant and admin authorisation of the dbt sync (`IngestManifestStreamAsync`); the new natural-key upsert never overwrites a relation of another tenant's tables (tables are resolved through the same tenant-scoped lookup as the sync).

## 7. Non-Goals

1. A repair or upgrade migration for existing `TABLE_RELATIONS` rows.
2. Auto-detecting relations from column names (the search heuristic is removed from output, `REQ-CAT-138`, not extended).
3. Enforcing that parent columns form a primary or unique key (warning only, Q-151-4).
4. New relation kinds (many-to-many through bridge tables) or cardinality inference; imported composite relations stay `OneToMany`.
5. Changing `TreeSqlCompiler` or `CrossSourceRelationResolver` semantics.
6. A relation administration API (belongs to I-18).

## 8. Deviations from the Issue

| # | Issue says | This PRD |
|---|---|---|
| D-1 | "A migration that keeps existing single-column relations unchanged" | Baseline DDL edit, no migration (project rule); old relations are re-created by re-import |
| D-2 | "JSON arrays or a key-column table" | Child table with ordered **pairs** (precedent #94, unequal lengths unrepresentable) |
| D-3 | Composite cross-source relations should fail closed until tuple key sets exist | Not needed: the resolver already supports composite keys (C9). A test locks the behaviour instead |
| D-4 | Skip when counts differ or a column is unknown | Kept and extended (missing lists, `expression`, unknown model, persistence failure, width cap) with reason codes |
| D-5 | Show all key columns in `describe_dataset` and search | Kept, restricted to visible columns (SEC-151-3); search join paths switch from heuristic to stored relations |
| D-6 | (not in the issue) | Added: no default `id` (N-1), idempotent upsert (N-2), SQLite id resolution (N-3), unknown-model skips reported (N-5) |
| D-7 | Optional `dbt_constraints` package test | Deferred (Q-151-3) |

## 9. Open Owner Questions

| # | Question | Recommended default |
|---|---|---|
| Q-151-1 | Child table of pairs or JSON array of pairs in one column | Child table `TABLE_RELATION_KEYS` (precedent #94, ordered rows, one bulk join); a JSON array of `{parent,child}` objects is an acceptable fallback if the owner prefers one column |
| Q-151-2 | Reference key columns by name or by `TABLE_COLUMNS.id` (as #94 does) | By name, validated on write: it matches the stored form today, avoids a join per read, and a column rename is an ingestion event that re-creates relations |
| Q-151-3 | Accept the `dbt_constraints` package `foreign_key` test | Not in this fix; add as a follow-up if a real project needs it |
| Q-151-4 | Warn when the parent key columns are not the declared primary or a unique index of the parent | Yes, as a warning code `PARENT_KEY_NOT_UNIQUE` using `TableMetadata.PrimaryKeyColumns`/`Indexes`; still imported (dbt constraints are not enforced on all adapters) |
| Q-151-5 | Step placement | Own small bugfix step before I-18; it does not wait for the catalog chunk |
| Q-151-6 | Maximum key width | 16 columns |

## 10. Increments and Waves

| Step | Content | Size | Wave | Depends on |
|---|---|:---:|---|---|
| INC-151-1 | Importer: all columns, reason-coded warnings, no default `id`, unknown-model and persistence warnings, parser result record (R-151-1..4) | S | 2 (bug step) | none |
| INC-151-2 | Storage: `TABLE_RELATION_KEYS`, natural-key upsert, write validation, SQLite id fix, read ordering, setter guard, baseline x3 and shapes (R-151-5..11, AC-151-1..4) | M | 2 (same step as INC-151-1 if the architect agrees; one PR is cleaner because the importer test needs the storage) | baseline DDL serialisation (G-SHARED) |
| INC-151-3 | Query tests for composite relations in both directions, masked key column, cross-source composite (R-151-12..15) | S | 2 (same PR, tests only unless a defect appears) | INC-151-2 |
| INC-151-4 | Discovery output with key pairs (R-151-16, R-151-17) | S | 3 (with `REQ-CAT-138` INC-138-2) | INC-151-2, I-7 for `McpDatasetCatalog.cs` |

Recommended delivery: INC-151-1..3 as one bugfix PR, before I-18 and before `REQ-CAT-138` INC-138-4; INC-151-4 later with the discovery work.

## 11. Test Strategy Hints (for Phase 2)

1. Parser unit tests for every skip reason and the composite happy path, including order and a three-column key.
2. Repository tests on all three engines (Testcontainers for PostgreSQL and SQL Server, in-process SQLite): round trip, duplicate import, write validation, relation without key rows, SQLite id resolution.
3. Integration: manifest from the issue, nested GraphQL query both directions against seeded data with rows that differ only in the second key column (the exact failure of the bug), then the same query with a row filter and a masked second key column.
4. Cross-source test with two data sources and a two-column relation.
5. Shape snapshot tests for `TABLE_RELATION_KEYS`, the removed columns and the new unique index on three engines.
6. Architecture test: no writer uses the single-key setters of `TableRelation`.
