# Plan: one rule for denied-column metadata on every channel (Issue #871)

- Issue: [#871](https://github.com/themulle/autheris/issues/871) (reporter rgruebel, v1.8.5). Related: #845, #838, WP-SIMPL-41.
- Phase: 2 (architecture). Next gate: Phase 3 security review.
- **Builds on PR #865** (`fix/845-838-noaccess-columns`, adds `DiscoveryColumnVisibility`). This work branches from #865 or from `main` after #865 is merged, and **must merge after #865**.
- Owner rule: the recommended default is taken for every open decision. Owner rule from #845/#838: a denied or no-access column is never named or counted on `get_table_schema` or GraphQL discovery.

## 1. Current state (`main` plus #865)

| Channel | Granted object, denied column | Ungranted object (request-access) |
|---|---|---|
| MCP `describe_dataset`, `get_table_schema`, schema resource (#865) | hidden | hidden (every column is `NoAccess`) |
| GraphQL `catalog`, `catalogObject` (#865, `Query.Catalog.cs:416,524`) | hidden | hidden |
| REST `/catalog/search` `relevantColumns` (#865, `CatalogDiscoveryProjection.cs:83`) | hidden | hidden |
| REST `/catalog/datasets/{id}` (`CatalogDiscoveryProjection.cs:29-49`, #826) | hidden | **listed as `maskingState: "Deny"` with name and description** |
| REST `/catalog/objects`, `/objects/{id}`, person view (`ExternalCatalogReader.cs:467-499`, `BuildExternalColumns` `:501-571`) | **listed with `accessState: "noaccess"` and description** | **listed** |
| REST `/catalog/objects`, hidden-column appendix for owners or `AllHiddenAllowed` (`ExternalCatalogReader.cs:541-570`) | **listed as `noaccess`** with description | **listed** |
| REST `/catalog/datasets` list (`ToDatasetSummary`, `:16-27`) | no column data | no column data |

`DiscoveryVisibleTextMatcher.cs:94-104,148` already skips `NoAccess` columns when matching search text, but compares states inline.

## 2. Decisions

- D1 **One rule for person callers on every channel:** a column whose caller state is not `Clear` or `Masked` (that is `NoAccess`, or a missing or unknown state) is hidden. That means no name, display name, description, type, classification, statistics, or count. The rule holds for granted and ungranted objects alike, and there is no administrator bypass. (default taken: it extends the #845/#838 owner rule to all channels and is least privilege.)
- D2 **`/catalog/datasets/{id}` no longer emits `maskingState: "Deny"` rows.** Persons see `Clear` and `Masked` only. The `Deny` value stays a valid contract value for compatibility but is never produced. (default taken: same rule as MCP and GraphQL.)
- D3 **The ungranted request-access listing keeps table-level metadata** (name, description, owners, sensitivity, `accessStatus`, requestability) **and lists no column.** (default taken: the owner rule says "never named"; MCP and GraphQL already list none.)
  - What the request flow loses is the column preview before access is granted. Nothing functional is lost. A table request needs no column names, and Clear requests accept only `Masked` columns of a granted object (`DiscoveryAccessRequestService.cs:166-171`), which stay visible.
- D4 **`/catalog/objects` person view** routes its columns through the helper. `accessState` for persons is only `clear` or `masked`. The hidden-column appendix is not emitted for persons: its columns have no caller state, so they are hidden (fail-closed). (default taken: curation of hidden columns belongs on the admin catalog surfaces, which this plan does not change.)
- D5 **The service-principal view (`MapAppObject`, `IsServicePrincipal`, `ExternalCatalogReader.cs:148-153,421-465`) is unchanged.** (default taken: catalog-sync principal, not a person channel. Phase 3 to confirm.)
- D6 **Search:** a term that matches only the name or description of a hidden column produces no hit. The matcher uses `DiscoveryColumnVisibility.IsVisible`.
- D7 `HiddenTextColumns` (masked text) and DiscoveryHidden table rules are unchanged. `/catalog/changes` carries no column data; its `seq` bump on a hidden-column edit is accepted (D-T4).

## 3. Single source: every path that must use `DiscoveryColumnVisibility`

- From #865 (already routed): `McpDatasetCatalog.Discovery.cs:157,314`; `McpToolExecutionHandler.cs:407,462,737,771`; `McpResourceProvider.cs:174`; `McpLinkedDocumentMapper.cs:29`; `Query.Catalog.cs:416,524`; `CatalogDiscoveryProjection.cs:83`.
- New in this plan: `CatalogDiscoveryProjection.ToDatasetDetail` (`:35-49`); `ExternalCatalogReader.MapUserObject` and `BuildExternalColumns` (`:467-473,501-571`); `DiscoveryVisibleTextMatcher.VisibleColumnNames` and the text enumerator (`:94-104,148`).
- Allowed exceptions, which compute state and do not project it: `DiscoveryAccessMapper.cs` (`:80,168,323,335`) and `DiscoveryAccessRequestService.MapClearColumns`.
- Update the helper's doc comment (`DiscoveryColumnVisibility.cs:9-15`). The sentence "The REST dataset detail keeps its own request-access projection" becomes false.

## 4. Work packages (Hydrogen-ready)

**WP-871-1 REST dataset detail (S, about 20 LOC production, about 100 LOC tests)**
- `src/Autheris.Application/Catalog/Discovery/CatalogDiscoveryProjection.cs:35-49`: replace the `granted` filter with `DiscoveryColumnVisibility.VisibleColumns(obj)`. Map `Clear` to `"Clear"` and `Masked` to `"Masked"`; the default arm is unreachable and throws `InvalidOperationException` (fail-closed).
- Unit: new `tests/Autheris.Tests.Unit/Catalog/Discovery/CatalogDiscoveryProjectionColumnRuleTests.cs`, all `[Trait("Category","Security")]`:
  - `ToDatasetDetail_GrantedObject_DeniedColumn_IsNotListed`
  - `ToDatasetDetail_UngrantedObject_ListsNoColumn`
  - `ToDatasetDetail_UnknownColumnState_IsNotListed`
  - `ToDatasetDetail_NeverEmitsDenyMaskingState`
- Adapt: `CatalogVisibilityTierTests`, `CatalogSearchColumnDenyOracleTests`, `DiscoveryColumnVisibilityTests` (the `CatalogDatasetDetail` overload stays as defense in depth).
- Integration: `tests/Autheris.Tests.Integration/CatalogChannelParityTests.cs` `Rest_Discovery_SharedTablesAreListedWithDeniedColumns_UnsharedTableIsOutOfScope` (about line 318) and `Rest_Discovery_ConsentedColumnIsClear_EveryOtherColumnStaysDeny_ForTheConsentedCallerOnly` (about line 344). Rename both and assert empty `columns` instead of all `Deny`.

**WP-871-2 External catalog person view (M, about 40 LOC production, about 150 LOC tests)**
- `src/Autheris.Application/Catalog/External/ExternalCatalogReader.cs`: `MapUserObject` passes `DiscoveryColumnVisibility.VisibleColumns(obj)`. `BuildExternalColumns` takes `bool personView` and skips the appendix (`:541-570`) when it is set. `MapAppObject` is unchanged (D5).
- Unit: extend `tests/Autheris.Tests.Unit/Catalog/ExternalCatalogApiTests.cs` and `ExternalCatalogAccessStatusFilterTests.cs`, all `[Trait("Category","Security")]` except the last:
  - `GetObject_Person_DeniedColumn_HasNoNameNorDescription`
  - `ListObjects_Person_UngrantedObject_HasNoColumns`
  - `GetObject_PersonOwnerOfHiddenTable_HiddenColumnAppendix_NotEmitted`
  - `GetObject_Person_AccessStateIsClearOrMaskedOnly`
  - `GetObject_ServicePrincipal_ColumnsUnchanged` (regression)

**WP-871-3 Search matcher, ratchet, cross-channel parity (S-M, about 15 LOC production, about 150 LOC tests)**
- `src/Autheris.Application/Catalog/Discovery/DiscoveryVisibleTextMatcher.cs:94-104,148`: use `!DiscoveryColumnVisibility.IsVisible(column.State)`.
- Unit: `Search_TermMatchingOnlyDeniedColumnDescription_ReturnsNoHit` `[Trait("Category","Security")]` in the existing search matcher tests (or `CatalogSearchColumnDenyOracleTests`).
- Architecture: `tests/Autheris.Tests.Architecture/DiscoveryArchitectureTests.cs`, `NoAccessStateIsOnlyComparedInVisibilityHelperAndMapper`. This is a source scan over `Catalog/`, `Mcp/` and `Autheris.GraphQL/`; the allowlist is §3.
- Integration: `tests/Autheris.Tests.Integration/CatalogChannelParityTests.cs`, new `DeniedColumn_IsAbsentOnEveryChannel_ForGrantedAndUngrantedObject` `[Trait("Category","Security")]`. One person has a grant with one denied column, and a second table is ungranted. The test asserts the denied column's name and description are absent from MCP `describe_dataset` and `get_table_schema`, REST `/objects`, `/objects/{id}`, `/datasets/{id}` and `/search`, and GraphQL `catalog` and `catalogObject`.
- Local filters: `FullyQualifiedName~CatalogDiscoveryProjectionColumnRuleTests|FullyQualifiedName~ExternalCatalog|FullyQualifiedName~CatalogSearchColumnDenyOracleTests`, plus `DiscoveryArchitectureTests` in the architecture project.

## 5. Threats for Phase 3

- D-T1 Inference through counts, ordinals or gaps (column positions, `relevantColumns` length): all counts come from the helper.
- D-T2 Search oracle: a term found only in hidden metadata must give no hit and no score change (WP-871-3).
- D-T3 Primary keys, required filters and indexes (`QueryShape`, #826) that name a hidden column: Phase 3 decides whether those names are filtered too (recommended: yes, by the same helper; follow-up if it is out of scope).
- D-T4 `/catalog/changes` reveals that object metadata changed (representation hash includes all columns, `ExternalCatalogReader.cs:207-248`). This is an object-level signal without names; accepted.
- D-T5 Service-principal view lists every column (D5); Phase 3 confirms that persons can never obtain `IsServicePrincipal`.

## 6. Phase 3 security conditions

**Verdict: CHANGES REQUIRED** (one leak path and one fail-open principal heuristic must join the scope; the rest is approved with the conditions below). Phase 4 must never put `WP-`, `SEC-` or `OD-` identifiers into `src/Autheris.Api` or `src/Autheris.Application` comments.

Verified against `fix/845-838-noaccess-columns`:
- D-T3 is largely closed already: `DiscoveryAccessMapper.MapQueryShape` (`:154-192`) lists primary keys and indexes only over Clear columns (`DatasetIndexProjection` omits an index whole) and required filters only over listed columns (group only when every member is listed). Residual: `listed` uses `State != NoAccess`, not `IsVisible`.
- ETags hash the response body; `/catalog/changes` carries ids and seq only. Accepted. GraphQL `columns`, `columnNames` and the `search` over `ColumnNames` (`Query.Catalog.cs:427,525,593`) use the filtered list.
- **Plan gap, leak:** `DiscoveryAccessMapper.cs:47-51` filters `DiscoveryTableStatistics.PartitionColumns` only by `DiscoveryHidden`, before caller states exist. A denied partition column is named in table statistics on REST `/objects`, GraphQL (`Query.Catalog.cs:439`) and MCP, for granted and ungranted objects.
- **Plan claim wrong (D5/D-T5):** two different "service principal" predicates exist. `ExternalApiPermissions.IsServicePrincipal` refuses the heuristic branch when a person-id claim exists (`:70-75`). `ExternalCatalogReader.IsAppOnlyPrincipal` (`:743-760`) uses `client id && no scopes && GetUserSid() is null`. A person token with `azp`, no scope claim and no resolvable SID gets the unfiltered app view (it still needs `Autheris.Catalog.Read`, then sees every non-hidden column of every in-scope table). `idtyp=app` and `gty=client-credentials` come from the token issuer, not the caller.

Conditions:
- **SEC-871-01** D1 to D4 and D6 are approved as written. (default taken: least privilege.) `DiscoveryColumnVisibility.IsVisible` is the only predicate for person-facing column projection; the architecture test in WP-871-3 is binding.
- **SEC-871-02** `MapQueryShape` uses `IsVisible` for the `listed` set. Primary keys, indexes, required columns and required groups never name a column that is not Clear (keys, indexes) or not visible (filters). Binding, not optional.
- **SEC-871-03** `PartitionColumns` is filtered to caller-visible columns after states are computed, on every channel (REST, GraphQL, MCP). An ungranted object lists none. An empty list stays empty.
- **SEC-871-04** Counts (column count, `relevantColumns` length, statistics column counts, score inputs) derive only from the visible list. A term found only in a hidden column's name, description or statistics gives no hit, no highlight and no score change.
- **SEC-871-05** No error message, audit detail or validation text on a catalog or access-request path contains a column name the caller cannot see. `DiscoveryAccessRequestService` keeps its uniform `InvalidColumns()` for unknown, denied and non-requestable names (verified `:160-175`; pin with a test).
- **SEC-871-06** Unreachable `default` arms (dataset detail, person view) throw `InvalidOperationException`; they never map to `"Deny"` or `"noaccess"`. The person view emits no hidden-column appendix for any person, including owners and `AllHiddenAllowed`.
- **SEC-871-07** D5 output stays unchanged, but the trigger is tightened. `IsAppOnlyPrincipal` returns true only on `idtyp=app`, `gty=client-credentials`, or the heuristic branch with no person-id claim (same claim set as `ExternalApiPermissions`). Unify both into one shared predicate so they cannot drift. Ambiguous means person view. A token with a valid SID never gets the app view.
- **SEC-871-08** Ungranted objects (D3) list table-level metadata only, and a table request needs no column name (pin it).

Mandatory tests, all `[Trait("Category","Security")]`; the plan's WP-871-1..3 names stay and are binding:
- Unit, local:
  - `MapQueryShape_UnknownColumnState_NamesNoColumn`, `MapQueryShape_DeniedColumnInCompositeIndex_OmitsIndexWhole`
  - `Statistics_PartitionColumn_Denied_IsNotNamed_Granted`, `Statistics_PartitionColumn_Denied_IsNotNamed_Ungranted` (mapper), the same assertion on the `ExternalCatalogReader` person view, the GraphQL projection and MCP `describe_dataset`
  - `Search_HitOnlyInDeniedColumnStatistics_ReturnsNoHit`
  - `AccessRequest_DeniedAndUnknownColumn_SameErrorShape_NoNameEcho`
  - `IsAppOnlyPrincipal_PersonWithAzpNoScopeNoSid_IsNotAppView`, `IsAppOnlyPrincipal_PersonWithSid_IsNeverAppView`, `IsAppOnlyPrincipal_IdtypApp_IsAppView`, `IsAppOnlyPrincipal_ClientCredentialsGty_IsAppView`, `AppOnlyPredicate_AgreesWith_ExternalApiPermissions` (theory over the same claim sets)
  - `GetObject_Person_NeverEmitsNoAccessAccessState`
- Architecture, local: `NoAccessStateIsOnlyComparedInVisibilityHelperAndMapper` (plan), plus `PartitionColumns_AreOnlyWrittenThroughVisibilityFilter`.
- Integration, GitHub Actions only (compile-check locally): `DeniedColumn_IsAbsentOnEveryChannel_ForGrantedAndUngrantedObject` (plan) also asserts absence from `statistics.partitionColumns`, `primaryKeyColumns`, `indexes` and `requiredFilterColumns` on REST, GraphQL and MCP; plus `RestCatalogObjects_PersonTokenWithAzpNoScope_ReturnsPersonView`.
