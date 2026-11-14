# Deduplicated Open Backlog & Phase 3 Audit (`main` @ `2b2876bd`)

- **Date:** 2026-11-14
- **Author:** Solution Architect (`csharp-architect`) & Security Reviewer (`csharp-security-expert`)
- **Baseline:** `origin/main` @ `2b2876bd` (`WP-SIMPL-46 PR 6b (#730)` merged)
- **Scope:** Complete, deduplicated verification of all Simplification Work Packages (`WP-SIMPL-01` .. `WP-SIMPL-54`) and all Open Issues Groups 1–9 (`docs/plans/2026-11-13-plan-issues-*.md` + `docs/plans/2026-11-13-sec-issues-group-*.md`).

---

## 1. Executive Summary & Phase 3 Gate Status

1. **Zero Unreviewed Plans:** Every single Simplification Work Package (`WP-SIMPL-01..54`) and every single Open Issue Work Package (`Groups 1–9`) now holds an explicit Phase 3 security verdict (`APPROVED` or `APPROVED WITH CONDITIONS`).
   - `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md`: Phase 3 `APPROVED WITH CONDITIONS` (`SEC-52-01..12`, Section 14).
   - `docs/plans/simplification/WP-SIMPL-54-golden-queries-from-dbt.md`: Phase 3 `APPROVED WITH CONDITIONS` (`SEC-54-01..07`, Section 10).
   - `docs/plans/2026-11-13-plan-issues-catalog-search.md` (`#628c`) & `docs/plans/2026-11-13-plan-issues-catalog-scale.md` (`#672`, `#677`): All three prior `CHANGES REQUIRED` items were reworked and upgraded to `APPROVED WITH CONDITIONS` in `2026-11-13-sec-issues-group-6.md`, `2026-11-13-sec-issues-group-7.md`, and `2026-11-14-owner-decisions-sec-groups-1-9.md`.
2. **Major Deduplication Findings (Already Merged on `main` — Do NOT Re-Dispatch):**
   - **`#443` (`REQ-LPP` set-based nested latest-per-parent):** Already merged on `main` (`f42dab64`).
   - **`WP-SIMPL-51` (`PR 1..PR 5`, `#527`, `#528`, `#529`, `#533`):** All 5 PRs are **100% merged** on `main` (`#566` `1d6fa34b`, `#570` `e96ee892`, `#581` `edeca24b` squash-merged PRs 3, 4, and 5). Unmerged branches `feat/simpl-51-pr-3`, `feat/simpl-51-pr-4`, and `feat/simpl-51-pr-5` are stale pre-squash branches and can be deleted.
   - **`WP-SIMPL-20` (`PR 20a..20e`):** All 5 PRs are **100% merged** on `main` (`#589`, `#600`, `#630`, `#635`, `#638`). Branch `lane-d/simpl-20a` is stale.
   - **`WP-SIMPL-42 PR 1`:** Already merged on `main` (`#598` `6c9e9ac1`). Branch `feat/simpl-42-pr1` points to `46b8bc84` (`WP-SIMPL-41 PR 1` `#633`, also merged).
   - **`WP-OI-SCALE-1 (#672)` (`CatalogWriteScope` restrictive-write immediate flush & dirty flag):** Both PR 1 (`19b050e8` / `906e3664`) and PR 2 (`f0e9f484` / `968c670e`) are **merged** on `main` (`src/Autheris.Application/Catalog/CatalogWriteScope.cs` already implements `FlushPendingAsync` and `_dirtyFlag.MarkDirty()` per `SEC-OI-672-1..5`).
   - **Groups 1 & 2 (`#469..#508` security/correctness issues):** **100% merged** on `main`.
   - **Group 8 (CI & Footprint `#518..#522`, `#611`, footprint-budget wrapper, Parity `!cancelled()`):** **Merged** on `main` (only `#523` post-merge timing verification run and `#609` live Databricks workspace secrets remain).

---

## 2. Open GitHub PRs Ready to Review / Merge Now

| PR | Branch (`SHA`) | Work Package / Issue | Summary |
| :--- | :--- | :--- | :--- |
| **PR #734** | `origin/pr-734` (`cbcec9de`) | `WP-OI-SRCH-624` (`#624`) | Catalog search relevance benchmark test set (`SEC-OI-624-1`). |
| **PR #744** | `origin/pr-744` (`9a279ca9`) | `WP-OI-WEB-669` (`#669`) | SQL Server synonyms regression test + structured diagnostic log (`SEC-OI-669-1`). |
| **PR #747** | `origin/pr-747` (`e3078e14`) | `WP-OI-WEB-668` (`#668`) | WebSQL `Dbt:DatabaseMappings` governed catalog resolution (`SEC-OI-668-1..2`). |
| **PR #748** | `origin/pr-748` (`56edc825`) | `WP-OI-CAT-1` (`#666`) | dbt physical table name from `identifier`/`alias` (`SEC-OI-666-1..3`). |

---

## 3. Active Wave 2 (In Flight) & Queued Wave 3 Packages

### 3.1 Wave 2 (Currently In Flight with Hydrogen Workers)
| Slot | Worker | Worktree / Branch | Package |
| :--- | :--- | :--- | :--- |
| **W2-1** | `@dev-health` | `/work/wt/simpl-53-pr4-gql-index-hints` | `WP-SIMPL-53 PR 4 (#573)` — GraphQL relation index hints (`SEC-53-01..05`). |
| **W2-2** | `@dev-tls` | `/work/wt/oi-cat-612-explain-subtrees` | `WP-OI-CAT-7 (#612)` — `ExplainQueryAsync` correlated/derived subtree walk (`SEC-OI-612-1..3`). |
| **W2-3** | `@dev-cursor` | `/work/wt/oi-cat-670-revoke-auth` | `WP-OI-CAT-3 (#670)` — Revoke-consent approver check inside repository transaction (`SEC-OI-670-1..2`). |
| **W2-4A** | `@dev-audit` | `/work/wt/oi-sql-478a-trino-chains` | `WP-OI-SQL-478-A (#478 SQL)` — Iterative left-associative `AND`/`OR` chain flattening in `TrinoSqlEngine` (`SEC-OI-478-1..2`). |
| **W2-4B** | *(Queued after W2-4A or in Wave 5)* | `/work/wt/oi-sql-478b-odata-chains` | `WP-OI-SQL-478-B (#478 OData)` — Iterative OData `$filter` binary chain parser (`SEC-OI-478-3`). |

### 3.2 Wave 3 (Briefed in `docs/plans/2026-11-14-hydrogen-briefs-wave-3.md`)
| Slot | Worktree / Branch | Package | Notes / DDL |
| :--- | :--- | :--- | :--- |
| **W3-1** | `/work/wt/simpl-41-pr2-discovery-ddl` | `WP-SIMPL-41 PR 2` | **Single DDL Slot `0013_catalog_discovery`**. Rebases `bfc86f73` (`origin/feat/simpl-41-pr2`) onto `main` and renumbers `0007` -> `0013`. Unblocks `WP-SIMPL-41 PR 3` (Wave 4). |
| **W3-2** | `/work/wt/oi-sql-486-required-filters` | `WP-OI-SQL-486 (#486)` | Required filter column satisfaction in `TrinoSqlEngine` (`AstSecurityVisitor.RequiredFilters.cs`, `SEC-OI-486-1..3`). Run after `W2-4A` (`#478-A`). |
| **W3-3** | `/work/wt/simpl-45-pr4-write-workflow` | `WP-SIMPL-45 PR 4` | Consent write workflow (`RequestedWriteRules`) + `GovernedSqlRewriter` decision-point write wiring (`SEC-SIMPL-45-01..10`). |
| **W3-4** | `/work/wt/oi-scale-675-pr1-coalesce` | `WP-OI-SCALE-4 PR 1 (#675)` | `CatalogSnapshotProvider` change-event coalescing (`SEC-OI-675-1`). Zero DDL. |

---

## 4. Complete Inventory of Simplification Work Packages (`WP-SIMPL-01` .. `WP-SIMPL-54`)

### 4.1 100% Merged on `main` (`2b2876bd`)
- **`WP-SIMPL-01` .. `WP-SIMPL-27`:** All 27 packages merged (`WP-SIMPL-14` policy-lint removal `#722`; `WP-SIMPL-20` PRs 20a–20e `#589`, `#600`, `#630`, `#635`, `#638`; `WP-SIMPL-26` `#721`; `WP-SIMPL-27` `#723`).
- **`WP-SIMPL-33` (`#656`), `WP-SIMPL-34` (`#648`), `WP-SIMPL-35` (`#645`):** Merged.
- **`WP-SIMPL-37` (`PR A` `#565`, `PR B` `#568`, `PR C` `#575`):** Merged.
- **`WP-SIMPL-39` (`PR 1` `#564`, `PR 2` `#567`, `PR 3` `#577`, `PR 4` `#583`):** Merged.
- **`WP-SIMPL-43` (`PR 1` `#563`, `PR 2` `#572`, `PR 3a` `#576`, `PR 3b` `#590`, `PR 4` `#596`, `PR 5a` `#727`, `PR 5b` `#733`, `PR 5c` `#742`):** Merged.
- **`WP-SIMPL-46` (`PR 1` `#571`, `PR 2` `#580`, `PR 3` `#586`, `PR 4` `#593`, `PR 5` `#605`, `PR 6a` `#726`, `PR 6b` `#730`):** Merged.
- **`WP-SIMPL-51` (`PR 1` `#566`, `PR 2` `#570`, `PR 3-5` `#581`):** Merged.

### 4.2 Partially Merged / Remaining Open Pre-Freeze Simplification Packages
| WP | Merged PRs on `main` | Remaining PRs | Phase 3 Status | Target Wave / Dependencies |
| :--- | :--- | :--- | :--- | :--- |
| **`WP-SIMPL-31`** | — | **PR 1** (Docs verification CI job + reference docs accuracy) | `APPROVED` (`SEC-31-01`) | **Wave 5+** (runs after feature PRs stabilize) |
| **`WP-SIMPL-32`** | PRs 1–4 (`#641`, `#646`, `#661`, `#664`) | **PR 5** (Final sweep after all `WP-SIMPL-36..49` merge) | `APPROVED` | **Final Wave** (gated by `WP-SIMPL-41..50`) |
| **`WP-SIMPL-36`** | Batches 1–5 (`#602..#620`), `36g` (`#728`), `36i` (`#737`) | **`36f` (`#441` cursor HMAC)**, **`36h` (`#288/#295/#313/#345`)**, **`36j` (`#338/#339/#344` Group 2)** | `APPROVED WITH CONDITIONS` (`SEC-36-*`) | **Wave 5+** (independent security batches) |
| **`WP-SIMPL-38`** | PRs 38a–38d (`#588`, `#595`, `#604`, `#636`) | **PR 38e** (`SqlInlineRenderer` retirement once callers migrate) | `APPROVED WITH CONDITIONS` (`SEC-38-*`) | **Wave 5+** |
| **`WP-SIMPL-40`** | PRs 40a–40c (`#597`, `#606`, `#634`) | **PR 40d** (MCP doc tools read/filter polish if any gap remains; note `WP-SIMPL-41 PR 6` adds `doc_links` & `read_document`) | `APPROVED WITH CONDITIONS` (`SEC-40-*`) | **Wave 5+** (with `WP-SIMPL-41 PR 6`) |
| **`WP-SIMPL-41`** | PR 1 (`#633`) | **PR 2** (Wave 3 DDL `0013`), **PR 3** (Wave 4 Discovery core + channels), **PR 4** (Artifacts & generator removal), **PR 5** (Request access + tier removal), **PR 6** (Linked docs + `#500` MCP text) | `APPROVED WITH CONDITIONS` (`SEC-DISC-01..16`) | **PR 2 in Wave 3**, **PR 3 in Wave 4**, **PR 4–6 in Wave 5+** |
| **`WP-SIMPL-42`** | PR 1 (`#598`) | **PR 2** (`ConsumerApp` k-anonymity/projections), **PR 3a/3b** (`UsageGroupSet` + grant IDs on audit/usage facts) | `APPROVED WITH CONDITIONS` (`SEC-42-*`) | **Wave 5+** (PR 3b after `WP-SIMPL-47 PR 2`) |
| **`WP-SIMPL-44`** | PR 1 (`#584`), PR 2 (`#592`) | **PR 3** (Diagnostics/explain polish if any remaining item from spec) | `APPROVED WITH CONDITIONS` (`SEC-44-*`) | **Wave 5+** |
| **`WP-SIMPL-45`** | PR 1 (`#594`), PR 2 (`#601`), PR 3 (`#632`) | **PR 4** (Wave 3 consent write workflow), **PR 5** (REST/OData/GraphQL/MCP write execution wiring + E2E tests) | `APPROVED WITH CONDITIONS` (`SEC-SIMPL-45-01..10`) | **PR 4 in Wave 3**, **PR 5 in Wave 5** |
| **`WP-SIMPL-47`** | — | **PR 1** (Wave 4 DDL `0014` + domain models + `grant_source`), **PR 2** (`IGrantWriter` + signed hash-chained `GRANT_RECORDS`), **PR 3** (`IConsentDecisionPipeline` + `GrantValidityPolicy`), **PR 4** (Routing + events + `GrantExpiryHostedService`), **PR 5** (REST `/api/v1/consent`), **PR 6** (External catalog read + OpenAPI), **PR 7** (Profile assignment approval + owner grants + effect preview) | `APPROVED WITH CONDITIONS` (`SEC-SIMPL-47-01..12`) | **PR 1 in Wave 4**, **PR 2 in Wave 5**, **PR 3–7 in Wave 6+** |
| **`WP-SIMPL-48`** | PR 1 (`#587`), PR 2 (`#599`), PR 3 (`#607`), PR 4 (`#639`) | **PR 5** (`PatternCoverageMonitor` + approving-owner rule), **PR 6** (`IGrantEffectPreview`) | `APPROVED WITH CONDITIONS` (`SEC-48-*`) | **Wave 5+** (before `WP-SIMPL-47 PR 7`) |
| **`WP-SIMPL-49`** | PR 1 (`#585`), PR 2 (`#591`) | **PR 3** (`CallerAttributeService` / external provider), **PR 4** (SCIM/claims mapping & tests) | `APPROVED WITH CONDITIONS` (`SEC-49-*`) | **Wave 5+** |
| **`WP-SIMPL-50`** | PR 1 (`#631`), PR 2 (`#637`), PR 3 (`#731`) | **PR 4** (Final dialect/federation gate & cleanup) | `APPROVED WITH CONDITIONS` (`SEC-50-*`) | **Wave 5+** |
| **`WP-SIMPL-52`** | — | **PR 1a..7** (10 slices: `0016+` DDL, unified `ACCESS_POLICIES` / `ACCESS_PROFILES` / `VIRTUAL_FILTERS` / `CONSENTS` retirement into `ACCESS_POLICIES` + `CONSENT_REQUESTS`) | `APPROVED WITH CONDITIONS` (`SEC-52-01..12`) | **Post-Wave 5** (after `WP-SIMPL-46/47/48` stabilize) |
| **`WP-SIMPL-53`** | PR 1 (`#640`), PR 2 (`#644`), PR 3 (`#741`) | **PR 4 (`#573`)** (In flight in Wave 2 `@dev-health`) | `APPROVED WITH CONDITIONS` (`SEC-53-01..05`) | **Wave 2 (In Flight)** |

### 4.3 Post-Freeze / Deferred Modularization Packages (Stage 3 Exit)
- **`WP-SIMPL-28`** (Module `dbt` + `OpenMetadata`), **`WP-SIMPL-29`** (Module `Events` / `CloudEvents`), **`WP-SIMPL-30`** (Module `Resilience` / `FinOps`), **`WP-SIMPL-54`** (`#580` Golden queries from dbt meta, Phase 3 `APPROVED WITH CONDITIONS` `SEC-54-01..07`): Scheduled after Stage 3 feature freeze per owner decision Q26.

---

## 5. Complete Inventory of Open Issues Work Packages (`Groups 1–9`)

| Group | Plan File | Merged on `main` | Open PR / Wave 2 / Wave 3 | Remaining Open for Wave 4+ |
| :--- | :--- | :--- | :--- | :--- |
| **Group 1** | `plan-issues-security-and-ops.md` | `#469`, `#472`, `#484`, `#489`, `#491`, `#503`, `#508` (**100% Merged**) | — | **None** |
| **Group 2** | `plan-issues-sql-security-and-privacy.md` | `#473`, `#475`, `#476`, `#482`, `#483`, `#485`, `#487`, `#505 (read-only)` (**100% Merged**) | — | **None** (`#505` write-path covered by `WP-SIMPL-45 PR 4-5`) |
| **Group 3** | `plan-issues-catalog-and-governance.md` | `#667` (`70ca931b`), `#610` (`b13ec678`) | **PR #748** (`#666`), **Wave 2** (`#670` `@dev-cursor`, `#612` `@dev-tls`) | **`WP-OI-CAT-4 (#671)`** (Declarative consent prune scoped to `grant_source = 'DECLARATIVE'` — gated by **Wave 4 Brief 1 `WP-SIMPL-47 PR 1`**) |
| **Group 4** | `plan-issues-graphql-and-batching.md` | `#665` (`e08816c3`), `#443` (`f42dab64`), `#569` (`WP-SIMPL-53 PR 1-3`) | **Wave 2** (`#573` / `WP-SIMPL-53 PR 4` `@dev-health`) | **None** |
| **Group 5** | `plan-issues-sql-odata-engine.md` | `#477` (`5d05a74f`) | **Wave 2** (`#478-A` `@dev-audit`), **Wave 3** (`#486`) | **Wave 4 Brief 3 (`#479-A`)** (Oracle `MOD`/`TRUNC`, Databricks `DIV`, `IntegralOperandClassifier`), **Wave 5 (`#478-B` OData)**, **Wave 5 (`#479-B` capabilities, `#479-D` `CompileLimits`, `#479-C` DuckDB/Snowflake/BigQuery)** |
| **Group 6** | `plan-issues-catalog-search.md` | `#623` (`4886db8a`) | **PR #734** (`#624`) | **Wave 5 (`#625` synonyms, `#626` compound tokens, `#628a` interface, `#628b` hybrid RRF, `#628c` table+column vectors, `#628d` ONNX provider)** |
| **Group 7** | `plan-issues-catalog-scale.md` | `#672` (`WP-OI-SCALE-1` PR 1 `906e3664` + PR 2 `968c670e`), `#674` (`WP-OI-SCALE-3` `18f5c702`), `#676` (`WP-OI-SCALE-5` `1d10df3a`) | **Wave 3** (`#675 PR 1` `CatalogSnapshotProvider` coalescing) | **Wave 4 Brief 4 (`#673` / `WP-OI-SCALE-2`)** (`UpsertTableMetadataBatchAsync` + ingestion callers), **Wave 5 (`#675 PR 2` DDL `0015` `CATALOG_WRITE_LOG` + cursor delta loader)**, **Wave 5 (`#677` / `WP-OI-SCALE-6` paged catalog query APIs)** |
| **Group 8** | `plan-issues-ci-and-footprint.md` | `#518`, `#519`, `#520`, `#521`, `#522`, `#611`, footprint-budget scheduled wrapper, Parity `!cancelled()` | — | **`#523`** (CI run duration measurement after merges) & **`#609`** (live Databricks workspace secrets/test — owner infra action) |
| **Group 9** | `plan-issues-websql-and-metadata.md` | `#682-A` (`70bb5089`) | **PR #747** (`#668`), **PR #744** (`#669`) | **Wave 5 (`#681` `WP-OI-WEB-681` & `#682-B` `WP-OI-WEB-682-B`)** — both gated by **Wave 4 Brief 2 (`WP-SIMPL-41 PR 3`)** |
