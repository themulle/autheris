# Implementation Plan: REQ-GOV-129 - Row Filters Based on Attributes of the Calling Person

**Document ID:** `PLAN-GOV-129` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-GOV-129](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md) section 3 (V-129-1..13, FR-129-1..12, AC-129-1..12, Q-129-1..7)
**Branch (proposed):** `feat/issue-129-caller-attribute-row-filters` (base `main` after #128 and #107 merged)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement `REQ-GOV-129`; delivery tracker [`PLAN-DELIVERY-W23`](2026-10-16-plan-waves-2-3-delivery.md) (step P-4, item I-25)
**Status:** PROPOSED (Phase 2 complete; next: dedicated Phase 3 security review, tracker step S-4)

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
