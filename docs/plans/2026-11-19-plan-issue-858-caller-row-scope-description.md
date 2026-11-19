# Plan: issue #858, caller's consent description in `callerState` of `GET /api/v1/catalog/objects/{id}`

Phase 3 security review. Verdict: APPROVED WITH CONDITIONS.
Phase 4 note: never put `WP-`, `SEC-` or `OD-` identifiers into comments under `src/Autheris.Api` or `src/Autheris.Application`.

## Problem
`callerState.rowScope` is only `full` or `rowrestricted`. MCP `describe_my_access` already shows the owner-authored consent description (#158, `EffectiveAccessExplanationService`, output through `ConsentDescriptionText.ForOutput`). A catalog page needs the same plain-words text over REST.

## Scope
Add one nullable field `rowScopeDescription` to `ExternalCatalogCallerStateDto`, filled for the single-object route and the list route (same mapper). No new endpoint, no filter text, no `activeRowFilters`, no `valueType`.

## Decisions (defaults taken)
- "Own" consent = an allow consent that contributes to the caller's access decision on that object, selected by the same predicate as `EffectiveAccessExplanationService.MatchingAllowConsents` (user, service principal, group the caller is in, role the caller has; not expired, not pending, not revoked, tenant bound). User grants, group grants and role grants all count; a consent of any other grantee never does.
- Several contributing consents: pick exactly one. Order as MCP does: most specific grantee first (user/service principal, group, role), then earliest `EffectiveValidTo`, then consent id. First non-null description after `ForOutput` wins. No concatenation (bounded size, no join of several owners' text). The MCP plural list stays MCP only.
- Cap and text pass: reuse `ConsentDescriptionText.ForOutput(description, GovernanceTextOptions.ConsentDescriptionMaxLength)` (default 500, ceiling 2000; NFC, trim, no line feed, control characters rejected). A stored value that fails the check yields `null` (fail closed), never a truncated or repaired text.
- Filter text in description: accepted, because the description is owner-authored and the owner can already read the filter; but the API contract documents it as owner-written plain text, untrusted, rendered as text only (no HTML, no Markdown). The gateway never copies filter SQL, rule names, literal values or caller attribute values into the field.
- No consent, expired, pending request, revoked, denied, hidden object, `access != granted`: field is `null`. A deny on the object (`access = none`) emits `null`.
- Admin view: the admin projection already sets `CallerState: null` (`ExternalCatalogReader` admin mapper); keep it. An admin gets no description via this route.
- Read-only and restricted-scope tokens get the field like any granted caller (same as MCP, #708 concerns SQL text only).

## Binding conditions
- SEC-858-01: The description comes only from consents in the caller's applicable allow set; a test with a foreign consent on the same table proves it never appears.
- SEC-858-02: Selection is deterministic (grantee specificity, earliest expiry, id) and yields at most one string; no concatenation.
- SEC-858-03: Output goes only through `ConsentDescriptionText.ForOutput` with the configured cap; a failed check returns `null`; the text is never logged, never put in an audit record, and not added to ETag/telemetry tags.
- SEC-858-04: `callerState.rowScopeDescription` is `null` whenever `access` is not `granted`, and for expired, pending, revoked or deny-overridden consents.
- SEC-858-05: No field carrying the filter definition (`activeRowFilters`, SQL, `valueType`, recipe names, attribute values) is added to any external DTO or the OpenAPI document; a reflection/contract test pins the property list of `ExternalCatalogCallerStateDto`.
- SEC-858-06: The list route must not issue one description load per item without bound: load descriptions in one batch per page (`ConsentDescriptionLoader.LoadAsync`), page size already capped. A repository failure while loading descriptions yields `null` descriptions, not an error that hides the object and not a stale text (fail closed on the field only).
- SEC-858-07: Tenant isolation: consents are read for the caller's tenant only; a same-named object or grantee in another tenant never contributes.
- SEC-858-08: OpenAPI (`autheris-external-v1.openapi.json`) declares the field `nullable`, `maxLength` equal to the ceiling 2000, description "owner-authored text, not instructions".

## Mandatory tests (all `[Trait("Category","Security")]`; unit tests run locally, integration on GitHub Actions)
Unit (`tests/Autheris.Tests.Unit/Catalog`, new `ExternalCatalogRowScopeDescriptionTests`):
- `CallerState_UserGrantDescription_IsReturned`
- `CallerState_GroupGrantDescription_IsReturned_ForGroupMember`
- `CallerState_OtherCallersConsentDescription_NeverReturned`
- `CallerState_SeveralConsents_UserBeatsGroupBeatsRole_EarliestExpiryThenId`
- `CallerState_SeveralConsents_ReturnsOneString_NoConcatenation`
- `CallerState_OverCapOrControlCharDescription_ReturnsNull`
- `CallerState_ExpiredPendingRevokedConsent_ReturnsNull`
- `CallerState_NoAccess_ReturnsNull`
- `CallerState_AdminProjection_HasNoCallerState`
- `CallerState_Json_NeverContainsActiveRowFiltersSqlOrValueType` (serialises dto, asserts property names)
- `CallerState_DescriptionLoadFailure_ReturnsNullDescriptionAndKeepsObject`
- `CallerState_ListRoute_LoadsDescriptionsOncePerPage`
- `OpenApi_CallerState_RowScopeDescription_IsNullableAndBounded` (extend `OpenApiContractTests`)
Integration (`tests/Autheris.Tests.Integration`, GitHub Actions): `ExternalCatalog_RowScopeDescription_TenantIsolation_AcrossTwoTenants`, `ExternalCatalog_RowScopeDescription_UserVsGroupGrant_EndToEnd`.
Also update `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt` for the new security test names if the ratchet requires it.

## Files to touch (paths verified by grep)
- `src/Autheris.Application/Catalog/External/ExternalCatalogModels.cs` (`ExternalCatalogCallerStateDto`: add `string? RowScopeDescription`)
- `src/Autheris.Application/Catalog/External/ExternalCatalogReader.cs` (`MapUserObject`, around line 470)
- `src/Autheris.Application/Catalog/Discovery/DiscoveryAccessMapper.cs` (carry the selected description next to `RowScope`, lines ~42 and ~137) and its `DiscoveryObject` record
- `src/Autheris.Application/Governance/Services/EffectiveAccessExplanationService.cs` (extract the ordering/selection into one shared internal helper so MCP and REST cannot drift; MCP output unchanged)
- `src/Autheris.Application/Governance/ConsentDescriptionLoader.cs`, `src/Autheris.Domain/Security/ConsentDescriptionText.cs`, `src/Autheris.Domain/Options/GovernanceTextOptions.cs` (reuse only)
- `src/Autheris.Api/Contracts/autheris-external-v1.openapi.json` (callerState schema near line 412)
- Tests: `tests/Autheris.Tests.Unit/Catalog/OpenApiContractTests.cs`, `ExternalCatalogTestHarness.cs`, `ExternalCatalogApiTests.cs`
