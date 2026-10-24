# Issue #341: GraphQL internal errors for unbacked tables, empty arguments and unknown ids (Phase 3 security addendum)

Branch `fix/issue-341-graphql-internal-errors`, head 48c48bb5, diff `origin/main...HEAD`. Reviewer: `csharp-security-expert`.

## Context and root causes
GraphQL answered masked `INTERNAL_SERVER_ERROR` for: (a) catalog entries without a data-source connection (`InvalidOperationException` in `GovernedTreeQueryService`), (b) blank or dotted table arguments (`TableIdentifier` ctor `ArgumentException` in resolvers, dbt health middleware, cost extraction, consent submit), (c) `WebSqlTableDeniedException`, `GDPR_SUBJECT_SCOPE_UNRESOLVED`, `SYNC_FAILED` codes not whitelisted, (d) non-whitelisted `INVALID_ARGUMENT`, (e) `KeyNotFoundException` / forbidden in `calculateConsentRevocationImpact`, (f) uncaught `SecurityException` in `syncDataCatalog`. Owner policy: fail closed, governed GraphQL error with existing sanitized codes, no 5xx, no weakened check.

## Threat review (verified against the diff)
1. Unbacked source: the throw is in the connection resolution that runs after `ResolveOnceAsync` + `AuditOnceAsync` + the `IsAllowed` deny for every table (lines 270-280). An unauthorized caller already gets `Access denied.` (ACCESS_DENIED) first; an authorized caller on an unbacked entry now gets the identical code and fixed text, so the two are not distinguishable and the old 500/mask difference (an existence oracle) is closed. No decision is skipped or reordered. The audit entry records ALLOW although the request is refused; acceptable (the decision was allow), cause only in the warning log (source name, no secret).
2. `TryCreate` has the same rules as the constructor (blank, NUL, CR, LF, dot), so validation is not more permissive. Resolvers: reject with fixed `BAD_REQUEST` before any lookup; no existence signal. dbt middleware `continue` on invalid: an invalid name can never match a model and the resolver rejects it, so no health state is bypassed. Cost extraction: invalid segment falls through to the shared `graphql.query.graphql` bucket, so the request is still charged and limited (not skipped); the resolver then rejects. `ConsentRequestService.SubmitAsync` stricter than before (dot/line break now rejected up front), never looser.
3. New whitelisted codes `SYNC_FAILED`, `GDPR_SUBJECT_SCOPE_UNRESOLVED` have entries in the fixed-message map (the filter emits the map text, not the exception message). `WebSqlTableDeniedException` is mapped by type to fixed `Access denied.` / `ACCESS_DENIED`; `WebSqlPolicyException` stays masked. `INVALID_ARGUMENT` -> `VALIDATION_ERROR` keeps the pin test (INVALID_ARGUMENT stays out of the filter). Residual: `VALIDATION_ERROR` consent messages are resolver/service literals (reason length, table component text), no caller values echoed in the diff.
4. `syncDataCatalog` broad catch: authorization (cluster-admin/tenant checks, write-ahead audit) runs before the try, so no authorization failure is swallowed there. Gaps: the catch does not log the cause (unlike the stated intent); `GatewayForbiddenException` raised inside the service would become `SYNC_FAILED` instead of `ACCESS_DENIED`; the write-ahead audit entry has no failure completion.
5. `calculateConsentRevocationImpact`: mapping forbidden and unknown (and foreign-tenant) consent to one fixed `NOT_FOUND` removes an enumeration oracle (forbidden vs missing) and matches the consent mutations' contract. Behaviour change FORBIDDEN -> NOT_FOUND for non-owner/admin is intended (no production yet).

## Binding conditions
- C341-1 (satisfied): the unbacked-source deny stays after resolve, audit and `IsAllowed` check; fixed `Access denied.`, `GatewayForbiddenException`, cause only in the server log.
- C341-2 (satisfied): `TryCreate` mirrors constructor rules exactly; any future rule change must change both (shared helper) and keep the parity test.
- C341-3 (satisfied): invalid table arguments answer fixed `BAD_REQUEST` ("The table identifier is invalid.") with no echo of the input and no catalog lookup.
- C341-4 (satisfied): cost/quota extraction must keep charging invalid names against the fallback bucket; the dbt health `continue` only for names that cannot construct an identifier.
- C341-5 (satisfied): every newly whitelisted code has a fixed message in the map; no exception message pass-through; `INVALID_ARGUMENT` stays non-whitelisted.
- C341-6 (must change): in the `syncDataCatalog` catch, log the exception server-side (ILogger, no secrets/URLs with credentials) before throwing `SYNC_FAILED`; if the sync service already logs every failure path including `SecurityException` from `EgressUrlPolicy`, document that with a test or comment. Also add `and not GatewayForbiddenException` to the filter so authorization failures surface as `ACCESS_DENIED`.
- C341-7 (should change): record a failure outcome for the write-ahead sync audit entry (DENY/ERROR completion) or state in the PR why the open write-ahead entry is sufficient.
- C341-8 (satisfied): `calculateConsentRevocationImpact` returns the same `NOT_FOUND` for missing, foreign-tenant and unauthorized; never `FORBIDDEN` or the service message.
- C341-9 (satisfied): `WebSqlPolicyException` and any other unlisted exception stay masked.

## Mandatory tests (`[Trait("Category","Security")]`)
Existing in diff: `Issue341GraphQlInternalErrorTests`, `Issue341ErrorMappingTests` (verify they cover the items below; add the missing).
- `UnbackedSource_AuthorizedCaller_AnswersAccessDenied_SameAsUnauthorized_NoSourceNameInResponse`
- `UnbackedSource_DeniedCaller_DecisionAndAuditRunBeforeConnectionCheck`
- `TableIdentifier_TryCreate_MatchesConstructorRules_ForBlankDotNulCrLf`
- `Query_EmptyOrDottedTableArgument_BadRequest_NoLookup_NoInputEcho`
- `CostAndQuota_InvalidTableName_StillChargedAgainstFallbackBucket`
- `DbtHealthMiddleware_InvalidModelName_Skipped_ResolverRejects`
- `ErrorFilter_WebSqlTableDenied_FixedAccessDenied_PolicyExceptionStaysMasked`
- `ErrorFilter_SyncFailedAndGdprScopeUnresolved_EmitFixedMessage_NotExceptionMessage`
- `ErrorFilter_InvalidArgumentCode_StillNotWhitelisted` (existing pin, keep green)
- `SyncDataCatalog_SecurityException_SyncFailedFixed_CauseLogged_AuthorizationBeforeTry` (C341-6)
- `SyncDataCatalog_GatewayForbidden_AnswersAccessDenied` (C341-6)
- `CalculateConsentRevocationImpact_Unknown_ForeignTenant_Forbidden_AllFixedNotFound`
