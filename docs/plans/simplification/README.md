# Simplification program: work package specifications

Developer-ready specifications for every work package of the [simplification plan](../2026-11-05-plan-project-simplification.md) ([PRD](../2026-11-05-req-project-simplification.md), [Phase 3 security review](../2026-11-05-sec-project-simplification.md), [ADR-019](../../adr/ADR-019-sql-engine-build-vs-buy.md)). Owner decisions OD-1..OD-17 in the PRD are binding; where a spec and an owner decision disagree, the owner decision wins.

Each spec has the same structure: goal, preconditions, scope, verified current state, TDD implementation steps, exact tests, verification commands, acceptance checklist, PR slicing (<= 400 net added src lines unless `size-exception`), risks and estimate. Specs were written against `docs/plans-project-simplification` @ `63cbb893` (code base `fe0e131d`); re-verify line numbers before editing. Items marked "verify" in a spec must be confirmed by the developer before Phase 4.

## Execution order

| Order | Lane | Work packages | Gate before start |
|---|---|---|---|
| 0 | CI | #516 (CI hang), #517 (timeouts, hang dumps) | none - nothing else is measurable before these land |
| 0 | Security (parallel to all stages) | [WP-SIMPL-36](WP-SIMPL-36-security-lane.md) | sub-package 0 first: finish fix/security-batch-5 and align it with OD-9 |
| 1 | Stage 0 quick wins | [01](WP-SIMPL-01-remove-readytorun.md), [02](WP-SIMPL-02-version-from-git-tag.md) `[WF]`, [03](WP-SIMPL-03-remove-performance-claims.md), [04](WP-SIMPL-04-docs-nonexistent-components.md), [05](WP-SIMPL-05-docs-hygiene-checks.md), [06](WP-SIMPL-06-unconsumed-options-config-reference.md) | #516/#517 |
| 1 | Stage 0 program | [07](WP-SIMPL-07-freeze-ratchets.md) `[WF]`, [08](WP-SIMPL-08-codeowners-identity.md), [09](WP-SIMPL-09-agent-definitions-cleanup.md) (after 03), [10](WP-SIMPL-10-measurement-baselines.md) (after #516, #517, 01) | |
| 2 | Stage 1 persistence | [19](WP-SIMPL-19-governance-repository-contract-suite.md), then [11](WP-SIMPL-11-drop-sqlite-governance-store.md) | #520 (test-host reuse) merged; #504 (security trait) merged |
| 2 | Stage 1 deletions | [12](WP-SIMPL-12-delete-catalog-connectors-backstage-itsm.md), [13](WP-SIMPL-13-connection-secret-policy-dialect-admission.md), [14](WP-SIMPL-14-delete-casbin.md), [15](WP-SIMPL-15-delete-garnet-and-traffic-shadowing.md), [16](WP-SIMPL-16-delete-flight-sql-finops-mesh-devportal.md), [17](WP-SIMPL-17-merge-duplicate-folders.md), [18](WP-SIMPL-18-borderline-triage-execution.md) | #504 merged (no security test may be lost unaccounted) |
| 3 | Stage 2 structure | [20](WP-SIMPL-20-unified-governance-repository.md) (serial 20a-20e), [21](WP-SIMPL-21-parity-real-engines-dialect-gate.md) `[WF]`, [22](WP-SIMPL-22-real-db-tests-out-of-unit.md), [23](WP-SIMPL-23-test-suite-restructuring-ratchets.md), [24](WP-SIMPL-24-application-folder-consolidation.md) (after 12, 15, 16, 17) | Stage 1 exit |
| 4 | Stage 3 modules | [25](WP-SIMPL-25-module-host.md) `[WF]` (signing scheme reviewed by Phase 3 first; #503 first), [26](WP-SIMPL-26-pilot-module-odata.md), [27](WP-SIMPL-27-module-lakehouse.md), [28](WP-SIMPL-28-module-dbt-openmetadata.md), [29](WP-SIMPL-29-module-wave-c-events-lineage.md), [30](WP-SIMPL-30-module-wave-d-identity-edge.md) | Stage 2 exit |
| 4 | Stage 3 cleanup | [31](WP-SIMPL-31-full-sqlite-removal-step-3.md), [32](WP-SIMPL-32-configuration-surface-reduction.md), [33](WP-SIMPL-33-plan-id-comment-cleanup.md), [34](WP-SIMPL-34-readme-feature-docs-migration-guide.md), [35](WP-SIMPL-35-footprint-budget-job.md) `[WF]` | |
| any | Feature | [37](WP-SIMPL-37-databricks-auth-modes.md) Databricks auth modes (OD-16) | security lane sub-package 0 |

`[WF]` = changes GitHub workflows; approved by the owner in the defined bundles (OD-17).

## Corrections found while writing the specs (supersede the plan text)

- **Catalog search embeddings stay (OD-4):** WP-18 PR G is removed (override note in [WP-18](WP-SIMPL-18-borderline-triage-execution.md)). `LocalOnnxEmbeddingGenerator` has no ONNX package; the plan's "native weight" argument does not apply.
- **WP-14 Casbin is a policy-engine replacement:** `CasbinEnforcementService` is the only `IPolicyEnforcementService` (~2,264 lines); estimate 2 -> 4 person-days, middle PR needs `size-exception`; ReBAC (OD-7) test must be green first.
- **WP-11 test migration is larger:** 145 Unit + 72 Integration files use the SQLite governance store (plan: 24 + 10); +1 person-day. SQL Server fixtures cannot clone a template DB (per-class migration).
- **WP-17:** `Application/Dbt` and `Application/OpenMetadata` hold 17 types used by core layers; they move to `Application/Catalog`, net LOC ~0 (plan: -500 to -1,000).
- **WP-25/27:** Parquet.Net stays in core (used by Parquet export/GraphQL/WebSQL); YamlDotNet stays in core (Procedures). `ModuleEndpointRegistrar` replaces the `IEndpointRouteBuilder` parameter of AD-3.1. The Api -> Extensions reference is removed only in WP-30.
- **WP-06:** `ScimOptions.AllowedIpRanges` is enforced and stays; `CasbinOptions.EnforceInQueryPipeline` and `McpOptions.RequirePiiMasking` are already removed. The baseline test is `OptionsConsumptionInventoryTests` (45 entries).
- **WP-37:** PAT is rejected outside Development in three places (incl. `GatewayStartupValidator.ValidateDatabricks`); the token cache key has no tenant or mode.
- **WP-07/10:** a broad grep finds 305 `Map*` calls (plan: 275); the ratchet regex is the baseline. WP-33 counts 684 plan-id comment lines with the narrow regex (PRD estimate ~1,860).
- **WP-24:** the <= 32 folder target holds only if WP-12, 15, 16 and 17 merge first.

## Kept by owner decision OD-18 (redesigned cleanly instead of deleted)

- [WP-SIMPL-38](WP-SIMPL-38-trino-protocol-clean.md) Trino client protocol `/v1/statement` (#470, #484).
- [WP-SIMPL-39](WP-SIMPL-39-procedures-clean.md) Governed stored procedures.
- [WP-SIMPL-40](WP-SIMPL-40-docs-ingestion-clean.md) Docs ingestion and MCP doc tools (GHSA-p234-g3ph-gqh2).

WP-SIMPL-18 therefore only deletes differential-privacy noise, CDN purge and SchemaRegistry (PRs A, B, E). WP-38..40 run in Stage 1/2 alongside the security lane; WP-40's storage move depends on WP-SIMPL-20.

## Added by owner decisions OD-19..OD-24

- [WP-SIMPL-44](WP-SIMPL-44-no-source-metadata-reads.md) No source-metadata reads (OD-25): only data access on sources; schema, keys, indexes, samples and statistics come from outside; folds in #459.
- [WP-SIMPL-45](WP-SIMPL-45-governed-write-access.md) Governed write access (OD-26): INSERT and column-level UPDATE per user/AD group via data-owner consent; supersedes the DML-bundle design of WP-SIMPL-36.
- [WP-SIMPL-47](WP-SIMPL-47-external-catalog-and-consent-api.md) Catalog and consent API, workflow internal or external via webhook, signed time-limited grants with verification (OD-28, OD-29).
- OD-22 core access model and OD-23 heterogeneous data gateway are recorded in the PRD and bind every WP.
- OD-24: OData stays core; [WP-SIMPL-26](WP-SIMPL-26-pilot-module-odata.md) is reduced to cleanup and Power BI / Excel interop tests; the module pilot is Lakehouse ([WP-SIMPL-27](WP-SIMPL-27-module-lakehouse.md)).


- [WP-SIMPL-41](WP-SIMPL-41-authorized-data-discovery.md) Authorized data discovery for every authenticated user (replaces anonymous OpenSchema; consent request to the data owner; separate column-level requests for PII).
- [WP-SIMPL-42](WP-SIMPL-42-usage-accounting-users-groups.md) Usage accounting per user and AD group (replaces FinOps).
- [WP-SIMPL-43](WP-SIMPL-43-authentication-authorization-core.md) Authentication and authorization core: direct and upstream-header (ForwardAuth) modes; WP-SIMPL-30 is cancelled, Envoy ext_authz stays core.

WP-41..43 are core product requirements and run under the freeze exception alongside the security lane.

## Security advisories

Neutral index of all referenced advisories with component, severity, state and work package: [advisories-overview.md](advisories-overview.md).

## Not committed

Root cause, fix approach and test notes for the open advisories are stored in the private GitHub advisories (section "Implementation notes (WP-SIMPL-36)"); never copy them into the repository (SEC-SIMPL-01).
