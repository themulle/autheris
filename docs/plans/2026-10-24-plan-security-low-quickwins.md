# Security LOW quick wins (lane fix/security-low-quickwins)

Source: `docs/plans/security.txt`. Verdicts are Phase 1 triage (2026-10-24), checked against current code.
QUICK = small local change, few files, clear fix, unit-testable, no design/owner decision. Partial findings: only the remaining part is rated.

## 1. Triage

| ID | Verdict | Scope of the fix | Files | Reason if deferred | Acceptance criteria |
|---|---|---|---|---|---|
| INF-07 | QUICK | On Databricks 401/403 evict the entry of the active auth mode: OBO (obo.ClientId + SHA-256 assertion hash of the caller, only that caller) or M2M (auth.ClientId); never wipe other users' OBO entries. Expose the eviction on the credential provider (it owns the hash). | `Infrastructure/Databricks/DatabricksStatementExecutionClient.cs` (line ~248), `IDatabricksCredentialProvider.cs`, `DatabricksOboTokenProvider.cs` | - | 1) 403 with delegation: the next call re-exchanges for that caller. 2) 403 for user A leaves user B's cached OBO token. 3) M2M 401 still evicts the M2M entry. 4) Auth.ClientId == Obo.ClientId does not purge all users. |
| INF-13 | DEFERRED | - | (CdcStreamAdminService, CdcStreamKeyring, KeyVaultCdcKekProvider) | Needs a new exception taxonomy (permanent vs transient key failure) across 3 crypto/infra files; irreversible-delete semantics call for Phase 2/3 design. | - |
| GQL-13 (rest) | DEFERRED | - | - | Fragments and once-per-request latency are done. Remaining: root fields and relation/alias type-to-table mapping need schema TypeInfo resolution (design). | - |
| GQL-14(a) | QUICK (sub-part) | Dbt quarantine gate follows inline fragments and named fragments (cycle-safe, as GQL-13 does). | `GraphQL/Interceptors/DbtHealthExecutionMiddleware.cs` (CollectModelsFromSelections/ExtractReferencedModels) | - | 1) `{ ... on Query { quarantined_table { id } } }` returns TABLE_IN_QUARANTINE. 2) Same via a named fragment, also nested. 3) Fragment cycle terminates. 4) Healthy models unaffected. |
| GQL-14(b,c) + variables | DEFERRED | - | - | (b) ownership via IDataOwnershipRepository/ClusterAdminPolicy and (c) full-identifier keying change the access model; variable-valued table/name args need coerced variables. Owner decision. | - |
| GQL-16 | DEFERRED | - | - | Row counting on HC 16 ResultDocument (shares GQL-15 root cause) or resolver-side counters across all list resolvers; cost-model design. | - |
| GQL-17 (MASKED whitelist) | QUICK (sub-part) | Add `"MASKED"` to `WhitelistedSafeCodes` so ReportMasked errors keep their fixed message and are not INTERNAL_SERVER_ERROR / Error-logged. | `Api/Middleware/ErrorSanitizingFilter.cs` | - | 1) Error with code MASKED passes the sanitizer with message intact. 2) Unlisted code still masked. 3) Not logged at Error level. |
| GQL-17 (type) | DEFERRED | - | - | Caller-aware GraphQL typing of HMAC-ruled numeric columns changes schema/filter/aggregate model. | - |
| MCP-15 (rest) | QUICK (sub-part: cap after filter) | In `get_golden_queries` apply the result cap after the visibility filter (service returns uncapped or takes the cap as parameter). Fail-closed without consent repo is already done. | `Application/Mcp/Services/GoldenQueryService.cs` (Take at ~81), `AiDataGuardrailService.cs` (~460-475) | Schema/tenant keying of GoldenQuery (model + callers McpDatasetCatalog, SemanticMcpCompiler) is DEFERRED: data-model change. | 1) With more goldens than cap, the visible ones are still returned. 2) Cap still limits the filtered result. 3) Existing fail-closed tests green. |
| INF-12 (rest) | DEFERRED | - | - | Key-name deny-list done. Remaining: scope-state processing (OTel scopes are read-only in the processor, needs design) and ex.Message call-site rewrites across many files. | - |
| VF-10 | DEFERRED | - | - | Persisted relaxation history with VF generation (repository/DDL) and definition-hash relaxation policy; owner decision. | - |
| EXT-17 (URI parsing) | QUICK (sub-part) | Parse qualifiedName with `Uri` (default mapping `mssql://host/db/schema/table` -> domain=db, schema, table; unparsable/unsupported entity is skipped and logged, never emptying the whole page); same parser in webhook Purview/EventGrid branch (no bogus domain "mssql:", no hard-coded schema "public"). Shared helper. | `Extensions/DataCatalog/PurviewDataCatalogClient.cs` (ParseAtlasSearchResponse), `CatalogWebhookHandler.cs` (~410-419), new small helper in `Extensions/DataCatalog/` | Column fetch + classifications and configurable domain mapping are DEFERRED (new API calls, config design). | 1) mssql URI yields correct identifier. 2) One bad entity does not drop the page. 3) Webhook invalidation targets the same identifier as the sync. 4) No empty schema reaches the TableIdentifier ctor. |

## 2. QUICK file sets (disjoint lanes)

- A: INF-07 (Databricks files only).
- B: GQL-14(a) (`DbtHealthExecutionMiddleware.cs`) and GQL-17 MASKED (`ErrorSanitizingFilter.cs`); disjoint, can share a lane.
- C: MCP-15 cap-after-filter (GoldenQueryService, AiDataGuardrailService).
- D: EXT-17 URI parsing (Extensions/DataCatalog).

## 3. Open questions

1. EXT-17 default mapping: database name as domain (recommended); revisit with the configurable map in a later plan.
2. GQL-14 variables: left out of this lane; recommend a follow-up with the architect.

## 4. Architecture addendum (Phase 2, 2026-10-24)

Verified against `fix/security-low-quickwins`. Owner default taken: EXT-17 database name becomes the domain. Deferred items in section 1 stay deferred.

### 4.1 Security-regression catalog
`tests/TrinoSqlEngine.Tests/Security/SecurityRegressionTraceabilityTests.cs` scans `tests/**/*.cs` for `(SEC|SQ|SQL|SR15|RR)-<digits>` (numeric suffix only) and requires a row in `SecurityRegressionCatalog.cs` (`LegacyIdEntry(Id, Kind, Summary, AstTests, ...)`, `Phase3Entry(Id, Summary, WorkPackages, Evidence)`). Finding IDs `INF-07`, `GQL-14`, `GQL-17`, `MCP-15`, `EXT-17` do not match the pattern. Decision: **no catalog rows**. Lanes name tests and comments only with the finding ID (e.g. `Inf07_...`) and must not introduce new `SEC-<digits>` / `SQ-<digits>` tokens. Nobody edits `SecurityRegressionCatalog.cs`, so it has no conflict. If Phase 3 asks for a catalog row, the lead adds it after the merge.

### 4.2 Lane A: INF-07 (Databricks token eviction)
1. `IDatabricksCredentialProvider`: add `void EvictToken(DatabricksDataSourceOptions db, DelegationContext? context);`.
2. `DatabricksOboTokenProvider.EvictToken`: if `Auth.Mode == OAuthObo`, take `obo.ClientId` and compute the assertion hash with the same normalization as `AcquireOboTokenAsync` (Trim, strip `Bearer `, SHA-256 hex). Move that into `private static string? CallerAssertionHash(DelegationContext?)` and use it from both places so the keys cannot drift. Then call `_tokenCache.Remove(db.Host, obo.ClientId, hash)`. If the caller token is missing, do nothing: there is no entry to evict, and the method never falls back to a wider purge. In every other mode, call `_tokenCache.Remove(db.Host, auth.ClientId ?? "", string.Empty)` (the M2M key, matching `Set` at ~288). Pat does nothing. The hash stays inside the provider and is never logged or put into exception text.
3. `DatabricksStatementExecutionClient.SendAsync` (~248): replace `_tokenCache.Remove(...)` with `_credentialProvider.EvictToken(db, delegation)`. Thread `DelegationContext? delegation` through `SendAsync` and its callers: `ExecuteAsync` passes `request.Delegation`, and cancel/poll paths pass the delegation they used for `AcquireTokenAsync` (null for M2M). Check whether `_tokenCache` is still needed in the client and remove the field if it is not.
4. Tests in `tests/Autheris.Tests.Unit/Databricks/DatabricksOboSecurityTests.cs` (class-level `[Trait("Category","Security")]`):
   - `Inf07_Obo403_EvictsOnlyCallerEntry_NextCallReExchanges`
   - `Inf07_Obo403ForUserA_KeepsUserBCachedToken`
   - `Inf07_M2M401_EvictsM2MEntry`
   - `Inf07_SameClientIdForAuthAndObo_DoesNotPurgeOtherCallers`
   - `Inf07_EvictWithoutCallerToken_IsNoOp`

   Update any fake `IDatabricksCredentialProvider` in `DatabricksTestSupport.cs` / `DatabricksStatementExecutionClientTests.cs`.

### 4.3 Lane B: GQL-14(a) + GQL-17 MASKED
5. `DbtHealthExecutionMiddleware.ExtractReferencedModels`: build `Dictionary<string, FragmentDefinitionNode>` from `document.Definitions` (Ordinal), following the pattern in `SchemaSunsettingExecutionMiddleware`. `CollectModelsFromSelections(selections, models, fragments, HashSet<string> visitedFragments)` handles `InlineFragmentNode` by recursing into its `SelectionSet`. It handles `FragmentSpreadNode` by recursing only if `visitedFragments.Add(name)` succeeds and the definition exists. That makes cycles and repeated spreads terminate.
6. Tests in `tests/Autheris.Tests.Unit/GraphQL/DbtHealthExecutionMiddlewareTests.cs` (new tests carry `[Trait("Category","Security")]`):
   - `Gql14_InlineFragmentOnQuery_ReturnsTableInQuarantine`
   - `Gql14_NamedFragment_ReturnsTableInQuarantine`
   - `Gql14_NestedNamedFragments_ReturnsTableInQuarantine`
   - `Gql14_FragmentCycle_Terminates`
   - `Gql14_HealthyModelViaFragment_PassesThrough`
7. `ErrorSanitizingFilter.WhitelistedSafeCodes`: add `"MASKED"` with the comment `// GQL-17: ReportMasked (CatalogGraphQlTypeModule) fixed message`.
8. Tests in `tests/Autheris.Tests.Unit/GraphQL/ErrorSanitizingFilterCodeTests.cs` (already Security):
   - `Gql17_MaskedCode_PassesWithFixedMessage`
   - `Gql17_MaskedCode_NotLoggedAtErrorLevel`
   - `Gql17_UnlistedCode_StillMaskedAsInternal`

### 4.4 Lane C: MCP-15 cap after visibility filter
9. `IGoldenQueryService`: add an overload `GetGoldenQueriesAsync(string? domain, string? tableName, Func<GoldenQuery, bool>? visible, CancellationToken ct)`. `GoldenQueryService` applies `visible` before `Take(_maxResults)`. The existing three-argument overload delegates with `null`, so `McpDatasetCatalog` (~510) is unchanged. Implement the overload in the test fakes in `McpSemanticResourceSecurityTests.cs`, `McpDatasetCatalogTests.cs` and `McpDatasetCatalogKeyVisibilityTests.cs`.
10. `AiDataGuardrailService` (~449-476): reorder the branch so it stays fail-closed.
    - Without a consent repository, and neither admin nor bypass: return an empty list and do not call the service.
    - With a consent repository: compute `visibleKeys` first, then call the overload with the predicate `q => visibleKeys.Contains(...)`.
    - Admin or bypass: pass a `null` predicate.
11. Tests in `tests/Autheris.Tests.Unit/Mcp/GoldenQuerySecurityTests.cs` (`[Trait("Category","Security")]`):
    - `Mcp15_MoreGoldensThanCap_VisibleOnesStillReturned`
    - `Mcp15_CapStillLimitsFilteredResult`
    - `Mcp15_NoConsentRepo_FailsClosedWithoutServiceCall`

    Existing fail-closed tests stay green.

### 4.5 Lane D: EXT-17 Purview qualifiedName via Uri
12. Add `src/Autheris.Extensions/DataCatalog/PurviewQualifiedName.cs`: `internal static class PurviewQualifiedName { public static bool TryParse(string? qualifiedName, [NotNullWhen(true)] out TableIdentifier? id); }`.
    - An absolute `Uri` such as `mssql://host[:port]/db/schema/table` uses its unescaped path segments `db/schema/table` and maps to (domain=db, schema, table). The scheme and host are never used as domain.
    - A bare path `db/schema/table` (3 segments) or `db/table` (2 segments, schema `dbo`) is accepted so the existing webhook fixture `finance_db/accounts` keeps working.
    - Fewer than 2 segments, more than 3 path segments, an empty segment, or a component that `TableIdentifier` rejects (catch `ArgumentException`) returns `false`.
    - The table comes from the qualifiedName, not from `attributes.name`, so sync and webhook resolve the same identifier.
13. `PurviewDataCatalogClient.ParseAtlasSearchResponse` (~185-195): handle each entity separately. If `TryParse` fails, log a warning with the entity index and the reason. Never log the raw qualifiedName, because it is attacker-influenced; if it must be logged, truncate and sanitize it. Then skip the entity. Narrow the outer `catch` to `JsonException` for the document-level parse only. A bad entity never empties the page.
14. `CatalogWebhookHandler` Purview/EventGrid branch (~410-419): replace `Split('/')` and `"public"` with `PurviewQualifiedName.TryParse`. If parsing fails, skip and log a warning.
15. Tests:
    - New `tests/Autheris.Tests.Unit/Catalog/PurviewQualifiedNameTests.cs` (`[Trait("Category","Security")]`): `Ext17_MssqlUri_MapsDbSchemaTable`, `Ext17_BarePath_TwoAndThreeSegments`, `Ext17_SchemeNeverBecomesDomain`, `Ext17_EmptyOrInvalidSegment_ReturnsFalse`, `Ext17_NoEmptySchemaReachesTableIdentifier`.
    - In `DataCatalogConnectorTests.cs`: `Ext17_OneBadEntity_DoesNotDropPage`.
    - In `tests/Autheris.Extensions.Tests/CatalogWebhookHandlerTests.cs`: `Ext17_PurviewWebhook_TargetsSameIdentifierAsSync`.

### 4.6 Disjointness
| Lane | Files (src + tests) |
|---|---|
| A | `Infrastructure/Databricks/{IDatabricksCredentialProvider,DatabricksOboTokenProvider,DatabricksStatementExecutionClient}.cs`; `Tests.Unit/Databricks/*` |
| B | `GraphQL/Interceptors/DbtHealthExecutionMiddleware.cs`, `Api/Middleware/ErrorSanitizingFilter.cs`; `Tests.Unit/GraphQL/{DbtHealthExecutionMiddlewareTests,ErrorSanitizingFilterCodeTests}.cs` |
| C | `Application/Mcp/Interfaces/IGoldenQueryService.cs`, `Application/Mcp/Services/{GoldenQueryService,AiDataGuardrailService}.cs`; `Tests.Unit/Mcp/{GoldenQuerySecurityTests,McpSemanticResourceSecurityTests,McpDatasetCatalogTests,McpDatasetCatalogKeyVisibilityTests}.cs` |
| D | `Extensions/DataCatalog/{PurviewQualifiedName (new),PurviewDataCatalogClient,CatalogWebhookHandler}.cs`; `Tests.Unit/Catalog/PurviewQualifiedNameTests.cs` (new), `Tests.Unit/DataCatalogConnectorTests.cs`, `Extensions.Tests/CatalogWebhookHandlerTests.cs` |

No file appears in two lanes. No lane touches the SEC catalog (4.1).

## 5. Security conditions (Phase 3, 2026-10-24)

Verdict: **APPROVED WITH CONDITIONS**. Verified against code: eviction key today is `Remove(db.Host, db.Auth.ClientId)` (does not match the OBO key host+obo.ClientId+assertionHash, so OBO 401/403 never evicts); `ReportMasked` interpolates only the column name; `FixedClientMessages` exists and is applied before the whitelist check. Deferred items stay deferred; no gate is loosened, no existing security test is weakened or removed.

### INF-07
- LQ-INF07-1: The assertion hash is computed in one shared helper, derived from the raw token via the same normalization as the cache Set/TryGet; it is never logged, put in exceptions, metrics, audit or responses. Eviction code logs at most host and mode, never client id with hash.
- LQ-INF07-2 (fail-closed choice): Without a caller token in OBO mode, `EvictToken` is a no-op. It never purges by client id or host (that would let one unauthenticated failure flush all users and enable a re-exchange storm). Safe because an OBO call without a caller token must already fail before reaching the cache.
- LQ-INF07-3: M2M mode evicts only the (host, auth.ClientId) entry; OBO mode never touches the M2M entry and vice versa. 401/403 handling still surfaces the same error to the caller (no retry loop added, no behaviour change in the error path).
- LQ-INF07-4: `EvictToken` must not throw on null/blank delegation or unknown auth mode (swallowing is fine, the original 401/403 is what propagates).

### GQL-14(a)
- LQ-GQL14-1: The collector is type-agnostic: it never filters on a fragment's type condition, so fragments on types it cannot resolve are still traversed (over-collection fails closed). Do not add schema TypeInfo lookups.
- LQ-GQL14-2: A spread whose definition is missing is skipped (the validator rejects such documents before this middleware); a spread already visited is skipped. Cycle protection is by visited set, not by depth guess; the visited set is per document.
- LQ-GQL14-3: Directives (`@skip`/`@include`) do not exempt a selection from the gate (current behaviour retained). Existing direct-field and argument (`name`/`table`) detection unchanged.
- LQ-GQL14-4: The refusal message/extensions are the existing ones; no new model-name echo beyond what the gate returns today.

### GQL-17 (MASKED)
- LQ-GQL17-1: Add `"MASKED"` to `FixedClientMessages` (fixed text, no interpolation) in addition to `WhitelistedSafeCodes`, so the client message can never carry resolver-built text. The existing message may carry the column name only (caller-requested identifier), never a value, mask pattern, hash or row data; a test asserts this.
- LQ-GQL17-2: Only the exact code `MASKED` is added; no prefix/contains matching; similar codes (`MASKED_COLUMN`, `masked`) stay masked as INTERNAL_SERVER_ERROR. The path/locations extension is kept as is.
- LQ-GQL17-3: No Error-level log for MASKED (no exception attached today; keep it Information at most, never log the message text with the column).

### MCP-15
- LQ-MCP15-1: Visibility predicate is evaluated before `Take(_maxResults)`; the cap applies to the filtered set and stays enforced on every path including admin/bypass (null predicate must not mean unbounded).
- LQ-MCP15-2: If `visibleKeys` cannot be evaluated (no consent repo for non-admin/non-bypass, repo throws, null result), return empty and do not call the service; never fall back to the null predicate or the unfiltered list. Exceptions from the repo propagate or yield empty, never "all".
- LQ-MCP15-3: The three-argument overload keeps its current cap semantics and is not used on the guardrail path; fakes implementing the new overload must apply the predicate (a fake that ignores it fails the tests).
- LQ-MCP15-4: A predicate that throws for one golden query excludes that entry (fail closed), not the whole request leaking others.

### EXT-17
- LQ-EXT17-1: Raw qualifiedName is never logged, in exceptions or audit; warnings carry only entity index and a fixed reason code. No truncated-sanitized fallback.
- LQ-EXT17-2: `TryParse` rejects: null/whitespace, control characters, `.`/`..` segments, empty segments (incl. double slash, trailing slash), backslash, percent-encoded separators after unescape (`%2f`, `%00`), more than 3 segments, over-length segments (use the `TableIdentifier` limits), query/fragment parts present, non-absolute scheme-like junk. Validate after unescape. Failure skips the entity; it never produces a partial or default identifier (no `"public"`, no scheme/host as domain).
- LQ-EXT17-3: Webhook invalidation derives the identifier only through `TryParse`; the table comes from qualifiedName only; the event cannot select a domain except by the parsed path, and an unparsable event invalidates nothing (not a wider/domain-wide purge). Bound the number of events processed per webhook payload and the qualifiedName length (reuse existing body-size limit; add a per-array cap if none).
- LQ-EXT17-4: Existing webhook authentication/signature checks stay before parsing and are untouched; a bad entity must not abort other valid entities but also must not weaken the auth path. The narrowed `catch (JsonException)` must not swallow other exceptions silently.

### Mandatory tests (all `[Trait("Category","Security")]`, finding IDs only)
Section 4 test lists are binding, plus:
- `Inf07_EvictWithoutCallerToken_NeverPurgesByClientId` (other user's and M2M entries survive); `Inf07_EvictionDoesNotLogAssertionHash`.
- `Gql14_FragmentOnUnknownType_StillGated`; `Gql14_FragmentSpreadMissingDefinition_DoesNotThrow`; `Gql14_FragmentSharedTwice_NotSkippedWrongly` (diamond spread still gated).
- `Gql17_MaskedMessage_ContainsNoValue`; `Gql17_SimilarCodes_StillMasked`.
- `Mcp15_AdminPath_StillCapped`; `Mcp15_ConsentRepoThrows_ReturnsEmpty`; `Mcp15_PredicateThrows_EntryExcluded`.
- `Ext17_TraversalAndEncodedSeparators_ReturnFalse` (`..`, `%2e%2e`, `%2f`, `%00`, backslash, control chars); `Ext17_TooManySegments_ReturnsFalse`; `Ext17_BadEntity_LogDoesNotContainQualifiedName`; `Ext17_UnparsableWebhookEvent_InvalidatesNothing`; `Ext17_WebhookEventCapped`.

### Catalog
Confirmed: `SecurityRegressionTraceabilityTests` only scans `(SEC|SQ|SQL|SR15|RR)-<digits>`; `INF-07`, `GQL-14`, `GQL-17`, `MCP-15`, `EXT-17` do not match, so no catalog rows are required. Requirement: no new test, comment or name may contain an `SEC-`/`SQ-`/`SQL-`/`SR15-`/`RR-` token followed by digits (the cited existing `SEC-PG-12` has no digits-only suffix and is already in source).
