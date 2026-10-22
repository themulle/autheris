# Plan: Security Findings, Area INF (+ OPS-01)

Source: `docs/plans/security.txt`. Scope is every `INF-*` finding rated HIGH or MEDIUM, plus `OPS-01`. LOW findings (INF-02, 03, 07, 10, 12, 13, 14) are out of scope.
Branch: `fix/security-review-open-findings`. Verified against HEAD `55dc7e27`. There is no production deployment yet, so breaking changes are allowed.

| ID | Sev | Verified status |
|----|-----|-----------------|
| INF-01 | HIGH | Mostly fixed in `c74dbb4f`. A residual gap remains (R1) |
| INF-08 | HIGH | Confirmed |
| INF-11 | HIGH | Confirmed |
| INF-04 | MED | Confirmed (latent: the service has no caller) |
| INF-05 | MED | Confirmed (latent: no upstream loader is registered) |
| INF-06 | MED | Confirmed |
| INF-09 | MED | Confirmed |
| INF-15 | MED | Confirmed |
| OPS-01 | MED | Confirmed (local `.git/config` only). Fixed by operations, no code change |

## Findings

### INF-11: `datasource:` refs escape their namespace (HIGH)
- **Root cause.** `DefaultEnvironmentSecretProvider.GetSecretBytes` (lines 51-215) returns a ref only if `_inMemorySecrets` has an exact hit. Otherwise it applies `Uri.TryCreate`, taking segment[0] as an alias (line 116). It then tries the `:`→`__` and `-` aliases (line 133) and resolves those through `IConfiguration` and the environment (lines 182-208). A ref like `datasource:Gateway:DataMasking:HmacSecret` therefore resolves operator secrets. `DatasourceSecretRefPolicy.ValidateClientRef` only checks the prefix.
- **Fix.**
  1. At the start of `GetSecretBytes`, check `DatasourceSecretRefPolicy.IsInDatasourceNamespace(secretRef)`. If true, resolve only from the server-managed store (`_inMemorySecrets`). If that misses, throw `KeyNotFoundException("datasource secret not found")`. The URI, alias, config, env, file and well-known fallbacks are never applied to these refs.
  2. Restrict the URI alias branch to `uri.Scheme == Uri.UriSchemeHttps` and to a host that ends in `.vault.azure.net` (or a configurable `Gateway:Secrets:VaultHostSuffixes`). Every other scheme skips the URI branch.
  3. `DatasourceSecretRefPolicy.ValidateClientRef` additionally rejects any client ref whose remainder after the prefix contains a known operator config root (`Gateway:`, `ConnectionStrings:`, `Authentication:`). This is defence in depth only; step 1 is the real fix.
  4. Default taken: datasource secrets are not persisted in this package. After a restart, a server-generated ref fails closed. The datasource then reports an auth error and must be re-credentialed. Durable encrypted storage is tracked as dependency D1.
- **Fail-closed.** Any `datasource:` ref that is not in the managed store throws. It never falls back to operator configuration.
- **Red tests** (`tests/Autheris.Tests.Unit/Security/DatasourceSecretNamespaceTests.cs`):
  - `DatasourceRef_MatchingOperatorConfigKey_IsNotResolved` (config contains `Gateway:DataMasking:HmacSecret`)
  - `DatasourceRef_MatchingEnvVarAlias_IsNotResolved`
  - `DatasourceRef_CaseVariantOfOperatorKey_IsNotResolved`
  - `DatasourceRef_SetViaSetSecret_IsResolved`
  - `NonHttpsUriRef_DoesNotUseSegmentAlias`
  - `HttpsVaultUri_StillResolvesSegmentAlias`
  - `ValidateClientRef_RejectsOperatorRootUnderDomainPrefix`
  - Executor-level test `DeclarativeHttpExecutor_DatasourceRefToOperatorKey_FailsWithoutSendingSecret`
- **Breaking.** Datasource secrets are lost on restart.

### INF-08: stream RLS tenant taken from raw claims (HIGH)
- **Root cause.** `StreamRlsPolicyEnforcer.EvaluateAndMaskAsync` (lines 88-91) reads the tenant from `FindFirst("tenant_id"|"tenant"|"tid")`. The correct source is `ClaimsPrincipal.GetTenantId()` (`Sid.cs:174`), which applies the realm `TenantBinding` first. `ClaimsNormalizer` (lines 109-125) adds `tenant_id` only when the claim is absent, so an IdP-supplied `tenant_id` survives.
- **Fix.**
  1. Enforcer: `var tenantId = subscriber.GetTenantId();`. Deny with `"Tenant mismatch"` when a raw `tenant_id` claim is present and differs (ordinal ignore-case) from `tenantId.Value`. Then compare that tenant with `cdcEvent.TenantId`. Remove the `TryParse` of the raw string. The consent lookup, Casbin and masking all use this resolved tenant.
  2. `ClaimsNormalizer`: when the principal has a realm with a `Fixed` or `ClaimMapped` binding, resolve the tenant through `GetTenantId()`, remove every existing `tenant_id` claim and add the bound value. The realm binding wins and the IdP claim is overwritten.
  3. GraphQL `Subscription` already checks with `GetTenantId()`. No change there.
- **Fail-closed.** If the realm lookup is missing or the binding cannot resolve, `GetTenantId` returns the legacy tenant, and that never matches a real event tenant, so the event is denied. Conflicting claims are denied.
- **Red tests** (`tests/Autheris.Tests.Unit/Streaming/StreamRlsTenantBindingTests.cs`):
  - `FixedRealmBinding_IdpTenantClaimForOtherTenant_EventDenied`
  - `FixedRealmBinding_EventOfBoundTenant_Allowed`
  - `ClaimMappedBinding_TidGuid_ResolvesBoundTenant`
  - `RawTenantClaimConflictsWithBinding_Denied`
  - In `ClaimsNormalizerTests`: `RealmBinding_OverwritesIdpTenantIdClaim`
- **Breaking.** Tokens that carry a conflicting `tenant_id` lose their streaming access.

### INF-01: lost consent revocation on Redis INCR failure (HIGH, mostly fixed)
- **Verification.** The current `EpochValidationService` already has these guards:
  - `RaiseEpochFloor` and `_pendingRedisBumps` in `InvalidateEpochAsync`
  - a floor-aware `ReconcileEpochAsync`
  - the read path returns non-authoritative while a bump is pending
  - `OnConnectionRestoredAsync` flushes pending bumps
  - peers raise a floor when they receive the invalidation

  The 8 tests in `tests/Autheris.Tests.Unit/Cache/EpochRevokeRedisFailureTests.cs` cover this. The finding is fixed as described.
- **Residual R1.** Floors and pending bumps live only in memory. Two windows remain:
  - The revoking node crashes before its flush.
  - A peer missed the pub/sub message during the outage, so it has no floor, and it reads Redis after reconnect before the revoker has flushed.

  In both cases a stale ALLOW can be served until the decision TTL expires.
- **Fix R1 (small).**
  1. In `OnConnectionRestoredAsync`, peers treat every table they have L1 epochs for as floored. In practice that means calling `RaiseEpochFloor` for each key in `_epochs` before the clear, so after reconnect each table needs one Redis INCR/advance. Alternative (default taken): set `_reconnectGraceUntil = now + Options.ReconnectEpochGrace` (default 5 s). Until then, `GetCurrentEpochCoreAsync` returns `Authoritative=false`, which forces re-evaluation without any extra Redis writes.
  2. Repositories already let a failed `PublishAsync` propagate. No change.
  3. The crash window is accepted. The bound is the consent decision TTL. Recorded as dependency D2 (an epoch outbox if Phase 3 requires it).
- **Red tests** (add to the existing file):
  - `Peer_AfterReconnect_WithinGrace_TreatsRedisEpochAsNonAuthoritative`
  - `Peer_AfterGraceExpired_RedisAuthoritativeAgain`

### INF-04: GovernedTableCacheExecutionService consent/masking (MEDIUM, latent)
- **Root cause.** Verified in `GovernedTableCacheExecutionService.cs`:
  - Line 105: the consent subject is `new Sid(Identity?.Name ?? "anonymous")` and the group set is empty.
  - Lines 53 and 64: the consent service is optional, and the gate is skipped when it is null.
  - Lines 221-241: only REDACT, HASH (unsalted SHA-256) and PARTIAL are handled. Any other mask passes the raw value.
  - Line 82: `columnMasks` is used with whatever comparer the caller passed.
- **Fix.**
  1. Constructor: make `IConsentResolutionService consentService` required and add a required `IColumnMaskingProvider maskingProvider`. Change the parameter to `IReadOnlyDictionary<string, MaskingRule>? columnMasks`, so masks go through the central `ColumnMaskingProvider.MaskValue` (HMAC with the keyed secret).
  2. Subject: `caller.Principal.GetUserSid()`. If it is null, throw `SecurityException(ConsentRequiredCode)`. Pass `subjectGroupSids: GetGroupSids()` and `allUserSids: GetAllUserSids()`.
  3. Masks: copy them into `new Dictionary<string, MaskingRule>(columnMasks, StringComparer.OrdinalIgnoreCase)`. If a masked column has a rule the provider cannot apply (it throws, or returns the raw value for an unknown type), replace the value with `null` and log `SEC-BL04-03 unknown mask`.
  4. Remove the local SHA-256 HASH branch.
- **Fail-closed.** Any of these cause denial: no user SID, no consent service (DI fails at startup), or an unknown mask type (the value is nulled).
- **Red tests** (`tests/Autheris.Tests.Unit/Caching/GovernedTableCacheConsentMaskTests.cs`):
  - `NameClaimEqualToGrantedSid_WithoutSidClaim_Denied`
  - `PrincipalWithoutSid_Denied`
  - `GroupConsent_ViaGroupSid_Allowed`
  - `UnknownMaskType_ValueIsNulled`
  - `HashMask_UsesKeyedProvider_NotPlainSha256`
  - `MaskLookup_IsCaseInsensitive`
  - `Constructor_WithoutConsentService_Throws`
- **Breaking.** The constructor and method signatures change. The service has no callers, so nothing else breaks.

### INF-05: table cache encryption uses the key name as key material (MEDIUM, latent)
- **Root cause.** Verified in `DuckDbPersistentTableCacheStore.cs`:
  - Line 59: the key is `UTF8(KeyVaultKeyName.PadRight(32,'0'))`.
  - Lines 454-461: `EncryptFileAtRestAsync` writes `.enc` but leaves the plaintext `generation_N.duckdb`.
  - Lines 463-472: `EnsureDecryptedFileAsync` writes plaintext back to disk.
  - Lines 228-242: the active view opens the plaintext file.
- **Fix.**
  1. Constructor: inject `IKeyVaultSecretProvider`. When `Encryption.Enabled` is true, take the KEK from `GetSecretBytes(Encryption.KeyVaultKeyName)` and require at least 32 bytes. If it is missing or too short, throw `InvalidOperationException` at construction. `customEncryptionKey` stays as an `internal` test seam only.
  2. Use DuckDB native encryption (DuckDB.NET 1.5.6 supports it). Generation files are created with `ATTACH '<path>' AS g (ENCRYPTION_KEY '<hex(HKDF(KEK, "duckdb-gen-"+epoch))>')`. Reader-pool and view connections attach the same way. Delete `EncryptFileAtRestAsync`, `EnsureDecryptedFileAsync` and the `.enc` files, so no plaintext is ever written.
  3. Derive the metadata HMAC key from the KEK with HKDF (`info="tablecache-meta"`), separate from the DEK.
  4. On startup, delete any legacy `*.duckdb.enc` and any unencrypted generation file (cache data can be rebuilt).
- **Fail-closed.** If the KEK is missing, the persistent store cannot be constructed and DI fails. Opening a generation file without the key fails and is treated as a cache miss followed by a reload. A wrong HMAC throws `SecurityException`, which is existing behaviour.
- **Red tests** (`tests/Autheris.Tests.Integration/TableCache/DuckDbEncryptedStoreTests.cs`):
  - `EncryptionEnabled_NoKek_ConstructorThrows`
  - `KekShorterThan32Bytes_Throws`
  - `GenerationFile_ContainsNoPlaintextMarkerRow` (grep the file bytes for a seeded marker)
  - `GenerationFile_OpenWithoutKey_Fails`
  - `MetadataHmac_ForgedWithKeyName_Rejected`
  - `NoEncFileOrDecryptedCopyLeftOnDisk`
- **Breaking.** Cache files from earlier builds are discarded. The KEK is now mandatory.

### INF-06: Databricks OBO token cache unbounded (MEDIUM)
- **Root cause.** `DatabricksTokenCache` is a plain `ConcurrentDictionary`. Entries are skipped on read but never removed, and there is no cap. `DatabricksOboTokenProvider` writes one entry per assertion hash (line 245).
- **Fix.** Back the cache with `MemoryCache` (owned instance, `SizeLimit = Options.Databricks.TokenCacheMaxEntries`, default 10 000). Each entry gets `Size = 1` and `AbsoluteExpiration = expiresAt`. Keep the current `TryGet`/`Set`/`Evict` signatures. `Evict(host, clientId)` tracks keys in a side `ConcurrentDictionary<(host, clientId), ConcurrentDictionary<key, byte>>` that is pruned by the eviction callback. Implement `IDisposable`.
- **Fail-closed.** When the cache is full, new entries are not cached: the token is fetched again on each call. That costs latency, not memory. A token is never served after `expiresAt`.
- **Red tests** (`tests/Autheris.Tests.Unit/Databricks/DatabricksTokenCacheBoundTests.cs`), all using `FakeTimeProvider`/clock injection:
  - `ExpiredEntries_AreRemoved_NotOnlySkipped`
  - `EntryCount_NeverExceedsSizeLimit`
  - `Evict_RemovesAllEntriesForHostAndClient`
- **Breaking.** None.

### INF-09: streaming row filter ignores collation (MEDIUM)
- **Root cause.** In `StreamingRowFilterAstEvaluator`:
  - LIKE always uses `RegexOptions.IgnoreCase` (line 373).
  - `=`, `<>` and ranges use `string.CompareOrdinal` (line 574).
  - IN uses an ordinal `HashSet` (line 279).
  - The public entry point `Matches(payload, filterSql)` takes no dialect.
  - The enforcer has `metadata.Dialect` available (line 352) but does not pass it.
- **Fix (default taken: fail closed rather than emulate collations).**
  1. Add the overload `Matches(IReadOnlyDictionary<string, object?> payload, string? filterSql, DatabaseDialect? dialect)`. Include the dialect in the plan-cache key.
  2. String comparisons return three-valued results. If the ordinal result and the case- and trailing-space-insensitive result disagree, the predicate yields `null` (UNKNOWN). This covers `=`, `<>`, `<`, `>` and `IN`.
  3. LIKE: build the regex without `IgnoreCase`. If the case-sensitive and case-insensitive matches disagree, the result is UNKNOWN.
  4. UNKNOWN propagates through NOT and AND/OR as in SQL. The top level treats UNKNOWN as no match, so the row is dropped.
  5. `StreamRlsPolicyEnforcer.MatchesStreamingRowFilter` passes `metadata.Dialect`. Callers in `InMemoryRowFilterEvaluator` and `CrossSourceRelationResolver` keep the dialect-less overload, which uses the same fail-closed semantics.
- **Fail-closed.** Any value whose verdict depends on the collation is dropped from the stream.
- **Red tests** (`tests/Autheris.Tests.Unit/Streaming/StreamingRowFilterCollationTests.cs`):
  - `NotEquals_CaseVariant_RowDropped` (`NOT (dept='HR')`, dept=`hr`)
  - `NotEquals_TrailingSpace_RowDropped`
  - `Like_CaseVariant_RowDropped` (`region LIKE 'EU%'`, `eu-internal`)
  - `NotIn_CaseVariant_RowDropped`
  - `ExactMatch_StillAllowed`
  - `Like_ExactCase_StillMatches`
- **Breaking.** Fewer rows are streamed for mixed-case data. This is intended.

### INF-15: RedisRebacStore HGETALL per graph step (MEDIUM)
- **Root cause.** `RedisRebacStore.GetTuplesAsync` (lines 66-104) runs HGETALL on the tenant hash, deserialises every tuple and filters in memory. `ZanzibarRebacEvaluator` calls it at lines 360, 390 and 419 on every step. The field key `user#relation@object` (line 138) is ambiguous.
- **Fix.**
  1. Field key: length-prefixed lower-case components, `"{u.Length}:{u}|{r.Length}:{r}|{o.Length}:{o}"`.
  2. Secondary index sets, written in the same `ITransaction` as the HSET/HDEL:
     - `rebac:{t}:obj:{object}#{relation}` → set of field keys
     - `rebac:{t}:user:{user}` → set of field keys

     Object and relation components are length-prefixed in the key names as well.
  3. Reads:
     - `obj`+`relation` given → SMEMBERS on the object index, then HMGET
     - `user` only → SMEMBERS on the user index, then HMGET
     - all three given → HGET on the exact field
     - no filter → keep HGETALL (admin only)
  4. `ClearTenantTuplesAsync` deletes the index keys by SCAN on `rebac:{t}:*`.
  5. Evaluator: add a per-check memo, a `Dictionary<(user?, relation?, obj?), IReadOnlyList<RebacTuple>>` bounded at 256 entries, passed through the recursion.
- **Fail-closed.** A dangling index member whose HMGET returns null is skipped. Tenant and expiry checks stay in place.
- **Red tests:**
  - `tests/Autheris.Tests.Unit/Rebac/RedisRebacStoreIndexTests.cs` (fake `IDatabase` or Testcontainers):
    - `GetByObjectRelation_DoesNotCallHashGetAll`
    - `FieldKeysWithHashAndAt_DoNotCollide` (`a#b`/`c@d` vs `a`/`b#c@d`)
    - `Delete_RemovesIndexMembers`
    - `ExpiredTuple_AbsentViaIndexPath`
  - `ZanzibarRebacEvaluatorTests.SameLookupWithinCheck_HitsStoreOnce`
- **Breaking.** The Redis key layout changes, so existing ReBAC tuples must be re-imported (no migration, owner rule).

### OPS-01: GitHub PAT in the remote URL (MEDIUM, no code change)
- **Verified.** The `origin` URL in `/root/autheris/.git/config` embeds a token. No credential helper is configured and `gh` is not logged in. The token value is not reproduced here.
- **Fix (owner, manual).**
  1. Revoke the token on GitHub.
  2. Run `git remote set-url origin https://github.com/themulle/autheris.git`.
  3. Run `gh auth login` followed by `gh auth setup-git`, or use `git config --global credential.helper store` with `~/.git-credentials` at mode 0600. Prefer a fine-grained, repo-scoped, short-lived PAT.
  4. Re-check every worktree under `/work/wt` (they share the config) and any clone scripts that build URLs containing a token.
- **Test.** `git remote -v` shows no `@` userinfo, and `git ls-remote origin` succeeds.

## Work packages (order)

| WP | Findings | Size | Notes |
|----|----------|------|-------|
| WP-0 | OPS-01 | XS, ops | Owner action before any push from agents |
| WP-1 | INF-11 | S (~150 LOC + tests) | Highest exploitability, isolated |
| WP-2 | INF-08 | S (~120 LOC) | Enforcer + ClaimsNormalizer |
| WP-3 | INF-09 | M (~250 LOC) | Evaluator three-valued string ops |
| WP-4 | INF-01 R1 | XS (~40 LOC) | Reconnect grace |
| WP-5 | INF-06 | S (~120 LOC) | Independent, parallel lane |
| WP-6 | INF-15 | M (~300 LOC) | Store index + evaluator memo |
| WP-7 | INF-04 + INF-05 | M (~400 LOC) | Latent table-cache path; one lane |

WP-5, WP-6 and WP-7 are independent of WP-1 to WP-4 and can run in parallel lanes.

## Dependencies on other areas
- **D1.** Durable encrypted datasource secret store (governance DB or Key Vault write-through). Needed so that datasource secrets survive restarts after INF-11. Separate plan.
- **D2.** Epoch bump outbox (governance area), only if Phase 3 rejects the accepted crash window of INF-01 R1.
- **D3.** INF-04 depends on the masking engine contract (`IColumnMaskingProvider`, `MaskingRule`) owned by the masking/governance area. Coordinate if that area changes those signatures.
- **D4.** INF-08 touches `ClaimsNormalizer` (identity/API area). Coordinate with any open realm/tenant-binding findings in that area.
- **D5.** The LOW items INF-03 (L1 key ambiguity) and INF-07 (Databricks eviction) touch the same files as INF-01 and INF-06. Fixing them in the same lanes is optional.

## Security conditions for Phase 3
1. INF-11: no `datasource:` ref may ever be resolved from `IConfiguration`, the environment, files or aliases. Phase 3 verifies this with a test that loads every well-known operator key into config and env.
2. INF-08: there must be exactly one tenant source for streaming, the realm-bound `GetTenantId()`. A grep gate shows no `FindFirst("tenant` in `src/Autheris.Application/Streaming`.
3. INF-09: UNKNOWN must never become a match at the top level or under NOT. Phase 3 asks for a property test over random case and padding variants, NOT-wrapped.
4. INF-05: no plaintext generation file exists on disk at any time, and starting without a KEK is impossible when encryption is enabled.
5. INF-04: the consent subject is never derived from `Identity.Name`, and an unknown mask type never returns the raw value.
6. INF-06 and INF-15: limits (cache size, memo size) are configurable with safe defaults and validated at startup (greater than 0).
7. INF-01 R1: Phase 3 decides whether the remaining crash window, bounded by the decision TTL, is acceptable or whether D2 is required.
8. OPS-01: the token is revoked (not only removed from the URL) before the next agent-driven push.
