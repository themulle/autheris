# WP-SIMPL-32 Configuration surface reduction (<= 600 properties)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-32-configuration-surface-reduction-req-simpl-064) | Issues: #514, #515 | Owner decisions: OD-30 (ReBAC keys removed by WP-SIMPL-46, SEC-AZ-08; nothing ReBAC-related is left here), OD-8, OD-3 and OD-26 (opt-in write switches stay), OD-18 (procedures opt-in stays), OD-24 (OData core) | Security conditions: SEC-SIMPL-31, 32, 61, 62

> **Rule (plan resolution R13):** opt-in switches of kept features stay (e.g. `SqlEndpoints:Procedures:Enabled`, `WebSql:Enabled`, write switches). This WP deletes only flags of deleted features and default-on flags of always-on core features.

## 1. Goal and outcome
After deletions and module moves, reduce the core configuration surface to: `*Options` classes <= 90 (PRD baseline 173), settable properties <= 600 (~1,173), `bool *Enabled` <= 40 (96), undocumented property names 0 (335). Module options live in modules. Every removed key fails fast at startup.

## 2. Preconditions and dependencies
- Merged: WP-SIMPL-06 (reference generator, `ConfigReferenceTests`), WP-SIMPL-07 (ratchets), WP-SIMPL-27..29 (module moves; WP-SIMPL-26 is OData core hardening, WP-SIMPL-30 is cancelled), WP-SIMPL-46 (ReBAC keys already removed).
- Must precede WP-SIMPL-33. Not parallel with module WPs touching options.

## 3. Scope
### In scope
- Merge small option classes per subsystem (one `<Subsystem>Options` with nested records).
- Remove `*Enabled` flags of deleted features and default-on `*Enabled` flags of always-on KEEP features; the feature becomes unconditional. Opt-in switches (default off) of KEEP features stay (step 3).
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
3. For each `*Enabled` (or `Allow*`) flag, in this order:
   - security-named or guarding a fail-closed default -> keep or make unconditional-secure (SEC-SIMPL-32);
   - opt-in switch of a KEEP feature (default off) -> keep. These include `SqlEndpoints:Procedures:Enabled` (OD-18, WP-SIMPL-39 kill switch), `WebSql:Enabled` (`WebSqlOptions.Enabled`, default false; WP-SIMPL-38 relies on it), `WebSqlOptions.AllowDml` and `WebSql:Dml:AllowDeleteGrants` (OD-3, OD-26: DELETE disabled by default), `ForwardAuthOptions.Enabled` (default false), and any switch named by an OD or by a WP "Decisions taken" section;
   - default-on flag of a KEEP feature -> delete flag, feature always on (removed key fails fast, step 4);
   - flag of a deleted feature -> delete with the feature's keys (removed key fails fast);
   - flag of a MODULE feature -> module presence replaces the flag.
   The inventory CSV of step 1 carries the chosen branch per flag; the PR lists every kept opt-in switch.
4. Merge classes per subsystem; keep configuration paths stable where possible; changed paths are listed in the migration guide and added to `UnknownGatewayKeyGuard` removed-key list with replacement message (SEC-SIMPL-31; fails also for `false`/empty).
5. Regenerate configuration reference (WP-SIMPL-06 generator); document every remaining property (undocumented = 0).
6. Lower ratchet baselines to targets; update `appsettings.json` and Helm values.

## 6. Tests
- `tests/Autheris.Tests.Architecture/OptionsBudgetTests`: `Options_classes_at_most_90`, `Settable_properties_at_most_600`, `Enabled_flags_at_most_40`, `No_undocumented_properties`.
- `OptionsAuditTests.No_enforcement_option_deleted_while_wired` [Security] (SEC-SIMPL-32).
- `OptionsAuditTests.Opt_in_switches_retained` [Security] (R13): the opt-in switches listed in step 3 exist, default to off and still gate their feature (Theory, one row per switch).
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
- [ ] Opt-in switches of KEEP features retained with default off (`Opt_in_switches_retained` green).

## 9. PR slicing
One PR per subsystem group (Security/Identity, Query/Sql, Governance/Audit, Runtime/State, Catalog), each <= 400 added src lines; final PR lowers baselines and enables `OptionsBudgetTests`.

## 10. Risks, rollback, must NOT
- Risk: removing an `Enabled` flag that guarded a fail-closed default -> SEC-SIMPL-32 audit per PR.
- Must NOT: make a security control configurable off; delete an opt-in switch of a kept feature (turning an off-by-default feature on); give secret-like properties defaults; silently ignore old keys.

## 11. Estimate
+~500 / -~2,500 src. 4 person-days.

## Decisions taken in this spec
- Budget counted over core `src/` only (module options excluded), consistent with PRD targets for the core.
- Opt-in switches of kept features count against the `*Enabled` budget and are never removed to meet it (R13).
