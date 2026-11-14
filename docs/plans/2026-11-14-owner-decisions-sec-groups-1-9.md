# Owner decisions 2026-11-14: Phase 3 security questions, groups 1-9

Status: **CONFIRMED** (owner instruction 2026-11-14: "use the defaults everywhere"). All recommended defaults of the
Phase 3 reviews are binding input for Phase 4. Each review document holds the binding conditions and the mandatory
security tests; this file records only the decisions.

| Id | Decision (default confirmed) | Review |
|---|---|---|
| Q-SEC1-1 | Redaction happens at the persistence chokepoint | [group 1](2026-11-13-sec-issues-group-1.md) |
| Q-SEC1-2 | Skipping an unlisted plugin DLL is allowed in Development only | group 1 |
| Q-SEC1-3 | Non-Development hosts without a keyset cursor key fail startup | group 1 |
| Q-SEC2-1 | Keep `GovernedTableCacheExecutionService` | [group 2](2026-11-13-sec-issues-group-2.md) |
| Q-SEC2-2 | Ambiguous multi-valued claims make the attribute unavailable | group 2 |
| Q-SEC2-3 | Drop the `principal` argument of `audit_access_compliance` | group 2 |
| Q-SEC3-1 | Removing the `ROLE_MEMBERS` branch also stops DB-role admins from approving consent requests | [group 3](2026-11-13-sec-issues-group-3.md) |
| Q-SEC3-2 | Reject all colliding dbt nodes instead of keeping the first | group 3 |
| Q-SEC3-3 | Cap the prune set at 10,000 consents per run (constant) | group 3 |
| Q-SEC4-1 | Sign the #681 cursor with the keyset/data-protection key | [group 4](2026-11-13-sec-issues-group-4.md) |
| Q-SEC4-2 | Remove legacy `offset` after one release | group 4 |
| Q-SEC4-3 | One-hop data source mappings only | group 4 |
| Q-SEC5-1 | Lower `MaxOperatorChainTerms` if a walker fails the small-stack test; never relax the test | [group 5](2026-11-13-sec-issues-group-5.md) |
| Q-SEC5-2 | Cap emitted SQL at 4 x `MaxQueryLength` (constant) | group 5 |
| Q-SEC5-3 | Comparisons against another column of the same table never satisfy a required filter | group 5 |
| Q-SEC6-1 | #625: callers with any hidden column get table-level fields only in the score | [group 6](2026-11-13-sec-issues-group-6.md) |
| Q-SEC6-2 | #626: query caps (256 chars, 8 tokens, 128 expansions) are constants | group 6 |
| Q-SEC6-3 | #628b: model and tokenizer pinned by SHA-256, mounted read-only, never downloaded | group 6 |
| Q-SEC7-1 | #672: restrictive catalog writes flush the version immediately | [group 7](2026-11-13-sec-issues-group-7.md) |
| Q-SEC7-2 | #675: full snapshot reload at least every 10 minutes as delta backstop (constant) | group 7 |
| Q-SEC7-3 | #677: raw read API stays internal, caller-level filter parity tests instead of a repository tenant filter | group 7 |
| Q-SEC7-4 | #678: per-tenant share cap of 50 % of cache entries (constant) | group 7 |
| Q-SEC8-1 | #522: Security tests combined with Spark/Parity/Oracle/Stress need a PR lane; existing combinations via a shrink-only baseline | [group 8](2026-11-13-sec-issues-group-8.md) |
| Q-SEC8-2 | #609: restrict G9 to `refs/heads/main` in the job condition | group 8 |
| Q-SEC8-3 | #609: stale-schema sweep (24 h, prefix only) in the live suite | group 8 |
| Q-SEC9-1 | #504: compare the Security test name set against a committed base | [group 9](2026-11-13-sec-issues-group-9.md) |
| Q-SEC9-2 | #613 2a: the analyzer fix never makes an allow decision more permissive | group 9 |

## Further decisions

- WP-SIMPL-14: delete `policy-lint.yml` and the lint tool after PR 3 merges (owner default yes).
- Container gate: temporary, expiring `.trivyignore` entries for the six libc MEDIUM CVEs (fix 2.39-0ubuntu8.12 is not in the newest `aspnet:10.0.15` image); removal when the base image is rebuilt.
- CI guard: the compile-only Parity step runs with `if: ${{ !cancelled() }}`, so it also runs when the main test step fails (test updated accordingly).
- Footprint budget gets a scheduled wrapper workflow with its own failure alert.

## Plans that need rework before Phase 4

The group 6-9 review returned CHANGES REQUIRED for three work packages. The architect revises the plans first:

- #628c: the table-level embedding text must not contain column names (Q-SRCH-5).
- #672: restrictive writes must bump the catalog version immediately and a failed flush must be repaired (follows Q-SEC7-1).
- #677: the plan assumes a tenant concept the catalog repository does not have; paging applies after visibility.

## Phase 4 order

Dependencies per review: #667 before #670; WP-SIMPL-47 PR 1-2 before #671; WP-SIMPL-41 PR 3 before #681 and #682 B; #492 stays deferred (Q-SQL-4). Group 1/2 packages start in the order ID-2, ID-4, ID-5, ID-1, ID-3, DP-1, then DP-2.
