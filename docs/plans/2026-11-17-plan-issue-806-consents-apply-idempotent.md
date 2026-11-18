# PLAN-806: consents/apply idempotency for columns with several rules (issue #806)

Issue: [#806](https://github.com/themulle/autheris/issues/806). Reported against v1.8.4, reproduced on `main` @ `595770e4`. Bug fix, no new options.

## 1. Problem

Re-applying an unchanged consent manifest revokes and re-creates every policy that has more than one rule for a column (the normal
base rule plus scope tightening, e.g. `email: Clear` and `email: Mask [Agent.Read]`). The reporter measured `applied 11, unchanged 16,
revoked 11` for an unchanged manifest of 27 group policies; each re-apply writes revocations, new consents, grant records, audit entries
and bumps the epoch.

## 2. Root cause

`DeclarativeConsentApplyService.IsIdentical` (`src/Autheris.Application/Governance/Services/DeclarativeConsentApplyService.cs:725-762`)
pairs each desired rule with the first stored rule of the same column name:

- `:736` `existing.ColumnRules.FirstOrDefault(c => c.ColumnName == desired.ColumnName)`: the second rule of a column is compared with
  the first stored one, so the policy never counts as unchanged (the reported churn).
- The same lookup is also fail-open: desired `[email Mask, email Mask]` against stored `[email Mask, email Clear]` reports `unchanged`
  although the stored base rules resolve to `Clear` (base rules take the maximum).
- `:743-744` scopes: count plus "every stored scope is desired" ignores duplicates. Stored `[Agent.Read, Agent.Read]` against desired
  `[Agent.Read, Report.Read]` reports `unchanged` although the rule no longer covers `Report.Read`.
- `:753` row filters: the same `FirstOrDefault` by column name (churn for two filters on one column, fail-open for duplicates);
  `ValueType` is not compared at all.
- `:728` validity: only individual grants compare `validUntil`; a group policy whose `validUntil` moves earlier reports `unchanged`, so
  the stored grant outlives the manifest.
- Write rules, row scopes and classification access of the existing consent are not compared. A non-declarative consent for the same
  grantee and table that carries them counts as `unchanged` although the manifest would create a consent without them.

## 3. Design

`IsIdentical` compares the existing consent with exactly what the create path would store:

- Effect equal; `validUntil` earlier than the stored `ValidTo` is a change for every grantee type (a later one stays `unchanged`: a
  re-apply never extends a grant, unchanged documented behaviour).
- The existing consent has no write rules, no row scope entries and no classification access (the manifest creates none).
- Column rules: multiset equality of `(ColumnName, AccessLevel, scope set)`, independent of order. The access level is parsed exactly as
  the create path does (one shared helper: case-insensitive name, unknown or undefined value = `Deny`). The scope set is the stored form:
  entries split on `,`, trimmed, empty ones dropped (what `GovernanceRepository` writes and reads back), de-duplicated and sorted.
- Row filters: multiset equality of `(FilterGroup, FilterType, ColumnName, Operator, ValueType, ValueJson, ValueSource ?? "LITERAL",
  UserAttribute)`; the desired side uses `FilterGroup = 0` and `SimpleColumnPredicate`, as the create path does.
- Strings are compared ordinally. The create path stores names, operators, values and scopes verbatim, so an unchanged manifest is
  always equal; a case-only edit is treated as a change (one revoke/create, never a wrong `unchanged`).
- Duplicate rules count: a manifest with a rule twice is `unchanged` against a store that holds it twice (the create path stores both)
  and `applied` against a store that holds it once.

Pattern grants (review finding): the subject read `GetActiveConsentsForSubjectsAsync` also returns the grantee's pattern grants of the
whole domain (`GovernanceRepository.Consent.cs:192`), and `MatchesGrantee` compares only grantee type and SID. A pattern grant could
therefore count as the identical consent, and the change path revoked it. `activeExisting` keeps only non-pattern consents for exactly
this table (`TableIdentifier.Equals`, case-insensitive like everywhere else).

Blank scope entries (review finding, separate commit): a scope list such as `[" "]` is stored as `''` and read back as no scopes, so a
scope rule silently becomes a base rule for every scope. The manifest validation rejects blank scope entries and entries with a comma
(the store separates scopes with commas) with `400` before anything is read or written; the consent request decision path
(`ConsentDecisionPipeline`, column masking input) gets the same check.

Out of scope (open point for the owner): when an identical consent exists, other active consents of the same grantee and table are kept
(`:273`); the change path revokes them all. Unchanged behaviour.

## 4. Work packages

- WP-806-1: tests first (`tests/Autheris.Tests.Unit/Governance/DeclarativeConsentApplyIdempotencyTests.cs`), red on `main`.
- WP-806-2: rewrite `IsIdentical` with key records and a multiset comparison; share the access level parsing with the create path;
  pass `validUntil` for every grantee type.
- WP-806-3: `CHANGELOG.md` entry under `[Unreleased] / Fixed`.

## 5. Test plan

Unchanged (no replace call, no audit entry): base rule plus scope rule re-applied; rules and scopes in a different order; duplicate
scopes in a rule; the same rule twice; two row filters on one column (same and different order); a later group `validUntil`.

Applied (`[Trait("Category", "Security")]`): scope set changed (added scope, case change), one rule removed, the level of the second
rule changed, a stored looser duplicate base rule, duplicate stored scopes against a new scope, a duplicated rule removed, row filter on
the same column changed (duplicate, other value), row filter `ValueType` or operator changed, a filter removed, earlier group `validUntil`, an existing consent with write rules.

## 6. Security conditions

- **SEC-806-1 (fail closed):** `unchanged` only when the stored consent equals what the create path would write for the policy; every
  difference in effect, earlier expiry, column rule, scope, row filter, write rule, row scope or classification access goes through the
  revoke/create path.
- **SEC-806-2:** no new output; the apply response keeps its counts and detail lines (no stored rule contents are echoed).
- **SEC-806-4:** a pattern grant of the grantee is never the identical consent and is never revoked by a manifest policy for one table.
- **SEC-806-5:** a scope entry that is blank or contains a comma is rejected (`400`, fixed message naming the policy index) in dry-run
  and apply; nothing is written.
- **SEC-806-3:** the comparison never throws on malformed input (null names or scope entries); it runs before any write, also in dry-run.
