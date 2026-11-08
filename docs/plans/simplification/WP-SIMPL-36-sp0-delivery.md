# WP-SIMPL-36 sub-package 0: delivery record

Parent spec: [WP-SIMPL-36-security-lane.md](WP-SIMPL-36-security-lane.md), section 5 "Sub-package 0".

## Status (2026-11-08)
- Step 1 (rebase and merge of `fix/security-batch-5`): already done. The branch was squash-merged as `4070d278` (#524). Its tip `eb1f0413` has an empty tree diff against that commit. The 10 commits that `git log origin/main..origin/fix/security-batch-5` still lists are therefore content-merged, and the branch is superseded.
- Steps 2-5 delivered on `feat/simpl-36-sp0`:
  - OD-9 alignment: `IsGlobalGovernanceAdmin` now means canonical ClusterAdmin only, and `IsTenantBoundGovernanceAdmin` was added.
  - Four redundant call sites now use `IsCanonicalClusterAdmin` directly.
  - Batch-5 test methods carry neutral names (SEC-SIMPL-01).
  - Advisory detail was removed from comments.
  - New `Governance/Batch5OD9ScopeTests`.
- Phase 3 approved; Phase 5 approved after one fix round.
- Local verification: solution build with `-warnaserror` is clean; the full unit suite and the architecture tests pass. Integration tests run in CI.

## Decisions (default taken)
- The renamed `Ghsa_*_regression` tests stay in `Batch5SecurityTestsA/B` rather than class `AdvisoryRegressionTests`, because private fixture members collide. Several old cases run from one public `[Fact]` per advisory, as private `Case_*` helpers. Follow-up: move or alias them into `AdvisoryRegressionTests` before the lane closes.
- `GovernanceClassificationEndpoints` owner-capable review: canonical ClusterAdmin or DataOwner only. A GovernanceAdmin follows the normal path.
- `IsTenantBoundGovernanceAdmin` has no production caller yet; sub-package 1 consumes it.

## Open follow-ups
- In `Tenant_bound_GovernanceAdmin_is_denied_on_global_only_sites`, four rows (DataContract privileged view, dbt cross-tenant operation, HitL cross-tenant view, classification owner-capable) assert through the helper, not through the handler.
- The SEC-SIMPL-88 privacy-officer-role and write-ahead-audit aspects rely on existing tests outside this delta.
