# WP-SIMPL-02 Version from git tag, Helm coupling, CHANGELOG
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-02-version-from-git-tag-helm-coupling-changelog-510-wf) | Issues: #510 | Owner decisions: v1.5.0 frozen migration base (REQ-SIMPL-10.5); Q11 (no MinVer), Q12 (hand-maintained CHANGELOG) default taken; OD-37 (image pushed as `vX.Y.Z` and `X.Y.Z`) | Security conditions: SEC-SIMPL-01 (owner of the advisory-marker scan), SEC-SIMPL-04, SEC-SIMPL-63, SEC-SIMPL-64, SEC-SIMPL-73, SEC-SIMPL-92 (release blocked by a failed nightly)

## 1. Goal and outcome
The git tag `vX.Y.Z` is the only version source. Assembly `InformationalVersion`, image label, Helm chart `version` and `appVersion` all equal `X.Y.Z` on a release. `docker-publish.yml` pushes the release image under both `vX.Y.Z` and `X.Y.Z` (OD-37), so the default image tag the packaged chart renders (`X.Y.Z` from `appVersion`) always exists in the registry. A release-validation step fails the release on any mismatch, on a missing pushed tag, or when the last nightly run failed. Repository files hold only `0.0.0-dev` placeholders. `CHANGELOG.md` exists (Keep a Changelog) and the GitHub release notes are extracted from it. This WP also owns the SEC-SIMPL-01 advisory-marker scan (`DocsAdvisoryMarkerTests`), which guards CHANGELOG and all other tracked text from the first PR on.

## 2. Preconditions and dependencies
- None. Workflow change (`[WF]`): needs CODEOWNERS review (SEC-SIMPL-73). Parallel with WP-SIMPL-01, 03..09.
- No release tag may be pushed between the merge of PR 1 and PR 2 (PR 1 sets the `0.0.0-dev` placeholders; only PR 2 passes `-p:Version`/`--app-version`). Alternatively merge PR 2 first; its workflow tests do not depend on the placeholders.

## 3. Scope
### In scope
- `Directory.Build.props:3` `<Version>1.2.1</Version>` -> `<Version Condition="'$(Version)' == ''">0.0.0-dev</Version>`.
- `deploy/helm/autheris/Chart.yaml:5-6` -> `version: 0.0.0-dev`, `appVersion: "0.0.0-dev"`.
- `.github/workflows/release.yml`: `VERSION=${RELEASE_TAG#v}`; pass `-p:Version=$VERSION` to the publish at line 79; `helm package deploy/helm/autheris --version $VERSION --app-version $VERSION` (`appVersion` = `X.Y.Z`, no `v`), attach the `.tgz` to the release; replace `generate_release_notes: true` (line 162) with `body_path` of the extracted notes; a nightly-status gate before publishing.
- `.github/workflows/docker-publish.yml`: both publishes (lines 72, 214) get `-p:Version=$VERSION` where `VERSION` = tag without `v` on `refs/tags/v*`, else `0.0.0-dev+${GITHUB_SHA::7}`; image label `org.opencontainers.image.version=$VERSION`; for release tags push `${IMAGE}:${REF_NAME}` (`vX.Y.Z`, as today) AND `${IMAGE}:${VERSION}` (`X.Y.Z`); pre-release tags never add `latest` or `getting-started`.
- New `scripts/release/extract-changelog.sh <version>` and `scripts/release/validate-release-version.sh <tag> <dll> <chart-tgz> <image-ref>`.
- New `CHANGELOG.md`.
- New `tests/Autheris.Tests.Architecture/DocsAdvisoryMarkerTests.cs` (SEC-SIMPL-01 scan).
### Out of scope
- MinVer or any versioning package; NuGet packing; cadence enforcement (process rule only); the migration-job fail-closed behavior already in the chart (only asserted, not changed).
- Module signing in the image build (`ReleaseWorkflowTests.Image_build_rejects_unsigned_module` is owned by WP-SIMPL-25; no modules exist yet).

## 4. Current state (verified on 63cbb893)
- `Directory.Build.props:3` Version 1.2.1; 56 tags v1.0.0..v1.7.0; no `CHANGELOG.md`.
- `deploy/helm/autheris/Chart.yaml:5` `version: 1.0.0`, `:6` `appVersion: "1.0.0"`; `values.yaml:8-9` comment "Empty tag falls back to the chart appVersion. Never use a mutable tag such as latest" and `tag: ""`; `templates/deployment.yaml:57` uses `autheris.image` helper; `templates/_helpers.tpl:89` renders `repository:(default .Chart.AppVersion .Values.image.tag)`.
- `release.yml`: `permissions: {}` (14), tag validation (25-30), checkout of `refs/tags/...` (39), publish (79), job `release` in environment `release` (131-137), `softprops/action-gh-release` pinned by SHA (158), `generate_release_notes: true` (162).
- `docker-publish.yml`: triggers on `tags: ['v*']` (5); manual dispatch may not set `latest`/`v*` (139-140); release tags push `${IMAGE}:${REF_NAME}` (146, i.e. `vX.Y.Z`), `${IMAGE}:latest` (147) and `${IMAGE}:getting-started` (148), with no pre-release guard; cosign identity regexp (188). Today no `X.Y.Z` tag without `v` is pushed, so a chart with `appVersion: X.Y.Z` would reference a missing image.
- Nightly workflows: `parity-nightly.yml`, `sql-compiler-gate-nightly.yml`, `simulation-nightly.yml` (schedule triggers). No release step checks their status.
- No test or CI check scans for advisory detail markers (SEC-SIMPL-01).

## 5. Implementation steps (TDD)
1. Red: add `tests/Autheris.Tests.Architecture/ReleaseVersionArchitectureTests.cs`, `ReleaseWorkflowTests.cs`, `HelmValuesTests.cs` and `DocsAdvisoryMarkerTests.cs` (tests in section 6). All fail except the marker scan, which must be green on the current tree (0 hits outside `docs/plans/`; the plan text is neutralized by the R17 review pass before this WP).
2. Green: change `Directory.Build.props:3` and `Chart.yaml:5-6` as in scope.
3. Green: `release.yml` build job, after tag validation: step `Derive version` writing `VERSION=${RELEASE_TAG#v}` to `$GITHUB_ENV`; validate format `^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$`. Step `Nightly gate` (SEC-SIMPL-92): for each of `parity-nightly.yml` and `sql-compiler-gate-nightly.yml`, `gh run list --workflow <file> --branch main --status completed --limit 1 --json conclusion -q '.[0].conclusion'` must print `success`, else exit 1 naming the workflow; the job gets `actions: read` only (read scope, no write scope added). Add `-p:Version=$VERSION` to the publish. Add `azure/setup-helm` pinned by SHA (or use preinstalled helm), `helm package ... --version "$VERSION" --app-version "$VERSION" -d dist`. Run `scripts/release/validate-release-version.sh "$RELEASE_TAG" <published Autheris.Api.dll> dist/autheris-$VERSION.tgz ghcr.io/<repo>`. Run `scripts/release/extract-changelog.sh "$VERSION" > dist/RELEASE_NOTES.md`; fail when the section is missing or empty.
4. Green: `docker-publish.yml`: same `Derive version` step; `-p:Version` on both publishes; add the version label. In the tag branch (146-148) push `${IMAGE}:${REF_NAME}` and `${IMAGE}:${VERSION}`; add `latest` and `getting-started` only when `VERSION` contains no `-` (pre-release tags never add `latest` or `getting-started`, SEC-SIMPL-63). After push, `validate-release-version.sh` compares the tag with the image label (`docker buildx imagetools inspect` / `docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.version" }}'` on `autheris:scan`).
5. Scripts: `validate-release-version.sh` reads the assembly `InformationalVersion` with `pwsh -NoProfile -c "(Get-Item '<dll>').VersionInfo.ProductVersion"` (pwsh is preinstalled on ubuntu-latest runners) and strips the `+sha` suffix before comparing. Chart: `helm show chart <tgz>` and compare `version` and `appVersion` with the tag without `v`. Default image: `helm template <tgz>` with default values, extract the Deployment image tag, assert it equals `appVersion` and that `docker buildx imagetools inspect <image-ref>:<tag>` succeeds (the tag was pushed). Exit 1 with a message naming the mismatching pair. `--self-test` runs the mismatch cases offline.
6. `CHANGELOG.md`: header per Keep a Changelog 1.1.0; `## [Unreleased]` with `### BREAKING`, `### Removed`, `### Moved to modules`, `### Fixed`, `### Security` (empty bullets allowed as `- None yet.`); backfill `## [1.7.x]` .. `## [1.0.x]` grouped by minor from `git log --oneline vA..vB` summaries (3-8 bullets per minor, no commit lists); v1.0-v1.4 sections start with `> Unsupported upgrade base. Oldest supported migration base: v1.5.0.` Security entries: GHSA id and the neutral title from `docs/plans/simplification/advisories-overview.md` only (SEC-SIMPL-01).
7. `DocsAdvisoryMarkerTests` (SEC-SIMPL-01): scan set = all tracked files under `docs/**`, `src/**`, `tests/**`, `.github/**` plus `CHANGELOG.md` (text files only: `*.md`, `*.cs`, `*.yml`, `*.yaml`, `*.json`, `*.txt`, `*.sh`; skip `bin/`, `obj/`, `node_modules/`). A line fails when it contains a real advisory id (regex `GHSA(-[23456789cfghjmpqrvwx]{4}){3}`, case-insensitive) and any marker word (case-insensitive, whole word or phrase): `payload`, `exploit`, `PoC`, `proof of concept`, `bypass via`, `root of`, `root cause`, `class of`, `reproduc`. Test names: every method in `tests/**/*.cs` whose name contains `Ghsa` (case-insensitive) must match `^Ghsa_[a-z0-9]{4}_[a-z0-9]{4}_regression$` inside class `AdvisoryRegressionTests`. Output lists `path:line` only, never the line text. No allowlist, no baseline (0 hits today outside the plan text neutralized by R17).
8. Add a short "Releasing" paragraph to `CONTRIBUTING.md` if it exists, else to `deploy/README.md`: tag `vX.Y.Z`, update CHANGELOG first, check the nightly runs are green, max one release per week plus security patches.

## 6. Tests (project `Autheris.Tests.Architecture`, use `RepositoryScan`)
- `ReleaseVersionArchitectureTests.Directory_build_props_holds_no_product_version`: the only `Version` element in `Directory.Build.props` is `0.0.0-dev` with condition `'$(Version)' == ''`; no `*.csproj` sets `Version`, `AssemblyVersion`, `FileVersion` or `InformationalVersion`.
- `ReleaseVersionArchitectureTests.Chart_versions_are_placeholders`: Chart.yaml `version` and `appVersion` equal `0.0.0-dev`.
- `ReleaseWorkflowTests.Release_passes_tag_version_to_publish_and_helm_package` [Security]: release.yml contains `-p:Version=`, `--app-version "$VERSION"` (tag without `v`), `validate-release-version.sh`, no `generate_release_notes: true`.
- `ReleaseWorkflowTests.Docker_publish_passes_version_and_never_tags_prerelease_latest_or_getting_started` [Security]: both publish commands carry `-p:Version=`; the `latest` and `getting-started` tags are added only under a non-prerelease check.
- `ReleaseWorkflowTests.Default_rendered_image_tag_is_a_pushed_tag` [Security] (SEC-SIMPL-64, OD-37): the docker-publish tag branch pushes both `${IMAGE}:${REF_NAME}` and `${IMAGE}:${VERSION}`; release.yml packages the chart with `--app-version "$VERSION"`; `_helpers.tpl` falls back to `.Chart.AppVersion`; `validate-release-version.sh` contains the `helm template` image check and the registry existence check. Together: the default rendered image tag equals a pushed tag.
- `ReleaseWorkflowTests.Release_blocked_when_last_nightly_failed` [Security] (SEC-SIMPL-92): release.yml has the `Nightly gate` step before the publish step, naming `parity-nightly.yml` and `sql-compiler-gate-nightly.yml` and failing on a conclusion other than `success`.
- `ReleaseWorkflowTests.Release_workflow_permissions_stay_least_privilege` [Security]: top-level `permissions: {}`; `contents: write`/`id-token: write` only in publishing jobs; `actions: read` is the only scope added; no `pull_request_target`.
- `HelmValuesTests.Image_tag_is_pinned_and_not_latest` [Security]: values.yaml `image.tag` is empty or a digest, never `latest`; the `autheris.image` helper falls back to `.Chart.AppVersion`.
- `ReleaseWorkflowTests.Changelog_exists_with_unreleased_section`: CHANGELOG.md has `## [Unreleased]` and sections for 1.0..1.7.
- `DocsAdvisoryMarkerTests.Docs_and_changelog_contain_no_advisory_detail_markers` [Security] (SEC-SIMPL-01): scan of step 7; plus an inline Theory proving a synthetic line with a real-format id and `bypass via` fails and a line with id plus neutral title passes.
- `DocsAdvisoryMarkerTests.Advisory_test_names_are_neutral` [Security]: test-name rule of step 7.
- Script self-test: `scripts/release/validate-release-version.sh --self-test` exits 0 and its mismatch cases exit 1 (run as a CI step in release.yml).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (local version 0.0.0-dev).
- `dotnet build Autheris.sln -c Release -p:Version=9.9.9` then check `Autheris.Api.dll` ProductVersion starts with `9.9.9`.
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build --filter "FullyQualifiedName~ReleaseVersionArchitectureTests|FullyQualifiedName~ReleaseWorkflowTests|FullyQualifiedName~HelmValuesTests|FullyQualifiedName~DocsAdvisoryMarkerTests"`
- `helm lint deploy/helm/autheris`; `helm package deploy/helm/autheris --version 9.9.9 --app-version 9.9.9 -d /tmp/h && helm template /tmp/h/autheris-9.9.9.tgz | grep 'image:.*:9.9.9'`.
- `bash scripts/release/extract-changelog.sh 1.7.0` prints a non-empty section; `bash scripts/release/validate-release-version.sh --self-test`.
- `actionlint .github/workflows/release.yml .github/workflows/docker-publish.yml` if available.

## 8. Acceptance criteria
- [ ] No hard-coded product version in props, csproj or Chart.yaml.
- [ ] Tag build: dll ProductVersion, image label, chart version and appVersion all equal the tag without `v`; validation step fails on mismatch.
- [ ] Release tags push the image as `vX.Y.Z` and `X.Y.Z`; `helm template` of the packaged chart with default values renders image tag `X.Y.Z`, and `Default_rendered_image_tag_is_a_pushed_tag` plus the release-time registry check prove that this tag was pushed.
- [ ] Pre-release tags never produce `latest` or `getting-started`.
- [ ] A release fails when the last `parity-nightly.yml` or `sql-compiler-gate-nightly.yml` run on main did not succeed.
- [ ] Release notes come from CHANGELOG; CHANGELOG covers v1.0..v1.7 and marks v1.0-v1.4 unsupported.
- [ ] Workflow permissions unchanged or narrower (only `actions: read` added for the nightly gate); actions pinned by SHA; provenance, SBOM, cosign signing steps unchanged.
- [ ] `DocsAdvisoryMarkerTests` green on `docs/**`, `src/**`, `tests/**`, `.github/**` and CHANGELOG (SEC-SIMPL-01).

## 9. PR slicing
- PR 1: props, Chart.yaml, `ReleaseVersionArchitectureTests`, `DocsAdvisoryMarkerTests`, CHANGELOG.md (docs + tests).
- PR 2 `[WF]`: release.yml, docker-publish.yml, scripts, `ReleaseWorkflowTests`, `HelmValuesTests`. CODEOWNERS review.
- No release tag between PR 1 and PR 2 (see section 2).

## 10. Risks, rollback, must NOT do
- Risk: main builds now report `0.0.0-dev+sha` instead of 1.2.1 (intended). Risk: first tag after merge fails validation; fix forward with a patch tag, never move a tag. Risk: a red nightly blocks an urgent security release; fix the nightly or re-run it, never bypass the gate.
- Must NOT: widen workflow permissions beyond `actions: read`, use `pull_request_target`, unpin actions, add MinVer, set `values.yaml` tag to `latest`, drop the `vX.Y.Z` image tag, write advisory details into CHANGELOG, retag existing releases, print matched advisory lines in test output.

## 11. Estimate
+~420 (CHANGELOG ~150, workflows and scripts ~70, tests ~200) / -3 LOC; 0 src lines; 2.5 person-days.

## Decisions taken in this spec
- D-02-1: Version override uses a `Condition` so `-p:Version` and `dotnet build` without it both work.
- D-02-2: InformationalVersion read through `pwsh` `VersionInfo.ProductVersion` (available on GitHub ubuntu runners), `+sha` suffix stripped.
- D-02-3: Chart `.tgz` is attached to the GitHub release; no chart registry push (not in plan).
- D-02-4: Both image tags are pushed (`vX.Y.Z` keeps existing pull references working; `X.Y.Z` matches `appVersion`), OD-37.
- D-02-5: The advisory-marker scan lives here (SEC-SIMPL-01 names WP-SIMPL-02 as owner) and covers `docs/**`, `src/**`, `tests/**`, `.github/**` and CHANGELOG; WP-SIMPL-05 does not duplicate it.
