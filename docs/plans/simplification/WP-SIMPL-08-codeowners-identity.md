# WP-SIMPL-08 CODEOWNERS subsystem map and identity consolidation
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-08-codeowners-subsystem-map-and-identity-consolidation) | Issues: #515 (Stage 0) | Owner decisions: D6 (default taken: one primary owner, declared vacant backups), OD-17 | Security conditions: SEC-SIMPL-71, SEC-SIMPL-73, SEC-SIMPL-70

## 1. Goal and outcome
Rewrite `.github/CODEOWNERS` into the 14 PRD 5.2 subsystems plus one line per module, each with a primary owner and a `# backup:` comment, keep every current entry, add the SEC-SIMPL-71 security paths, and add `.mailmap` consolidating the commit identities. Outcome: uncovered `src/` top-level folders = 0, uncovered `src/Autheris.Application` and `src/Autheris.Api` top-level folders = 0, all SEC-SIMPL-71 paths owned, all enforced by `CodeOwnersTests`.

## 2. Preconditions and dependencies
- #516/#517 merged (Stage 0 rule). No WP dependency; lane L-DOC, parallel to WP-SIMPL-09.
- Owner input D6: the only human owner today is `@themulle`; backups are `vacant` until the owner names a second reviewer.

## 3. Scope
### In scope
- `.github/CODEOWNERS` rewrite; new `.mailmap`; new `tests/Autheris.Tests.Architecture/CodeOwnersTests.cs`.
### Out of scope
- Branch protection "require CODEOWNERS review" (owner setting); GitHub team creation; module folders that do not exist yet (`modules/` is absent; the test covers it once created).

## 4. Current state (verified on 63cbb893)
- `.github/CODEOWNERS` has 22 lines / 18 entries, all `@themulle`: `src/Autheris.Api/Security/**`, `.github/**`, `deploy/**`, `Dockerfile*`, `Directory.Build.props`, `NuGet.config`, `**/packages.lock.json`, `tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences*.json`, `src/TrinoSqlEngine/Ast/Capabilities/DialectCapabilityTable.cs`, `src/TrinoSqlEngine/TimeZones/**`, `tests/TrinoSqlEngine.Tests/TimeZones/Cldr/**`, `tests/TrinoSqlEngine.Tests/TimeZones/IanaWindowsZonesGenerator.cs`, `src/Autheris.Infrastructure/Persistence/Migrations/**`, `tests/Autheris.Tests.Architecture/Golden/GovernanceBaseline/**`, `.../Golden/GovernanceSteps/**`.
- `src/` top-level: Autheris.Api, Autheris.Application, Autheris.Domain, Autheris.Extensions, Autheris.GraphQL, Autheris.Infrastructure, TrinoSqlEngine (no `src/Autheris.Governance`; SEC-SIMPL-71's `src/Autheris.Governance/**` maps to the governance namespaces in Application/Infrastructure).
- No `.mailmap`. Identities in `git log`: `Manuel Müller <themulle@gmail.com>` (1156), `Manuel <30625725+themulle@users.noreply.github.com>` (293), `Manuel.Mueller2@liebherr.com <Manuel.Mueller2@liebherr.com>` (49), `themulle <themulle@gmail.com>` (2); dependabot stays separate.
- Helper: `RepositoryScan.RepoRoot`, `RepositoryScan.ReadText` (tests/Autheris.Tests.Architecture/RepositoryScan.cs:18,73).

## 5. Implementation steps
1. Red: add `CodeOwnersTests` (below) against the current file -> `Every_entry_declares_a_backup` and `Security_paths_are_owned` fail.
2. Parser (private static in the test class): read `.github/CODEOWNERS`, keep non-comment lines, split pattern/owners; record the comment block directly above each entry. Match a repo path against a pattern with CODEOWNERS semantics (last match wins; `**` any depth, `*` one segment, leading `/` anchors root, trailing `/` or `/**` = directory). Implement with a small glob-to-regex converter; no new package.
3. Rewrite `.github/CODEOWNERS` in this order (later lines win, so security lines go last):
   - `* @themulle` fallback with `# subsystem: default  # backup: vacant`.
   - 14 subsystem sections, each a header comment `# subsystem: <name> | primary: @themulle | backup: vacant`, then its paths. Map: Consent (`src/Autheris.Application/Consent/`), RLS/VirtualFilters, Masking, Audit, Policy/RBAC/ReBAC, Identity/Tenancy, SQL compiler/gate (`src/TrinoSqlEngine/`, `tests/Autheris.Tests.SqlCompilerGate/`), Catalog/Classification, Governance persistence (`src/Autheris.Infrastructure/Persistence/`), GraphQL (`src/Autheris.GraphQL/`), MCP, Api/REST (`src/Autheris.Api/`), Infrastructure/ops/CI (`src/Autheris.Infrastructure/`, `deploy/`, `.github/`, `Dockerfile*`, build props), Docs (`docs/`, `*.md`). The developer assigns every remaining `src/Autheris.Application/<Folder>/`, `src/Autheris.Api/<Folder>/`, `src/Autheris.Extensions/<Folder>/` and `src/Autheris.Domain/` to exactly one subsystem (list from `ls -d`), so the uncovered test passes without the `*` fallback.
   - Module section: `# subsystem: modules` with one commented template line `# modules/<Name>/ @themulle  # backup: vacant` (activated by WP-SIMPL-25+).
   - Security section (`# security: SEC-SIMPL-71 | primary: @themulle | backup: vacant`): all 18 current entries verbatim, plus `src/Autheris.Infrastructure/Persistence/**`, `src/Autheris.Application/**/Security*/**`, `src/Autheris.Application/**/*Security*.cs`, `src/TrinoSqlEngine/Ast/Security/**` (developer verifies the AST security namespace folder name with `git ls-files src/TrinoSqlEngine | grep -i secur` and fuzz corpus with `git ls-files tests | grep -i fuzz`), `src/Autheris.Api/Hosting/**`, `deploy/**`, `.github/workflows/**`, `tests/**/Security/**`, `docs/adr/**`, `src/Autheris.Api/Security/**`.
4. `.mailmap` (canonical first identity):
   ```
   Manuel Müller <themulle@gmail.com> Manuel <30625725+themulle@users.noreply.github.com>
   Manuel Müller <themulle@gmail.com> <Manuel.Mueller2@liebherr.com>
   Manuel Müller <themulle@gmail.com> themulle <themulle@gmail.com>
   ```
5. Green: all tests pass; run `git shortlog -sne HEAD | head` to show one human identity (plus dependabot).

## 6. Tests (`tests/Autheris.Tests.Architecture/CodeOwnersTests.cs`, all [Trait("Category","Security")])
- `CodeOwnersTests.Every_src_and_module_folder_has_an_owner`: every directory directly under `src/`, under `src/Autheris.Application/`, `src/Autheris.Api/`, `src/Autheris.Extensions/`, and under `modules/` (if it exists) is matched by a non-fallback entry (`*` excluded).
- `CodeOwnersTests.Every_entry_declares_a_backup`: every entry belongs to a section whose header has `backup: <@handle|vacant>`; empty backup fails.
- `CodeOwnersTests.Security_paths_are_owned`: a fixed list of sample paths (one per SEC-SIMPL-71 item, e.g. `src/Autheris.Infrastructure/Persistence/AuditChainAnchorStores.cs`, `src/Autheris.Api/Hosting/FederationMaskingStartupCheck.cs` once merged, otherwise an existing Hosting file, `.github/workflows/ci.yml`, `deploy/<existing file>`, `docs/adr/ADR-019-sql-engine-build-vs-buy.md`) resolves (last match wins) to an entry inside the security section.
- `CodeOwnersTests.Current_security_entries_are_preserved`: the 18 entries listed in section 4 still exist verbatim.
- `CodeOwnersTests.Glob_matching_follows_codeowners_semantics` (Theory): `**`, `*`, anchored, last-match-wins samples.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build --filter "FullyQualifiedName~CodeOwnersTests"`
- `git check-mailmap "Manuel <30625725+themulle@users.noreply.github.com>"` returns the canonical identity.

## 8. Acceptance criteria
- [ ] Uncovered src top-level folders = 0 (test green).
- [ ] Every entry has a backup (`vacant` allowed); all 18 previous entries preserved; SEC-SIMPL-71 paths owned.
- [ ] `.mailmap` maps the three secondary identities to one.
- [ ] PR description states the open SEC-SIMPL-71 two-reviewer requirement as an owner action (D6).

## 9. PR slicing
One PR (~+120 test lines, CODEOWNERS ~+90, .mailmap 3). Touches `.github/CODEOWNERS`, so it needs CODEOWNERS approval (SEC-SIMPL-73), not `[WF]`.

## 10. Risks, rollback, must NOT
- Must NOT drop or weaken any current entry; must NOT rewrite history (mailmap only). Must NOT invent GitHub handles: only `@themulle` and `vacant`.
- Risk: a single owner cannot satisfy "two eligible reviewers" (SEC-SIMPL-71); this is owner-gated, not a developer workaround.
- Rollback: revert the PR.

## 11. Estimate
+~220 lines (test, CODEOWNERS, mailmap), 0 src. 0.5-1 pd.

## Decisions taken in this spec
- One test class `CodeOwnersTests` hosts the plan's `CodeOwnersArchitectureTests` methods and the security test (SEC gate matrix names `CodeOwnersTests`).
- Backup is declared per section header, not per line, to keep the file readable; the test enforces it.
- Second-level folders of Application/Api/Extensions are covered too, since one owner per subsystem is meaningless at the project level.
