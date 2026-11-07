# Simplification program: work package specifications

Developer-ready specifications for every work package of the [simplification plan](../2026-11-05-plan-project-simplification.md) ([PRD](../2026-11-05-req-project-simplification.md), [Phase 3 security review](../2026-11-05-sec-project-simplification.md), [ADR-019](../../adr/ADR-019-sql-engine-build-vs-buy.md), [ADR-020](../../adr/ADR-020-unified-authorization-model.md)). Owner decisions OD-1..OD-44 in the PRD (section 2.0) and ADR-020 (Accepted, OD-30) are binding; where a spec and an owner decision disagree, the owner decision wins, and a later owner decision wins over an earlier one.

Each spec has the same structure: goal, preconditions, scope, verified current state, TDD implementation steps, exact tests, verification commands, acceptance checklist, PR slicing (<= 400 net added src lines unless `size-exception`), risks and estimate. Specs were written against `docs/plans-project-simplification` @ `63cbb893` (code base `fe0e131d`); re-verify line numbers before editing. Items marked "verify" in a spec must be confirmed by the developer before Phase 4. Migration steps are never hard-coded by number: each WP that changes governance DDL uses the next free forward-only migration step at merge time (per the frozen v1.5.0 base policy).

## Execution order

| Order | Lane | Work packages | Gate before start |
|---|---|---|---|
| 0 | CI | #516 (CI hang), #517 (timeouts, hang dumps) | none - nothing else is measurable before these land |
| 0 | Security (parallel to all stages) | [WP-SIMPL-36](WP-SIMPL-36-security-lane.md) | sub-package 0 first: finish fix/security-batch-5 and align it with OD-9 |
| 1 | Stage 0 quick wins | [01](WP-SIMPL-01-remove-readytorun.md), [02](WP-SIMPL-02-version-from-git-tag.md) `[WF]`, [03](WP-SIMPL-03-remove-performance-claims.md), [04](WP-SIMPL-04-docs-nonexistent-components.md), [05](WP-SIMPL-05-docs-hygiene-checks.md), [06](WP-SIMPL-06-unconsumed-options-config-reference.md) | #516/#517 |
| 1 | Stage 0 program | [07](WP-SIMPL-07-freeze-ratchets.md) `[WF]`, [08](WP-SIMPL-08-codeowners-identity.md), [09](WP-SIMPL-09-agent-definitions-cleanup.md) (after 03), [10](WP-SIMPL-10-measurement-baselines.md) (PR 1 before 01; PR 2 after #516, #517, 01) | |
| 2 | Stage 1 persistence | [11](WP-SIMPL-11-drop-sqlite-governance-store.md), then [12](WP-SIMPL-12-delete-catalog-connectors-backstage-itsm.md) | #520 (test-host reuse) merged; #504 (security trait) merged |
| 2 | Stage 1 deletions | [13](WP-SIMPL-13-connection-secret-policy-dialect-admission.md), [15](WP-SIMPL-15-delete-garnet-and-traffic-shadowing.md), [16](WP-SIMPL-16-delete-flight-sql-finops-mesh-devportal.md), [17](WP-SIMPL-17-merge-duplicate-folders.md) (after 12), [18](WP-SIMPL-18-borderline-triage-execution.md). [14](WP-SIMPL-14-delete-casbin.md) moved to the core authorization lane (after WP-46) | #504 merged (no security test may be lost unaccounted) |
| 3 | Stage 2 structure | [19](WP-SIMPL-19-governance-repository-contract-suite.md) (after 11, 12), then [20](WP-SIMPL-20-unified-governance-repository.md) (serial 20a-20e), [21](WP-SIMPL-21-parity-real-engines-dialect-gate.md) `[WF]`, [22](WP-SIMPL-22-real-db-tests-out-of-unit.md), [23](WP-SIMPL-23-test-suite-restructuring-ratchets.md), [24](WP-SIMPL-24-application-folder-consolidation.md) (after 12, 15, 16, 17) | Stage 1 exit |
| 4 | Stage 3 modules | [25](WP-SIMPL-25-module-host.md) `[WF]` (signing scheme reviewed by Phase 3 first; #503 first), [27](WP-SIMPL-27-module-lakehouse.md) (pilot; runtime vs compile-time loading decided here, OD-37), [28](WP-SIMPL-28-module-dbt-openmetadata.md) (after 27, 44), [29](WP-SIMPL-29-module-wave-c-events-lineage.md) (after 27, 20 and 26; removes the Api -> Extensions reference). [30](WP-SIMPL-30-module-wave-d-identity-edge.md) is cancelled (OD-21) | Stage 2 exit; WP-46 PR 5 merged (no `RebacEndpointFilter` left for module route groups) |
| 4 | Stage 3 cleanup | [31](WP-SIMPL-31-full-sqlite-removal-step-3.md), [32](WP-SIMPL-32-configuration-surface-reduction.md), [33](WP-SIMPL-33-plan-id-comment-cleanup.md), [34](WP-SIMPL-34-readme-feature-docs-migration-guide.md), [35](WP-SIMPL-35-footprint-budget-job.md) `[WF]` | |
| any | Feature (engine lane) | [37](WP-SIMPL-37-databricks-auth-modes.md) Databricks auth modes (OD-16) and Databricks tier flip on WebSQL, Trino and MCP (OD-36) | security lane sub-package 0 |
| 2-3 | Core features (OD-18) | [38](WP-SIMPL-38-trino-protocol-clean.md) Trino `/v1/statement`; [39](WP-SIMPL-39-procedures-clean.md) procedures | none blocking; 38 step 6 (metadata access status) after WP-46 PR 1 and the WP-41 discovery service (`DecideManyAsync`, R8) |
| 2-3 | Core features (OD-18) | [40](WP-SIMPL-40-docs-ingestion-clean.md) docs ingestion: PR 40a | security lane sub-package 0 (rebase if not merged) |
| 3 | Persistence lane | 40: PR 40b, then 40c, then 40d | 40b: WP-11 and WP-20a-20e merged |
| 2 | Core features (OD-25) | [44](WP-SIMPL-44-no-source-metadata-reads.md) no source-metadata reads | WP-12, WP-17, WP-39 PR 3 merged; must merge before WP-28 |
| 2 | Core authorization 1 | [43](WP-SIMPL-43-authentication-authorization-core.md) PR 1, 2, 3a, 3b (canonical effective group SIDs); [45](WP-SIMPL-45-governed-write-access.md) PR 1-3 (write-rule model, evaluator, engine permit; no decision logic outside the decision point: the evaluator has no production call site before WP-46 PR 1 and the rewriter denies every DML statement until WP-46 PR 2; DML stays dark) | 43: security lane sub-packages 0 and 1 (#469), GHSA-hw85-rqjh-cwxx fix before PR 2. 45: security lane sub-packages 0 and 3 |
| 2 | Core authorization 2 | [46](WP-SIMPL-46-unified-authorization-model.md) PR 1 (decision point `IAccessDecisionPoint`; decision record lists every contributing grant id, OD-42) | WP-43 PR 2, 3a, 3b; WP-45 PR 1-3; security lane sub-package 0; ADR-020 accepted (OD-30) |
| 2 | Core authorization 3 | 46 PR 2-5 (re-route transports incl. the WP-45 write decision, bypass paths incl. Envoy and the MCP guardrail pre-checks, explain, ReBAC removal); 46 PR 6 (parity matrix) | WP-46 PR 1; PR 6 after WP-43 PR 5a/5b; not in parallel with WP-45 PR 4, WP-39 PR 1/4, WP-44 PR 3, WP-42 PR-3 (shared files) |
| 2 | Core authorization 3 | 43 PR 4 (Envoy ext_authz step 11), PR 5a, 5b, 5c | PR 4: WP-46 PR 3. PR 5a: WP-11. PR 5c: security lane sub-package 2 |
| 2 | Core authorization 3 | 45 PR 4-6 (calls the decision point only; WebSQL only, OD-35) | WP-46 PR 1; PR 4 after WP-43 PR 3; PR 5 after the WP-41 discovery DTO PR |
| 2 | Core authorization 3 | [41](WP-SIMPL-41-authorized-data-discovery.md) authorized discovery | PR 1-2: security lane sub-package 0. PR 3+: WP-46 PR 1 and WP-43 PR 3b. PR 4: WP-39 PR 4. PR 5 (GraphQL/MCP request access) needs no WP-47 PR; the REST request route is delivered later by WP-47 PR 5 (WP-47 depends on WP-41 PR 5, no cycle). PR 6: WP-40 PR 40c |
| 2 | Core authorization 3 | [48](WP-SIMPL-48-row-policies-dynamic-coverage.md) row policies, dynamic object coverage, per-cell column state, effective-access report (OD-38..OD-43) | PR 1, 2, 5: WP-46 PR 1. PR 3, 4: WP-46 PR 2. PR 7 only if WP-46 PR 6 lacks S20..S27; not in parallel with WP-46 PR 3-5 or WP-45 PR 4 (shared files) |
| 2 | Core authorization 3 | [47](WP-SIMPL-47-external-catalog-and-consent-api.md) catalog and consent API, grant records | WP-12; WP-43; WP-46 PR 1; WP-41 PR 3 and PR 5; WP-45 PR 1-3; PR 7: WP-48 PR 5 and 6 |
| 3 | Core authorization 4 | [14](WP-SIMPL-14-delete-casbin.md) Casbin deletion | WP-06; WP-46 PR 1-5; WP-43 PR 4 (Envoy); WP-45 PR 1-3 (every `IPolicyEnforcementService` consumer incl. Envoy ext_authz calls the decision point); not in parallel with WP-46 or WP-45 PR 4 |
| 3 | Core authorization 4 | [26](WP-SIMPL-26-pilot-module-odata.md) OData core hardening (OD-24) | WP-46 merged; must merge before WP-29 |
| 3 | Persistence lane | [42](WP-SIMPL-42-usage-accounting-users-groups.md) usage accounting (replaces FinOps) | PR-1 any time; PR-2+ after WP-11, WP-19, WP-20a-20d; PR-3b after WP-46 PR 1 (grant id on the decision record); PR-7 after WP-37 |
| 5 | After the feature freeze (Stage 3 exit) | [49](WP-SIMPL-49-data-products.md) data products: materialized and live views, stored procedures with their own product owner (OD-44) | WP-46, 47, 48, 41, 42, 44, 21, 20 merged; not under `freeze-exception` |

`[WF]` = changes GitHub workflows; approved by the owner in the defined bundles (OD-17).

## Freeze exception

The freeze ratchets of [WP-SIMPL-07](WP-SIMPL-07-freeze-ratchets.md) (endpoints, options, `*Enabled` flags, Application folders, plan-id comments) may grow only when the same PR updates the committed baseline and carries the label `freeze-exception`. The label applies to:
- security PRs (GHSA/CVE fixes, fail-closed regressions);
- the owner-mandated work packages WP-SIMPL-38, 39, 40, 41, 42, 43, 44, 45, 46, 47 and 48 (OD-18..OD-43).

The CI check reads the label from the PR event; the architecture test reads the committed baseline. Any other growth requires an owner decision recorded in the PR description.

## Corrections found while writing the specs (supersede the plan text)

- **Catalog search embeddings stay (OD-4):** WP-18 PR G is removed (override note in [WP-18](WP-SIMPL-18-borderline-triage-execution.md)). `LocalOnnxEmbeddingGenerator` has no ONNX package; the plan's "native weight" argument does not apply.
- **WP-14 Casbin:** deleted without a data-path replacement (OD-30, ADR-020 decision 7), after WP-46 has moved every `IPolicyEnforcementService` consumer (incl. Envoy ext_authz) to the access decision point. No native policy engine, no ReBAC gate and no role-based (`DmlWriterRoles`) write check; no `size-exception` needed.
- **WP-11 test migration is larger:** 145 Unit + 72 Integration files use the SQLite governance store (plan: 24 + 10); +1 person-day. SQL Server fixtures cannot clone a template DB (per-class migration).
- **WP-17:** `Application/Dbt` and `Application/OpenMetadata` hold 17 types used by core layers; they move to `Application/Catalog`, net LOC ~0 (plan: -500 to -1,000).
- **WP-25/27:** Parquet.Net stays in core (used by Parquet export/GraphQL/WebSQL); YamlDotNet stays in core (Procedures). `ModuleEndpointRegistrar` replaces the `IEndpointRouteBuilder` parameter of AD-3.1. The `Autheris.Api -> Autheris.Extensions` project reference is removed by WP-SIMPL-29 (last module WP), after WP-SIMPL-26 has moved OData out of `Autheris.Extensions` into core.
- **WP-06:** `ScimOptions.AllowedIpRanges` is enforced and stays; `CasbinOptions.EnforceInQueryPipeline` and `McpOptions.RequirePiiMasking` are already removed. The baseline test is `OptionsConsumptionInventoryTests` (45 entries); 14 of them are read in their declaring file (false positives) and are removed from the baseline, not deleted; WP-06 deletes only options verified unconsumed.
- **WP-37:** PAT is rejected outside Development in three places (incl. `GatewayStartupValidator.ValidateDatabricks`); the token cache key has no tenant or mode.
- **WP-07/10:** a broad grep finds 305 `Map*` calls (plan: 275); the ratchet regex is the baseline. WP-33 counts 684 plan-id comment lines with the narrow regex (PRD estimate ~1,860).
- **WP-24:** WP-16 deletes only Flight-over-HTTP and the dev portal files (no top-level folder); FinOps becomes `Application/Usage` (WP-42), Mesh, Docs and Procedures stay. Folder count: 42 before WP-24, 31 after its merges, which meets <= 32 with one folder of slack. Any new top-level folder from WP-38..48 must be placed under an existing folder.

## Kept by owner decision OD-18 (redesigned cleanly instead of deleted)

- [WP-SIMPL-38](WP-SIMPL-38-trino-protocol-clean.md) Trino client protocol `/v1/statement` (#470, #484).
- [WP-SIMPL-39](WP-SIMPL-39-procedures-clean.md) Governed stored procedures.
- [WP-SIMPL-40](WP-SIMPL-40-docs-ingestion-clean.md) Docs ingestion and MCP doc tools (GHSA-p234-g3ph-gqh2).

WP-SIMPL-18 therefore only deletes differential-privacy noise, CDN purge and SchemaRegistry (PRs A, B, E). WP-38..40 run in Stage 1/2 alongside the security lane; WP-40's storage move (PR 40b) depends on WP-SIMPL-20.

## Added by owner decisions OD-19..OD-31

- [WP-SIMPL-41](WP-SIMPL-41-authorized-data-discovery.md) Authorized data discovery for every authenticated user (OD-19; replaces anonymous OpenSchema; separate column-level requests for PII). Visibility: everything visible to authenticated users unless explicitly hidden; hidden objects only for admins and the data owner. Access status comes from `IAccessDecisionPoint.DecideManyAsync`; consent requests use the WP-SIMPL-47 API.
- [WP-SIMPL-42](WP-SIMPL-42-usage-accounting-users-groups.md) Usage accounting per user and AD group (OD-20; replaces FinOps); usage is attributed to every contributing grant id of the decision record, each counted in full (OD-42).
- [WP-SIMPL-43](WP-SIMPL-43-authentication-authorization-core.md) Authentication and authorization core (OD-21, OD-27): direct and upstream-header (ForwardAuth) modes, SCIM groups; WP-SIMPL-30 is cancelled, Envoy ext_authz stays core.
- [WP-SIMPL-44](WP-SIMPL-44-no-source-metadata-reads.md) No source-metadata reads (OD-25): only data access on sources; schema, keys, indexes, samples and statistics come from outside; folds in #459.
- [WP-SIMPL-45](WP-SIMPL-45-governed-write-access.md) Governed write access (OD-26): INSERT and column-level UPDATE per user/AD group via data-owner consent, as grant records with validity; supersedes the DML-bundle design of WP-SIMPL-36.
- [WP-SIMPL-46](WP-SIMPL-46-unified-authorization-model.md) Unified authorization model per [ADR-020](../../adr/ADR-020-unified-authorization-model.md) (Accepted, OD-30): one grant model and one decision point for all transports, discovery and Envoy; plugin auth hooks narrowing only (OD-31).
- [WP-SIMPL-47](WP-SIMPL-47-external-catalog-and-consent-api.md) Catalog and consent API (single owner of the consent request REST route), workflow internal or external via webhook, signed time-limited grants with verification (OD-28, OD-29).
- OD-22 core access model and OD-23 heterogeneous data gateway are recorded in the PRD and bind every WP.
- OD-24: OData stays core; [WP-SIMPL-26](WP-SIMPL-26-pilot-module-odata.md) becomes "OData core hardening": move OData from `src/Autheris.Extensions/OData` into a core project, Power BI / Excel interop tests, OD-22 access-model controls on OData. The module pilot is Lakehouse ([WP-SIMPL-27](WP-SIMPL-27-module-lakehouse.md)), which also takes the runtime-versus-compile-time loading decision.

## Owner decisions OD-32..OD-37 (2026-11-05)

- **OD-32:** assigning an access profile to a user or AD group runs the consent approval path and, on approval, materializes time-limited grant records (revised by OD-40: one record per profile item, patterns matched at decision time); a profile never grants by itself; roles are no longer valid assignees; break-glass assignments are removed. Affects WP-41, 45, 46 (D-46b, SEC-AZ-04) and 47.
- **OD-33:** write grants are independent of read grants; UPDATE "own rows" applies the caller's row-level-security predicate; WP-46 does not require a read allow for writes.
- **OD-34:** data owners read their own data only through an automatically created, auditable owner grant record (counted in usage under its grant id); no implicit owner access, no `basis:owner`.
- **OD-35:** write enforcement sits in the shared pipeline; WP-45 wires WebSQL only; OData, GraphQL and REST reject writes fail-closed until a later, separately approved WP wires them.
- **OD-36:** the Databricks tier flip (X1-DBX: WebSQL, Trino protocol and MCP, with parity tests) is part of WP-37.
- **OD-37:** dev portal deleted (WP-16 wins over WP-41); footprint image budget = plan section 3 value (325 MB); CI target = PR median < 30 min incl. queue (enforced by WP-35); images pushed as `vX.Y.Z` and `X.Y.Z`; runtime-vs-compile-time module loading decided in WP-27; denies assigned to SCIM-only groups always apply (`DropScimGroups` never drops a group carrying a deny, SEC-AZ-09).

## Owner decisions OD-38..OD-43 (2026-11-06)

New work package [WP-SIMPL-48](WP-SIMPL-48-row-policies-dynamic-coverage.md); amendments to WP-41, 42, 45, 46 and 47, ADR-020 and the sec plan (SEC-AZ-15..19).
- **OD-38:** named row policies (the existing virtual-filter definitions) with typed parameters, composite keys compared pair-wise, `Live` or `KeySet` per policy (`KeySet` mandatory across data sources); SQL policies only with approval by the owners of every table they read; grants carry only policy references with parameter values or simple column filters; free SQL in grants and profiles is removed.
- **OD-39:** two levels: grant row scopes ORed across grants (a filtered and an unfiltered grant of the same user yield all rows, by intent); hard row policies assigned to users or AD groups, ANDed after the union, not bypassable; uncovered objects `Deny` by default, `Skip` explicitly.
- **OD-40:** grant object patterns are evaluated at decision time and cover objects created later (revises OD-32); the data source is fixed in the pattern; PII without explicit Clear is Masked; internal mode covers only objects of approving owners; catalog epoch in the decision cache key.
- **OD-41:** per-cell column state when overlapping grants differ in columns or masking: one signature compiles to a plain `WHERE`, several signatures to flags computed once plus `CASE`; no `UNION ALL`.
- **OD-42:** the decision record lists every contributing grant; audit and usage count the access for each grant in full, user/group/tenant totals once (revises R6).
- **OD-43:** non-blocking approval-time hint (redundant grant, empty intersection, future objects included) and an effective-access report with provenance.

## Owner decision OD-44 (2026-11-06, revised)

- **OD-44 (revised 2026-11-06):** data products: views (two-stage DuckDB execution; load modes schedule, onDemand(ttl), live) and stored procedures (declared read or write) defined in Autheris, each with its own product owner who decides all grants; base owners are decoupled (notified, no approval). Created by data owners or delegates over own objects, data-source owners over their source, the new role `DataProductAdmin` (implied by no other role, never product owner of its own creations) or Git sync (`DataProductSync`). Product grants suffice; base-table rules do not apply. Planned now, built after the feature freeze: [WP-SIMPL-49](WP-SIMPL-49-data-products.md).

## Security advisories

Neutral index of all referenced advisories with component, severity, state and work package: [advisories-overview.md](advisories-overview.md).

## Not committed

Root cause, fix approach and test notes for the open advisories are stored in the private GitHub advisories (section "Implementation notes (WP-SIMPL-36)"); never copy them into the repository (SEC-SIMPL-01).
