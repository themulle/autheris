# Plan: O-5b step 2, PR fix/no-legacy-error-bodies (csharp-architect, 2026-10-26)


Parent plan: [`2026-10-26-plan-def-02-04-no-legacy-fallbacks.md`](2026-10-26-plan-def-02-04-no-legacy-fallbacks.md) (O-5b, NL-SC-20..26). Baseline at branch start: the ratchet `NoNewBareErrorBodiesTests` lists 272 bare error bodies (after #391).
Verified against origin/main 7588717b (#375). Base: origin/main after fix/no-legacy-followups (ratchet, `IsBareErrorShape`, no top-level `error` in ProblemDetails). Binding: NL-SC-20..26, NL-T-20..25. Owner: no legacy shapes. Breaking changes are allowed.

### 1. Inventory
A scan of `src/**/*.cs` for anonymous objects with a top-level `error` member finds **273** (240 single-line `{ error =` plus 33 multi-line), plus **1** dictionary body (`SqlErrorResponse.cs:43`). Not migrated: **4** audit `DetailsJson` payloads, which are not responses and stay as they are under NL-SC-20 (`CallerAttributeAdminEndpoints.cs:316`, `VirtualFilterEndpoints.cs:172`, `EffectiveFilterInspector.cs:36`, `DifferentialPrivacyEngine.cs:169`). Also not migrated: **2** sites that already use the executor shape `{isError,error{code,message}}` (`McpToolExecutionHandler.cs:465,604`) and **13** protocol sites (section 3). That leaves **254** sites to migrate. Status codes stay the same at every site.

| WP | Files (sites) | Codes (existing unless marked *new*) |
|---|---|---|
| WP-1 MCP + docs (guardrail-fed) | McpDocTools 23, McpSemanticTools 5, DocIngestionEndpoints 13, McpEndpoints:66 1, StrictMcpGuardMiddleware 1 = **43** | MCP tools use executor codes (INVALID_PARAMS, INVALID_JSON, NOT_FOUND, FORBIDDEN, secEx.ErrorCode); REST: BAD_REQUEST, RESOURCE_NOT_FOUND, ACCESS_DENIED, PAYLOAD_TOO_LARGE, UNSUPPORTED_MEDIA_TYPE |
| WP-2 SQL/WebSQL/procedures/OLAP | SqlEndpointRoutes 13, WebSqlEndpoints.Execution 10, .Governance 5, GatewayDenialStatusMapper 1, SqlErrorResponse 1 (dict), ProcedureEndpointRoutes 6, DuckDbOlapEndpoints 6 = **42** | mapped.Code, BAD_REQUEST, UNAUTHORIZED, ACCESS_DENIED, RESOURCE_NOT_FOUND, CONFLICT, TOO_MANY_REQUESTS, NOT_IMPLEMENTED, INTERNAL_ERROR, PAYLOAD_TOO_LARGE, PROCEDURE_ARGUMENT_INVALID, OLAP_QUERY_FAILED, SERVICE_UNAVAILABLE; *new* PROCEDURE_REJECTED |
| WP-3 Catalog/data plane | CatalogApiEndpoints 23, TableStreamEndpoints 17, DbtEndpoints 2, DataContractEndpoints 2 = **44** | BAD_REQUEST, ACCESS_DENIED, RESOURCE_NOT_FOUND, SERVICE_UNAVAILABLE, TOO_MANY_REQUESTS, REQUIRED_FILTER_COLUMN_MISSING, DATA_CONTRACT_INVALID; *new* TENANCY_MODE_CLAIMLESS_ADMIN, INVALID_TAG, INVALID_VISIBILITY, TAG_LIMIT_EXCEEDED, OFFSET_NOT_SUPPORTED, KEYSET_STREAMING_UNAVAILABLE, INVALID_CURSOR |
| WP-4 Governance | GovernanceEndpoints.Access 22, .Audit 5, .Catalog 5, .RowFilterEvaluation 3, .SimulationSunsetting 2, GovernanceClassificationEndpoints 9, GovernancePlanEndpoints 4 = **50** | BAD_REQUEST, ACCESS_DENIED, RESOURCE_NOT_FOUND, UNPROCESSABLE_ENTITY, GOVERNANCE_PLAN_INVALID, ROW_FILTER_SYNTAX_INVALID, ex.Code / grantDecision.ErrorCode; *new* DATA_OWNER_APPROVAL_REQUIRED, INVALID_GRANTEE_TYPE, INVALID_TABLE, API_VERSION_SUNSET (410), API_VERSION_UPGRADE_REQUIRED (426) |
| WP-5 Security/admin/integrations | RebacEndpoints 10, WebhookEndpoints 9, DevPortalEndpoints 8, HitLEndpoints 4, EndpointSecurity 3, EnvoyExtAuthzEndpoints:97,122 2, TotpFirstEnrollmentGuard 3, TokenRevocationEndpoints 2, TrustedDocumentAdminEndpoints 2, SubjectLifecycleEndpoints 1, GatewayCorsCsrfMiddleware 3, TenantRegistryAdminEndpoints 3 = **50** | BAD_REQUEST, UNAUTHORIZED, ACCESS_DENIED, RESOURCE_NOT_FOUND, CONFLICT, PAYLOAD_TOO_LARGE, INVALID_TENANT_ID, WEBHOOK_SIGNATURE_OR_FORMAT_INVALID, WEBHOOK_PAYLOAD_INVALID, CSRF_VALIDATION_FAILED, UNTRUSTED_ORIGIN, decision.ErrorCode; *new* INVALID_REBAC_SUBJECT |
| WP-6 Schema/CDC/system/VF | SchemaRegistryEndpoints 8, StreamingCdcEndpoints 6, CdcStreamAdminEndpoints 4, SystemEndpoints 3, VirtualFilterEndpoints:105,107 2, .Crud 2, .Seeding 1 = **26** | BAD_REQUEST, ACCESS_DENIED, RESOURCE_NOT_FOUND, SERVICE_UNAVAILABLE, CDC_FILTER_INVALID, CdcActivationCodes.*, VIRTUAL_FILTER_INVALID |

Code mapping where the old body already carried a code: FORBIDDEN becomes ACCESS_DENIED, NOT_FOUND becomes RESOURCE_NOT_FOUND, INVALID_REQUEST and VALIDATION_ERROR become BAD_REQUEST, UNAVAILABLE becomes SERVICE_UNAVAILABLE, and RATE_LIMIT_EXCEEDED becomes TOO_MANY_REQUESTS. INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM and the ACCESS_PROFILE_* codes come from exceptions; WP-0 registers them as constants.

### 2. Decisions
1. **WP-0 foundation (no group migration).** (a) Add `ApiProblems.WriteAsync(HttpContext, int status, string code, string detail, CancellationToken, IReadOnlyDictionary<string,object?>? ext = null)`. It becomes the only raw-stream writer; WP-2 replaces the private `WriteProblemAsync` copies in `DatasetDataEndpoints`/`ProcedureEndpointRoutes` with it. (b) In `ApiProblems`, a code must match `^[A-Z][A-Z0-9_]{2,63}$` and come from `ApiErrorCodes`. Otherwise it falls back to the status default code. This keeps the dynamic `ex.Code`/`ErrorCode` sources fail-closed. (c) All new codes from section 1 go into `ApiErrorCodes.cs` in WP-0 only, so no later WP touches that file. (d) Add `McpToolError.Serialize(code, message)` in Application, which writes the executor shape; `McpToolExecutionHandler`'s 2 sites adopt it. (e) NL-SC-26: `TryGetExecutorError` also returns true for an object with integer `status` >= 400 and string `code` or `type` (skip if fix/no-legacy-followups already did this). Tests `Guardrail_Denies_ProblemDetails_Tool_Result` and `Guardrail_Denies_All_Mcp_Reachable_Error_Shapes` (NL-T-24, first run). (f) The ratchet gets two named lists: the protocol allowlist (section 3) and the non-response exclusion list (the 4 audit sites). Test `Protocol_Mandated_Error_Bodies_Keep_Spec_Shape` (NL-T-23).
2. **Target shapes.** HTTP: `application/problem+json` via `ApiProblems`. MCP tool results (WP-1): the executor shape, not ProblemDetails, because it is the MCP `isError` convention and the guardrail already denies it. Extra members are kept only as non-content extensions: `retryAfterSeconds`, `maxValidTo`, `phase`/`sunsetDate`/`replacement`, `limit` (kind only), `discardedEvents`. `message` and `traceId` are dropped (folded into `detail` and the built-in traceId).
3. **Detail text (NL-SC-21).** `detail` is a fixed text per code. Interpolated input ($"... '{id}' ..." at ~30 sites), `ex.Message`, `knf.Message`, `result.Message`, `decision.Reason`, `inspection.Error`, `outcome.Error` and the Origin header in CORS/CSRF never reach the body. Operator-declared text stays allowed, as it is today: `ProcedureBusinessException.MappedMessage` (SEC-PROC152-08) and `DeprecationNotice`.
4. **Anti-enumeration pairs (NL-SC-22, default taken).** Each pair uses one shared `ApiProblems.Indistinguishable(status, code, detail)` with no `traceId`, `instance` or `correlationId`, so the bodies stay byte-identical across requests. NL-T-22 pins the new golden bytes and asserts that pair members match. Bodies are not identical before and after migration, because the owner ruled out legacy shapes. Phase 3 must confirm this reading. Pairs: procedure unknown/forbidden/unavailable (`T_PROC152_01`, `T_PROC152I2_08/13`); catalog tags (`SEC_111_08`); keys (`UnknownOrInactiveDataset_404_SameBody`); synthetic samples (`SyntheticSamplesGet_HiddenDataset_SameBodyAsUnknown`); TableStream hidden or unauthorized table (`HandleBulkStreamAsync_*Returns404AntiEnumeration`); WebSQL statement not found (Execution:406,413); webhook rejection vs unknown instance (DEF-SC-12, `Itsm_Webhook_Rejections_Are_Indistinguishable_From_Unknown_Instance` plus a new endpoint-level byte test); feature-disabled 404s (HitL, System, bulk stream).
5. **Per-group commit (NL-SC-20/23).** Each WP is one commit per file group. The commit message carries the before/after status+code table and the baseline entries removed in the same commit. Status changes: none planned. The WP adds its rows to `Bare_Error_Migration_Preserves_Status_And_Code_Per_Group` (NL-T-20) and canary cases to `Migrated_Error_Bodies_Do_Not_Echo_Exception_Or_Input` (NL-T-21). Feature docs are updated in the WP that owns them: f-gov-21 (WP-3), f-gov-11 and f-gov-22 (WP-4), f-gov-14 (WP-6).
6. **WP-7 close-out.** Run NL-T-24 again, then delete `IsBareErrorShape` (NL-SC-25). `Bare_Error_Baseline_Is_Empty_Except_Protocol_Allowlist` (NL-T-25).

### 3. Protocol allowlist (class C, spec shape kept)
| Site | Spec |
|---|---|
| `McpEndpoints.cs:105` | JSON-RPC 2.0 §5.1 error object |
| `ODataEndpoints.Query.cs` (6), `ODataResponseFormatter.cs:69`, `BasicAuthenticationHandler.cs:343` | OData v4 JSON §21 `error{code,message}` |
| `ODataEndpoints.cs:178` | Reshaped in WP-0 to the OData error object with a fixed message (today it is a bare string that echoes the table name) |
| `IcebergRestCatalogEndpoints.cs:114` | Iceberg REST `ErrorModel{message,type,code}` |
| `BackstageEndpoints.cs:68` | Reshaped in WP-0 to Backstage `ErrorResponseBody{error{name,message},response{statusCode}}` with a fixed message (no `{name}` echo) |
| `EnvoyExtAuthzEndpoints.cs:73` | ext_authz denied-response pass-through (NL-SC-22) |

Not on the allowlist: WebSQL/Trino HTTP-level errors (Trino clients read non-200 bodies as text) and the OAuth-looking `/api/admin/tokens/revoke` (an admin API, not RFC 7009). Trino in-protocol `QueryResults.error` (`WebSqlEndpoints.Governance.cs:445`, a dictionary) is spec and is not a bare body.

### 4. Tests that read `error` (update in the owning WP)
- WP-1: Mcp/McpGuardrailLimitsTests, Mcp/McpOpenFindingsSecurityTests, Docs/DocRestScopingAndResourceGuardTests, McpServerTests, Integration/McpSdkTransportTests, Integration/McpDatasetToolsIntegrationTests.
- WP-2: SqlEndpointResponseFormatTests, WebSqlTrinoProtocolTests, WebSqlRequestValidationTests, SecurityReview20261002WebSqlTests, SecurityReview20261002GraphQLTests (GatewayDenialStatusMapper is shared with GatewayExceptionHandler), SecurityFindingsRemediationTests, Sql/Errors/SqlErrorSurfacesTests (pin), Sql/GovernedSqlRejectionAuditTests, Sql/GovernedSqlExecutorX1Tests, VirtualFilters/Federation/WebSqlGatewayDenialMappingTests, FederatedFailureAuditHygieneTests, Procedures/ProcedureSecurityInc1Tests and ProcedureLimitsInc2Tests (pins), Api/RawExceptionMessageNeverReachesClientTests, Parity/Channels/WebSqlChannel, Parity/Robin/RobinClient, Parity/Invariant/ParityAssertTests.
- WP-3: Catalog/CatalogTagsVisibilityEndpointTests and CatalogKeysEndpointTests (pins), Catalog/SyntheticSamplesGetEndpointTests (pin), Endpoints/TableStreamEndpointsTests (pin).
- WP-4: Governance/Issue117AccessExplanationDisclosureTests (check), DevExperienceTests (check).
- WP-5: Webhooks/ItsmWebhookNoLegacyFallbackTests.
- WP-6: CdcStream/CdcStreamAdminEndpointsTests, VirtualFilters/VirtualFilterEndpointsTests.
- Allowlist: Extensions.Tests/ODataTests, Integration/ODataIntegrationTests (unchanged).
- Not ours (upstream Databricks bodies, leave): Databricks/*, Integration/AstCompilerDatabricksSpark*.

This list comes from a grep. The developer also runs the WP's test projects; any other failure that asserts a bare body belongs to the same WP.

### 5. Order and file ownership
WP-0, then WP-1 (guardrail-fed, needs WP-0 e), then WP-2, WP-3, WP-4, WP-5, WP-6, and finally WP-7. No two WPs touch the same `src` file. The only shared file is the ratchet baseline. It is sorted by file, each WP removes only its own block, and the WPs run sequentially under the build lock. Sizes: WP-0 S, WP-1..6 M (26-50 sites), WP-7 S.

## Phase 3 rulings for step 2 (csharp-security-expert, 2026-10-26)
Verdict: APPROVED WITH CONDITIONS. NL-SC-20..26 / NL-T-20..25 stay binding.

1. Anti-enumeration: ACCEPTED. Byte-identity is required between pair members and across requests/principals/tenants, not against the old bytes. The old-vs-new clause of NL-T-22 is replaced by golden pins of the new bytes.
2. GatewayDenialStatusMapper: ACCEPTED. WP-2 may change every REST denial body only if the global handler and the mapper use one code path, the status/code table is unchanged (NL-SC-20), and parity/Robin clients plus their tests are updated in the same PR.
3. Dynamic codes and detail: CONFIRMED, fixed text only. Codes from ex.Code/ErrorCode/mapped.Code must pass the governed-format check (`^[A-Z][A-Z0-9_]{1,63}$` and a registered-code set or status default); an unregistered or failing code falls back to the status default and is never echoed.
4. Ratchet: REQUIRED to detect multi-line anonymous objects, dictionary bodies (`["error"]`, Dictionary/Results.Json with an error key) and `Results.Json(new { error`/`WriteAsJsonAsync(new { error` shapes. Its baseline must equal the architect's 274-site inventory (273 + 1 dict) minus the 4 DetailsJson audit payloads, and a pre-WP-0 self-test must prove it finds known multi-line and dictionary samples.
5. Allowlist: ACCEPTED (MCP executor shape, WebSQL/Trino and /api/admin/tokens/revoke migrate; Envoy deny, JSON-RPC, OData, Iceberg, Backstage stay), with NL-SC-29.
6. Guardrail ordering: ACCEPTED, hard gate. NL-SC-26 detector lands and its tests are green in WP-0, before WP-1; IsBareErrorShape stays until NL-T-24 passes over every MCP-reachable shape.

New conditions
- NL-SC-27: Governed-code check (ruling 3) is enforced inside ApiProblems.WriteAsync/Problem factories, so no call site can bypass it; ext members are limited to a fixed key allowlist (retryAfterSeconds, maxValidTo, phase, sunsetDate, replacement, limit, discardedEvents) with scalar values only.
- NL-SC-28: Indistinguishable emits no traceId/instance/correlationId/Retry-After difference, and the same headers and status for both branches; the existence check must not change response size or add ext members on either branch.
- NL-SC-29: Allowlist entries are an explicit file+line-independent list in the ratchet test; reshaped OData:178 and Backstage:68 use fixed messages (no table/name echo), and an allowlist addition needs a new Phase 3 note.
- NL-SC-30: Operator-declared text (ProcedureBusinessException.MappedMessage, Dep... text) passes a length cap and control-char strip before it reaches detail; Origin header, decision.Reason, inspection.Error, outcome.Error never reach bodies.
- NL-SC-31: MCP tool error results carry only fixed code+message; the executor shape is produced by one helper, and the guardrail treats both it and ProblemDetails as audited DENY.
- NL-T-26 `Ratchet_Detects_MultiLine_And_Dictionary_Bare_Error_Shapes` [Security].
- NL-T-27 `ApiProblems_Rejects_Ungoverned_Dynamic_Code_And_Uses_Status_Default` [Security].
- NL-T-28 `Indistinguishable_Pair_Bodies_And_Headers_Are_Identical_Across_Principals` [Security].
- NL-T-29 `Problem_Extensions_Outside_Allowlist_Are_Dropped` [Security].
- NL-T-30 `Gateway_Denial_Mapper_And_Global_Handler_Produce_Same_Problem_Shape` [Security].
- NL-T-31 `Operator_Mapped_Message_Is_Length_Capped_And_Sanitized` [Security].
All new tests carry [Trait("Category","Security")].

## Phase 3 rulings for part 1 issues (csharp-security-expert, 2026-10-29)
Verdict: APPROVED WITH CONDITIONS. NL-SC-20..31 stay binding.

1. Privacy-budget 429 extensions: EXTEND (consistent with ARCH-MNT-08 ruling 9). NL-SC-32: the allowlist gains clientId, consumed, totalBudget; clientId is only the caller's own client id (no other principal/tenant), consumed/totalBudget are numeric and typed (not free text, not stringified exceptions). Values come from the budget decision object, never from request input or ex.Message. Drop-on-unknown stays for every other key. NL-T-32 `PrivacyBudget_429_Keeps_Only_Allowlisted_Typed_Extensions` [Security].
2. WebSqlEndpoints.Governance:445 (Trino QueryResults.error): ACCEPTED as protocol shape (NL-SC-29). NL-SC-33: fixed message and fixed errorName/errorType per denial class; no ex.Message, stack, SQL text, table names or principal data; errorCode numeric from a fixed table; entry listed explicitly in the ratchet allowlist (file-line independent). NL-T-33 `Trino_InProtocol_Error_Has_Fixed_Message_And_No_Exception_Text` [Security].
3. ODataEndpoints.Query:61 echo: the echo is inside the Development-only branch; non-Development already returns the fixed ACCESS_DENIED 403 for both "missing" and "denied" (no enumeration in production). ACCEPTED as is, conditional: NL-SC-34: the echo stays strictly behind the IsDevelopment check, the value is only serialized via the JSON serializer (no HTML/format interpolation outside it), and the ratchet records it as an explicit dev-only allowlist entry. A fixed message is preferred and the developer may switch without a new ruling. NL-T-34 `OData_EntitySet_NotFound_Is_Fixed_Access_Denied_Outside_Development` [Security].
4. FORBIDDEN/NOT_FOUND fallbacks to ACCESS_DENIED/RESOURCE_NOT_FOUND: CONFIRMED. They are the governed codes, the status table is unchanged (NL-SC-20), and the fallback is fail-closed (an unmapped code never leaks as-is). NL-T-35 `Legacy_Forbidden_And_NotFound_Codes_Map_To_Governed_Codes` [Security].
All new tests carry [Trait("Category","Security")].
