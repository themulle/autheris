## Summary

<!-- Brief description of changes -->

## Linked issue

<!-- Closes #issue or Ref #issue -->

## Freeze checklist

- [ ] no new feature, endpoint, option, *Enabled flag or Application folder
- [ ] ratchet baselines only lowered
- [ ] net src lines <= 400 or size-exception justified

## Labels

### freeze-exception
- Reserved for security fixes (SEC-SIMPL-70: GHSA id, Dependabot/Trivy alert, or Phase 3 condition) or owner-mandated work packages WP-SIMPL-38..47 listed in `docs/plans/simplification/README.md` (named with their OD in the PR description).
- Allows raising ratchet baselines in the same PR and bypasses the PR size step.
- The baseline diff requires CODEOWNERS approval with neutral title (SEC-SIMPL-70/01).

### size-exception
- Requires a paragraph explaining why the change cannot be split + link to owner approval (SEC-SIMPL-72).
- Limited to max two per week across the repository.
- Deletion PRs of whole subsystems must list SEC-SIMPL-02 items.

## Workflow changes note

<!-- SEC-SIMPL-73: State any security gate or CI workflow added, modified, loosened, or removed. If none, state "None". -->
