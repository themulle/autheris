# WP-SIMPL-02 Version from git tag, Helm coupling, CHANGELOG
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-02-version-from-git-tag-helm-coupling-changelog-510-wf) | Issues: #510 | Owner decisions: v1.5.0 frozen migration base (REQ-SIMPL-10.5); Q11 (no MinVer), Q12 (hand-maintained CHANGELOG) default taken | Security conditions: SEC-SIMPL-01, SEC-SIMPL-04, SEC-SIMPL-63, SEC-SIMPL-64, SEC-SIMPL-73

## 1. Goal and outcome
The git tag `vX.Y.Z` is the only version source. Assembly `InformationalVersion`, image tag, Helm chart `version` and `appVersion` all equal `X.Y.Z` on a release; a release-validation step fails the release on any mismatch. Repository files hold only `0.0.0-dev` placeholders. `CHANGELOG.md` exists (Keep a Changelog) and the GitHub release notes are extracted from it.

## 2. Preconditions and dependencies
- None. Workflow change (`[WF]`): needs CODEOWNERS review (SEC-SIMPL-73). Parallel with WP-SIMPL-01, 03..09.

## 3. Scope
### In scope
- `Directory.Build.props:3` `<Version>1.2.1</Version>` -> `<Version Condition="'$(Version)' == ''">0.0.0-dev</Version>`.
- `deploy/helm/autheris/Chart.yaml:5-6` -> `version: 0.0.0-dev`, `appVersion: "0.0.0-dev"`.
- `.github/workflows/release.yml`: `VERSION=${RELEASE_TAG#v}`; pass `-p:Version=$VERSION` to the publish at line 79; `helm package deploy/helm/autheris --version $VERSION --app-version $VERSION`, attach the `.tgz` to the release; replace `generate_release_notes: true` (line 162) with `body_path` of the extracted notes.
- `.github/workflows/docker-publish.yml`: both publishes (lines 72, 214) get `-p:Version=$VERSION` where `VERSION` = tag without `v` on `refs/tags/v*`, else `0.0.0-dev+${GITHUB_SHA::7}`; image label `org.opencontainers.image.version=$VERSION`.
- New `scripts/release/extract-changelog.sh <version>` and `scripts/release/validate-release-version.sh <tag> <dll> <chart-tgz> <image-ref-or-label>`.
- New `CHANGELOG.md`.
### Out of scope
- MinVer or any versioning package; NuGet packing; cadence enforcement (process rule only); the migration-job fail-closed behavior already in the chart (only asserted, not changed).

## 4. Current state (verified on 63cbb893)
- `Directory.Build.props:3` Version 1.2.1; 56 tags v1.0.0..v1.7.0; no `CHANGELOG.md`.
- `deploy/helm/autheris/Chart.yaml:5` `version: 1.0.0`, `:6` `appVersion: "1.0.0"`; `values.yaml:8-9` comment "Empty tag falls back to the chart appVersion. Never use a mutable tag such as latest" and `tag: ""`; `templates/deployment.yaml:57` uses `autheris.image` helper.
- `release.yml`: `permissions: {}` (14), tag validation (25-30), checkout of `refs/tags/...` (39), publish (79), job `release` in environment `release` (131-137), `softprops/action-gh-release` pinned by SHA (158), `generate_release_notes: true` (162).
- `docker-publish.yml`: triggers on `tags: ['v*']` (5); manual dispatch may not set `latest`/`v*` (139-140); `latest` only for release tags (147-151); cosign identity regexp (188).

## 5. Implementation steps (TDD)
1. Red: add `tests/Autheris.Tests.Architecture/ReleaseVersionArchitectureTests.cs` and `ReleaseWorkflowTests.cs`, `HelmValuesTests.cs` (tests in section 6). All fail.
2. Green: change `Directory.Build.props:3` and `Chart.yaml:5-6` as in scope.
3. Green: `release.yml` build job, after tag validation: step `Derive version` writing `VERSION=${RELEASE_TAG#v}` to `$GITHUB_ENV`; validate format `^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$`. Add `-p:Version=$VERSION` to the publish. Add `azure/setup-helm` pinned by SHA (or use preinstalled helm), `helm package ... --version "$VERSION" --app-version "$VERSION" -d dist`. Run `scripts/release/validate-release-version.sh "$RELEASE_TAG" <published Autheris.Api.dll> dist/autheris-$VERSION.tgz`. Run `scripts/release/extract-changelog.sh "$VERSION" > dist/RELEASE_NOTES.md`; fail when the section is missing or empty. Permissions unchanged (no new write scopes).
4. Green: `docker-publish.yml`: same `Derive version` step; `-p:Version` on both publishes; add the version label; after push, `validate-release-version.sh` compares tag with the image label (`docker buildx imagetools inspect` / `docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.version" }}'` on `autheris:scan`). Pre-release tags (`-` in version) never add `latest` (extend the existing condition at 147).
5. Scripts: `validate-release-version.sh` reads the assembly `InformationalVersion` with `pwsh -NoProfile -c "(Get-Item '<dll>').VersionInfo.ProductVersion"` (pwsh is preinstalled on ubuntu-latest runners) and strips the `+sha` suffix before comparing. Chart: `helm show chart <tgz>` and compare `version` and `appVersion`. Exit 1 with a message naming the mismatching pair.
6. `CHANGELOG.md`: header per Keep a Changelog 1.1.0; `## [Unreleased]` with `### BREAKING`, `### Removed`, `### Moved to modules`, `### Fixed`, `### Security` (empty bullets allowed as `- None yet.`); backfill `## [1.7.x]` .. `## [1.0.x]` grouped by minor from `git log --oneline vA..vB` summaries (3-8 bullets per minor, no commit lists); v1.0-v1.4 sections start with `> Unsupported upgrade base. Oldest supported migration base: v1.5.0.` Security entries: GHSA id and neutral title only (SEC-SIMPL-01).
7. Add a short "Releasing" paragraph to `CONTRIBUTING.md` if it exists, else to `deploy/README.md`: tag `vX.Y.Z`, update CHANGELOG first, max one release per week plus security patches.

## 6. Tests (project `Autheris.Tests.Architecture`, use `RepositoryScan`)
- `ReleaseVersionArchitectureTests.Directory_build_props_holds_no_product_version`: the only `Version` element in `Directory.Build.props` is `0.0.0-dev` with condition `'$(Version)' == ''`; no `*.csproj` sets `Version`, `AssemblyVersion`, `FileVersion` or `InformationalVersion`.
- `ReleaseVersionArchitectureTests.Chart_versions_are_placeholders`: Chart.yaml `version` and `appVersion` equal `0.0.0-dev`.
- `ReleaseWorkflowTests.Release_passes_tag_version_to_publish_and_helm_package` [Security]: release.yml contains `-p:Version=`, `--app-version`, `validate-release-version.sh`, no `generate_release_notes: true`.
- `ReleaseWorkflowTests.Docker_publish_passes_version_and_never_tags_prerelease_latest` [Security]: both publish commands carry `-p:Version=`; the `latest` branch is guarded by a non-prerelease check.
- `ReleaseWorkflowTests.Release_workflow_permissions_stay_least_privilege` [Security]: top-level `permissions: {}`; `contents: write`/`id-token: write` only in publishing jobs; no `pull_request_target`.
- `HelmValuesTests.Image_tag_is_pinned_and_not_latest` [Security]: values.yaml `image.tag` is empty or a digest, never `latest`; the `autheris.image` helper falls back to `.Chart.AppVersion`.
- `ReleaseWorkflowTests.Changelog_exists_with_unreleased_section`: CHANGELOG.md has `## [Unreleased]` and sections for 1.0..1.7.
- Script self-test: `scripts/release/validate-release-version.sh` run with mismatching inputs exits 1 (documented as a CI step in release.yml using `--self-test`).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (local version 0.0.0-dev).
- `dotnet build Autheris.sln -c Release -p:Version=9.9.9` then check `Autheris.Api.dll` ProductVersion starts with `9.9.9`.
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build --filter "FullyQualifiedName~ReleaseVersionArchitectureTests|FullyQualifiedName~ReleaseWorkflowTests|FullyQualifiedName~HelmValuesTests"`
- `helm lint deploy/helm/autheris`; `helm package deploy/helm/autheris --version 9.9.9 --app-version 9.9.9 -d /tmp/h && helm template /tmp/h/autheris-9.9.9.tgz | grep 'image:.*:9.9.9'`.
- `bash scripts/release/extract-changelog.sh 1.7.0` prints a non-empty section.
- `actionlint .github/workflows/release.yml .github/workflows/docker-publish.yml` if available.

## 8. Acceptance criteria
- [ ] No hard-coded product version in props, csproj or Chart.yaml.
- [ ] Tag build: dll ProductVersion, image label, chart version and appVersion all equal the tag without `v`; validation step fails on mismatch.
- [ ] `helm template` with default values renders the image tag equal to the packaged appVersion.
- [ ] Pre-release tags never produce `latest`.
- [ ] Release notes come from CHANGELOG; CHANGELOG covers v1.0..v1.7 and marks v1.0-v1.4 unsupported.
- [ ] Workflow permissions unchanged or narrower; actions pinned by SHA; provenance, SBOM, cosign signing steps unchanged.
- [ ] Advisory-marker scan clean on CHANGELOG (SEC-SIMPL-01).

## 9. PR slicing
- PR 1: props, Chart.yaml, architecture tests, CHANGELOG.md (docs + tests).
- PR 2 `[WF]`: release.yml, docker-publish.yml, scripts, workflow tests. CODEOWNERS review.

## 10. Risks, rollback, must NOT do
- Risk: main builds now report `0.0.0-dev+sha` instead of 1.2.1 (intended). Risk: first tag after merge fails validation; fix forward with a patch tag, never move a tag.
- Must NOT: widen workflow permissions, use `pull_request_target`, unpin actions, add MinVer, set `values.yaml` tag to `latest`, write advisory details into CHANGELOG, retag existing releases.

## 11. Estimate
+~320 (CHANGELOG ~150, workflows and scripts ~50, tests ~120) / -3 LOC; 0 src lines; 2 person-days.

## Decisions taken in this spec
- D-02-1: Version override uses a `Condition` so `-p:Version` and `dotnet build` without it both work.
- D-02-2: InformationalVersion read through `pwsh` `VersionInfo.ProductVersion` (available on GitHub ubuntu runners), `+sha` suffix stripped.
- D-02-3: Chart `.tgz` is attached to the GitHub release; no chart registry push (not in plan).
