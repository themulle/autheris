# Plan: ARCH-MNT-22 step 1 - dialect dedup of ConsentGrants, ITSM Outbox and WebhookOutbox

Parent: `docs/plans/2026-10-26-plan-architecture-maintainability.md` (row ARCH-MNT-22, evidence REV-05).
Phase 2 (`csharp-architect`), branch `refactor/architecture-maintainability-10`, base `origin/main` a19a3a50.
Owner rules: only if time permits; one PR per work package; behaviour-preserving; security must not regress; no DDL.

## 1. Measured duplication (current branch)

Normalised lines = trimmed, blank/comment/brace-only lines dropped, provider names (`PostgreSql|SqlServer|Sqlite|Npgsql|SqlClient`) masked; `difflib` matching lines.

| Partial | Raw lines PG / MSSQL / SQLite | Normalised | PG~MSSQL | PG~SQLite | MSSQL~SQLite | Identical in all 3 |
|---|---|---|---|---|---|---|
| `ConsentGrants` | 284 / 283 / 283 | 180 / 179 / 179 | 95% / 96% | 83% / 84% | 83% | 148 (82%) |
| `Outbox` (ITSM) | 167 / 166 / 186 | 126 / 124 / 135 | 92% / 94% | 89% / 83% | 84% / 77% | 103 (82%) |
| `WebhookOutbox` | 179 / 184 / 172 | 127 / 127 / 127 | 83% | 76% | 69% | 85 (67%) |

Total today: 1,904 raw lines in 9 files.

True differences (everything else is copy): SQL text (paging `LIMIT`/`TOP`/`OFFSET FETCH`; insert-if-absent `ON CONFLICT`/`NOT EXISTS ... UPDLOCK, HOLDLOCK`; claim `FOR UPDATE SKIP LOCKED ... RETURNING`/`UPDLOCK, READPAST ... OUTPUT inserted.`/`IN (...) RETURNING`; row locks `FOR UPDATE OF c`/`WITH (UPDLOCK, ROWLOCK)`/none; case folding `LOWER()`/`*_lc` columns/`COLLATE NOCASE`; `dbo.` prefix), connection acquisition (`OpenConnectionAsync` vs SQLite `ReadScopeAsync`/`WriteScopeAsync`/`WriteAsync` gates), the SQL Server unique-violation catch (2627/2601) on webhook enqueue, and the PG typed `@status` parameter.

Behavioural divergences found (NOT fixed in step 1; preserved and made explicit, see Phase 3 questions):
- D1 revoke audit `Decision`: SQLite `"REVOKED"`, PG/MSSQL `"REVOKE"`.
- D2 revoke tenant parse: SQLite `new TenantId(raw)` (throws), PG/MSSQL `TenantId.TryParse` with silent fallback to `LegacySingleTenant`.
- D3 `ReplaceConsentsAsync`: PG/MSSQL evict `_metadataCache`, SQLite does not (SQLite has the cache).
- D4 revoke approver check: PG/MSSQL `IsAuthorizedApproverForTableInternalAsync(table, actor, null, ct)` outside the transaction; SQLite on the same connection.
- D5 webhook `FencedUpdateAsync`: PG/MSSQL bind `@status/@error/@next` only when the set clause uses them (PG types `@status` as `Integer`); SQLite binds all three always.

## 2. Decisions

1. **Pattern: per-dialect SQL text + shared engine-neutral core** (precedent `CdcStreamSqlCore`). Per area one `internal static` core over `DbConnection`/`DbTransaction`/`DbCommand`/`DbDataReader` owns parameter binding, row mapping and the shared statements. Each per-dialect partial keeps only: its SQL text, connection/scope acquisition, transaction orchestration, audit/authorisation calls and the true differences above. No ORM, no SQL builder, no string templating beyond what exists today.
2. **SQL text location.** Dialect SQL lives in a nested `private static class {Area}Sql` inside the per-dialect partial (`ConsentGrantSql`, `ItsmOutboxSql`, `WebhookOutboxSql`). Statements byte-identical in all three dialects move once into the core as `const string` (ITSM `Insert`, `MarkCompleted`, `MarkFailed`). Literals are moved **verbatim** (same literal kind, same embedded indentation, same concatenations/interpolations); no raw-string (`"""`) conversion. Dynamic statements become a static method in `{Area}Sql` whose body is the original expression (`ActiveRoleConsents(bool withTenant)`, `ListDeadLetters(WebhookOwnerScope)`, `FencedUpdate(string setClause)`).
3. **Binding.** The core binds via one helper `DbCommand.AddParameter(name, value)` = `CreateParameter()` + `ParameterName` + `Value`, which yields the same provider parameter as `AddWithValue` (Npgsql/SqlClient/SQLite `AddWithValue` is `new XParameter(name, value)`). Dialect-specific binding (D5, PG `NpgsqlDbType.Integer`) stays in the partial as a `static void Bind{X}(DbCommand, ...)` passed to the core as a delegate.
4. **Clock.** Wall-clock reads stay at the same evaluation point: cores take `TimeProvider clock`, partials pass `TimeProvider.System` (`GetUtcNow()` equals `DateTimeOffset.UtcNow`). No `DateTime(Offset).UtcNow` text in new files (ratchet `ClockAndHttpClientRatchetTests`); partial counts only fall.
5. **ConsentGrants scope (security-driven).** Only statement-level pieces move: `ReadActiveRoleConsentsAsync` (binding + mapping), `UpdateDescriptionInTransactionAsync` (select valid_to, expiry check, update), `ReadRevokeTargetAsync` (returns `RevokeTarget(TableIdentifier, string? TenantRaw, bool IsGranteeSelf, bool AlreadyRevoked)`), `MarkRevokedAsync`. Transactions, `RecordAuditEvent*`, epoch bump, cache eviction, approver check, tenant parse (D2) and the audit entry literal (D1) stay in each partial. `HydrateConsentsBatchAsync` and `InsertConsentCoreAsync` are untouched (`ConsentMaterializationGuardArchitectureTests` reads `*.Consent.cs`).
6. **Naming guard.** Core types must not contain `GovernanceRepository` in their name (`ConsentGrantSqlCore`, `ItsmOutboxSqlCore`, `WebhookOutboxSqlCore`) so `ConsentQueryTenantArchitectureTests` keeps inspecting them (it skips `*GovernanceRepository*`).
7. **Kept in partials verbatim:** SQL Server `catch (SqlException ex) when (ex.Number is 2627 or 2601)`, SQLite `WriteAsync`/`ReadScopeAsync(snapshot: true)` wrappers, `TransactionRollback`/`OnTransaction*` hooks. Cores never open, gate or dispose connections.
8. **DDL untouched.** No file under `Persistence/Migrations/**`, no schema resource, no `WebhookOutboxMapping.cs` change. Reviewer gate: `git diff --stat` shows none of these.

## 3. Proof of byte-identical SQL (Docker unavailable locally)

- **L1 static golden (WP0, generated from the current code FIRST, before any production change).** `DialectSqlFragmentExtractor` (Mono.Cecil, already in the Architecture tests via NetArchTest) reads the built `Autheris.Infrastructure` and collects, per dialect and area, every `ldstr` operand and every `ldsfld` of a `static string` field (rendered `{field:Type.Name}`) from: the area's manifest methods in `{Dialect}GovernanceRepository` (including compiler-generated async/lambda types attributed by the `<Name>` prefix), all methods of the nested `{Area}Sql` type (if present) and the `{Area}SqlCore` type (if present). Output: `tests/Autheris.Tests.Architecture/Golden/DialectSql/{Dialect}.{Area}.txt` with a `## set` section (sorted distinct strings) and one `## method <Name>` section per manifest method (ordered fragments plus a `set_Transaction` count). One C#-escaped fragment per line (`\n`, `\r`, `\t`, `\\`), so line endings are explicit. Writing requires `AUTHERIS_APPROVE_DIALECT_SQL=1`; a missing golden fails (fail-closed). Post-refactor the `## set` section must be unchanged; the strings include SQL, parameter names, audit event/decision literals (D1) and exception messages.
- **L2 runtime golden (per WP, Unit tests).** `RecordingDbConnection` (WP0) records `CommandText`, parameters (name, value, `DbType`, provider type) and whether `Transaction` was set; `CreateDbParameter` returns the real provider parameter of the dialect under test; readers come from `DataTable`. Each core method is driven with each dialect's `{Area}Sql` statements and asserts: `CommandText` equals the golden fragment(s) of that method (explicit concatenation per variant), parameter names in binding order equal the method's `@...` fragments, each parameter's `DbType`/provider type equals a reference built with the provider's `AddWithValue`, and the `Transaction`-set count equals the golden.
- **Tenant predicate test.** `DialectSqlGoldenArchitectureTests.TenantScopedStatements_KeepTheirTenantPredicate` (WP0) asserts per dialect that the area set contains: ConsentGrants `c.tenant_id = @tenantId` and both `tenant_id = @t AND is_revoked = 0` statements; WebhookOutbox `owner_tenant_id = @tenant` and `owner_tenant_id IS NULL`. Per WP, L2 adds `{Method}_WithTenant_BindsTenantPredicate` theories (dialect x variant) asserting predicate text and bound tenant value. Exempt (documented in the test): revoke select/update (id-keyed under row lock, tenant read from the row), ITSM outbox (system queue).

## 4. Work packages (no shared files; WP0 merges first, WP1-WP3 in any order, then WP4)

| WP | Files (new / changed) | Tests |
|---|---|---|
| WP0 golden (tests only) | new `tests/Autheris.Tests.Architecture/DialectSql/DialectSqlFragmentExtractor.cs`, `DialectSqlGoldenArchitectureTests.cs`, `Golden/DialectSql/*.txt` (9); new `tests/Autheris.Tests.Unit/Persistence/DialectSql/RecordingDbConnection.cs`, `DialectSqlGolden.cs` (golden reader) | `GoldenMatchesCurrentCode_{Dialect}_{Area}` (9), `EveryManifestMethod_HasFragments`, `TenantScopedStatements_KeepTheirTenantPredicate`, `RecordingDbConnection_RecordsCommandsAndParameters` |
| WP1 ITSM outbox | new `src/Autheris.Infrastructure/Persistence/ItsmOutboxSqlCore.cs`; changed `{PostgreSql,SqlServer,Sqlite}GovernanceRepository.Outbox.cs` | new `tests/Autheris.Tests.Unit/Persistence/DialectSql/ItsmOutboxSqlCoreGoldenTests.cs` (5 methods x 3 dialects) |
| WP2 webhook outbox | new `.../Persistence/WebhookOutboxSqlCore.cs`; changed `*GovernanceRepository.WebhookOutbox.cs` (3) | new `.../DialectSql/WebhookOutboxSqlCoreGoldenTests.cs` (incl. D5 binding, cluster/tenant dead-letter variants) |
| WP3 consent grants | new `.../Persistence/ConsentGrantSqlCore.cs`; changed `*GovernanceRepository.ConsentGrants.cs` (3) | new `.../DialectSql/ConsentGrantSqlCoreGoldenTests.cs` (incl. tenant/no-tenant variants, `RevokeTarget` mapping) |
| WP4 housekeeping | `tests/Autheris.Tests.Architecture/Baselines/datetime-now-counts.txt` (lower the 6 lines only if the ratchet hints) | ratchet green |

Every WP runs the existing tests unchanged; no existing test is edited.

## 5. Verification

- Unit (one build at a time; disk budget): SQLite paths `SqliteWebhookOutboxContractTests`, `WebhookOutboxContractTests`, `ItsmOutboxTests`, `PostgreSqlGovernanceProviderTests`, `ConsentReplaceRepositoryTests`, `SetConsentDescriptionTests`, `SqliteConsentRevocationAndWithdrawTests`, `SqliteGovernanceConcurrencyTests`, `ConsentAuditAnchorAckTests`, `ConsentQueryEquivalenceTests`, plus the new L2 tests.
- Architecture: all, notably `ConsentQueryTenantArchitectureTests`, `AuditArchitectureTests`, `ConsentMaterializationGuardArchitectureTests`, `IndividualGrantPolicyArchitectureTests`, `ClockAndHttpClientRatchetTests`, `SourceSizeRatchetArchitectureTests`, L1 golden.
- Integration (CI only, Docker): `PostgreSqlWebhookOutboxContractTests`, `SqlServerWebhookOutboxContractTests`, `PostgreSqlConsentContractTests`, `SqlServerConsentContractTests` (`ConsentQueryContractTestsBase`). Gap: no PG/MSSQL integration test for the ITSM outbox; L1 + L2 carry that proof. Merge gate: CI integration job green.

## 6. Expected LOC (production, raw lines)

| Area | Today | After (3 partials + core) | Delta |
|---|---|---|---|
| ITSM outbox | 519 | ~60+60+75 + ~130 = ~325 | about -195 |
| WebhookOutbox | 535 | ~95 x 3 + ~110 = ~395 | about -140 |
| ConsentGrants | 850 | ~160 x 3 + ~95 = ~575 | about -275 |
| Total | 1,904 | ~1,295 | about -610 (32%) |

Tests add about +450 (WP0 ~250, WP1-WP3 ~70 each) plus 9 golden files.

## 7. Risks

- R1 Indentation drift when moving verbatim literals: L1 `## set` diff fails.
- R2 Parameter type inference change: L2 compares against provider `AddWithValue` references.
- R3 Lost `cmd.Transaction = tx`: L2 `set_Transaction` count.
- R4 SQLite gate/lock semantics: cores never touch scopes; SQLite unit suite.
- R5 SQL Server race catch dropped: kept in partial; MSSQL webhook integration test (CI); reviewer checklist.
- R6 Divergences D1-D5 silently unified: L1 (literals) and L2 (binding) fail; any fix is a separate behaviour-change PR.
- R7 Extractor attribution of compiler-generated methods changes with refactor: `EveryManifestMethod_HasFragments` pre-refactor; post-refactor only `## set` is compared.

## 8. Security questions for Phase 3 (light)

1. D1/D2: keep both as-is in step 1 and open a follow-up behaviour fix (one audit decision literal; strict tenant parse instead of the PG/MSSQL fallback to `LegacySingleTenant`)?
2. D3: is the missing SQLite `_metadataCache` eviction in `ReplaceConsentsAsync` a stale-authorisation risk needing its own fix?
3. D4: approver check outside the revoke transaction on PG/MSSQL; accept for step 1?
4. Is L1 + L2 + CI integration sufficient as the merge gate without local Docker?
5. Confirm the tenant-predicate exemptions (revoke by id under lock; ITSM system queue) and Decision 6 (core names outside the `GovernanceRepository` exemption) as binding conditions.
