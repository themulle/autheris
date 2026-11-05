# Security advisories: overview for the simplification program

Neutral index of the repository security advisories referenced by the simplification plan. Disclosure rule (SEC-SIMPL-01): advisories are named only by GHSA id and a neutral component title. Root cause, fix approach and test notes are recorded in the private advisories themselves (section "Implementation notes (WP-SIMPL-36)"), visible to maintainers only. Do not copy those details into commits, PR bodies, test names, code comments or docs.

State as of 2026-11-05. "Closed" means resolved on `main`; re-check the advisory before relying on it.

## Open (draft)

| Advisory | Component | Severity | Work package |
|---|---|---|---|
| GHSA-jcf8-hchm-55h3 | WebSQL DML authorization | High | [WP-SIMPL-36](WP-SIMPL-36-security-lane.md) (DML bundle) |
| GHSA-7ccv-hfgq-5qwm | Federation column access checks | High | WP-SIMPL-36 (P0) |
| GHSA-7pjw-hcrc-hvff | Federation masking catalog | Medium | WP-SIMPL-36 (P0) |
| GHSA-v5c2-29pv-cc86 | Data source registration | Medium | WP-SIMPL-36 (P0, OD-9 role model) |
| GHSA-9229-wx6j-wvm9 | Admin catalog operations | Medium | WP-SIMPL-36 (P0, OD-9 role model) |
| GHSA-3prq-qxg7-92qf | WebSQL DML scoping | Medium | WP-SIMPL-36 (DML bundle) |
| GHSA-83g2-c2jr-c8vq | WebSQL DML token scopes | Medium | WP-SIMPL-36 (DML bundle) |
| GHSA-66xj-4459-x8gx | WebSQL row-filter evaluation | Medium | WP-SIMPL-36 (proof on PostgreSQL, SQL Server, Oracle, Databricks) |
| GHSA-jw88-9j35-3c96 | dbt semantic manifest import | Medium | WP-SIMPL-36, before [WP-SIMPL-28](WP-SIMPL-28-module-dbt-openmetadata.md) |
| GHSA-rxr4-mf28-p2hv | Governed aggregation | Medium | WP-SIMPL-36 (OD-11) |
| GHSA-92vp-663x-pp52 | Transport gate | Medium | WP-SIMPL-36; procedures part in [WP-SIMPL-39](WP-SIMPL-39-procedures-clean.md) |
| GHSA-p234-g3ph-gqh2 | Docs ingestion store | Low | WP-SIMPL-36 (sub-package 7, owner of the regression test); [WP-SIMPL-40](WP-SIMPL-40-docs-ingestion-clean.md) keeps it green (OD-18) |
| GHSA-v77c-jh96-7rgr | Access profile persistence | Low | WP-SIMPL-36 |
| GHSA-x8g9-cm3h-p6jq | Authorization caches | Low | WP-SIMPL-36 |
| GHSA-v75c-28mx-7wm7 | WebSQL DML request handling | Low | WP-SIMPL-36 (DML bundle) |

## Closed

GHSA-qx32-576x-5cq2 (GDPR disclosure report), GHSA-xpw9-4m63-69pf (federation result masking), GHSA-95f9-ffc7-vw2c (catalog search), GHSA-53m3-948q-hj4x (async job export), GHSA-jfrx-2mgc-pfh5 (external catalog sync), GHSA-4qx8-fv2p-q8r2 (audit archive), GHSA-78j7-3gh6-7cqx (ReBAC enforcement), GHSA-335j-6vwc-7p4c (access profile revoke), GHSA-cj7m-c75j-6p6h (resource limits), GHSA-qfx6-4vp6-4hjv (Windows authentication), GHSA-jcpx-6m27-2m97 (error responses), GHSA-547h-29x4-m4ch (dbt sync), GHSA-6523-m29q-c397 (governed aggregation), GHSA-mgv5-4r69-mwww (admin role model), GHSA-wg8p-wfw5-qvgj (step-up approval), GHSA-hw85-rqjh-cwxx (token client allowlist), GHSA-wvwx-q6jg-44wv (CDC webhooks), GHSA-3953-r994-fg4m (transport gate).

Closed advisories are not reopened by later work packages. Where an older plan text lists a residual follow-up for a closed advisory (GHSA-95f9-ffc7-vw2c, GHSA-jfrx-2mgc-pfh5, GHSA-78j7-3gh6-7cqx, GHSA-4qx8-fv2p-q8r2), [WP-SIMPL-36](WP-SIMPL-36-security-lane.md) re-checks it against `main` and tracks any remaining work as hardening of the owning work package (details: private advisory). GHSA-78j7-3gh6-7cqx needs no follow-up after ReBAC is removed by [WP-SIMPL-46](WP-SIMPL-46-unified-authorization-model.md) (OD-30).
