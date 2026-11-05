# WP-SIMPL-32 Configuration surface reduction (<= 600 properties)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-32-configuration-surface-reduction-req-simpl-064) | Issues: #514, #515 | Owner decisions: OD-7 (single ReBAC switch), OD-8 | Security conditions: SEC-SIMPL-31, 32, 61, 62, 85

## 1. Goal and outcome
After deletions and module moves, reduce the core configuration surface to: `*Options` classes <= 90 (PRD baseline 173), settable properties <= 600 (~1,173), `bool *Enabled` <= 40 (96), undocumented property names 0 (335). Module options live in modules. Every removed key fails fast at startup.

## 2. Preconditions and dependencies
- Merged: WP-SIMPL-06 (reference generator, `ConfigReferenceTests`), WP-SIMPL-07 (ratchets), WP-SIMPL-26..30 (module moves).
- Must precede WP-SIMPL-33. Not parallel with module WPs touching options.

## 3. Scope
### In scope
- Merge small option classes per subsystem (one `<Subsystem>Options` with nested records).
- Remove `*Enabled` flags of always-on KEEP features; the feature becomes unconditional.
- Move remaining module option classes into modules; lower ratchet baselines to targets.
### Out of scope
- Renaming sections that are not merged; changing defaults of security controls (SEC-SIMPL-62).

## 4. Current state (verified on 63cbb893, before module waves)
- `src/Autheris.Domain/Options/**`: 40 files; ~1,137 `{ get; set|init; }` properties there; 74 `bool *Enabled` properties there. PRD count over all `src/`: 173 classes / ~1,173 props / 96 `*Enabled`.
- Guard: `Api/Configuration/UnknownGatewayKeyGuard.ThrowIfUnknownKeys` rejects keys outside `GatewayConfigurationSchema`.
- Baselines: `tests/Autheris.Tests.Architecture/Baselines/unconsumed-options.txt`, `options-consumed-indirectly.txt`.

## 5. Implementation steps
1. Script `tools/options-inventory/options_inventory.py` (new, no build): lists every `*Options` class, property, consumer count (git grep), `Enabled` flags and security-name match (`Enforce|Require|Allowed|Deny|Mask|Pii|Tls|Auth`). Output CSV into `artifacts/` (not committed); summary numbers in the PR.
2. Red: add `OptionsBudgetTests` (section 6) with targets; fails today.
3. For each `*Enabled` flag: if security-named or guarding a fail-closed default -> keep or make unconditional-secure (SEC-SIMPL-32); else if the feature is KEEP -> delete flag, feature always on; if MODULE -> module presence replaces the flag.
4. Merge classes per subsystem; keep configuration paths stable where possible; changed paths are listed in the migration guide and added to `UnknownGatewayKeyGuard` removed-key list with replacement message (SEC-SIMPL-31; fails also for `false`/empty).
5. Regenerate configuration reference (WP-SIMPL-06 generator); document every remaining property (undocumented = 0).
6. Lower ratchet baselines to targets; update `appsettings.json` and Helm values.

## 6. Tests
- `tests/Autheris.Tests.Architecture/OptionsBudgetTests`: `Options_classes_at_most_90`, `Settable_properties_at_most_600`, `Enabled_flags_at_most_40`, `No_undocumented_properties`.
- `OptionsAuditTests.No_enforcement_option_deleted_while_wired` [Security] (SEC-SIMPL-32).
- `SecureDefaultsSnapshotTests.Security_defaults_unchanged` [Security] (SEC-SIMPL-62).
- `ConfigReferenceTests.Secret_properties_have_no_default` [Security] (SEC-SIMPL-61).
- `RemovedConfigKeysTests.Removed_key_fails_fast(string key)` [Theory, Security] - one row per removed key including `Enabled=false`.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "Category=Security"`
- `python3 tools/options-inventory/options_inventory.py --summary`

## 8. Acceptance criteria
- [ ] Options <= 90, props <= 600, `*Enabled` <= 40, undocumented 0 (OptionsBudgetTests green).
- [ ] Every removed key in `RemovedConfigKeysTests` and failing fast.
- [ ] Security defaults snapshot unchanged; no wired enforcement option removed.

## 9. PR slicing
One PR per subsystem group (Security/Identity, Query/Sql, Governance/Audit, Runtime/State, Catalog), each <= 400 added src lines; final PR lowers baselines and enables `OptionsBudgetTests`.

## 10. Risks, rollback, must NOT
- Risk: removing an `Enabled` flag that guarded a fail-closed default -> SEC-SIMPL-32 audit per PR.
- Must NOT: make a security control configurable off; give secret-like properties defaults; silently ignore old keys.

## 11. Estimate
+~500 / -~2,500 src. 4 person-days.

## Decisions taken in this spec
- Budget counted over core `src/` only (module options excluded), consistent with PRD targets for the core.
