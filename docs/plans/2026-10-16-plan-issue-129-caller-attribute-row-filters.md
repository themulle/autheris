# Implementation Plan: REQ-GOV-129 - Row Filters Based on Attributes of the Calling Person

**Document ID:** `PLAN-GOV-129` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-GOV-129](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md) section 3 (V-129-1..13, FR-129-1..12, AC-129-1..12, Q-129-1..7)
**Branch (proposed):** `feat/issue-129-caller-attribute-row-filters` (base `main` after #128 and #107 merged)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement `REQ-GOV-129`; delivery tracker [`PLAN-DELIVERY-W23`](2026-10-16-plan-waves-2-3-delivery.md) (step P-4, item I-25)
**Status:** PHASE 3 REVIEWED - APPROVED WITH CONDITIONS (2026-10-16, tracker step S-4; binding conditions BC-129-01..16 in the Phase 3 section; next: Phase 4 after the plan is revised to revision 2 or the conditions are carried as binding test/acceptance items)

**Binding project rules applied:** no upgrade/repair migrations (the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server is extended in place, the `0001` checksum constants stay unchanged, the shape snapshots under `src/Autheris.Infrastructure/Persistence/Migrations/Shape/` and their tests are updated); least privilege on sources (SELECT only); fail closed with **one generic outcome**; CI stays fast (no sleeps, `TimeProvider`/`FakeTimeProvider`, shared fixtures); Phase-4 TDD developers are Sonnet, one sequential developer for this item; `src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.cs` is **not** modified; values never appear in SQL text, logs, spans, audit, exception text or cache keys in clear.

**Owner decisions applied:** Q-129-1 decided (#129 replaces `WP-SC-04`, defines `ICallerAttributeProvider`, adopted later by `WP-SC-05`). Q-129-2..7 default to the PRD recommendation: parenthesised placeholder form; claims plus attribute table first (SCIM provider later, `WP-SC-07`); TTL 60 s, maximum 300 s, 200 values; attribute table excluded from the catalog by default; service attribute sets per application id with four-eyes; attribute filters not in `KeySetCache` in the first step.

---

## 1. Verification of the PRD Against the Code (`11654124`)

| PRD / task claim | Result |
|---|---|
| `USER_ATTRIBUTE` fails closed today | **Confirmed**, in three places. Model: `src/Autheris.Domain/Model/ConsentModels.cs:51-52` (`ValueSource` `LITERAL`/`USER_ATTRIBUTE`, `UserAttribute`). Typed builder: `src/Autheris.Application/Sql/Policy/TypedRowFilterBuilder.cs:137-141` returns `Constant(isDeny)` (`:207`). Text builder: `src/Autheris.Application/Services/RowFilterSqlBuilder.cs:142-147` (`FormatConditionParameterized`) and `:283-290` (`FormatCondition`) return `isDeny ? "1 = 1" : "1 = 0"`. Parity corpus pins this: `tests/Autheris.Tests.SqlCompilerGate/Parity/ParityCases.cs:40,145-149`; unit pins: `tests/Autheris.Tests.Unit/ConsentResolutionTests.cs:494-511`, `Sql/Policy/TypedRowFilterBuilderTests.cs:130`, `Security/Phase1AndPhase2SecurityExpertTests.cs:182`, `PipelineAndInfrastructureSecurityTests.cs:260` (all must be re-pinned, section 9). |
| `USER_ATTRIBUTE` and `UserAttribute` are persisted | **Confirmed**: `CONSENT_ROW_FILTERS.value_source`, `user_attribute` (`SqliteBaselineSchema.cs:131-153`, `PostgreSqlBaselineSchema.cs:126`, `SqlServerBaselineSchema.cs:139-140`); written only by `CreateConsentAsync` (`SqliteGovernanceRepository.Consent.cs:1362-1363`, `PostgreSqlGovernanceRepository.Consent.cs:493-494`, `SqlServerGovernanceRepository.Consent.cs:526-527`), read back at `SqliteGovernanceRepository.Consent.cs:449-450,585-586` etc. and by `SqliteGovernanceLintSource.cs:150-151`. **No DDL change for consents is needed.** |
| No save-time validation of `ValueSource`/`UserAttribute` | **Confirmed**: the writers are `IConsentRepository.CreateConsentAsync` callers `GraphQL/Types/MutationTypes.cs:356`, `Extensions/OpenMetadata/OpenMetadataSyncService.cs:505`, `DeclarativeConsentApplyService.cs:232-233` and the REST admin path; there is no central validator (grep for a row-filter validator finds none). Validation must sit on the repository seam (A-9). |
| Consent decisions are cached and per user | **Confirmed**: `TableAccessPolicy.ResolveConsentAsync` (`src/Autheris.Application/Policy/TableAccessPolicy.Consent.cs:33-86`) caches by `(tenant, userSid, table, contextHash)` with `contextHash = ComputeSubjectContextHash(groups, roles, scopes)` (`:40`), TTL `ComputeDecisionCacheTtl` (`ConsentResolutionService.cs:131-170`; 10 min, 60 s highly sensitive). The cache stores `CachedConsentEnvelope`, i.e. the legacy text **and** `ConsentFilterRecipe` JSON (`CachedConsentEnvelope.cs:44,79`; `ConsentRowFilterRecipe.cs:13-38` serializes the `ConsentRowFilter` objects, so `ValueSource`/`UserAttribute` already round-trip). The typed policy is **rebuilt per request** from the recipe by `WithTypedConsentPolicy` (`TableAccessPolicy.Consent.cs:105-125`, calls `RowPolicyFactory.FromConsentFilters`, `RowPolicyFactory.cs:61-67`, which calls `new TypedRowFilterBuilder().BuildCombined(recipe.Allow, recipe.Deny, default, target)`). So the decision cache can stay value-free and the values can be bound per request exactly where the typed policy is built. Typed policy is built **only** for `DataSourceType.Sql` (`TableAccessPolicy.Consent.cs:92-93`); other sources keep the legacy text (constant for attributes). |
| One decision point for all channels | **Confirmed**: `TableAccessPolicy` doc comment (`TableAccessPolicy.cs:70-84`): REST/OData/GraphQL (`GatewayExecutionService`), WebSQL, stored procedures, DuckDB OLAP and the unified PDP decide in `DecideAsync` -> `DecideCoreAsync` (`:152-322`). Query construction sites: `TableAccessQuery.ForPrincipal` (`:47-69`), `UnifiedPolicyDecisionPoint.cs:76`, `GovernedSqlRewriter.cs:489`, `FederatedDuckDbExecutionService.cs:449`. `TableAccessQuery` already carries `Claims` (`:38`) but not the principal. **Exception:** CDC/stream delivery has its own consent path `StreamRlsPolicyEnforcer.ResolveConsentDecisionAsync` (`Streaming/Services/StreamRlsPolicyEnforcer.cs:305-342`, text decision, no typed policy) and evaluates the text with `StreamingRowFilterAstEvaluator` (`:245-247,371-374`). |
| Virtual Filters only know `{{tenant}}`; the tenant is a quoted literal | **Confirmed**: `SqlFilterCompiler.TenantPlaceholder` (`src/Autheris.Application/VirtualFilters/SqlFilterCompiler.cs:35`, written as `'{{tenant}}'` in a conjunct, `RequireTenantBinding` `:233-243`, `Substitute` `:253-254`, `BindTenant` `:262-307`). `Validate` rejects `?`, `@`, `:` outside string literals (`:77-81`), rejects literals containing `target` (`:83-86`), and the typed form binds every literal that is a direct operand of a comparison, IN item, BETWEEN or LIKE (`TypedShaper.Bind`, `:538-562`; `CompileTyped` `:421-446`). A bare `{{caller.x}}` is not parseable, so the pre-parse rewrite of the PRD is required. |
| Virtual Filter resolution is memoized and value-free | **Confirmed**: `MandatoryRowFilterResolver._memo` is a `BoundedCache<VirtualFilterMemoKey, MandatoryFilterOutcome>` (`MandatoryRowFilterResolver.cs:180,225-241`) keyed by generation, tenant, user sid, groups, roles, table, dialect, kind, all-user-sids (`VirtualFilterMemoKey.cs:12-40`). Attribute **values** must therefore never enter the memoized outcome (they would be served to the next caller with the same key tuple after an attribute change). The outcome carries a text (`PredicateSql`) and a typed form (`TypedPredicate`, `:21-27`); the resolver builds both per filter (`:488-494`) and an `InvalidOperationException`/`ArgumentException` during build denies the **whole** resolution (`:523-526`). |
| Virtual Filter attach sites | **Confirmed**: typed attach `TableAccessPolicy.cs:309-317` (`FromPredicate` + `WithMandatoryPredicate(IRowPolicy)`); text-only attach in the consent-bypass-without-consent-services fallbacks `GovernedProcedureExecutionService.cs:709-718` and `FederatedDuckDbExecutionService.cs:490-502`. |
| Text form must equal the typed projection | **Confirmed**: `TableAccessDecision.RequireTypedRowPolicy` (`Domain/Interfaces/TableAccessDecision.cs:108-127`), `WithMandatoryPredicate(IRowPolicy, ...)` (`:60-86`, text becomes `projection.Sql`, values merged strictly with `MergeStrict` `:229-241`), `WithPolicy` (`:178-199`). The markers are `@<parameter name>` (`PolicyPredicateRenderer.cs:20,70`). Binding values **before** the policy enters the decision (A-2, A-4) keeps these invariants untouched. |
| Compile path, policy subqueries, `PolicySubqueryTenantRewriter` | **Confirmed** (`GovernancePolicyFactory.CreateAsync`, `Sql/Policy/GovernancePolicyFactory.cs:57-160`): a typed `PolicyPredicate` is taken from the decision and compiled; policy subqueries (`PolicySubqueryTables.Names`, `:75-81`) are governed by `PolicySubqueryTenantRewriter` (derived table with governed projection and tenant predicate, no masks, no consent filters; `AstSecurityVisitor.Tables.cs:76-77`, `AstSecurityVisitor.Expressions.cs:252-253`). An attribute value is a bound parameter **inside** the predicate; it never changes which tables are read, so the subquery governance is unaffected. `PolicyColumnTypeAnnotator` (`TrinoSqlEngine/Ast/Security/PolicyColumnTypeAnnotator.cs:18`) types IN items against the target column. |
| Template cache keys are value-free and contain the policy shape | **Confirmed**: `CompileCacheKey.Material`/`Write` serializes the `CompileRequest` including `GovernancePolicy.RowFilters` (validated per table, `CompiledSqlTemplate.cs:243-258`); `PolicyPredicate.Fingerprint` is the SHA-256 of nodes, parameter names and types, **not values** (`PolicyPredicate.cs:34-36,67`). Consequence: an attribute **value** change needs no cache invalidation, a **shape** change (scalar vs list, list length, constant) is a different template automatically. Padding (A-6) bounds the number of shapes. |
| `ShareAcrossUsers` / WP-PF-09 | **Confirmed Casbin-only**: `CasbinDecisionCacheOptions.ShareAcrossUsers` (`Domain/Options/CasbinDecisionCacheOptions.cs:23`), used at `CasbinEnforcementService.Columns.cs:416-425`; `PolicyShapeAnalyzer` decides `Shareable` from the rule text (`Governance/Caching/PolicyShapeAnalyzer.cs:16-70`). Consent decisions are never shared (key carries the user). #129 adds **no** attribute to Casbin rules, so no shared Casbin decision can contain an attribute value; a test pins that a Casbin rule text containing `{{caller.` is refused (A-10) and that the shared cache key is untouched. |
| Delegated sources | **Confirmed**: `SecurityEvaluationContext.IsDelegatedSource` (`Domain/Model/GovernanceV2Models.cs:32`) means the source decides per user; an attribute filter there is refused at save time (V-129-4) and fails closed at runtime. |
| `GetUserSid()` is not a safe subject key | **Found (not in the PRD):** `ClaimsPrincipalExtensions.GetUserSid` (`Domain/Common/Sid.cs:21-36`) falls back to `sub`, `appid`, `client_id`, `azp`, so an application-only token yields its **application id** as "user sid". The attribute subject key must come from `IIdentitySubjectResolver` (`Application/Services/IdentitySubjectResolver.cs:10-150`: `SubjectType.ServicePrincipal` for `idtyp=app` or an app id without human claim `:57-73`; certificates `:30-48`) plus the immutable-claim lists (`Sid.cs:41-52`, `GetAllUserSids` `:110-135`), never from `GetUserSid()`, never UPN/mail (V-129-1, V-129-9). |
| Claims are not all token claims | **Found:** the principal also carries claims added by `ClaimsNormalizer` (`Api/Security/ClaimsNormalizer.cs:8-70`), `ForwardAuth` (header-derived, `ForwardAuthAuthenticationHandler.cs:270-300`), BasicAuth and TestAuth. Only JwtBearer claims of validated issuers are cryptographically attested (`GatewayServiceCollectionExtensions.cs:340-375`: `ValidIssuers`, audiences, lifetime, signing key always validated). The claims provider must therefore be restricted to claims whose `Claim.Issuer` is a configured trusted issuer **and** whose identity authentication type is the bearer scheme (A-5, Phase-3 item 1). |
| #107 key-set cache "implemented contracts on main" | **Partly**: on `main` only the **contracts and stubs** exist (`Sql/Policy/RowFilterKeySets/SingleFlightKeySetCache.cs:14-37`, `CorrelatedFilterSiteRegistry.cs`, `CorrelatedKeySetSubstitution.cs`, `RowFilterKeySetContracts.cs` all throw `NotImplementedException("WP-B/C")`); `RowFilterOptions.KeySetCache` (`Domain/Options/Security/RlsOptions.cs:102-115`) and `DataSourceConnectionOptions.KeySetCacheAllowed` (`Domain/Options/Storage/DatabaseOptions.cs:62`) exist; `TABLE_ROW_FILTER_STRATEGIES` is in the baselines (`SqliteBaselineSchema.cs:509`, `PostgreSqlBaselineSchema.cs:519`, `SqlServerBaselineSchema.cs:607`). `VirtualFilterKeySetCache` and `SqlVirtualFilterKeySetProvider` (`VirtualFilters/Federation/SqlVirtualFilterKeySetProvider.cs:61-175,239-300`) are implemented and are the reusable read pattern. Per tracker I-25 depends on I-3: this plan assumes `SingleFlightKeySetCache<T>` is implemented (contract at `SingleFlightKeySetCache.cs:14-37`) when WP-2 starts; gate G-ORDER enforces it. |
| Attribute leaf and `KeySetCache` (Q-129-7) | **Confirmed not eligible by construction**: the site registry only registers `EXISTS` nodes created by `AdvancedRlsPredicateBuilder.CorrelatedExpression` (PLAN-GOV-107 A-1); consent attribute leaves are plain column predicates (not correlated), and a Virtual Filter EXISTS compiled by `SqlFilterCompiler.CompileTyped` (`:421-446`) is never registered, so a filter with a caller placeholder stays `EXISTS`. A test pins it (Q-129-7). |
| List reuse from #128 | **Per `PLAN-SQL-128` A-10** (`2026-10-16-plan-issue-128-list-parameters.md:92-104`): reuse `ListParameterNormalizer` (value carrier, type homogeneity, no `NULL`, de-dup, count) and `ListParameterLimits.EffectiveMax` (limit formula); do **not** reuse `ClientListShapes`, `ClientListExpander` or the client polarity; policy lists are expanded as policy parameters (origin `Policy`), never `ParameterOrigin.ClientNamed` (`CompiledSql.cs:10`). |
| REST/text row-filter paths and `RowFilters:SubqueryStrategy` (#136) | **Confirmed**: the compiler path ignores the setting (always `Correlated`; `InertOptionWarnings.cs:18-23`); it affects only the text builders `RowFilterSqlBuilder`/`AdvancedRlsFilterGenerator` (`AdvancedRlsFilterGenerator.cs:27-105`, `RlsOptions.cs:104`) for correlated filters. Attribute leaves are plain predicates: strategy-independent. In-memory evaluators that consume text (`StreamingRowFilterAstEvaluator.cs:17,85-92` rejects `@`/`?`, hence typed text with markers never matches there) are handled in A-8. |
| Audit | **Confirmed**: statement audit details `GovernedSqlExecutor.cs:340` (`virtual_filters`), `AddCompiledAuditFields` `:674-685` (`bind_count`, digest, optional compiled text), `AuditEventTypes.cs:116-117` (#107 admin events as precedent). `LogGeneratedSql` logs only marker text (`GovernedSqlRewriter` Debug line). |
| Four-eyes precedent | **Confirmed**: Virtual Filter drafts with `FilterApprovalStatus` and `RequireApproval` (`VirtualFilterModels.cs:8-13`, `VirtualFilterAdministrationService.cs:180-215`, audit `VIRTUAL_FILTER_PENDING_APPROVAL`); registry changes reuse this pattern (A-7). |

**PRD refinements (no veto reverted):**
1. FR-129-1 asks for a new `ParameterOrigin.CallerAttribute`. This plan keeps origin `Policy` and identifies caller binds by a reserved parameter-name prefix (A-4): a new enum member would touch `CompiledSql.cs`, every binder and the mutation controls for no security benefit, and PLAN-SQL-128 A-10 already forbids routing policy values through client origins. Owner flag F-2.
2. FR-129-2 says "both builders, three call sites". The text builders deliberately **stay constant** (A-3): their output is cached in the consent decision and consumed by value-less text consumers; values are bound only in the typed form that is built per request.
3. V-129-11 lists `NOT IN`. The consent operator set has no `NOT_IN` (`RowFilterSqlBuilder.cs:20`, `TypedRowFilterBuilder.cs:24-29`); consent filters support `EQ`, `NEQ`, `IN`; `NOT IN` exists only in Virtual Filter SQL. Owner flag F-3.
4. V-129-9/12 "one resolution per request" needs a principal handle that `TableAccessQuery` does not carry; a nullable `Principal` member is appended (A-6).

---

## 2. Architecture Decisions

### A-1 Placement: values are bound where the per-request typed policy is built, never in a cached object
Three layers keep **recipes and shapes only**: the consent decision cache (recipe + constant text), the `MandatoryRowFilterResolver._memo` (typed predicate with caller **slots**, no values), the compile template cache (shape). Values exist only in the per-request `TableAccessDecision` produced by `DecideCoreAsync`. Two bind points, both inside `TableAccessPolicy`:
- **B-consent:** `WithTypedConsentPolicy` (`TableAccessPolicy.Consent.cs:105-125`) becomes async (`WithTypedConsentPolicyAsync`): collect the attribute names of the recipe (`ValueSource == USER_ATTRIBUTE`), resolve them once (`ICallerAttributeResolver`), pass the `CallerAttributeSet` to `RowPolicyFactory.FromConsentFilters(recipe, dialect, attributes)` and on to `TypedRowFilterBuilder.BuildCombined(..., attributes)`.
- **B-vf:** the typed attach (`TableAccessPolicy.cs:309-317`): the outcome lists the attribute names its typed predicate needs (`MandatoryFilterOutcome.CallerAttributes`); resolve, bind with `CallerAttributeBinder.Bind(predicate, set)`, then `FromPredicate` + `WithMandatoryPredicate`.
Because the decision is complete before `AttachTypedMasks`, Casbin `Restrict`, the profile merge and every channel see the same bound policy, and `RequireTypedRowPolicy`/`MarkersAgree` (`GovernancePolicyFactory.cs:335-345`) compare typed projection and text exactly as for literal filters.

### A-2 `ICallerAttributeProvider` (defined here, adopted by `WP-SC-05`, `WP-SC-07`)
```csharp
public interface ICallerAttributeProvider
{
    string Kind { get; }                                   // "claims", "attribute-table", "service-set"; later "scim", "graph", "ldaps"
    ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct);
}
```
Providers are bound per attribute by the registry (`definition.Provider`); the resolver (`CallerAttributeResolver`) is the only caller: it picks the provider by kind, applies timeout, circuit breaker, normalization (`ListParameterNormalizer`), cardinality cap, caching, and request memo. `WP-SC-05` later contributes providers or a chain provider through the same DI registration (`IEnumerable<ICallerAttributeProvider>`); `WP-SC-07` adds `kind = "scim"`. No provider ever sees another provider's data and none returns a raw database or token object (result below).

### A-3 Consent leaf: typed form binds values; text form stays constant
`TypedRowFilterBuilder.Condition` (`:125-141`) gets the attribute set (appended optional parameter through `Build`, both `BuildCombined` overloads, `ConsentExpression`, `Condition`). For `USER_ATTRIBUTE`:
- operators: `EQ`/`NEQ` need a **single** attribute, `IN` accepts single or list; anything else, or a cardinality mismatch, is the fail-closed constant;
- resolved: `col = :p`, `col <> :p`, or `col IN (:p0 ... :pk)` with items typed from the attribute (`SqlParameterType` String/Int64/Decimal/Boolean via `PolicyParameterNamer.ToPolicyValue` rules; guid attributes are bound as `String`, no GUID member exists in `SqlParameterType.cs`), padded (A-6);
- not resolved (missing, empty list, failed, over cardinality, unknown name, no subject, set absent): `Constant(isDeny)` exactly as today (`:140`, polarity RR-L4-01: ALLOW `1 = 0`, DENY `1 = 1` so `NOT (1 = 1)` blocks).
`RowFilterSqlBuilder` (text) is **not** changed functionally: the cached decision text keeps the constant, so value-less consumers (non-SQL sources, legacy text consumers) fail closed. For SQL sources the typed policy replaces the text (`WithTypedConsentPolicy` nulls `CombinedRowFilterSql`, `TableAccessPolicy.Consent.cs:117`). Only the comments at `:142-146`/`:283-289` are updated.

### A-4 Virtual Filter placeholder: pre-parse rewrite to a reserved literal, then a slot parameter, then a value-binding pass
- **Grammar:** `{{caller.<name>}}`, `<name>` = `[a-z][a-z0-9_]{0,63}`, unquoted, only (a) as a scalar operand of `=`, `<>`, `<`, `<=`, `>`, `>=` or (b) as the **sole** item of `IN ( {{caller.x}} )` / `NOT IN ( {{caller.x}} )` (Q-129-2: parenthesised form; the issue's unparenthesised `IN {{caller.x}}` is **not** accepted, no ambiguity risk taken). Any other position (function argument, `LIKE`, `BETWEEN`, `CASE`, select list, mixed IN list) is refused at definition time and at compile time.
- **Rewrite (new `CallerPlaceholders` class in `SqlFilterCompiler`'s folder):** scan the fragment outside string literals (reuse the `StringLiteral` regex `:46-47`), replace each placeholder with the quoted sentinel `'__autheris_caller__<name>'`. Before the rewrite the fragment is **refused** if any string literal or identifier contains `__autheris_caller__` (case-insensitive) or `{{caller.` inside a literal (same technique as the `target` literal check `:83-86`), so a definition author cannot spell a sentinel and a stored definition is re-checked at every compile (`BindTenant` entry).
- **Validate (`:56-160`):** after the rewrite the fragment parses; the AST check proves the allowed positions; `ValidateAstPredicates` (`:317-380`) treats the sentinel like a literal (tautology and literal-literal checks still apply: `col = {{caller.x}}` must still reference `target`); the attribute name must be registered and active for the filter's tenant, the registry type must fit the compared column (checked against the catalog column type when the column is a target column; otherwise skipped), list attributes are allowed only in the IN form, scalar attributes anywhere allowed; the validation compile (`:151-157`) substitutes a harmless value.
- **Typed compile (`CompileTyped`, `TypedShaper.Bind` `:538-562`):** a string literal equal to the sentinel becomes `PolicyParameterNamer.AddCallerAttribute(name)`: a `PolicyParameterExpression` named `__gql_caller_<prefixTag>_<name>_<n>` with `PolicyValue(null, Null)` (origin `Policy`; the reversible name is the slot marker, `CallerAttributeParameterName.TryParse`). An unbound slot compiles to a comparison with `NULL`: `=`/`IN`/`NOT IN` then match no rows, so a slot that escapes binding fails closed by SQL semantics (defense in depth, tested).
- **Text compile (`Compile`, `:392-406`; `StructuredFilterSqlBuilder.Build` `:50-62`):** a definition that contains a caller placeholder never produces value text. The builder returns the constant `(1 = 0)` for the text form (deny all rows of that filter; the typed form is authoritative where a row policy factory exists). This closes the only text path that could fail open (`NOT IN ('__autheris_caller__x')` would match every row). Consequence: the two text-only fallback attach sites (`GovernedProcedureExecutionService.cs:718`, `FederatedDuckDbExecutionService.cs:501`, consent bypass without consent services) deny the table for an attribute filter (generic outcome) instead of binding; documented, tested.
- **Binding pass (`CallerAttributeBinder.Bind`, new, `SqlAstRewriter`-based):** walks the `PolicyPredicate.Expression`; a slot inside a comparison becomes a typed policy parameter holding the value (`__gql_caller_...` keeps the name, now with value); a slot as sole `IN` item expands to the padded item list (A-6); if **any** slot is unresolved, wrong cardinality or fails type/size checks the **whole** Virtual Filter predicate becomes `PolicyPredicate.DenyAll` (`PolicyPredicate.cs:43`), i.e. no rows (a Virtual Filter is a restriction ANDed with the consent decision, never a grant, `TableAccessPolicy.cs:300-318`). Result: `PolicyPredicate.Create(newExpression, newValues)` (parameter/type check, new fingerprint).

### A-5 Providers delivered by #129
1. **`claims`** (`ClaimsAttributeProvider`): allowlist mapping `claimType -> attribute name`, set per attribute in the definition; reads only claims of `CallerSubject.Claims` whose `Claim.Issuer` equals a trusted issuer of `Gateway:CallerAttributes:Claims:TrustedIssuers` (startup validation: every trusted issuer must also be a configured `ValidIssuers` entry of the bearer scheme) and only if the identity's authentication type is in `Claims:AllowedSchemes` (default: the JwtBearer scheme only; `ForwardAuth`, BasicAuth and TestAuth are **off** by default, a deployment that trusts an authenticating reverse proxy must list the scheme explicitly). Multi-valued claim = list attribute (one `Claim` per value, or a JSON array claim parsed with a strict parser). Subject-bound claims only: the claim must belong to the same identity that provided the subject key. Sync, no I/O.
2. **`attribute-table`** (`AttributeTableProvider`): administrator-defined **fixed lookup** (V-129-5): `{ dataSource, schema, table, subjectKeyColumn, subjectKeyKind (EntraObjectId | WindowsSid | Subject), valueColumn, tenantColumn (required unless the table is in `DataSources:TenantColumnExemptTables`), maxValues }`. The statement is **built by the gateway from validated identifiers** (`^[A-Za-z_][A-Za-z0-9_$#]{0,127}$`, `DatabaseDialect.QuoteIdentifier`), shape `SELECT <value> FROM <schema>.<table> WHERE <key> = @subject [AND <tenant> = @tenant]` with `Max+1` rows read (`maxValues + 1`), bound subject and tenant, **no** user text. It does not go through the governed SQL compiler because the table is by default not in the catalog (Q-129-5); it follows the read pattern of `SqlVirtualFilterKeySetProvider.ReadAsync` (`:239-300`): governed read connection `DataSources:Connections[<dataSource>]` (`_connections.CreateOpenConnectionAsync`), provider/dialect check, PostgreSQL `SET TRANSACTION READ ONLY` and rollback, session context with the **tenant only** (`IDbSessionContextInitializer.InitializeSessionAsync(..., userSid: null, ...)`, `:262`), `CommandTimeout` = provider timeout, database exceptions mapped to code `CALLER_ATTRIBUTE_SOURCE_UNAVAILABLE` with the exception only as inner and only its type logged (SEC-11 pattern `:147-152`). Dialects: SQL Server, PostgreSQL, SQLite (as the key-set provider); others refused at registration. Per-person row-level security at the source is **not** assumed: the lookup runs as the gateway service identity, so the definition must set `DataSourceConnectionOptions`-level attestation `CallerAttributeLookupAllowed` (new, default `false`, same idea as `KeySetCacheAllowed`, `DatabaseOptions.cs:62`).
3. **`service-set`** (`ServiceAttributeProvider`): for application-only subjects only (V-129-9, Q-129-6): static values per `(tenant, applicationId, attribute)` from `CALLER_ATTRIBUTE_SERVICE_SETS`, four-eyes, audited. A user subject never reads this provider; an application subject never reads the other providers (the resolver routes by `CallerSubject.Kind`).
`ICallerAttributeProvider` registrations for SCIM/Graph/LDAPS are explicitly **not** implemented; a unit test with a fake `kind = "scim"` provider proves the plug-in seam.

### A-6 Resolution, cardinality, padding, request scope
- **`CallerSubject`** (record): `TenantId Tenant`, `CallerSubjectKind Kind` (`User` | `Application` | `None`), `string? KeyValue` per key kind (resolved lazily by kind from the immutable claim lists, **never** UPN/mail/display name), `string? ApplicationId`, `IReadOnlyList<Claim> Claims` (materialized once), `string? Issuer`, `string? AuthenticationType`. Built by `CallerSubjectFactory` from `IIdentitySubjectResolver.ResolveSubject` (ServicePrincipal -> `Application`; unauthenticated/anonymous/`user:unknown` fallbacks -> `None`). `None` resolves every attribute as `Missing`.
- **`ICallerAttributeResolver.ResolveAsync(CallerSubject, IReadOnlyCollection<string> names, ct) -> CallerAttributeSet`**: per name: definition lookup (registry snapshot, A-7), provider routing, value cache, provider call under `ProviderTimeout`, normalization through `ListParameterNormalizer.Normalize` (catching `ClientListException` -> `Failed(NotNormalizable)`), cap `EffectiveMax = min(definition.MaxValues, Gateway:CallerAttributes:MaxValuesHardCap, ListParameterLimits.EffectiveMax(configured, caps) - BindReserve)` where `BindReserve` defaults to 400; **over the cap is `Failed(OverCardinality)`, never truncated** (V-129-7). Empty list is normalized to `Missing`.
- **Result types** (no values in `ToString`, INV-16):
```csharp
public enum CallerAttributeOutcome { Resolved, Missing, Failed, OverCardinality, UnknownAttribute, NoSubject }
public sealed class CallerAttributeValue  // ElementType (SqlParameterType), Count, this[int]; ToString() = "CallerAttributeValue { Type, Count }"
public sealed record CallerAttributeResult(CallerAttributeOutcome Outcome, CallerAttributeValue? Value, string? ReasonCode);
public sealed class CallerAttributeSet     // name -> result; TryGet; Outcomes (names+outcomes+reason codes only); IsEmpty
```
- **Padding (A-6 of #107, same rationale):** an `IN` expansion has length `min(nextPow2(count), MaxValues)` filled by repeating the last value (harmless for IN and NOT IN), so the template cache sees at most about 9 shapes per attribute (`Gateway:CallerAttributes:PadListsToBuckets`, default `true`).
- **Request scope (V-129-13):** `TableAccessQuery` gets an appended nullable member `ClaimsPrincipal? Principal` (set by `ForPrincipal` and the three `new TableAccessQuery(` sites). `CallerAttributeRequestScope` is a `ConditionalWeakTable<ClaimsPrincipal, RequestState>` whose state memoizes `Lazy<Task<CallerAttributeResult>>` per `(tenant, attribute)`, so every filter and every nested/cross-source read of one request sees one state. With `Principal == null` only the TTL cache applies (documented; an architecture test fails if a construction site omits the principal).

### A-7 Registry, four-eyes, persistence, generation
- **Definition** (`CallerAttributeDefinition`, stored as `definition_json` + `definition_hash`): `name`, `valueType` (`string`/`int`/`guid`/`decimal`/`bool`), `cardinality` (`single`/`list`), `maxValues` (default 200, hard cap), `ttlSeconds` (default 60, hard max 300), `provider` (`kind` + config), `description`, `status`. `CallerAttributeRegistry` loads an immutable snapshot with a **generation** (table `CALLER_ATTRIBUTE_GENERATION`, the `VirtualFilterSnapshotProvider` pattern: generation compare, `Invalidate()` after local write).
- **Four-eyes:** create/change/delete of a definition, an attribute-table provider config, or a service set is a **draft** (`FilterApprovalStatus.PendingApproval`) that a different administrator approves; the active version stays last-known-good (pattern of `VirtualFilterAdministrationService.cs:180-215`); approver equals creator is refused; audit events `CALLER_ATTRIBUTE_DEFINITION_PENDING`, `_APPLIED`, `_REJECTED`, `CALLER_ATTRIBUTE_SERVICE_SET_*` carry before/after **hashes** only.
- **Effects of a registry change:** bump generation, `Invalidate()`, drop value-cache entries of that attribute (cache key contains `definition_hash`, so stale entries cannot be read even if the drop is missed). Decision caches and template caches need **no** invalidation (they hold no values, section 7).

### A-8 Channels and in-memory consumers
- **Compiled SQL channels** (WebSQL, Trino protocol, SQL endpoints, REST, OData, GraphQL incl. tree and nested/cross-source relations, MCP, Arrow/Flight, async jobs, OLAP/federated staging): all decide through `TableAccessPolicy` and compile the typed policy; no change below the decision. Federated staging statements reach the same decision (`FederatedDuckDbExecutionService.cs:449`) and `GovernedSqlRewriter.cs:489`; the integration test proves it (section 9). GraphQL tree compiles the text projection with the decision's parameters (`TreeSqlCompiler.cs:537-538`): attribute lists are already expanded to scalar parameters.
- **In-memory typed consumers:** `InMemoryRowFilterEvaluator.FilterRows(rows, decision, metadata)` already prefers the typed policy (`InMemoryRowFilterEvaluator.cs:38-58`): attribute filters work without change.
- **In-memory text consumers** that call `StreamingRowFilterAstEvaluator.Matches(row, text)` (rejects `@`, `StreamingRowFilterAstEvaluator.cs:85-92`): `GovernedChangeDetectionService.Cursors.cs:299,305`, `CrossSourceRelationResolver.Helpers.cs:249,255`, `InMemoryCrossSourceRowReader.cs:73`, `StreamRlsPolicyEnforcer.cs:245-247`. WP-5 first writes a **characterization test** that records which of them receive typed decisions; each consumer that can receive a typed attribute policy is switched to `InMemoryRowFilterEvaluator.FilterRows(rows, decision, metadata)` (typed first, text only when no typed policy exists). The CDC enforcer builds the typed consent policy from the recipe with `CallerAttributeResolver` per event (subscriber principal captured at subscribe time, TTL cache keeps the cost per event at one memory lookup). A consumer that still cannot evaluate a typed policy denies the row (existing fail-closed behaviour); a test pins "no consumer returns a row for an attribute filter when its attribute is missing".
- **Casbin ABAC:** unchanged; `{{caller.` in a Casbin rule filter or sub-rule is refused when the policy is published (linter rule in `PolicyPredicateLinter`, `Application/Policy/Lint/PolicyPredicateLinter.cs:69` region, plus a runtime parse failure that already denies), so a shared decision can never embed an attribute (A-10 of the verification table).

### A-9 Save-time validation (V-129-4)
`ConsentRowFilterAttributeValidator` (new, pure) is invoked from a decorator `AttributeValidatingConsentRepository : IConsentRepository` (DI-wrapped around the three repositories) on `CreateConsentAsync` (the only writer of `CONSENT_ROW_FILTERS`), so GraphQL, REST admin, OpenMetadata sync and declarative apply are all covered with one seam. Rules: `ValueSource` must be `LITERAL` or `USER_ATTRIBUTE` (other value: refused; today anything is stored); `USER_ATTRIBUTE` needs a registered, active attribute (`UserAttribute` matches the name grammar), an operator in {`EQ`,`NEQ`,`IN`}, cardinality fit (`EQ`/`NEQ` single only), a SQL, non-delegated data source of the consent's table, and not `FilterType` correlated/cross-source (attribute leaves exist only on `SimpleColumnPredicate`); `ValueJson` is ignored for `USER_ATTRIBUTE` and must be `[]`/empty (a literal fallback is never used). Error text names the filter and the rule, never an attribute value. The same validator runs in `PolicyPredicateLinter` for already stored rows (lint source `SqliteGovernanceLintSource.cs:150-151`) so a registry deletion shows up as a lint finding. Virtual Filters are validated by `SqlFilterCompiler.Validate` (A-4) through `VirtualFilterAdministrationService.Evaluation.cs:25` and the linter (`PolicyPredicateLinter.cs:69`).

### A-10 Attribute table is not a catalog table by default (Q-129-5)
Registering an attribute-table provider records `schema.table` in `CALLER_ATTRIBUTES` only. Catalog ingestion (`TABLES`) and consents on that table are refused/warned: a startup/lint check (`AttributeSourceExposureCheck`) lists every catalog table that equals a registered attribute source; consent creation for such a table is refused unless `Gateway:CallerAttributes:AllowAttributeSourceInCatalog` is set for that table (explicit opt-in, warning in lint). This keeps the privileged read (service identity) from becoming readable through WebSQL/MCP by ordinary callers.

### A-11 Generic outcome and diagnostics
- **Caller-visible:** every non-`Resolved` outcome produces the constant predicate of A-3/A-4 (no rows for ALLOW/Virtual Filter, blocking for DENY). The decision, status, body and audit shape are identical for `Missing`, `Failed`, `OverCardinality`, `UnknownAttribute`, `NoSubject`. No new error code is exposed to callers; no exception escapes the bind step for these causes.
- **Operator-visible (audit/metrics only):** the statement audit gets `caller_attributes` = list of `{ id: <registry attribute id or "unregistered">, outcome, reasonCode }` (never names of unregistered input, never values; names hashed `sha256:` for non-registered), `caller_attribute_binds` count. Metrics on `GatewayDiagnostics.Meter`: `autheris.callerattr.resolutions{provider,outcome}`, `.cache.hits/.misses`, `.provider.duration` (histogram), `.circuit.open`; tags limited to `provider`, `outcome`.
- **Timing class:** all unresolved outcomes run the same constant-predicate path after resolution; provider failures are bounded by `ProviderTimeout` (default 2 s) and a per-provider circuit breaker (open after 5 consecutive failures for 10 s, immediate `Failed` while open) so a dead source costs one bounded wait, not one per request. The residual timing difference between `Missing` (cache hit) and `Failed` (first failure) is accepted and documented (AC-129-4 tolerance, Phase-3 item 6).
- **Disclosure:** `describe_my_access` and caller views show only "an attribute filter applies" (V-129-10, `EffectiveAccessExplanationService.cs`); the admin simulation `effective-filters` (FilterAdmin, audited as an administrative read) shows names, resolved values (masked beyond the first 3 in the response) and outcome for a chosen subject through the same resolver with an explicit subject.

---

## 3. Contracts (fixed up front, first commit "contracts", bodies throw `NotImplementedException`)

### C-1 Domain (`src/Autheris.Domain/Model/CallerAttributes.cs`, new)
```csharp
public enum CallerAttributeType { String = 0, Int64 = 1, Decimal = 2, Boolean = 3, Guid = 4 }
public enum CallerAttributeCardinality { Single = 0, List = 1 }
public sealed record CallerAttributeProviderBinding(string Kind, IReadOnlyDictionary<string, string> Config);
public sealed record CallerAttributeDefinition(
    Guid Id, TenantId Tenant, string Name, CallerAttributeType ValueType, CallerAttributeCardinality Cardinality,
    int MaxValues, int TtlSeconds, CallerAttributeProviderBinding Provider, string? Description, FilterApprovalStatus Status)
{ public string ComputeDefinitionHash(); }   // SHA-256 over a canonical form, like VirtualFilter.ComputeDefinitionHash
public sealed record ServiceAttributeSet(Guid Id, TenantId Tenant, string ApplicationId, string AttributeName,
    IReadOnlyList<string> Values, FilterApprovalStatus Status);
// ConsentRowFilter (ConsentModels.cs): no new member (scale-plan file-ownership note); ValueSource/UserAttribute are used as is.
```
`AuditEventTypes` (`Domain/Audit/AuditEventTypes.cs`, after `:117`): `CallerAttributeDefinitionPending/Applied/Rejected/Deleted`, `CallerAttributeServiceSetChanged`, `CallerAttributeSimulationRead`.

### C-2 Application (`src/Autheris.Application/Identity/Attributes/`, new)
```csharp
public sealed record CallerSubject(TenantId Tenant, CallerSubjectKind Kind, string? ApplicationId,
    IReadOnlyList<Claim> Claims, string? Issuer, string? AuthenticationType)
{ public string? KeyFor(SubjectKeyKind kind); }                       // EntraObjectId | WindowsSid | Subject; never UPN/mail
public interface ICallerAttributeProvider { string Kind { get; } ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject s, CallerAttributeDefinition d, CancellationToken ct); }
public interface ICallerAttributeResolver { ValueTask<CallerAttributeSet> ResolveAsync(CallerSubject s, IReadOnlyCollection<string> names, CancellationToken ct); }
public interface ICallerAttributeRegistry { ValueTask<CallerAttributeSnapshot> GetAsync(CancellationToken ct); void Invalidate(); }
public static class CallerSubjectFactory { public static CallerSubject Create(ClaimsPrincipal? principal, TenantId tenant, IIdentitySubjectResolver resolver); }
public static class CallerPlaceholders   // Application/VirtualFilters
{
    public const string SentinelPrefix = "__autheris_caller__";
    public static bool Contains(string fragment);
    public static string Rewrite(string fragment, out IReadOnlyList<string> names);       // throws ArgumentException on a forbidden spelling
}
public static class CallerAttributeParameterName { public static bool TryParse(string name, out string attribute); }
public static class CallerAttributeBinder { public static PolicyPredicate Bind(PolicyPredicate predicate, CallerAttributeSet set, CallerAttributePadding padding); }
// RowPolicyFactory.FromConsentFilters(ConsentRowFilterRecipe recipe, DatabaseDialect dialect, CallerAttributeSet? attributes = null)
// TypedRowFilterBuilder.Build/BuildCombined(..., CallerAttributeSet? attributes = null)    // appended optional; null = constants as today
// PolicyParameterNamer.AddCallerAttribute(string attribute)
// MandatoryFilterOutcome.CallerAttributes : IReadOnlyList<string> (appended init member, empty default; part of Equals)
// TableAccessQuery(..., ClaimsPrincipal? Principal = null)                                 // appended
```

### C-3 Repositories (`Autheris.Domain/Interfaces`, three engines)
`ICallerAttributeRepository`: `LoadSnapshotAsync`, `SaveDefinitionAsync` (draft/approve/reject/delete with approver != creator enforced in the service, hash check in the repository), `SaveServiceSetAsync`, `IncrementGenerationAsync`, `GetGenerationAsync`. Registered for SQLite, PostgreSQL, SQL Server next to the Virtual Filter repositories (same connection and transaction conventions).

### C-4 Options (`Gateway:CallerAttributes`, `RlsOptions.cs` file ownership per tracker I-25, validated in `GatewayCoreOptionsValidator.cs`)
| Key | Default | Rule |
|---|---|---|
| `Enabled` | `false` | off: `USER_ATTRIBUTE`/placeholder stay constants (today's behaviour), definitions refused at save |
| `MaxValuesHardCap` | 200 | 1..1000 |
| `DefaultTtlSeconds` / `MaxTtlSeconds` | 60 / 300 | default <= max <= 300 (hard) |
| `NegativeTtlSeconds` | 15 | <= default TTL |
| `ProviderTimeoutMilliseconds` | 2000 | 100..10000 |
| `CircuitBreaker:FailureThreshold` / `OpenSeconds` | 5 / 10 | |
| `BindReserve` | 400 | |
| `PadListsToBuckets` | `true` | |
| `Cache:SizeLimitBytes` | 8 MiB | own `SingleFlightKeySetCache<ResolvedAttributeEntry>` instance, never shared with #107 |
| `Claims:TrustedIssuers`, `Claims:AllowedSchemes` | empty / bearer only | issuers must be a subset of the bearer `ValidIssuers`; empty list disables the claims provider |
| `AllowAttributeSourceInCatalog` | empty list | tables only |
Connection level (`DatabaseOptions.cs`, after `KeySetCacheAllowed`): `CallerAttributeLookupAllowed` (default `false`).

### C-5 Governance DDL (in place in `0001`, checksums unchanged; **no change to `CONSENT_ROW_FILTERS`**)
SQLite (PostgreSQL: same with `TEXT`/`INTEGER`/`BIGINT`; SQL Server: `NVARCHAR(128/256/64/MAX)`, `INT`, `BIGINT`, `IF OBJECT_ID(...) IS NULL` guards, mirroring `VIRTUAL_FILTERS` at `SqliteBaselineSchema.cs:282`, `PostgreSqlBaselineSchema.cs:291`, `SqlServerBaselineSchema.cs:336`):
```sql
CREATE TABLE IF NOT EXISTS CALLER_ATTRIBUTES (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    definition_json TEXT NOT NULL,
    definition_hash TEXT NOT NULL,
    status INTEGER NOT NULL,                  -- FilterApprovalStatus of the active version
    created_by TEXT NOT NULL,
    approved_by TEXT NULL,
    approved_at TEXT NULL,
    draft_definition_json TEXT NULL,          -- last-known-good: the active row stays, the change waits here
    draft_definition_hash TEXT NULL,
    draft_created_by TEXT NULL,
    updated_by TEXT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id, name)
);
CREATE TABLE IF NOT EXISTS CALLER_ATTRIBUTE_SERVICE_SETS (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    application_id TEXT NOT NULL,
    attribute_name TEXT NOT NULL,
    values_json TEXT NOT NULL,
    values_hash TEXT NOT NULL,
    status INTEGER NOT NULL,
    created_by TEXT NOT NULL,
    approved_by TEXT NULL,
    approved_at TEXT NULL,
    draft_values_json TEXT NULL,
    draft_created_by TEXT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id, application_id, attribute_name)
);
CREATE TABLE IF NOT EXISTS CALLER_ATTRIBUTE_GENERATION (id INTEGER PRIMARY KEY CHECK (id = 1), generation INTEGER NOT NULL);
INSERT INTO CALLER_ATTRIBUTE_GENERATION (id, generation) VALUES (1, 0) ON CONFLICT (id) DO NOTHING;   -- engine-specific upsert as VIRTUAL_FILTER_GENERATION
```
Shape snapshots `governance-shape.{sqlite,postgresql,sqlserver}.json` and `GovernanceSchemaShape*` tests are updated (new tables only). Index `ix_caller_attributes_tenant (tenant_id, name)` is the unique constraint; none other needed (registry is loaded as a snapshot, service sets read by the unique key).

---

## 4. Changed Files and Ownership (one developer; every file has exactly one work package)

### WP-1 - Contracts, domain, options, DDL, repositories, registry service
- New: `src/Autheris.Domain/Model/CallerAttributes.cs`, `src/Autheris.Domain/Interfaces/ICallerAttributeRepository.cs`, `src/Autheris.Application/Identity/Attributes/{CallerAttributeContracts,CallerAttributeRegistry,CallerAttributeAdministrationService,CallerAttributeNameRules}.cs`, `src/Autheris.Infrastructure/Persistence/{Sqlite,PostgreSql,SqlServer}CallerAttributeRepository.cs`.
- Edit: `src/Autheris.Domain/Audit/AuditEventTypes.cs` (after `:117`), `src/Autheris.Domain/Options/Security/RlsOptions.cs` (`CallerAttributes` options, after `RowFilterOptions` `:102-115`), `src/Autheris.Domain/Options/Storage/DatabaseOptions.cs` (`CallerAttributeLookupAllowed`), `src/Autheris.Api/Configuration/Validation/GatewayCoreOptionsValidator.cs`, `Migrations/{Sqlite,PostgreSql,SqlServer}BaselineSchema.cs`, the three shape JSON files, DI in `GatewayGovernanceServiceExtensions.cs` (`:118` region).
- Tests: `tests/Autheris.Tests.Unit/Identity/Attributes/CallerAttributeDefinitionTests.cs`, `CallerAttributeAdministrationServiceTests.cs` (four-eyes, last-known-good, approver != creator), repository tests per engine next to the Virtual Filter repository tests, shape tests, options validator tests.

### WP-2 - Provider layer and resolver
- New: `src/Autheris.Application/Identity/Attributes/{CallerSubjectFactory,CallerAttributeResolver,CallerAttributeValueCache,CallerAttributeRequestScope,CallerAttributeCircuitBreaker,ClaimsAttributeProvider,AttributeTableProvider,AttributeTableQuery,ServiceAttributeProvider}.cs`.
- Edit: none outside new files except DI.
- Tests: `tests/Autheris.Tests.Unit/Identity/Attributes/{CallerSubjectFactoryTests,ClaimsAttributeProviderTests,AttributeTableProviderTests,ServiceAttributeProviderTests,CallerAttributeResolverTests,CallerAttributeCacheTests}.cs` (SQLite fixture for the table provider; `FakeTimeProvider`), `AttributeTableQueryTests` (injection corpus on identifiers).

### WP-3 - Consent path
- Edit: `src/Autheris.Application/Sql/Policy/TypedRowFilterBuilder.cs` (`:31,49,61,106,125-141`: attribute parameter and leaf), `src/Autheris.Application/Policy/RowPolicyFactory.cs` (`:61`), `src/Autheris.Application/Policy/TableAccessPolicy.Consent.cs` (`:85,44,105-125` async attach), `src/Autheris.Application/Policy/TableAccessPolicy.cs` (`TableAccessQuery` `Principal`, `ForPrincipal` `:47-69`, resolver injection), `src/Autheris.Application/Sql/Policy/PolicyParameterNamer.cs` (`AddCallerAttribute`), comments in `src/Autheris.Application/Services/RowFilterSqlBuilder.cs` (`:142-146,283-289`, behaviour unchanged), `UnifiedPolicyDecisionPoint.cs:76`, `GovernedSqlRewriter.cs:489`, `FederatedDuckDbExecutionService.cs:449` (pass the principal), new `src/Autheris.Application/Identity/Attributes/{ConsentRowFilterAttributeValidator,AttributeValidatingConsentRepository}.cs`, `PolicyPredicateLinter.cs`.
- Tests: `tests/Autheris.Tests.Unit/Sql/Policy/TypedRowFilterBuilderAttributeTests.cs`, `Policy/TableAccessPolicyCallerAttributeTests.cs`, `ConsentRowFilterAttributeValidatorTests.cs`; re-pin `ConsentResolutionTests.cs:494-511`, `TypedRowFilterBuilderTests.cs:130`, `Phase1AndPhase2SecurityExpertTests.cs:182`, `PipelineAndInfrastructureSecurityTests.cs:260`, `ParityCases.cs:40,145-149` (see section 9 for the new expected values).

### WP-4 - Virtual Filter path
- New: `src/Autheris.Application/VirtualFilters/CallerPlaceholders.cs`, `src/Autheris.Application/Identity/Attributes/{CallerAttributeBinder,CallerAttributeParameterName,VirtualFilterAttributeAttacher}.cs`.
- Edit: `src/Autheris.Application/VirtualFilters/SqlFilterCompiler.cs` (`Validate` `:56-160`, `Compile` `:392-406`, `CompileTyped` `:421-446`, `TypedShaper.Bind` `:538-562`, `BindTenant` `:262-307` entry check), `VirtualFilters/StructuredFilterSqlBuilder.cs` (`Build` `:50-62` constant text, `BuildTyped` `:136-149`), `VirtualFilters/MandatoryRowFilterResolver.cs` (`MandatoryFilterOutcome.CallerAttributes`, collection in `:488-505`, the constant-text path must not deny the whole resolution), `Policy/TableAccessPolicy.cs` (`:309-317` attacher), `Procedures/Services/GovernedProcedureExecutionService.cs:709-718` and `Sql/Services/FederatedDuckDbExecutionService.cs:490-502` (deny for attribute filters, no binding).
- Tests: `tests/Autheris.Tests.Unit/VirtualFilters/{CallerPlaceholderTests,SqlFilterCompilerCallerAttributeTests,CallerAttributeBinderTests,MandatoryRowFilterResolverCallerAttributeTests}.cs`.

### WP-5 - In-memory and stream consumers
- Edit: `src/Autheris.Application/Streaming/Services/StreamRlsPolicyEnforcer.cs` (`:134,245-247,305-342,371-374`), `Data/Changes/GovernedChangeDetectionService.Cursors.cs:285-311`, `Data/Relations/CrossSourceRelationResolver.Helpers.cs:249-255`, `Data/Relations/InMemoryCrossSourceRowReader.cs:73` (typed-first evaluation, only for decisions carrying a typed policy).
- Tests: `tests/Autheris.Tests.Unit/Streaming/StreamRlsCallerAttributeTests.cs`, `Data/Changes/ChangeDetectionCallerAttributeTests.cs`, `CrossSourceCallerAttributeTests.cs`, the characterization test of A-8.

### WP-6 - Admin surface, simulation, disclosure, audit, metrics
- New: `src/Autheris.Api/Endpoints/CallerAttributeAdminEndpoints.cs` (`GET|PUT|DELETE /api/v1/governance/caller-attributes[/{name}]`, `POST .../{name}/approve|reject`, `GET|PUT /api/v1/governance/caller-attributes/service-sets`; GovernanceAdmin, `WithAudit(AuditLevel.Full, ...)`), MCP admin tools `admin_plan_caller_attribute` and `admin_apply_caller_attribute` reusing the confirm flow (`AccessPlanningService.ConfirmPlanAsync`, plan kind added as in PLAN-GOV-107 A-8), `src/Autheris.Application/Identity/Attributes/CallerAttributeSimulationService.cs`.
- Edit: `src/Autheris.Application/VirtualFilters/EffectiveFilterInspector.cs` (simulation for a subject, FilterAdmin), `src/Autheris.Application/Services/EffectiveAccessExplanationService.cs` (caller view: "an attribute filter applies" only), `src/Autheris.Application/Sql/Services/GovernedSqlExecutor.cs` (`:340` and `AddCompiledAuditFields` `:674-685`: `caller_attributes`, `caller_attribute_binds`), `src/Autheris.Domain/Diagnostics/GatewayDiagnostics.cs` (metrics).
- Tests: `tests/Autheris.Tests.Unit/Api/CallerAttributeAdminEndpointTests.cs`, `Mcp/CallerAttributePlanTests.cs`, `Identity/Attributes/CallerAttributeDisclosureTests.cs`, audit tests next to the existing executor audit tests.

### WP-7 - Integration, parity, security corpus, gates
- Tests only: `tests/Autheris.Tests.Integration/CallerAttributeChannelParityTests.cs` (extends `RowFilterChannelParityTests.ChannelMatrix`, `tests/Autheris.Tests.Integration/RowFilterChannelParityTests.cs:85`; shared SQLite fixture; PostgreSQL/SQL Server via the shared Testcontainers fixtures), `tests/Autheris.Tests.Unit/Security/CallerAttributeSecurityTests.cs`, SqlGate parity cases (`ParityCases.cs`), architecture test (`TableAccessQuery` construction sites pass the principal; no `ClientNamed` origin for caller binds), G8 allocation and `CompilePathPerformanceTests` re-run (lead builds).
- Phase 6 hand-over (not part of ownership): `docs/features/f-gov-14-*`, new feature file `docs/features/f-gov-<n>-caller-attribute-row-filters.md` registered in `docs/features/README.md`, `docs/configuration-guide.md`, master plan, scale plan row 22 pointer.

---

## 5. Fail-Closed Behaviour

| Situation | Consent filter ALLOW | Consent filter DENY | Virtual Filter | Caller sees |
|---|---|---|---|---|
| Resolved scalar/list | `col <op> bound` | `NOT (col <op> bound)` | typed slot bound | rows per the filter |
| `Missing` / empty list | `1 = 0` | `1 = 1` (blocks) | `DenyAll` | no rows / same decision shape |
| Provider failure, timeout, circuit open | as Missing | as Missing | as Missing | identical to Missing |
| Over cardinality (never truncated) | as Missing | as Missing | as Missing | identical |
| Unknown / unregistered / inactive attribute at runtime | as Missing | as Missing | as Missing | identical |
| Wrong operator or cardinality at runtime (stored row bypassed validator) | constant | constant | `DenyAll` | identical |
| No subject (anonymous, `user:unknown`, certificate without key) | as Missing | as Missing | as Missing | identical |
| Application-only token, no service set | as Missing | as Missing | as Missing | identical |
| Service set exists but is `PendingApproval` | as Missing | as Missing | as Missing | identical |
| `Gateway:CallerAttributes:Enabled = false` | constant (today) | constant (today) | `DenyAll` | identical |
| Non-SQL source, or delegated source | constant (text only) | constant | n/a | identical; refused at save |
| Text-only VF attach (consent bypass without services) | n/a | n/a | table denied | generic denial |
| Slot left unbound by a defect | n/a | n/a | `= NULL`/`IN (NULL)` matches no rows | no rows |
| Bind budget exceeded after expansion | statement denied (`SQL_LIMIT_EXCEEDED`) | statement denied | statement denied | existing generic limit error, never unfiltered |
| Token scope restricted (#26) | filter applies unchanged (V-129-8) | | | |
The caller-visible decision never names the cause; the audit record carries stable codes (`Missing`, `Failed:SOURCE_UNAVAILABLE`, `Failed:TIMEOUT`, `OverCardinality`, `UnknownAttribute`, `NoSubject`, `NotNormalizable`) and registry ids, never values.

---

## 6. Cache and Epoch Design

| Cache | Holds | Keyed by | Invalidated by |
|---|---|---|---|
| Consent decision cache (existing) | recipe + constant text, **no values** | tenant, user, table, context hash | existing epoch/consent revocation (immediate: AC-129-5 revocation clause holds without new code) |
| `MandatoryRowFilterResolver._memo` (existing) | typed predicate with caller **slots** | VF generation + query tuple | VF generation (existing); registry changes do not matter (slots carry names only) |
| Compile template cache (existing) | SQL shape with markers | `PolicyPredicate.Fingerprint` (shape) | never needed for values; shape change = new template; padding bounds shapes |
| Attribute value cache (new, `SingleFlightKeySetCache<ResolvedAttributeEntry>`, own instance) | values (memory only) | `SHA-256("cav/v1" \| tenant \| issuer \| subjectKeyKind \| subjectKey \| attributeName \| definitionHash \| providerGeneration)` (64 hex, no truncation; key material is hashed so the subject key never sits in clear in the cache index) | TTL (absolute, default 60 s, max 300 s; `Missing` cached `NegativeTtl`), failures never cached, registry/provider reconfiguration (definition hash in key plus explicit drop), explicit `ICallerAttributeCacheInvalidator.InvalidateSubject/InvalidateAttribute` (hook for `WP-SC-07` SCIM lock and admin "refresh") |
| Request memo (new) | one result per `(tenant, attribute)` per principal | `ClaimsPrincipal` reference | request end (weak table) |
**Why attribute changes need no decision/template invalidation:** no cache above holds a value. A changed value is visible to the next request after the value-cache TTL (AC-129-5); a changed *definition* is visible immediately (generation + hash in the key); a consent/profile revocation is visible immediately through the existing consent epoch. **Shared decision cache (`ShareAcrossUsers`, WP-PF-09):** unaffected (Casbin-only); a test asserts that Casbin rules cannot carry caller placeholders, so no attribute-dependent filter can be shared across users, and that `DecisionKey.CreateShared` inputs are unchanged. **`PolicyFingerprint`:** attribute shape (scalar, list bucket, constant) is part of the fingerprint through parameter names and node structure; values are not (existing contract). **`KeySetCache` (#107):** not eligible (Q-129-7); a filter with a caller slot, and a correlated filter, stays `EXISTS`; the key-set cache key never contains attribute material.

---

## 7. Configuration

See C-4. No setting changes behaviour while `Enabled = false`; `Enabled = true` with an empty `Claims:TrustedIssuers` and no attribute-table provider is valid (service sets only). Startup validation: trusted issuers subset of bearer `ValidIssuers`; every registered attribute-table provider references an existing `DataSources:Connections` entry with `CallerAttributeLookupAllowed = true` (otherwise the definition is refused at approval and fails closed at runtime with `Failed:SOURCE_NOT_ALLOWED`).

---

## 8. Test Strategy: TDD List (write red first, in this order)

Group R (re-pin, red because behaviour changes):
1. `TypedRowFilterBuilderTests` (`:130`): `USER_ATTRIBUTE` with an attribute set binds `col = @p` (ALLOW), `NOT (col = @p)` (DENY); without a set the constant remains (ALLOW `1 = 0`, DENY `1 = 1`).
2. `ParityCases` (`:40,145-149`): the SqlGate parity reference model gets the attribute-set variant; the "no set" cases keep their constants.
3. `ConsentResolutionService`/`RowFilterSqlBuilder` text path: still `1 = 0`/`1 = 1` (unchanged, now explicitly pinned as "text never carries values").

Group A (providers and resolver):
4. Claims provider: only claims of a trusted issuer **and** allowed scheme; a forged claim of another issuer, a header-derived (ForwardAuth/Basic/Test) claim, a claim on an app-only principal: `Missing`.
5. Subject key: app-only token (`idtyp=app`, `appid`, `client_id`, `azp`) never becomes a user key although `GetUserSid()` returns the app id (regression for `Sid.cs:21-36`); UPN/mail never used; same `oid`, different tenant: different cache entry.
6. Attribute-table provider: parameterized statement golden text per dialect, bound subject and tenant, tenant predicate present, `Max+1` read, over cap -> `OverCardinality` (not truncated), database exception text never in message/log (log capture), read-only transaction on PostgreSQL, tenant-only session context, `CallerAttributeLookupAllowed=false` -> `Failed`.
7. Identifier injection corpus for schema/table/column definitions (quotes, comment markers, brackets, unicode, 129 chars) refused at registration and at runtime.
8. Service-set provider: only for application subjects; user subject never reads it; `PendingApproval` not applied.
9. Resolver: TTL via `FakeTimeProvider` (60 s, never above 300 s even if configured higher), `Missing` negative TTL, failures not cached, single-flight (100 concurrent resolutions, one provider call), circuit breaker opens after 5 failures and closes after 10 s, provider timeout, definition-hash change drops stale entries, empty list = `Missing`, de-dup/type homogeneity through `ListParameterNormalizer`, no truncation.
10. Request scope: two filters and a nested read of one request see one state even if the TTL boundary passes between them; a second request sees the new state.
11. Plug-in seam: fake `kind = "scim"` provider registered through DI is used for an attribute bound to it (SC-05/SC-07 adoption).
12. Registry and admin: four-eyes (approver == creator refused), last-known-good during pending change, generation bump and snapshot refresh, audit records carry hashes only.

Group C (consent path):
13. Two persons with different cost centres on one table through one consent/profile: each gets exactly their rows; a third person without the attribute gets none (AC-129-1), through `TableAccessPolicy.DecideAsync`.
14. Same decision-cache entry serves both persons (cache holds recipe only): mutation control: bind values into the cached decision and assert the test fails.
15. Operators `EQ`/`NEQ`/`IN` scalar and list; `EQ` with a list attribute and a stored invalid operator fall to the constant; DENY polarity matrix (missing in DENY blocks; empty list in ALLOW matches none; AC-129-9).
16. Padding: list of 1, 3, 5, 200 values -> 1, 4, 8, 256 items; repeated last value; template cache hit across different value sets of the same bucket; shape differs across buckets.
17. Save-time validation: unknown/inactive attribute, bad name, non-SQL source, delegated source, correlated/cross-source `FilterType`, list attribute with `EQ`, `ValueSource` outside {`LITERAL`,`USER_ATTRIBUTE`}, non-empty `ValueJson` for an attribute filter, attribute table registered in the catalog: all refused through GraphQL mutation, REST admin and declarative apply (one decorator seam); a stored row whose attribute disappeared is a lint finding and fails closed at runtime (AC-129-10).
18. Typed/text agreement: `RequireTypedRowPolicy` and `MarkersAgree` pass for decisions with bound attributes; text projection has `@__gql_rls_n` markers only, never a value.

Group V (Virtual Filters):
19. Placeholder rewrite: scalar and `IN ( {{caller.x}} )`; unparenthesised `IN {{caller.x}}`, function argument, `LIKE`, `BETWEEN`, mixed IN list, quoted `'{{caller.x}}'`, a literal containing `__autheris_caller__`: all refused at `Validate` and re-refused at `CompileTyped` for a stored definition.
20. `NOT IN ({{caller.x}})` with a resolved list excludes exactly those rows; with `Missing` the filter denies all rows (never matches all); text form returns the `(1 = 0)` constant and never a sentinel literal (fail-open control for the text path).
21. Memo safety: two callers with the same `(generation, user, groups, roles, table)` tuple but different attributes (simulate by changing the provider between calls): the outcome object is identical and value-free; values differ after binding.
22. Unbound slot defense: bypass the binder in a test, compile, assert the emitted predicate matches no rows on SQLite.
23. Text-only fallback sites (`GovernedProcedureExecutionService.cs:718`, `FederatedDuckDbExecutionService.cs:501`) deny a table with an attribute Virtual Filter; no value, no sentinel in any SQL text.
24. Gateway-side enforcement mode (`WithoutEnforcedVirtualFilters`, F-GOV-14) and the pushed predicate use the same resolved set within one request (AC-129-12); a pushed list above `MaxPushdownKeys`/bind budget fails closed.
25. Casbin: a rule filter or sub-rule containing `{{caller.` is refused by the linter; `PolicyShapeAnalyzer` shareability and `DecisionKey.CreateShared` inputs unchanged.
26. #107 interplay: a correlated consent filter and a Virtual Filter with a caller slot stay `EXISTS` with `KeySetCache` globally on (`SEC-RF107-*` conditions untouched; no attribute material in key-set cache keys).

Group P (cross-channel parity; `CallerAttributeChannelParityTests`, claims attribute and attribute-table attribute, two persons, one fixture):
27. WebSQL, Trino protocol, SQL endpoint, REST, OData, GraphQL (flat, tree, nested relation, cross-source relation), MCP query/sample rows, Arrow export, Flight SQL, async job, OLAP/federated staging: each returns exactly the rows of person A for A and of person B for B, identical id sets across channels (AC-129-2), under a restricted token scope (V-129-8).
28. CDC delivery and webhook enforcement (`StreamRlsPolicyEnforcer`) and change detection: events of rows outside the person's attribute are not delivered; attribute change applies after TTL; subscriber without attribute receives nothing (WP-5 characterization first).
29. Failed lookup vs missing attribute: identical status, body, row count and audit shape; audit codes differ; timing class within tolerance using the circuit breaker and `FakeTimeProvider` (AC-129-4).
30. Application-only token: no rows unless a service set exists (AC-129-6); with a service set the static attributes apply on all channels.
31. Forged-input corpus (AC-129-3): header, query, body parameter, `{{caller.x}}` typed by a WebSQL client, a client parameter named like a caller slot, a claim of another issuer, a claim added by ForwardAuth: no effect on the resolved value.
32. Value hygiene (AC-129-11): attribute values with quotes, `--`, `/*`, `{{caller.`, 10 KB strings never appear in SQL text, log capture, audit JSON, exception messages, `ToString()` of any result/set/binder type, cache keys; `LogGeneratedSql=true` run.
33. Disclosure (AC-129-7): `describe_my_access` and caller views say only "an attribute filter applies"; the simulation shows names/values/outcome, is audited, and requires FilterAdmin.
34. Bind budget (AC-129-8): list at the cap plus policy and mask parameters on SQL Server (2,100 binds) fails closed (error, never unfiltered); over-cap list never truncated, ALLOW none / DENY all.
35. Architecture tests: every `new TableAccessQuery(` site passes `Principal`; caller binds never use `ParameterOrigin.ClientNamed`/`ClientPositional`; `CallerAttributeBinder` is the only code that constructs a `__gql_caller_` parameter with a value.
36. Gates: SqlGate and AST security suites, `RowFilterChannelParityTests`, `VirtualFilterChannelParityTests`, G8 allocation gate (no new allocation on a decision without attribute filters: the resolver is skipped when the recipe/outcome names no attribute) stay green.

---

## 9. Work Packages for One Sequential Developer

Order is fixed (gates G-ONE, G-ORDER: starts after #128 (I-5) and #107 (I-3) are merged); each WP ends green on its own tests, the lead builds once per item (G-BUILD, Release with warnings as errors).
1. **WP-1** contracts commit first (C-1..C-4 compile with `NotImplementedException` bodies), tests 12 red, then domain, options, DDL x3, shapes, repositories, registry service.
2. **WP-2** tests 4-11 red, then providers, resolver, caches.
3. **WP-3** tests 1-3, 13-18 red, then builder, factory, `TableAccessPolicy`, validator decorator; re-pin the listed existing tests in the same WP.
4. **WP-4** tests 19-26 red, then placeholder rewrite, compiler, binder, resolver outcome, attacher, fallback sites.
5. **WP-5** characterization test, tests 28 red, then stream and in-memory consumers.
6. **WP-6** tests 12 (endpoints), 33 red, then endpoints, MCP tools, simulation, disclosure, audit, metrics.
7. **WP-7** tests 27, 29-32, 34-36 (integration, security, architecture, gates). Hand-over note for Phase 6.
Gate per WP: SqlGate and AST security suites, existing consent, Virtual Filter, GraphQL, CDC and WebSQL tests stay green.

---

## 10. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | A consumer binds neither typed policy nor constant (text-only path with a sentinel) and fails **open** (`NOT IN`) | Text form of a caller-placeholder filter is always the constant `(1 = 0)`; slot parameter is `NULL` valued; tests 20, 22, 23; Phase-3 item 3 |
| R-2 | Value baked into a cached object by a later refactoring | Mutation control in test 14 and 21; value-free `ToString` (test 32); architecture test 35 |
| R-3 | Claims provider trusts claims not attested by a validated issuer (ForwardAuth, normalizer additions) | Issuer + scheme allowlist, off by default, test 4; Phase-3 item 1 |
| R-4 | Subject key confusion (`GetUserSid()` fallback to app id; UPN reuse) | `CallerSubjectFactory` over `IIdentitySubjectResolver`; key kinds are immutable ids; test 5 |
| R-5 | Attribute-table lookup runs as the service identity and could read other tenants or become a catalog table | Mandatory tenant column (unless exempt) and tenant predicate, tenant-only session, `CallerAttributeLookupAllowed`, A-10 exposure check, four-eyes; Phase-3 item 2 |
| R-6 | Template/plan churn from list shapes | Power-of-two padding (about 9 shapes per attribute), measured in WP-7 (test 36) |
| R-7 | Bind budget exceeded with several list attributes on SQL Server (2,100) | Cap minus reserve; compile-time limit error denies (test 34); documented |
| R-8 | CDC/stream path stays text-only for attributes | WP-5 characterization first; typed-first switch; consumers that cannot evaluate typed policies deny; test 28; if a consumer cannot be switched safely it is listed as an accepted fail-closed deviation by the lead before Phase 4 ends |
| R-9 | Long-lived CDC subscription keeps stale token claims | Claims provider reads the captured subscriber principal; staleness bound = token lifetime; flagged for Phase 3 (attribute-table/service-set providers are independent of the token and honour TTL) |
| R-10 | Timing side channel between `Missing` and `Failed` | Bounded timeout, circuit breaker, constant-predicate convergence; residual difference documented (Phase-3 item 6) |
| R-11 | Recipe replay: an old cached decision recipe refers to an attribute that was deleted | Runtime unknown-attribute is `Missing` (constant); lint finding; test 17 |
| R-12 | Scope creep into SC-05/SC-07 | Only the interface and a fake provider test; no Graph/LDAPS/SCIM code |
| R-13 | Shared files with #107/#128/#130/#131 (`RlsOptions.cs`, `DatabaseOptions.cs`, `ConsentModels.cs` untouched here, `CompiledSql.cs` untouched here) | Edit order per tracker gate G-SHARED; this plan appends members only |

---

## 11. Owner Flags and Open Questions

| ID | Question | Default used in this plan |
|---|---|---|
| F-1 | `TableAccessQuery` gets a nullable `Principal` (public record member, 4 construction sites change) | Accept; needed for one resolution per request |
| F-2 | PRD FR-129-1 `ParameterOrigin.CallerAttribute` is **not** added (origin `Policy` + reserved name) | Accept, or approve the enum member in a follow-up |
| F-3 | `NOT IN` for consent filters (new operator `NOT_IN` would touch both builders, the evaluators and the in-memory path) | Not in this item; Virtual Filter SQL has it; DENY consent + `IN` expresses exclusion |
| F-4 | Virtual Filter attribute under consent-bypass-without-consent-services (two text-only fallback sites) is denied | Accept (dev/bypass configuration only) |
| F-5 | Text-only consumers that cannot evaluate typed policies stay fail-closed for attributes (A-8 list) | Accept after the WP-5 characterization result |
| F-6 | Claims provider default: bearer only; `ForwardAuth` must be enabled explicitly per deployment | Accept |
| F-7 | Attribute-table dialect scope: SQL Server, PostgreSQL, SQLite (Oracle later) | Accept |
| Q-129-2..7 | Defaults of the PRD recommendation (decided above) | See header |

---

## 12. Security Notes for Phase 3 (dedicated review, tracker S-4)

1. **Provider trust and claim spoofing.** Review `ClaimsAttributeProvider`: trusted issuer subset of `ValidIssuers` (`GatewayServiceCollectionExtensions.cs:364`), authentication-type allowlist, claims added by `ClaimsNormalizer.cs:8-70` and `EnterpriseClaimsTransformation`, header-derived claims of `ForwardAuthAuthenticationHandler.cs:270-300`, `TestAuthHandler` claims in non-development builds, claims with the same type from two identities, claim size/count bounds, JSON-array claim parsing.
2. **Attribute-table provider is a privileged read** (V-129-5). Review statement construction (identifier grammar, quoting per dialect, bound subject/tenant, `Max+1`), tenant predicate and session context, read-only transaction, `CallerAttributeLookupAllowed`, exclusion from the catalog (A-10), four-eyes bypass paths (declarative apply, GraphQL, seeding), service-account privilege (SELECT on the attribute table only).
3. **Fail-open candidates.** Walk every consumer of a decision for a caller-placeholder filter: text projection, `MandatoryRowPredicateSql`, `RowFiltersAfterVirtualFilters`, `WithoutEnforcedVirtualFilters`, `GraphQL tree` text binder, procedures, DuckDB staging, in-memory text evaluators; confirm the constant/`NULL` slot semantics and the `NOT IN` case; confirm the sentinel cannot be spelled by a definition author, a WebSQL client or a stored definition predating the feature.
4. **One generic outcome (V-129-3, #30).** Diff status, body, row count, headers and audit shape across causes; confirm no exception text, reason code or timing class distinguishes `Missing` from `Failed` for the caller (also nested GraphQL relations and async job result paths).
5. **Cache keys and value placement (V-129-2, V-129-6).** Confirm no value in decision cache, memo, template cache, key-set cache, logs, spans, audit; confirm the value-cache key hashes subject material and contains the definition hash; single-flight cannot serve one subject's load to another; failures not cached; negative caching bounded; interplay with `SEC-RF107-*` (a correlated filter and a caller slot never become key-set eligible) and with `ShareAcrossUsers` (Casbin refusal of the placeholder).
6. **Timing and resource.** Provider timeout, circuit breaker thresholds, per-subject concurrency, memory bound of the cache (`SizeLimitBytes`), enumeration of subjects through the simulation endpoint (FilterAdmin, audited, rate limit), residual `Missing` vs `Failed` timing.
7. **Cardinality and budget (V-129-7).** Never truncate; cap formula; padding semantics for `NOT IN` (repeated last value is neutral); bind budget at SQL Server 2,100 with several attributes; `ListParameterNormalizer` reuse does not route a policy list through client origins (`PLAN-SQL-128` A-10).
8. **Subject key and application-only tokens (V-129-1, V-129-9).** `CallerSubjectFactory` mapping for certificates, ForwardAuth, BasicAuth, TestAuth, Entra app-only, on-prem SID; `GetUserSid()` fallback to `appid`/`client_id`/`azp` must not leak into the key; service-set approval and application-id spoofing.
9. **Scope and polarity (V-129-8, FR-129-8).** Restricted token scopes never skip the filter; ALLOW/DENY polarity matrix; empty list equals missing in both polarities.
10. **Disclosure (V-129-10).** Caller views, error bodies, `effective-filters`, audit exports, diagnostics endpoints: no attribute names or values for ordinary callers; simulation masking and audit.
11. **Registry governance.** Four-eyes enforcement in the service and the repository, last-known-good during drafts, generation/snapshot race, deletion of an attribute used by consents or Virtual Filters (lint, runtime constant), declarative/managed definitions (`ManagedBy` semantics if added later).
12. **Engine safety.** `CallerAttributeBinder` rewrites only slot parameters it created; confirm it cannot replace a client or tenant parameter, that `PolicyPredicate.Create` re-validates, and that `AstSecurityVisitor.cs` and `PolicySubqueryTenantRewriter` are untouched (policy subqueries with a slot inside a VF EXISTS remain governed by the same rewriter).

---

## 13. Size Estimate (L)

| Area | Production LOC | Test LOC |
|---|---:|---:|
| WP-1 domain, options, DDL x3, shapes, repositories x3, registry/admin service | 460 | 520 |
| WP-2 subject, resolver, caches, circuit breaker, three providers, attribute-table query | 520 | 640 |
| WP-3 builder leaf, factory, `TableAccessPolicy`, validator + decorator, linter | 230 | 520 |
| WP-4 placeholder rewrite, compiler changes, binder, resolver outcome, attacher, fallback sites | 330 | 480 |
| WP-5 stream and in-memory consumers | 110 | 220 |
| WP-6 endpoints, MCP tools, simulation, disclosure, audit, metrics | 330 | 330 |
| WP-7 integration, parity, security, architecture | 0 | 560 |
| **Total** | **about 1,980** | **about 3,270** |
Above the PRD estimate (1,600 / 2,200) because the plan adds the stream/in-memory consumers, the save-time seam, the request scope and the simulation; the SC-05 chain and SC-07 SCIM implementations remain excluded. One sequential developer; shared-file order per tracker gate G-SHARED (`RlsOptions.cs`, `DatabaseOptions.cs`, `TableAccessPolicy.cs`, the three baseline DDL files, `GovernedSqlExecutor.cs`).

---

## 14. Changelog

| Revision | Date | Change |
|---|---|---|
| 1 | 2026-10-16 | Initial Phase 2 plan (status PROPOSED), Q-129-1 decided, Q-129-2..7 at PRD default. |
| 1.1 | 2026-10-16 | Phase 3 security review appended (`SEC-129-01..16`, binding conditions `BC-129-01..16`, mandatory tests `ST-129-01..24`); verdict APPROVED WITH CONDITIONS. No change to sections 1-13. |
| 1.2 | 2026-10-17 | Phase 4 start (I-25), G-PLAN-REBASE drift and decisions: (1) `SingleFlightKeySetCache<T>` is implemented and lives in `Autheris.Application/Caching/SingleFlightKeySetCache.cs` (I-3 merged; section 1 said stub path). Its loads are detached from caller cancellation and bounded by an own timeout, which satisfies BC-129-08 directly. (2) The consent operator set on this branch is `EQ, NEQ, LT, GT, LTE, GTE, LIKE, IN`; the attribute leaf still accepts only `EQ`, `NEQ`, `IN`. (3) Enum member names of `CallerAttributeType`/`CallerAttributeCardinality` differ from C-1 (`Text, Whole, Number, Flag, Uuid`; `Scalar, List`) because analyzer CA1720 forbids type names. (4) `ICallerAttributeRepository` is a set of four-eyes steps (submit, approve-by-hash, reject, request deletion, service sets) instead of `SaveDefinitionAsync`, so the repository itself guards approver != creator and the reviewed hash (BC-129-10); it is implemented as a partial of the three governance repositories (the connection and write-gate helpers are members there) over one shared `CallerAttributeStore`. (5) `ICallerAttributeResolver` gained `CreateSubject(principal, tenant, longLived)` and `IsEnabled`; `CallerSubjectFactory` takes `CallerAttributeOptions` (allowed schemes, person issuers). (6) Added options `ProviderMaxConcurrency`, `RequestMemoSeconds`, `RequireSeparateApproverRole`, `SimulationRequestsPerMinute`, trusted issuer objects with `SubjectKeyIsPerson`, and connection flags `CallerAttributeDedicatedLogin` / `CallerAttributeSharedLoginAcknowledged` (BC-129-03, F-8). |

---

## Phase 3 Security Review (2026-10-16)

**Reviewer:** `csharp-security-expert` (tracker step S-4, dedicated review required by the PRD for #129). **Scope:** PLAN-GOV-129 revision 1 against PRD section 3 (V-129-1..13, Q-129-1 decided, Q-129-2..7 defaults) and the code at the cited lines (verified in the worktree, `main` + planning commits). **Related:** PLAN-SQL-128 section 14 conditions (policy lists never through the client list path, per-statement expansion cap, values never in errors/logs) are re-checked here and adopted as binding for #129. Reviews are sequential; no source was changed.

### P3.1 Threat model

Assets: (a) the row set a person may see (attribute decides it), (b) the attribute values themselves (HR/organisational data), (c) the attribute store (a privileged, service-identity read), (d) the registry/service sets (control plane that decides who sees what).

| ID | Actor / threat | Entry point | Plan control | Residual (see finding) |
|---|---|---|---|---|
| T-1 | Caller forges own attribute (header, query, body, client parameter, `{{caller.x}}` typed in WebSQL, claim of another issuer, ForwardAuth claim) | every channel | typed-only binding, issuer + scheme allowlist, sentinel refusal | user-editable attested claims (SEC-129-04), app-only subject (SEC-129-01) |
| T-2 | App-only/service token obtains person attributes or another app's set | `GetUserSid()` fallback | `CallerSubjectFactory` over `IIdentitySubjectResolver`, per-application service sets | SEC-129-01, SEC-129-02 |
| T-3 | Cross-user leakage through a shared object (decision cache, memo, template cache, `ShareAcrossUsers`, value cache, single-flight, request scope) | caches | value-free recipes/slots, hashed value-cache key | SEC-129-07, SEC-129-08 |
| T-4 | Fail-open: a consumer sees an unbound filter (text `NOT IN`, text-only evaluators, procedures, DuckDB staging, CDC) | text paths | constant `(1 = 0)` text, `NULL` slot, deny-on-fallback | SEC-129-05, SEC-129-06 |
| T-5 | Oracle: caller distinguishes missing/failed/over-cap/unknown attribute (status, body, timing, audit exposure, row count) | all channels | one constant outcome | SEC-129-09 |
| T-6 | Attribute store abuse: write access (self-escalation), cross-tenant read, injection through identifiers, catalog exposure, privileged read used as an oracle | attribute table, registry | fixed statement, tenant predicate, four-eyes, A-10 | SEC-129-03, SEC-129-10, SEC-129-11 |
| T-7 | DoS: huge lists, template churn, bind budget, dead source, cache memory, per-event CDC resolution, subject enumeration via simulation | resolver, caches | hard caps, padding, timeout, breaker | SEC-129-12, SEC-129-13 |
| T-8 | Stale authorization after revocation/deprovisioning (token lifetime, TTL, long-lived principals) | claims, CDC, request scope | TTL <= 300 s | SEC-129-04, SEC-129-14 |
| T-9 | Disclosure of names/values in audit, logs, errors, caller views | audit, metrics | value-free types, hashes | SEC-129-15 |
| T-10 | Registry tampering without second person (declarative apply, GraphQL, seeding, direct DB) | admin surface | draft + approver != creator | SEC-129-10 |

### P3.2 Verified facts that the findings rest on

- `GetUserSid()` returns the first of PrimarySid/objectSid/onprem_sid/oid/NameIdentifier/`sub`/`appid`/`client_id`/`azp` (`src/Autheris.Domain/Common/Sid.cs:21-36`): an app-only token yields its application id as the person key. The consent decision cache and the CDC enforcer key on exactly this value (`TableAccessPolicy.Consent.cs:40-43`, `StreamRlsPolicyEnforcer.cs:111,321-322`). `IdentitySubjectResolver` classifies app-only by `idtyp=app` **or** (app id and no human claim) (`IdentitySubjectResolver.cs:58-73`); the classification is claim-presence based, so a person token that lacks `upn`/`email`/`preferred_username`/`unique_name` but carries `azp` (common for some Entra/OIDC configurations) is classified as an application. Conversely an app token that carries any human claim is classified as a person (see SEC-129-01).
- ForwardAuth builds a `ClaimsIdentity` with default claim issuer (`LOCAL AUTHORITY`) and its own scheme name (`ForwardAuthAuthenticationHandler.cs:292-299`); `ClaimsNormalizer` adds claims with issuers `Autheris:*` (`ClaimsNormalizer.cs:21-22,207-208`). Both are excluded by an issuer allowlist, provided the check is on `Claim.Issuer` of the claim itself and on the **owning identity's** authentication type (a principal may carry several identities).
- Typed consent leaf today returns `Constant(isDeny)` (`TypedRowFilterBuilder.cs:137-141`); the cached decision holds recipe + text (`TableAccessPolicy.Consent.cs:36-46`); the Virtual Filter typed predicate is attached per request, falling back to text when `TypedPredicate == null` (`TableAccessPolicy.cs:304-317`), and a typed build failure for one filter makes the **whole** outcome typed-incomplete and the text path authoritative (`MandatoryRowFilterResolver.cs:488-505`).
- The attribute-table read pattern initializes the session with the tenant only (`SqlVirtualFilterKeySetProvider.cs:263`).
- CDC resolves consent per event from `subscriber.GetUserSid()` without token scopes in the context hash (`StreamRlsPolicyEnforcer.cs:111,134,321`).
- No `ConditionalWeakTable<ClaimsPrincipal, ...>` exists in `src` today (only for options/predicates, `BasicAuthAttemptGuard.cs:33`, `GovernancePolicyFactory.cs:414`), so the request-scope design is new and untested.

### P3.3 Findings

Severity: Critical = cross-subject/privilege break, High = fail-open or cross-tenant/cross-user leak possible if unaddressed, Medium = weakens a stated guarantee, Low = hardening.

**SEC-129-01 (High) - Subject classification is claim-presence based; person/app confusion in both directions.**
Evidence: `IdentitySubjectResolver.cs:57-73`, `Sid.cs:21-36`. The plan correctly stops using `GetUserSid()` (section 1 row "GetUserSid is not a safe subject key") but A-6 builds `CallerSubject.Kind` from the same resolver. Two gaps: (1) an app token with a stray human claim becomes `User` and then reads person attributes keyed by its `oid`/`sub` (usually a service principal object id, which is not in the person attribute table, but a claims provider mapping `department` from an app token would resolve); (2) a person token without human claims is `Application` and silently loses its attributes (fail-closed, availability only).
Required change: `CallerSubjectFactory` must classify as `User` only if (a) `idtyp` is absent or not `app`, **and** (b) an immutable person key of the requested kind (`oid` for Entra with `idtyp` != `app`, on-prem SID, or `sub` of a user-flow issuer configured as person issuer) exists, **and** (c) the identity's authentication type is an allowed scheme. `idtyp=app`, or `appid`/`azp`/`client_id` equal to the subject key, or `oid == sub == appid`-style service principal shape always yields `Application`. The `Subject` key kind (`sub`) is allowed only per trusted issuer (`Claims:TrustedIssuers` entry flags `SubjectKeyIsPerson`), because `sub` for client-credential tokens is the client id. Everything unclassifiable is `None` (all attributes `Missing`). Binding conditions BC-129-01.

**SEC-129-02 (High) - Service attribute sets keyed by a claim-derived application id.**
Evidence: A-5.3, `IdentitySubjectResolver.cs:58-73` (`appId` = first of `appid`/`client_id`/`azp`). The application id must be the **validated token's** application id, not any identity-controlled claim, and must be tenant-scoped (`tid` already enforced by tenant resolution). Risks: (a) a multi-tenant app registration id is identical across tenants, so the set key `(tenant, applicationId, attribute)` is correct only if `tenant` comes from the gateway tenant resolution and not from a claim of the same token without issuer check; (b) a token for application X must not match a set of application Y through case/Unicode folding, trailing whitespace or alternative claim (`azp` vs `appid`); (c) four-eyes on approval is specified but the service set has no tenant-scoped audience binding (`aud`) - an application token issued for another API but accepted by the gateway would still be authenticated, so this is accepted as the authentication layer's job.
Required change: compare application ids ordinally after GUID-canonicalisation (lower-case `D` format when parseable, otherwise exact ordinal); take the application id from exactly one claim by fixed precedence (`appid` for v1 tokens, `azp` for v2, `client_id` only for non-Entra trusted issuers) and refuse a token whose candidates disagree (generic outcome `Missing`); service-set rows are scoped to `(tenant, issuer-kind)` implicitly through tenant only; an application subject never reads user providers and a user subject never reads service sets (already specified; mutation test required, ST-129-02). BC-129-02.

**SEC-129-03 (High) - Attribute-table provider: write access to the table is the real authorization root.**
Evidence: A-5.2, A-10. Whoever can write the attribute table (HR sync job, DBA, the same data source used by governed queries) can change who sees what; the plan secures the registry with four-eyes but not the table. Additionally the lookup runs under the gateway service identity with `SELECT` on a table that may live in a connection also used for governed reads (`DataSources:Connections[<dataSource>]`).
Required change: (a) `CallerAttributeLookupAllowed` connections must use a **dedicated read-only login** that has SELECT on the registered attribute tables only and no other grants; the gateway verifies at approval time that the connection's `GovernedSqlRole`/user differs from the connection used for governed user queries, or records an explicit owner acknowledgement (F-8). (b) The attribute table must not be writable by the gateway's own service identities (startup/approval check where the dialect allows an effective-permission probe; otherwise documented operational control). (c) Registry approval must show the table, key kind and value column and require approver != creator **and** a role distinct from `GovernanceAdmin` creator when `Gateway:CallerAttributes:RequireSeparateApproverRole` is set (default `true` for attribute-table and service-set changes, since these grant data access). (d) Row changes to the attribute store are outside gateway control: documented as an assumption in `docs/features/` (Phase 6), not as a guarantee. BC-129-03.

**SEC-129-04 (High) - "Attested claim" is not "authoritative claim"; token-lifetime staleness and user-controlled claims.**
Evidence: A-5.1, R-9, `GatewayServiceCollectionExtensions.cs:340-375` (issuer/audience/lifetime/signature validated). A claim from a validated issuer is authentic but may be (a) user-editable in the IdP (Entra extension attributes, `jobTitle`, custom OIDC claims editable by the user), (b) stale until token expiry (typically 60-90 min, longer than the 60-300 s TTL the PRD advertises as the staleness bound), (c) present twice (two identities or the same claim type with differing values: attacker adds a second value to a multi-valued claim via an IdP group-like attribute).
Required change: (a) the claim allowlist is **per attribute** and per issuer (`claimType`, `issuer`), and the admin UI/approval records that the claim is IdP-controlled, not user-editable (acknowledgement field in the definition, part of `definition_hash`); (b) documentation and `describe_my_access` admin simulation state "claims-based attributes are as fresh as the token" - the 300 s bound applies only to table/service-set providers; a definition may set `maxTokenAgeSeconds` and the claims provider then reads `iat`/`auth_time` of the same identity and returns `Missing` when older (default off, recommended <= 900 s for sensitive attributes); (c) a single-valued attribute with two different claim values (same or different identity) resolves `Failed(Ambiguous)` -> generic outcome; claim count and total size caps (<= `MaxValues` claims of that type, <= 256 chars per value, <= 16 KiB total) before normalization; (d) JSON-array claim parser is strict (no duplicates keys, depth 1, string/number/bool only). BC-129-04.

**SEC-129-05 (High) - Text-only path completeness: the `(1 = 0)` constant must hold on every text consumer, including the "typed incomplete" fallback.**
Evidence: `TableAccessPolicy.cs:313-317`, `MandatoryRowFilterResolver.cs:488-505,523-526`. When any other filter of the same table has `typed == null`, `typedComplete = false`, `TypedPredicate = null`, and the AND-joined **text** (including the caller-filter constant `(1 = 0)`) is authoritative: this is fail-closed but turns a **resolved** attribute into a deny-all for the table without an explanation, and it means that for such tables the typed binder never runs. Separately, the plan's A-4 sentence "an InvalidOperationException/ArgumentException during build denies the whole resolution" is the correct fail-closed behaviour but needs to cover the sentinel-refusal `ArgumentException` of `CallerPlaceholders.Rewrite`.
Required change: (a) the text builder for a definition containing a placeholder must emit the constant `(1 = 0)` **and** the resolver must mark the outcome `CallerAttributesTextConstant = true`; (b) if `TypedPredicate == null` and the outcome names caller attributes, `TableAccessPolicy` denies the table (generic denial, audit code `TextPathAttributeFilter`) instead of falling back to the text, so the behaviour is identical on every table and not dependent on whether a sibling filter has a typed form; (c) an architecture test enumerates all readers of `MandatoryFilterOutcome.PredicateSql`/`MandatoryRowPredicateSql`/`RowFiltersAfterVirtualFilters` and fails when a new reader appears that is not in an allowlist with a documented caller-attribute behaviour. BC-129-05.

**SEC-129-06 (High) - Sentinel and slot design: defense in depth is sound but the `NULL` slot is only fail-closed for `=` and `IN`, and `NOT IN` text with a literal sentinel is fail-open.**
Evidence: A-4. `col = NULL`, `col IN (NULL)`, `col <> NULL` evaluate to UNKNOWN -> no rows (OK, for ALLOW semantics of a Virtual Filter, which is ANDed). But `col NOT IN (NULL)` is UNKNOWN too, hence no rows (OK), while the **sentinel literal text** `NOT IN ('__autheris_caller__x')` matches every row (fail-open) - the plan removes this by never emitting value text. Remaining holes: (1) `<>`/`NOT IN` combined with `OR` or `NOT (...)` in the same Virtual Filter definition: `NOT (col = {{caller.x}})` with a `NULL` slot is `NOT UNKNOWN` = UNKNOWN (no rows, OK) but `NOT (col = {{caller.x}} OR 1 = 1)` is out of scope since tautologies are rejected; however `col <> {{caller.x}} OR other = 1` with an unbound slot returns rows where `other = 1`, i.e. the slot's failure silently weakens a disjunction into a different filter; (2) `COALESCE`/`IS NULL` wrappers are excluded by the position grammar (good); (3) a filter whose definition mixes caller placeholders with `OR` of other conditions is only safe when the binder denies the **whole** predicate for any unresolved slot (specified, A-4) and the slot-NULL defense is only the second line.
Required change: placeholders are allowed only inside a top-level **conjunct** of the definition (top-level `OR` containing a placeholder is refused at `Validate` and `CompileTyped`; `NOT` over a placeholder comparison is refused; use `<>`/`NOT IN` operators instead); the binder runs under a try/finally contract that returns `PolicyPredicate.DenyAll` on **any** exception, not just unresolved slots; ST-129-07/08 cover disjunction and negation shapes. BC-129-06.

**SEC-129-07 (High) - Request-scope memo keyed by `ClaimsPrincipal` reference is unsafe for long-lived principals and unproven for shared principals.**
Evidence: A-6 (`ConditionalWeakTable<ClaimsPrincipal, RequestState>`), CDC subscriber principal captured at subscribe time (A-8, R-9), `StreamRlsPolicyEnforcer.cs:111`. A weak table lives as long as the principal object. For HTTP requests that is the request; for a long-lived CDC/webhook subscription, Flight/Trino sessions or any cached principal it is unbounded, so the memo would serve a value beyond the TTL (a revoked attribute stays valid for the life of the subscription) and would never observe a definition change. The memo also must not be shared by two distinct callers if a framework reuses a principal instance (e.g. a cached/ticketed principal for connection-oriented protocols).
Required change: the memoized `Lazy<Task<...>>` entry stores its creation time from `TimeProvider` and is **discarded when older than `min(attribute TtlSeconds, 30 s)`** (one request scope is seconds); the state also stores the `definition_hash` and generation and is discarded on mismatch; no memo for principals the resolver marks long-lived (CDC/webhook/subscription, tagged by the caller via `CallerAttributeScope.PerEvent`) - those always go through the TTL value cache; a test with `FakeTimeProvider` proves that a principal object reused after TTL gets fresh values. BC-129-07.

**SEC-129-08 (High) - Value cache and single-flight: key sufficiency, tenant, issuer, and failure sharing.**
Evidence: section 6 (key = SHA-256 of tenant, issuer, subjectKeyKind, subjectKey, attributeName, definitionHash, providerGeneration) and A-6. The key is adequate if every component is length-prefixed/domain-separated (otherwise `("ab","c")` collides with `("a","bc")`), the key contains the **provider config hash** (data source, table, columns, claim mapping, trusted issuer set, `maxTokenAgeSeconds`) - which is covered by `definitionHash` - and the **subject kind** (`User` vs `Application`): without the kind, application id `X` and a person key `X` collide. Single-flight keyed by the same hash is safe because identical key = identical subject; but the **caller's cancellation and tenant context** must not leak: a load started by caller A with A's `CancellationToken` must not fail caller B when A cancels (shared-load cancellation), and a `Failed` result must never be cached (specified) nor shared beyond the in-flight window (a failure propagated to concurrent waiters is acceptable, a failure stored is not).
Required change: canonical, length-prefixed key material including `subjectKind`; single-flight load runs with a gateway-owned token bounded by `ProviderTimeout`, not the first caller's token; `ResolvedAttributeEntry` stores the key hash for a defensive equality check on read; cache capacity counts bytes of values (`SizeLimitBytes`) and refuses to cache entries above `MaxValues` (never reached by construction). BC-129-08.

**SEC-129-09 (Medium) - "One generic outcome" has four leaks the plan does not close.**
Evidence: A-11, section 5. (1) **Over-cap vs missing timing**: provider read of `Max+1` rows takes longer than a missing lookup; bounded by timeout but measurable. (2) **Binds in error**: a list near the SQL Server 2,100 bind budget fails with `SQL_LIMIT_EXCEEDED` (section 5 row "Bind budget exceeded after expansion: statement denied (SQL_LIMIT_EXCEEDED)") while a short list succeeds: a caller can learn the **size class** of their own attribute list by sending a statement with many client parameters until the limit error appears; this is a different outcome than Missing (which returns zero rows). (3) **Row-count oracle on writes/aggregates**: not applicable to reads only if attribute filters are applied before any `COUNT`/existence response (INSERT/UPDATE/DELETE and `EXISTS` through MCP are the same decision path; test required). (4) **Audit exposure**: `caller_attributes` with outcome codes in the statement audit is visible to auditors, not to callers - acceptable only if audit export APIs that callers can read (e.g. "my activity" views, `describe_my_access`) never include the field. (5) The nested GraphQL relation and async job results must not carry a different error shape for a failed attribute than for missing.
Required change: (a) the bind-budget/limit failure for **policy** expansion must produce the same generic row-filter outcome as `Missing` (deny/no rows) rather than a distinct `SQL_LIMIT_EXCEEDED` whenever the budget excess is caused by an attribute list, or the cap formula must **guarantee** that attribute expansion alone can never exceed the budget (`BindReserve` default 400 must be enforced per statement as a **sum over all attributes in the statement** against the dialect budget, computed before the provider call) so the limit error can only arise from client parameters; (b) the timing difference is documented as accepted (already) with a note that over-cap detection must stop reading at `Max+1` rows; (c) audit-field exclusion test (ST-129-10). BC-129-09.

**SEC-129-10 (High) - Four-eyes bypass paths and approval integrity.**
Evidence: A-7, `VirtualFilterAdministrationService.cs:180-215` pattern. Required: (1) approver != creator compared on the **authenticated immutable subject key**, not display name/UPN; (2) the approved draft is bound to `draft_definition_hash` and the approval request carries the hash the approver saw (approve-by-hash) so a draft changed between review and approval cannot be approved (TOCTOU); (3) every writer enforces the draft flow: REST admin, GraphQL, MCP admin tools, declarative apply (`DeclarativeConsentApplyService`) and seeding/bootstrap - a repository-level guard rejects a write of `status = Active` unless an approval record with a different subject exists (the plan only puts the guard in the service and a hash check in the repository); (4) registry deletion of an attribute referenced by active consents/Virtual Filters is blocked or produces a lint finding **and** a runtime constant (already: R-11), but deletion/rename must also be four-eyes; (5) `Enabled=false` must refuse approval, not only definition save; (6) service-set approval: the creator of a set may not approve it, and a service set for an application that holds **GovernanceAdmin** or equivalent rights is flagged.
Required change: add the repository-level guard and approve-by-hash; add declarative/seed paths to the mandatory tests. BC-129-10.

**SEC-129-11 (High) - Attribute table in the catalog, injection, and tenant isolation of the provider.**
Evidence: A-5.2, A-10, `SqlVirtualFilterKeySetProvider.cs:263`. (1) Identifier validation `^[A-Za-z_][A-Za-z0-9_$#]{0,127}$` allows `$` and `#`: `#` prefixes SQL Server temp tables and `$` has meaning in some dialect contexts; reserved words are quoted by `QuoteIdentifier`, but a `#`-prefixed name must be refused for SQL Server, and a name containing `.` is not allowed (already, since the class excludes it). (2) The tenant column is "required unless the table is in `TenantColumnExemptTables`": a tenant-exempt attribute table is by definition a **global** table, so the subject key must then be globally unique per issuer (an Entra `oid` is unique per tenant directory but identical GUID space; on-prem SIDs collide across domains); a tenant-exempt table must include an issuer/domain column in the key or be refused unless the owner confirms (F-9). (3) A tenant-aware table with `tenant_id` equal to the gateway tenant id: the comparison must be on the gateway's normalized tenant string, parameter bound, **case-sensitive per dialect collation documented** (SQL Server default collation is case-insensitive; a tenant id differing by case would match). (4) A-10 exposure check must also consider **views and synonyms** over the attribute table and the schema-wide catalog ingestion; at minimum the check compares fully qualified normalized names case-insensitively on all three dialects, and consent/Casbin policy creation for a registered attribute source is refused (not just consents). (5) The provider read must be `SET TRANSACTION READ ONLY`/`ApplicationIntent` equivalents per dialect and a `SET LOCK_TIMEOUT`/statement timeout; SQLite opens read-only.
Required change: refuse `#` leading identifiers (SQL Server) and quote everywhere; tenant-exempt tables need an issuer/domain key component or owner acknowledgement recorded in the definition hash; collation note and a test with differently-cased tenant ids; extend A-10 to Casbin and relations. BC-129-11.

**SEC-129-12 (Medium) - Cardinality and DoS: caps are per attribute, budget is per statement; no per-subject concurrency or per-tenant provider budget.**
Evidence: A-6 (`EffectiveMax`), C-4. (1) `EffectiveMax` is per attribute; a statement referencing several list attributes, several tables (nested GraphQL, federated staging, joins of N tables each with a filter) multiplies parameters; the sum must be bounded per statement (see SEC-129-09). (2) Provider calls per second are bounded by TTL and single-flight per subject, but a **flood of distinct subjects** (many tokens, cold cache) produces provider calls up to `ProviderTimeout` each; no per-provider concurrency limit is specified, only a breaker. (3) The circuit breaker opens on 5 consecutive failures for 10 s and then everything is `Failed`: a single misbehaving subject (e.g. a user with an over-cap list that triggers `OverCardinality`) must **not** count as a provider failure and must not open the breaker for everyone (availability attack by an authenticated user who controls their own attribute size). (4) Padding by repeating the last value changes `NOT IN`/`IN` semantics not at all, but the template-cache bound of about 9 shapes per attribute multiplies per table/filter; a bounded cache (existing) is assumed. (5) CDC per-event resolution relies on the TTL cache: a cold cache with many subscribers is a thundering herd unless single-flight covers it.
Required change: per-provider `MaxConcurrency` (default 16, bulkhead with generic `Failed` on rejection); only infrastructure errors/timeouts count for the breaker, never `OverCardinality`/`NotNormalizable`/`Missing`; per-statement attribute bind sum check (SEC-129-09); simulation endpoint rate-limited per administrator. BC-129-12.

**SEC-129-13 (Medium) - Simulation/effective-filters endpoint is an attribute enumeration and disclosure tool.**
Evidence: A-11 disclosure, `effective-filters` FilterAdmin, "masked beyond the first 3". Masking after the first 3 characters of a department/cost-centre id is not masking; the endpoint accepts an arbitrary subject and returns resolved values: any FilterAdmin can read any person's organisational attributes (and the attribute-table provider runs under the privileged service identity).
Required change: simulation returns names, outcome, **count and value hashes (first 8 hex of SHA-256 with a per-tenant pepper)** by default; clear values only with an additional audited `reveal` flag requiring a separate role (`CallerAttributeInspector`), a mandatory reason string, rate limit, and the subject restricted to subjects in the administrator's tenant. BC-129-13.

**SEC-129-14 (Medium) - Deprovisioning and revocation: staleness bounds are not stated per provider.**
Evidence: AC-129-5, section 6. Effective revocation latencies: consent revocation immediate via epoch (OK), table/service-set provider <= TTL (60 s default, 300 s hard), claims provider <= token lifetime (SEC-129-04), CDC subscriptions <= token lifetime **or forever** if the principal is never re-validated (R-9), negative cache `Missing` 15 s (a re-provisioned person waits 15 s, fine). A deprovisioned person whose token is still valid keeps claims-based attributes.
Required change: document the per-provider bound; CDC/webhook subscriptions must re-resolve the subscriber principal (or reject at `exp`) and the enforcer must stop delivering when the captured principal's `exp` has passed (verify existing behaviour; otherwise add ST-129-17); `ICallerAttributeCacheInvalidator.InvalidateSubject` is wired to the existing user-deprovisioning/lock flow where one exists (otherwise recorded as a gap for WP-SC-07). BC-129-14.

**SEC-129-15 (Medium) - Audit and diagnostics: attribute names may themselves be sensitive; hashes are unsalted.**
Evidence: A-11 (`caller_attributes` = registry id, outcome, reason code), definition audits "before/after hashes". Registry ids are fine; `sha256:` of **unregistered input names** (client-chosen? no - names come from stored recipes/definitions, so they are admin-chosen) are low-entropy and reversible by dictionary, which is acceptable for names but not for any value. Metrics tags are limited to provider and outcome (good). Spans: ensure `Activity` tags and log scopes do not include attribute names plus subject key together. `LogGeneratedSql` logs marker text only (verified claim in section 1; keep the test).
Required change: audit records contain registry ids, outcomes, counts and the `definition_hash` only; no subject key, no values, no unsalted hash of any value; add `caller_attributes` to the redaction/export allowlist of caller-visible audit views as **excluded** (ST-129-10). BC-129-15.

**SEC-129-16 (Low) - Misc hardening.**
(1) `Gateway:CallerAttributes:Enabled=false` must also disable the admin write surface and the validator decorator must still refuse `ValueSource` values other than `LITERAL`/`USER_ATTRIBUTE` (today anything is stored, section 1 row 3). (2) `ConsentRowFilter.ValueJson` ignored for `USER_ATTRIBUTE`: a non-empty literal must be refused at save (specified) **and** ignored at runtime (never used as fallback) - pin with a test. (3) Reserved parameter-name prefix `__gql_caller_` must be refused for client parameters at all binders (client named parameter spelled like a slot; also case variants), ST-129-14. (4) `CallerAttributeBinder` must only replace slots it recognizes and verify the slot's arity against the definition; it must never replace the tenant parameter (`PolicyPredicate.Create` revalidation is specified). (5) Guid attributes bound as strings: compare ordinally lower-case-canonicalised, because a GUID column on SQL Server compares as `uniqueidentifier` and a string parameter is implicitly converted - an invalid GUID text raises a conversion error rather than a no-match and could become a distinguishable outcome; canonicalise/validate at normalization and treat failure as `Failed(NotNormalizable)`. (6) Case/collation: string comparison semantics of `=` follow the column collation; the attribute value `Finance` matching `finance` on a case-insensitive collation is the database's behaviour and must be documented as such. (7) `IN` with an empty padded list can never occur (empty = `Missing`).

### P3.4 Topic-by-topic conclusions

- **Subject binding:** acceptable after BC-129-01/02. `GetUserSid()` must not be used for attribute subjects anywhere (architecture test: no reference to `GetUserSid`/`GetAllUserSids` in `Identity/Attributes/**`).
- **Attribute source trust:** claims from validated bearer issuers only, never ForwardAuth/Basic/Test/mTLS-derived; per-attribute issuer allowlist; attribute-table and service-set require a read-only dedicated principal and four-eyes with approve-by-hash (BC-129-03/04/10).
- **Fail-closed matrix (section 5):** correct and complete for the listed cases with these additions: conjunct-only placeholder grammar (SEC-129-06), table-deny instead of text fallback when `TypedPredicate == null` (SEC-129-05), ambiguous single claim -> `Failed`, attribute-expansion budget failure -> same outcome as `Missing` (SEC-129-09).
- **Text-only Virtual Filter `NOT IN`:** the constant `(1 = 0)` and `NULL` slot are the right controls; they are sufficient only with BC-129-05/06 (no text fallback for attribute filters, no top-level `OR`/`NOT` over a placeholder).
- **Caching:** consent decision cache and template cache stay value-free (verified structure: recipe + text, typed rebuilt per request). `ShareAcrossUsers` is Casbin-only; the placeholder refusal in Casbin rules (A-8) plus the test is sufficient **if** the refusal also covers Casbin policy import/sync paths and sub-rule filters, not only the publish linter. Value cache and request scope: see SEC-129-07/08.
- **Cross-channel parity:** one decision point holds for compiled-SQL channels (A-8). The weak points are the in-memory text consumers (`StreamingRowFilterAstEvaluator` rejects `@`), CDC per-event principal capture, and stored procedures/DuckDB fallbacks; all must deny rather than evaluate. `#107 KeySetCache` exclusion (Q-129-7) is accepted; the test must also assert the key-set cache key never includes attribute material.
- **Cardinality/DoS:** acceptable with per-statement sum, bulkhead, breaker hygiene (BC-129-09/12).
- **Audit:** names/registry ids and outcome codes only; never values or subject keys (BC-129-15).
- **Tenant isolation:** gateway-supplied tenant on every provider call; cache key contains tenant; attribute table tenant predicate mandatory (exemption needs issuer component, BC-129-11); service sets unique per tenant.
- **PRD deviations:** see P3.7.
- **Planned tests:** strong on happy path, parity and value hygiene; insufficient for the items listed in P3.6 (disjunction/negation grammar, long-lived principal memo, cancellation of shared loads, breaker hygiene, subject classification matrix, approve-by-hash, bind-sum budget, tenant case sensitivity, redaction of caller-visible audit).

### P3.5 Binding conditions

| ID | Condition |
|---|---|
| BC-129-01 | `CallerSubjectFactory` classifies `User` only with a non-app token, an immutable person key of the requested kind and an allowed scheme; `Subject` key kind only for issuers flagged as person issuers; everything else `None`/`Application`; no use of `GetUserSid()` anywhere under `Identity/Attributes` (architecture test). |
| BC-129-02 | Service sets: application id from one fixed-precedence claim, canonicalised and ordinally compared, conflicting candidates -> `Missing`; user never reads sets, application never reads user providers. |
| BC-129-03 | Attribute-table provider requires a dedicated read-only login (or recorded owner acknowledgement), `CallerAttributeLookupAllowed`, SELECT on the registered tables only, separate-approver role for provider changes (default on), table write access documented as the trust root. |
| BC-129-04 | Claims provider: per attribute issuer + claim allowlist, scheme allowlist on the owning identity, ambiguity -> `Failed`, size/count caps, strict array parser, optional `maxTokenAgeSeconds`, documented token-lifetime staleness. |
| BC-129-05 | A Virtual Filter outcome that names caller attributes and has no typed predicate denies the table; the text form is the constant `(1 = 0)` and never contains a sentinel or value; allowlist architecture test on readers of the predicate text. |
| BC-129-06 | Placeholders only in top-level conjuncts with the specified operators; no `OR`/`NOT` over a placeholder; binder returns `DenyAll` on any exception or unresolved slot; unbound slot is `NULL` typed. |
| BC-129-07 | Request memo bounded by `min(TtlSeconds, 30 s)` and by definition hash/generation; none for long-lived principals (CDC/webhook/subscription). |
| BC-129-08 | Value-cache key length-prefixed with subject kind, tenant, issuer, definition hash; single-flight uses a gateway-owned token; failures never cached; breaker counts infrastructure failures only. |
| BC-129-09 | Attribute-list expansion can never produce a caller-distinguishable limit error: per-statement bind sum is checked against the dialect budget before provider calls and exceeding it yields the same outcome as `Missing`; over-cap detection stops at `Max+1` rows. |
| BC-129-10 | Four-eyes enforced at the repository level and on all writers (REST, GraphQL, MCP, declarative apply, seed) with approve-by-hash and immutable subject comparison; deletion/rename also four-eyes; `Enabled=false` refuses approval. |
| BC-129-11 | `#`-leading identifiers refused (SQL Server); tenant-exempt tables need issuer/domain key component or recorded acknowledgement; tenant comparison collation documented and tested; A-10 covers Casbin, views/synonyms and relations. |
| BC-129-12 | Per-provider concurrency bulkhead; `OverCardinality`/`NotNormalizable`/`Missing` never open the breaker; simulation rate-limited. |
| BC-129-13 | Simulation returns names, outcome, counts and peppered value hashes by default; clear values need a separate role, reason and audit. |
| BC-129-14 | Per-provider revocation bound documented; CDC/webhook subscriptions stop at principal expiry; subject invalidator hook wired where a lock/deprovision event exists. |
| BC-129-15 | Audit/spans/logs/metrics: registry ids, outcomes, counts, hashes of definitions only; `caller_attributes` excluded from caller-visible audit views. |
| BC-129-16 | PLAN-SQL-128 section 14 conditions apply verbatim: policy lists never use `ClientListShapes`/`ClientListExpander`/client origins, per-statement expansion cap, values never in errors or logs; `AstSecurityVisitor.cs` stays unmodified; the reserved `__gql_caller_` name prefix is refused for client parameters at every binder. |

### P3.6 Mandatory security tests (in addition to plan section 8; sequence in WP-7 unless stated)

| ID | Test |
|---|---|
| ST-129-01 | Subject classification matrix: Entra user (`oid`, `upn`), Entra user without human claims but with `azp`, app-only (`idtyp=app`), app token with stray `email`, client-credentials token (`sub = client_id`), certificate, ForwardAuth user, Basic, Test, anonymous: expected `User`/`Application`/`None`; only `User` with the right key kind reads person attributes (BC-129-01). |
| ST-129-02 | Service-set isolation: application X token with `azp`/`appid` disagreement -> `Missing`; GUID case/whitespace variants; X never gets Y's set; user subject never reads a set; pending set not applied. |
| ST-129-03 | Claims trust: claim with issuer `LOCAL AUTHORITY`, `Autheris:*` normalizer issuer, a second identity with the same claim type, claim of untrusted issuer, claim on a non-bearer identity of a principal that also holds a bearer identity: `Missing`; two differing values for a single attribute: `Failed`; oversized claim; duplicate-key JSON array. |
| ST-129-04 | `maxTokenAgeSeconds`: token older than the bound returns `Missing`; `FakeTimeProvider`. |
| ST-129-05 | Disjunction/negation grammar: `{{caller.x}}` under top-level `OR`, `NOT (...)`, in `CASE`, function, `LIKE`, `BETWEEN`: refused at `Validate` and at `CompileTyped` for stored definitions; a binder forced to throw yields `DenyAll`. |
| ST-129-06 | Fail-open mutation control: replace the text builder by a version that emits the sentinel/`NOT IN` literal; the suite must fail (tests 20, 23). Typed-incomplete fallback: a sibling filter without typed form makes the table deny when the caller filter is present (BC-129-05), on SQLite end to end. |
| ST-129-07 | Cross-user cache isolation matrix: same decision-cache entry shape for user A and B, same memo key tuple, same template cache hit, same single-flight window with distinct subject keys and tenants: A never sees B's rows; app id equal to a person key value (collision) is separate by subject kind. |
| ST-129-08 | Memo staleness: principal object reused after TTL/30 s, after definition hash change, and for a CDC-scoped principal gets fresh values (`FakeTimeProvider`). |
| ST-129-09 | Shared-load cancellation: caller A cancels, caller B still receives the value; failures are not cached; single provider call for 100 concurrent callers of one subject, but N calls for N subjects (bulkhead bound respected). |
| ST-129-10 | Oracle diff: for Missing, Failed(timeout), Failed(source unavailable), OverCardinality, UnknownAttribute, NoSubject, Ambiguous, bind-budget attribute excess, over a WebSQL query, REST, GraphQL nested relation and an async job: identical status, body, headers (except timing), row count; caller-visible audit/`describe_my_access` views never contain `caller_attributes`. |
| ST-129-11 | Breaker hygiene: 1000 requests of one subject with an over-cap attribute do not open the breaker for other subjects; infrastructure failures do. |
| ST-129-12 | Attribute-table provider: `#`-prefixed and bracket/quote/comment identifiers refused; tenant-exempt table without issuer/domain key refused; tenant ids differing only by case on SQL Server collation produce no match; read-only dedicated login check; exempt/non-exempt tenant predicate golden text per dialect; no cross-tenant row ever returned (two tenants with the same subject key in one table). |
| ST-129-13 | Four-eyes: approver == creator (by immutable subject key, not UPN) refused on REST, GraphQL, MCP, declarative apply and seed; approve-by-hash with a draft changed after review refused; repository refuses `Active` without approval record; delete/rename four-eyes; `Enabled=false` refuses approval. |
| ST-129-14 | Client parameter named `__gql_caller_*` (any case) refused or ignored on every binder; `CallerAttributeBinder` cannot replace the tenant parameter or a client parameter; only the binder constructs valued `__gql_caller_` parameters (architecture test). |
| ST-129-15 | Bind-sum budget: several list attributes across several tables (nested GraphQL, federated staging) on SQL Server never exceed 2,100 binds due to attributes; excess is the generic outcome (BC-129-09). |
| ST-129-16 | Simulation: default output has no clear values; `reveal` needs role + reason, is audited and rate-limited; subject outside the administrator's tenant refused. |
| ST-129-17 | CDC/webhook: subscriber principal past `exp` receives nothing; attribute revoked in the table stops delivery after TTL; an app-only subscriber without a service set receives nothing; a typed attribute policy is evaluated by `InMemoryRowFilterEvaluator`, text-only consumers deny the row. |
| ST-129-18 | `ShareAcrossUsers` on: a Casbin rule/sub-rule/import containing `{{caller.` is refused on publish, import and sync; shared `DecisionKey` inputs unchanged; consent decisions never shared. |
| ST-129-19 | #107 interplay: attribute leaf/slot never registered as a correlated site; key-set cache key never contains attribute material; `KeySetCache` globally on with attribute filters. |
| ST-129-20 | Guid attribute: invalid GUID text -> `Failed(NotNormalizable)` generic outcome, no SQL conversion error; canonical lower-case comparison. |
| ST-129-21 | Parity of mutating/aggregating paths: `COUNT(*)`, `EXISTS`, `UPDATE`/`DELETE`/`INSERT ... SELECT` through WebSQL/MCP with a missing attribute return the same shape as an empty permitted set. |
| ST-129-22 | Value hygiene extended: span/activity tags, metric tags, exception `Data`, `ToString` of `CallerAttributeSet`/`CallerAttributeResult`/`CallerSubject`, audit JSON, simulation default output, log capture with `LogGeneratedSql=true`. |
| ST-129-23 | Architecture: no reference to `GetUserSid`/`GetAllUserSids` or UPN/email claims under `Identity/Attributes/**`; every `TableAccessQuery` construction passes `Principal`; allowlist of readers of VF predicate text; `AstSecurityVisitor.cs` byte-identical. |
| ST-129-24 | Saved-filter validator seam: direct repository `CreateConsentAsync` through each of the three engines, GraphQL, REST admin, OpenMetadata sync, declarative apply: unknown attribute, wrong operator, list with `EQ`, non-SQL/delegated source, `ValueSource` outside {`LITERAL`,`USER_ATTRIBUTE`}, non-empty `ValueJson` for an attribute filter refused; a stored `ValueJson` literal is never used as runtime fallback. |

### P3.7 Recommendations for owner flags

- **F-1 (`TableAccessQuery.Principal`):** accept, with the principal used **only** for resolution (not logged, not hashed into cache keys) and the architecture test of ST-129-23. Prefer passing a minimal immutable `CallerSubject` instead of the live `ClaimsPrincipal` if the record is public API.
- **F-2 (no `ParameterOrigin.CallerAttribute`):** accept. Origin `Policy` plus the reserved prefix is acceptable **only** with BC-129-16 and ST-129-14; revisit if a mutation control shows that a prefix check can be bypassed.
- **F-3 (no consent `NOT_IN`):** accept. DENY consent with `IN` expresses exclusion; DENY with a missing attribute blocks, which is the correct polarity. Virtual Filter `NOT IN` stays with the conjunct-only grammar (BC-129-06).
- **F-4 (VF attribute denied at text-only fallback sites):** accept; the fallback sites must deny, and BC-129-05 extends the same rule to the "typed incomplete" case on the main path so behaviour does not depend on sibling filters.
- **F-5 (text-only consumers stay fail-closed):** accept after the WP-5 characterization; any consumer that cannot evaluate a typed policy must deny rows, never evaluate the constant text as a grant. For DENY polarity a constant `1 = 1` text evaluated as "match -> exclude" is fail-closed, but verify each consumer treats DENY text consistently (ST-129-17).
- **F-6 (claims provider bearer only):** accept; a deployment trusting ForwardAuth must list the scheme explicitly **and** accept that header-derived claims are attested only by network topology (document; recommend not enabling for access-granting attributes).
- **F-7 (dialect scope):** accept; refuse unsupported dialects at registration.
- **New F-8:** dedicated read-only login for the attribute store versus recorded owner acknowledgement (BC-129-03). Recommendation: require the dedicated login for production, allow acknowledgement only in Development.
- **New F-9:** tenant-exempt attribute tables (BC-129-11). Recommendation: refuse unless an issuer/domain column is part of the key.
- **New F-10:** `Enabled` default `false` stays; the first release recommends `claims` + `service-set` only for sensitive tenants until the attribute-table provider passes ST-129-12 on all three engines.

### P3.8 Verdict

**APPROVED WITH CONDITIONS.** The architecture is sound: values are bound only into the per-request typed policy, every cached object stays value-free, unresolved states converge on the existing constant polarity, the text Virtual Filter path cannot emit a value or sentinel, and the subject key no longer comes from `GetUserSid()`. Phase 4 may start once the lead either (a) revises the plan to revision 2 incorporating BC-129-01..16 and ST-129-01..24, or (b) records them as binding acceptance items of the Phase 4 brief. No condition changes the architecture (A-1..A-11); they tighten classification, source trust, grammar, memo lifetime, budget, approval integrity and tests. Items outside the gateway's control (attribute store write access, IdP claim editability, token lifetime) are documented assumptions, not guarantees, and must appear in the Phase 6 feature document.
