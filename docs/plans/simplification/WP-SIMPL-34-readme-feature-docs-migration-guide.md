# WP-SIMPL-34 README rewrite, feature docs, migration guide completion
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-34-readme-rewrite-feature-docs-migration-guide-completion) | Issues: #515, #511 | Owner decisions: OD-1..OD-37 in PRD section 2.0 are binding (later OD wins); in particular OD-17(6) (freeze exit), OD-12/OD-15 (what stays core), OD-18 (Trino protocol, procedures, docs ingestion core), OD-21 (authentication and authorization core), OD-24 (OData core), OD-29 (product core statement), OD-30 (ReBAC and Casbin deleted), OD-31 (customer DLL plugins stay) | Security conditions: none specific; docs must not describe advisory details (GHSA id + neutral title only)

> **Owner addendum (OD-29, 2026-11-05):** the README opens with the product core statement of OD-29 (seven points: optional identity, authorization on data as the core, discovery, external approval workflow, cryptographic proof with time-limited grants, usage and per-source cost models, external metadata) and structures the rest of the document along it.

## 1. Goal and outcome
README.md describes the simplified product per PRD REQ-SIMPL-11.5; `docs/features/` holds at most 45 feature docs (deleted features lose their docs, module features move to module READMEs); `docs/operations/migration-simplification.md` has one row per DELETE/MODULE item; architecture docs describe the core + module model. Measurable: doc count <= 45, all `DocsIntegrityTests`/`DocsClaimsArchitectureTests` green, owner accepts README (freeze exit criterion).

## 2. Preconditions and dependencies
- WP-SIMPL-33 merged (plan-id comment cleanup), and all DELETE/MODULE WPs (Stage 1-3) merged so the inventory is final.
- WP-SIMPL-09 doc tests (`DocsIntegrityTests`) and WP-SIMPL-03 (`DocsClaimsArchitectureTests`) exist.
- Parallel with WP-SIMPL-35 (README links the results file it produces; use the WP-SIMPL-10 baseline file if 35 is not merged yet).

## 3. Scope
### In scope
- Rewrite `README.md` (826 lines today) to <= ~250 lines.
- Prune/move `docs/features/*.md` and `docs/features/README.md` index.
- Complete `docs/operations/migration-simplification.md` (created by deleting WPs).
- Update `docs/architecture/arc42.md` and `docs/architecture/autheris-overview.svg` caption/text to the module model; `docs/configuration-guide.md` and `docs/developer-guide.md` remove deleted keys/projects.
### Out of scope
- New feature docs (owner rule: only on request).
- `docs/plans/`, ADR content, CHANGELOG (WP-SIMPL-11), code changes.

## 4. Current state (verified on 63cbb893)
- `README.md`: 826 lines; headings with emojis (`:37 What Autheris Does`, `:52 Unrivaled Performance`, `:66 Complete Enterprise Feature Inventory` with Pillars 1-7 at `:70-276`), architecture `:309-374`, Quick Start `:393`, auth `:475`.
- `docs/features/`: 95 files besides README.md (target <= 45).
- `docs/operations/` does not exist yet (created by the first deleting WP per plan section 6).
- `docs/architecture/`: `arc42.md`, `autheris-overview.svg`, `konzept-data-governance-und-mcp-documentation.md` (German file name; content language to be checked by `DocsIntegrityTests.Docs_are_english`).

## 5. Implementation steps
1. Red: run `DocsIntegrityTests` and add `DocsInventoryTests.Feature_doc_count_is_at_most_45` and `DocsInventoryTests.Readme_has_required_sections` (fails on current README).
2. Build the feature-doc map: for each `docs/features/f-*.md` classify KEEP / DELETE / MODULE using the classification list of section 5.1 below (derived from PRD section 2.0 OD-1..OD-37, later OD wins; do not classify from the plan section 5 table). An item not covered by section 5.1 is classified by the owning WP's spec and listed in the PR for owner review. Delete DELETE docs; move MODULE docs to `modules/<Module>/README.md` (merge, keep config + architecture sections); merge KEEP docs that cover the same capability until <= 45. Update `docs/features/README.md` index.
3. README rewrite, sections in order: title + one positioning sentence; Core capabilities (<= 12 bullets); Transports: GraphQL, REST (incl. Web SQL and Arrow IPC export), OData (Power BI/Excel, OD-24), Trino client protocol `/v1/statement` (OD-18), MCP (OD-29(2)); Arrow Flight SQL over HTTP and PG wire are not offered; Modules (table: module, purpose, install line `Gateway:Modules:Enabled` / image `autheris-full`); Quick start (container + compose, existing commands verified); Authentication (short, link to configuration guide); Project status ("not yet production-hardened"; deprecation policy text from PRD section 6.1); Benchmarks (only linked numbers into `benchmarks/results/` or `benchmarks/protocol/`); Contributing/License. No emojis, no feature inventory, no plan IDs.
4. Migration guide: verify one row per DELETE/MODULE item of section 5.1 (columns: item, removed config keys, UnknownGatewayKeyGuard message, replacement, module install). Add missing rows. KEEP items get no row unless a config key or route of theirs was removed or renamed.
5. Architecture docs: `arc42.md` building-block view = core + modules; remove deleted components; translate/rename `konzept-...md` if the English check flags it (rename to English slug, fix links).
6. Green; fix all link failures reported by `DocsIntegrityTests`.

### 5.1 Classification list (binding for steps 2-4)
- **KEEP (core):**
  - Transports: GraphQL; REST (incl. Web SQL and Arrow IPC export); OData (OD-24); Trino client protocol `/v1/statement` (OD-18); MCP incl. the MCP doc tools.
  - Authentication: OIDC/JWT (Entra, AD FS), Basic, Negotiate (Kerberos/NTLM), TOTP, SCIM (incl. on-premises AD groups, OD-27), ForwardAuth, Envoy ext_authz as a core decision endpoint (OD-21).
  - Authorization and governance: consent and access-profile grants evaluated by one access decision point (OD-30, OD-32), row-level security, PII masking, column-level security (OD-22), governed write grants (OD-26, OD-33), consent/catalog API (OD-28), authorized data discovery (OD-19), audit, GDPR disclosure report (OD-10), customer DLL plugins with narrowing-only hooks (OD-31).
  - Data: Oracle, Databricks (incl. its three auth modes, OD-16), SQL Server, PostgreSQL data sources (OD-12); declarative HTTP/API sources (OD-23); cross-source federation, Olap and DuckDB (OD-15); governed aggregation with small-group suppression (OD-11); governed stored procedures and docs ingestion (OD-18); catalog search incl. semantic search and embeddings (OD-4).
  - Usage accounting per user and AD group (OD-20, WP-SIMPL-42); the former FinOps docs are replaced by it (merged, not kept as FinOps). "Mesh" docs survive only as the Envoy ext_authz decision endpoint (WP-SIMPL-43/46).
- **MODULE:** Lakehouse (Delta/Iceberg, WP-SIMPL-27); dbt and OpenMetadata (WP-SIMPL-28); CDC/Streaming and event webhooks, Lineage impact analysis and OpenLineage export (WP-SIMPL-29). The workflow/consent webhook stays core (OD-29, OD-31).
- **DELETE:** ReBAC and Casbin (OD-30); Arrow Flight SQL over HTTP and PG wire; FinOps cost accounting as a separate feature (replaced, OD-20); the dev portal (OD-37); Snowflake generator (OD-14); SQLite governance store (OD-1) and SQLite data source (OD-2); embedded Garnet (OD-17(1)); traffic shadowing; Purview, Collibra, Alation, DataHub connectors; Backstage; ITSM integration and break-glass flows (OD-5, OD-32; replaced by the consent API, OD-28); differential-privacy noise, CDN cache purge, SchemaRegistry (OD-11, WP-SIMPL-18); the anonymous OpenSchema flag (replaced by discovery, OD-19).

## 6. Tests (tests/Autheris.Tests.Architecture)
- New `DocsInventoryTests.Feature_doc_count_is_at_most_45` (count `docs/features/f-*.md`).
- New `DocsInventoryTests.Readme_has_required_sections` (headings: Core capabilities, Transports, Modules, Quick start, Project status; no emoji code points; no "Feature Inventory").
- New `DocsInventoryTests.Migration_guide_lists_every_removed_config_key` (every key in the removed-keys list used by `UnknownGatewayKeyGuard` appears in `docs/operations/migration-simplification.md`).
- Existing must stay green: `DocsIntegrityTests.No_absolute_file_links`, `.Relative_links_resolve`, `.Backticked_source_paths_exist`, `.No_status_tags_outside_plans`, `.Docs_are_english`; `DocsClaimsArchitectureTests.Performance_figures_link_a_results_file`.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build --filter "FullyQualifiedName~Docs"`
- `ls docs/features/f-*.md | wc -l` -> <= 45
- `grep -nP '[\x{1F300}-\x{1FAFF}\x{2600}-\x{27BF}]' README.md` -> empty

## 8. Acceptance criteria
- [ ] README follows REQ-SIMPL-11.5; no unlinked numbers, no emojis, no inventory.
- [ ] <= 45 feature docs; index matches files; module docs live in module READMEs.
- [ ] Migration guide has a row for every DELETE/MODULE item of section 5.1 and every removed config key; no KEEP item of section 5.1 is described as removed or as a module.
- [ ] README lists the five transport families of step 3 (GraphQL, REST incl. Web SQL, OData, Trino `/v1/statement`, MCP).
- [ ] arc42 reflects core + modules; all doc tests green.
- [ ] Owner accepts README (recorded in the PR review).

## 9. PR slicing
1. PR A: feature-doc pruning/moves + index + `DocsInventoryTests` count test.
2. PR B: migration guide completion + architecture docs + key test.
3. PR C: README rewrite + section test (owner review).
No src lines; each PR docs/tests only.

## 10. Risks, rollback, and things the developer must NOT do
- Do not add progress/status text outside `docs/plans/`; do not write new feature docs.
- Do not cite performance numbers without a results link; do not describe advisory details.
- Do not delete docs of KEEP features to hit the count; merge instead.
- Rollback: revert the docs PRs.

## 11. Estimate
Docs: about -6,000 / +1,500 lines; tests +120; 3 person-days.

## Decisions taken in this spec
- README target length ~250 lines (PRD gives no number).
- KEEP docs are merged, not deleted, to reach <= 45.
- Inventory/section checks are added as architecture tests so the freeze exit is objectively checkable.
- Classification comes from the explicit list in section 5.1 (OD-1..OD-37), not from the plan section 5 table, so this WP does not depend on that table being current.
