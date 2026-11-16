# Implementation Plan: WP-SIMPL-46 PR 6 (Parity Matrix, #733 REST Explain Parity, PDP Degraded-Groups Precondition) & Issue #730 (GET Guard for DML SQL Endpoints)

- **Date:** 2026-11-14
- **Author:** Solution Architect (`csharp-architect`)
- **Work Package:** `WP-SIMPL-46` PR 6 (Cross-Transport Authorization Parity Matrix, `#733` REST Explain Parity, PDP Degraded-Groups Precondition) + Issue `#730` (GET Guard for DML SQL Endpoints)
- **Specs & References:**
  - `docs/plans/simplification/WP-SIMPL-46-unified-authorization-model.md` (§5 Steps 2 & 6, §6.1–6.4, §7–10)
  - `docs/plans/simplification/WP-SIMPL-43-authentication-authorization-core.md` (Addendum PR 4 items 8 & 11, `SEC-AUTH-04a`, `SEC-AUTH-04c`, `SEC-AUTH-06h`–`06k`)
  - `docs/adr/ADR-020-unified-authorization-model.md`
- **Target Branch / Worktree:** `feat/simpl-46-pr6-parity-matrix`

---

## 1. Executive Summary & Architectural Scope

This implementation plan delivers the final slice (**PR 6**) of `WP-SIMPL-46` together with the two tracked authorization/transport hardening items (`#733` and `#730`):

1. **PDP Degraded-Groups Precondition (`AccessDecisionPoint.DecideAsync`, `SEC-AUTH-04a` / `SEC-AUTH-04c`):**
   - Move the degraded-group-resolution check to the top of `AccessDecisionPoint.DecideAsync` as a `DecisionStep.Precondition` check (`GROUPS_DEGRADED`) evaluated **before** any consent, access-profile, virtual-filter, or cache lookup.
   - Eliminate reliance on `Principal` claim markers alone so principal-less `SecurityPrincipalContext` instances (e.g., `DegradedExplainSubjectResolver`, `DuckDbOlapEndpoints`, `GovernanceEndpoints.Catalog`, or unmarked `SecurityPrincipalContext` instances) fail closed with `identity.groups_unresolved` and `DecisionReason(DecisionStep.Precondition, "GROUPS_DEGRADED", "Identity", null)`.
   - Preserve in-process unit-test ergonomics in `SecurityPrincipalContext.FromPrincipal` by distinguishing pipeline-processed principals (`GroupResolutionClaims.HasPipelineMarker`) from synthetic unit-test principals while keeping `SecurityContextFactory.CreateFromHttpContext` strict (`GroupResolutionClaims.Read(user)`).

2. **Issue #733 — REST Explain Parity & Row-Filter/Mask Assertion (`SecurityContextFactory`, `ConsentResolutionService`, `AccessDecisionPoint`, `AuthorizationExplainEndpoints`, `AuthModeEndToEndTests`):**
   - **Root Cause of #733:** `SecurityContextFactory.BuildContext` (`src/Autheris.Api/Security/SecurityContextFactory.cs:137-153`) did not populate `Principal = user` or `Claims = user.Claims` on the `SecurityPrincipalContext` stored in `HttpContext.Items[SecurityPrincipalContext.ItemKey]`. When `POST /api/v1/authz/explain` called `AccessExplanationService.ExplainAsync(context.GetSecurityContext(), ...)`, `FindObjectAsync` passed `new CatalogCaller(caller.Principal, tenant, CatalogChannel.Rest)` to `CatalogVisibilityService.FindVisibleAsync`, which saw `caller.Principal == null` at `CatalogVisibilityService.Prepare` (`line 241`) and returned `null` (`ExplainOutcome.NotFound` -> HTTP `404`).
   - **Fix:**
     - Attach `Principal = user` and `Claims = user.Claims` in `SecurityContextFactory.BuildContext`.
     - Populate `TableAccessDecision.Grants` (`DecisionGrantRef`) and `TableAccessDecision.Reasons` (`DecisionReason` for `GRANT_ALLOW`, `ROW_FILTER`, `COLUMN_MASK`, `COLUMN_DENY`, `GRANT_DENY`, `GRANT_EXPIRED`, `DEFAULT_DENY` using IDs and column names only, never predicate literal values) in `ConsentResolutionService.ResolveAccess` and `AccessDecisionPoint.DecideAsync`.
     - Populate full table column states in `AccessDecisionPoint.ExplainAsync` across `request.Object.Columns` (falling back to `decision.ColumnAccess` when `request.Object.Columns` is empty).
     - Replace the anonymous `{ grants, truncated }` payload in `AuthorizationExplainEndpoints.cs` with a named `GrantsListResponse` record (closing the PR 4 review follow-up).
     - Enable full self-explain assertions (`POST /api/v1/authz/explain`) in `AuthModeEndToEndTests.Same_user_same_groups_identical_security_context_across_modes` across `Mode.Jwt`, `Mode.Basic`, and `Mode.ForwardAuth` for `Restricted` (row filter + mask), `Open` (all clear), and `NoGrant` (uniform `404`).

3. **Cross-Transport Authorization Parity Matrix (`WP-SIMPL-46` §6.3 & §6.4):**
   - Implement `tests/Autheris.Tests.Integration/Security/CrossTransportAuthorizationParityTests.cs` covering all 19 authorization scenarios (`S1`–`S19`) across the 15 transports and data sources (`GraphQl`, `Rest`, `OData`, `WebSql`, `Trino`, `Mcp`, `Procedure`, `Analytics`, `Olap`, `Federation`, `ArrowExport`, `EnvoyExtAuthz`, `Discovery`, `HttpApiSource`, `DatabricksDelegated`).
   - Add the runtime route-table assertion to `RemovedAuthzKeysTests.RebacRoutes_Return404` (closing the PR 5 review follow-up).
   - Add `benchmarks/Autheris.Benchmarks/AccessDecisionPointBenchmark.cs` (§6.4).

4. **Issue #730 — GET Guard for DML SQL Endpoints (`SqlEndpointRoutes`, `SqlEndpointExecutionService`, `SqlEndpointLoader`):**
   - Enforce that `GET /api/v1/queries/{name}` (and `HEAD`) only executes endpoints whose `HttpMethod` is `"GET"` AND whose AST-classified SQL statement is strictly a single `SELECT` query (`RequestStatementClass.Read`).
   - Reject any DML/DDL/non-SELECT statement (`INSERT`, `UPDATE`, `DELETE`, `MERGE`, `CREATE`, `ALTER`, `DROP`, `TRUNCATE`, `EXEC`/`CALL`, or unparsable SQL) or `HttpMethod == "POST"` endpoint invoked via `GET /api/v1/queries/{name}` **fail-closed before execution** with `405 Method Not Allowed` (`Allow: POST`) for **all** callers (including write-scoped `Data.ReadWrite` tokens and `ClusterAdmin`).
   - On `POST /api/v1/queries/{name}`, enforce that any non-SELECT endpoint requires `WebSql:AllowDmlStatements == true`, `!user.IsReadOnly()`, and `user.HasScope(TokenAccessScope.WriteScope)` (otherwise `403 Forbidden`).
   - On `POST /api/v1/queries` (`HandleRegisterEndpoint`), reject registering a non-SELECT statement when `HttpMethod` is explicitly `"GET"` (`400 BadRequest`), and default `HttpMethod` to `"POST"` when registering a DML statement without an explicit method.

---

## 2. Requirement 1: PDP Degraded-Groups Precondition Across Realms

### 2.1 How Degraded Group Resolution Arises Across Realms

The identity pipeline (`IdentityPipeline.ProcessAsync`) and realm group resolvers (`GroupSidResolver`, `ScimEffectiveGroupResolver`) produce `GroupResolutionStatus.Degraded` with an empty `GroupSids` set whenever a caller's complete group membership cannot be verified:

| Realm Category | Failure / Condition | Resulting `SecurityPrincipalContext` State | PDP Decision (`AccessDecisionPoint.DecideAsync`) |
| :--- | :--- | :--- | :--- |
| **Direct Realms** (`EntraWorkforce`, `EntraCustomer`, `AdFs`, `Kerberos`, `Basic`) | - Entra token `_claim_names` / `hasgroups` overage without working Graph resolver<br>- Graph / LDAPS provider timeout or exception<br>- Non-canonical SID in token or directory response<br>- Group count exceeds `MaxGroupSidsPerCaller` (1024)<br>- Role resolver (`IRoleResolver`) returns `IsDegraded = true` | `GroupResolutionStatus = Degraded`, `GroupSids = []`, pipeline status claim `autheris:group_resolution = degraded` (and/or `autheris:role_resolution = degraded`) | **Deny** at `DecisionStep.Precondition` (`Code = "GROUPS_DEGRADED"`, message contains `identity.groups_unresolved`). Never evaluated as "member of no groups". |
| **Upstream Realms** (`ForwardAuth`) | - `ForwardAuthGroupSource.Header` with malformed/non-canonical `X-Forwarded-Groups` SID<br>- `ForwardAuthGroupSource.Resolver` when resolver chain fails or tenant is unresolved | `GroupResolutionStatus = Degraded`, `GroupSids = []` | **Deny** at `DecisionStep.Precondition` (`Code = "GROUPS_DEGRADED"`). |
| **SCIM Realms** (`Provisioning = Scim`) | - Unprovisioned user when `RequireCorrelation = true` (`USER_NOT_PROVISIONED`)<br>- Ambiguous multi-row match (`AMBIGUOUS_CORRELATION`)<br>- Provisioned user `active = false` or `deprovision_epoch` bumped (`USER_INACTIVE`)<br>- Group nesting depth > 32 (`NESTING_DEPTH_EXCEEDED`)<br>- Sync age > `MaxSyncAgeMinutes` with `StaleSyncBehavior = Degrade` (`SCIM_SYNC_STALE`)<br>- Sync age > `MaxSyncAgeMinutes` with `StaleSyncBehavior = DropScimGroups` when any SCIM-only group of the user holds an active `Deny` consent or `Deny` virtual filter (`SCIM_STALE_DENY_GUARD`) | `GroupResolutionStatus = Degraded`, `GroupSids = []` | **Deny** at `DecisionStep.Precondition` (`Code = "GROUPS_DEGRADED"`). Prevents bypassing a group-scoped `Deny` via stale or failed SCIM sync. |
| **Principal-less / Unmarked Contexts** (`DegradedExplainSubjectResolver`, `new SecurityPrincipalContext` without pipeline marker) | - Foreign subject in `POST /api/v1/authz/explain` without directory token (`DegradedExplainSubjectResolver`)<br>- Request context constructed without `Principal` or without `GroupResolutionStatus = Complete` | `GroupResolutionStatus = Degraded`, `Principal = null`, `GroupSids = []` | **Deny** at `DecisionStep.Precondition` (`Code = "GROUPS_DEGRADED"`) even when `subject.Principal` is `null`. |

### 2.2 Changes to `src/Autheris.Domain/Security/SecurityPrincipalContext.cs`

In `SecurityPrincipalContext.FromPrincipal` (`src/Autheris.Domain/Security/SecurityPrincipalContext.cs:233`), preserve strict pipeline-marker validation for every principal that passed through `IdentityPipeline`, `McpProtocolHandler.BuildPrincipalFromSession`, or `CdcSubscriptionPrincipal`, while allowing in-process unit-test `ClaimsPrincipal` instances (which carry neither a pipeline marker nor a degraded marker) to resolve as `Complete` unless marked degraded:

```csharp
        var groupStatus = Autheris.Domain.Identity.GroupResolutionClaims.HasPipelineMarker(principal)
            ? Autheris.Domain.Identity.GroupResolutionClaims.Read(principal)
            : Autheris.Domain.Identity.GroupResolutionClaims.IsMarkedDegraded(principal)
                ? Autheris.Domain.Identity.GroupResolutionStatus.Degraded
                : Autheris.Domain.Identity.GroupResolutionStatus.Complete;
```

- **Note:** `SecurityContextFactory.CreateFromHttpContext` continues to call `GroupResolutionClaims.Read(user)` directly (`src/Autheris.Api/Security/SecurityContextFactory.cs:126`), so any HTTP request whose `HttpContext.User` did not pass through `IdentityPipeline` still receives `GroupResolutionStatus.Degraded` and empty `GroupSids` (`IdentityPipelineTests.Principal_without_pipeline_marker_is_degraded` remains green).
- Default property behavior on `new SecurityPrincipalContext { ... }` when `GroupResolutionStatus` is omitted remains `GroupResolutionStatus.Degraded` for authenticated subjects (`PdpGroupsUnresolvedTests.Unmarked_security_context_defaults_to_degraded` remains green).

### 2.3 Changes to `src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs`

Update `AccessDecisionPoint.DecideAsync` (`src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs:102-180`) to enforce all `DecisionStep.Precondition` checks at the top of `DecideAsync` and attach structured `DecisionReason` entries:

```csharp
    public async ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subject = request.Subject;
        var metadata = request.Object;
        ArgumentNullException.ThrowIfNull(metadata);
        var table = metadata.Identifier;

        if (!metadata.Table.IsActive)
        {
            return TableAccessDecision.Denied(table, $"Table '{table.ToQualifiedName()}' is not active.") with
            {
                EvaluatedByPdp = true,
                Reasons = [new DecisionReason(DecisionStep.Precondition, "TABLE_INACTIVE", "Catalog", table.ToString())]
            };
        }

        bool consentBypassed = _options.Value.IsConsentBypassed;

        if (subject is null || (!subject.IsAuthenticated && !consentBypassed) || (subject.IsAuthenticated && string.IsNullOrWhiteSpace(subject.UserSid.Value)))
        {
            return TableAccessDecision.Denied(table, "Access denied: caller identity is unauthenticated or incomplete.") with
            {
                EvaluatedByPdp = true,
                Reasons = [new DecisionReason(DecisionStep.Precondition, "IDENTITY_INCOMPLETE", "Identity", null)]
            };
        }

        if (subject.IsAuthenticated && (subject.TenantClaimInvalid || subject.ResolvedTenant is not { Value: not null }))
        {
            return TableAccessDecision.Denied(table, "Access denied: caller tenant is unresolved or invalid.") with
            {
                EvaluatedByPdp = true,
                Reasons = [new DecisionReason(DecisionStep.Precondition, "TENANT_UNRESOLVED", "Identity", null)]
            };
        }

        if (subject.GroupResolutionStatus != Autheris.Domain.Identity.GroupResolutionStatus.Complete
            || Autheris.Domain.Identity.GroupResolutionClaims.IsMarkedDegraded(subject.Principal))
        {
            return TableAccessDecision.Denied(table, "identity.groups_unresolved: Group resolution unavailable (fail-closed).") with
            {
                EvaluatedByPdp = true,
                Reasons = [new DecisionReason(DecisionStep.Precondition, "GROUPS_DEGRADED", "Identity", null)]
            };
        }

        if (request.Operation is AccessOperation.Insert or AccessOperation.Update or AccessOperation.Delete)
        {
            var canWrite = _policy != null && await _policy.CanWriteTableAsync(
                subject.TenantId,
                metadata,
                ct: ct).ConfigureAwait(false);

            if (!canWrite)
            {
                return TableAccessDecision.Denied(table, $"Write access to table '{table.ToQualifiedName()}' is not permitted.") with
                {
                    EvaluatedByPdp = true,
                    Reasons = [new DecisionReason(DecisionStep.Precondition, "WRITE_TRANSPORT_NOT_WIRED", "Transport", request.Transport.ToString())]
                };
            }
        }
```

---

## 3. Requirement 2: Issue #733 (REST Explain Parity & Row-Filter/Mask Assertion)

### 3.1 Attach `Principal` and `Claims` in `SecurityContextFactory.BuildContext`

**File:** `src/Autheris.Api/Security/SecurityContextFactory.cs` (lines 137–155)

Update `BuildContext` so every `SecurityPrincipalContext` created from `HttpContext` carries `Claims = user.Claims` and `Principal = user` (matching `SecurityPrincipalContext.FromPrincipal`):

```csharp
        return new SecurityPrincipalContext
        {
            UserSid = userSid ?? new Sid(string.Empty),
            ResolvedTenant = resolvedTenant,
            TenantClaimInvalid = tenantClaimInvalid,
            TenantSource = tenantSource,
            GroupSids = groupSids,
            GroupResolutionStatus = groupStatus,
            GroupsExpireAt = Autheris.Domain.Identity.GroupResolutionClaims.ReadExpiresAt(user),
            ControlPlaneEnrichment = enrichment,
            TenantRoles = tenantRoles,
            ClusterRoles = clusterRoles,
            AuthenticationScheme = authScheme,
            IsAuthenticated = isAuthenticated,
            TenantSelectedByHeader = tenantSelectedByHeader,
            ClientIp = clientIp,
            Caller = callerContext,
            Claims = user.Claims,
            Principal = user
        };
```

### 3.2 Populate `Grants` and `Reasons` in `ConsentResolutionService.ResolveAccess`

**File:** `src/Autheris.Application/Services/ConsentResolutionService.cs`

1. On **table-level explicit deny** (`hasTableDeny`, line 55):
   ```csharp
   var denyConsents = dConsents.Where(static c => c.ColumnRules.Count == 0 && c.RowFilters.Count == 0).ToList();
   return TableAccessDecision.Denied(table, "Hard DENY: Active table-level DENY applies to subject.") with
   {
       Reasons = denyConsents
           .OrderBy(static c => c.Id)
           .Select(static c => new DecisionReason(DecisionStep.ExplicitDeny, "GRANT_DENY", "Grant", c.Id.ToString()))
           .ToList()
   };
   ```
2. On **zero-trust default deny** (`aConsents.Count == 0`, lines 61–81):
   - When `clampedConsent != null`: attach `Reasons = [new DecisionReason(DecisionStep.DefaultDeny, "GRANT_EXPIRED", "Grant", clampedConsent.Id.ToString())]`.
   - Otherwise: attach `Reasons = [new DecisionReason(DecisionStep.DefaultDeny, "NO_GRANT", null, null)]`.
3. On **allow** (`aConsents.Count > 0`, lines 165–169):
   - Build deterministic `Grants` ordered by `GrantId` (`StringComparer.Ordinal`):
     ```csharp
     var grants = aConsents
         .OrderBy(static c => c.Id.ToString(), StringComparer.Ordinal)
         .Select(static c => new DecisionGrantRef(c.Id.ToString(), GrantSourceKind.ConsentRequest, c.ValidFrom, c.EffectiveValidTo))
         .ToList();
     ```
   - Build deterministic `Reasons` (containing **only** grant/filter IDs and column names, **never** row-filter literal values):
     ```csharp
     var reasons = new List<DecisionReason>();
     foreach (var c in dConsents.OrderBy(static x => x.Id.ToString(), StringComparer.Ordinal))
     {
         foreach (var cr in c.ColumnRules.Where(r => RuleAppliesToScopes(r, callerScopes)))
         {
             var code = cr.AccessLevel == ColumnAccessLevel.Deny ? "COLUMN_DENY" : "COLUMN_MASK";
             reasons.Add(new DecisionReason(DecisionStep.ExplicitDeny, code, "Grant", c.Id.ToString(), cr.ColumnName));
         }
         foreach (var rf in c.RowFilters)
         {
             reasons.Add(new DecisionReason(DecisionStep.ExplicitDeny, "ROW_DENY", "Grant", rf.Id.ToString(), rf.ColumnName));
         }
     }
     foreach (var c in aConsents.OrderBy(static x => x.Id.ToString(), StringComparer.Ordinal))
     {
         reasons.Add(new DecisionReason(DecisionStep.Grant, "GRANT_ALLOW", "Grant", c.Id.ToString()));
         foreach (var rf in c.RowFilters)
         {
             reasons.Add(new DecisionReason(DecisionStep.Narrowing, "ROW_FILTER", "Grant", rf.Id.ToString(), rf.ColumnName));
         }
         foreach (var cr in c.ColumnRules.Where(r => RuleAppliesToScopes(r, callerScopes) && r.AccessLevel != ColumnAccessLevel.Clear))
         {
             var step = cr.AccessLevel == ColumnAccessLevel.Deny ? DecisionStep.ExplicitDeny : DecisionStep.Narrowing;
             var code = cr.AccessLevel == ColumnAccessLevel.Deny ? "COLUMN_DENY" : "COLUMN_MASK";
             reasons.Add(new DecisionReason(step, code, "Grant", c.Id.ToString(), cr.ColumnName));
         }
     }
     ```
   - Return:
     ```csharp
     return TableAccessDecision.Allowed(table, columnAccess, rowFilterSql, hasUnconstrainedColumnAllow) with
     {
         ConsentFilterRecipe = recipe,
         ScopeClearance = scopeClearance,
         Grants = grants,
         Reasons = reasons
     };
     ```

### 3.3 Complete Column State Mapping in `AccessDecisionPoint.ExplainAsync`

**File:** `src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs` (lines 201–217)

When `request.Object.Columns` is non-empty, project every catalog column through `decision.GetEffectiveColumnAccess(col.ColumnName, request.Object)` (or `ColumnAccessLevel.Deny` when `!decision.IsAllowed`), and populate `MaskType` when a column is masked; fall back to `decision.ColumnAccess` when `request.Object.Columns` is empty (preserving unit tests that construct bare `TableMetadata` without columns):

```csharp
    public async ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default)
    {
        var decision = await DecideAsync(request, ct).ConfigureAwait(false);
        IReadOnlyList<ColumnStateExplanation> cols;
        if (request.Object.Columns.Count > 0)
        {
            cols = request.Object.Columns
                .Select(c =>
                {
                    var level = decision.IsAllowed
                        ? decision.GetEffectiveColumnAccess(c.ColumnName, request.Object)
                        : ColumnAccessLevel.Deny;
                    string? maskType = null;
                    if (level == ColumnAccessLevel.Mask)
                    {
                        if (decision.ColumnMasks != null && decision.ColumnMasks.TryGetValue(c.ColumnName, out var typedMask))
                        {
                            maskType = typedMask.Kind.ToString();
                        }
                        else if (request.Object.ColumnMaskingRules != null && request.Object.ColumnMaskingRules.TryGetValue(c.ColumnName, out var rule))
                        {
                            maskType = rule.RuleType;
                        }
                        else
                        {
                            maskType = "Default";
                        }
                    }

                    return new ColumnStateExplanation(c.ColumnName, level, maskType);
                })
                .ToList();
        }
        else
        {
            cols = decision.ColumnAccess.Select(kv => new ColumnStateExplanation(kv.Key, kv.Value)).ToList();
        }

        var groups = new EffectiveGroupsExplanation(
            request.Subject.GroupSids.Select(s => s.Value).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            "Token",
            request.Subject.GroupResolutionStatus.ToString());

        return new AccessExplanation(
            decision.IsAllowed,
            decision.Reasons,
            cols,
            groups,
            decision.Grants,
            decision.PolicyEpoch);
    }
```

### 3.4 Named `GrantsListResponse` Record in `AuthorizationExplainEndpoints.cs`

**File:** `src/Autheris.Api/Endpoints/AuthorizationExplainEndpoints.cs`

Add `public sealed record GrantsListResponse(IReadOnlyList<GrantRecordView> Grants, bool Truncated);` and return `new GrantsListResponse(result.Grants!, result.Truncated)` instead of the anonymous object at line 42.

### 3.5 Update `AuthModeEndToEndTests.Same_user_same_groups_identical_security_context_across_modes`

**File:** `tests/Autheris.Tests.Integration/Security/AuthModeEndToEndTests.cs` (lines 276–293)

Remove the `// Row filter and mask parity through the explain endpoint is pending #733` comment and add `POST /api/v1/authz/explain` assertions across `Mode.Jwt`, `Mode.Basic`, and `Mode.ForwardAuth`:

```csharp
        // Self-explain parity on the row-filtered and masked table across JWT, Basic and ForwardAuth (#733).
        using var explainRestricted = PostExplain(Restricted, mode);
        var restrictedResponse = await _fixture.Factory.CreateClient().SendAsync(explainRestricted);
        restrictedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var restrictedBody = await restrictedResponse.Content.ReadAsStringAsync();
        restrictedBody.ShouldNotContain("EU"); // Never discloses row-filter literal values
        using var restrictedJson = JsonDocument.Parse(restrictedBody);
        var root = restrictedJson.RootElement;
        root.GetProperty("allowed").GetBoolean().ShouldBeTrue();
        root.GetProperty("subjectResolved").GetBoolean().ShouldBeTrue();
        root.GetProperty("groups").GetProperty("status").GetString().ShouldBe("Complete");
        root.GetProperty("groups").GetProperty("groupSids").EnumerateArray().Select(x => x.GetString()).ShouldBe([GroupSid]);
        root.GetProperty("grants").GetArrayLength().ShouldBe(1);

        var columns = root.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("columnName").GetString()!, c => c.GetProperty("level").GetInt32());
        columns["id"].ShouldBe((int)ColumnAccessLevel.Clear);
        columns["region"].ShouldBe((int)ColumnAccessLevel.Clear);
        columns["email"].ShouldBe((int)ColumnAccessLevel.Mask);

        var traceCodes = root.GetProperty("trace").EnumerateArray()
            .Select(t => (Code: t.GetProperty("code").GetString(), Column: t.TryGetProperty("column", out var col) ? col.GetString() : null))
            .ToList();
        traceCodes.ShouldContain(t => t.Code == "GRANT_ALLOW");
        traceCodes.ShouldContain(t => t.Code == "ROW_FILTER" && t.Column == "region");
        traceCodes.ShouldContain(t => t.Code == "COLUMN_MASK" && t.Column == "email");

        // Self-explain on the table without a grant returns uniform 404 NotFound (no existence oracle).
        using var explainNoGrant = PostExplain(NoGrant, mode);
        (await _fixture.Factory.CreateClient().SendAsync(explainNoGrant)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
```

---

## 4. Requirement 3: Cross-Transport Authorization Parity Matrix (`WP-SIMPL-46` §6.3 & §6.4)

### 4.1 Parity Matrix Design (`tests/Autheris.Tests.Integration/Security/CrossTransportAuthorizationParityTests.cs`)

Create `tests/Autheris.Tests.Integration/Security/CrossTransportAuthorizationParityTests.cs` (`[Trait("Category", "Security")]`) verifying that all **19 scenarios (`S1`–`S19`)** produce identical authorization outcomes across all **15 transports and data sources**:

#### Scenarios (`ParityScenario` enum, 19 values)

| ID | Enum Member | Setup | Expected PDP Outcome |
| :--- | :--- | :--- | :--- |
| `S1` | `S01_UserGrant` | Active `Allow` grant for `UserSid`, all columns `Clear` | `Allowed = true`, columns `Clear`, all 3 rows `[1, 2, 3]`, 1 grant ref |
| `S2` | `S02_TokenGroupGrant` | Active `Allow` grant for token `GroupSid` | `Allowed = true`, columns `Clear`, rows `[1, 2, 3]`, 1 grant ref |
| `S3` | `S03_ScimGroupGrant` | Active `Allow` grant for SCIM-resolved group SID | `Allowed = true`, columns `Clear`, rows `[1, 2, 3]`, 1 grant ref |
| `S4` | `S04_NoGrant` | No active grant for caller or groups | `Allowed = false`, `NO_GRANT`, 0 rows, 0 grants |
| `S5` | `S05_GroupTableDenyBeatsUserAllow` | User `Allow` grant + Group table-level `Deny` grant | `Allowed = false`, `GRANT_DENY` (`ExplicitDeny`), 0 rows, 0 grants |
| `S6` | `S06_ColumnDenyRule` | `Allow` grant with `email = Deny` | `Allowed = true`, `id/region = Clear`, `email = Deny`, rows `[1, 2, 3]` |
| `S7` | `S07_PiiDefaultMasked` | `email` marked `IsSensitive = true` with default mask rule, unconstrained `Allow` | `Allowed = true`, `id/region = Clear`, `email = Mask`, rows `[1, 2, 3]` |
| `S8` | `S08_PiiExplicitClearRule` | `email` marked `IsSensitive = true`, `Allow` grant with explicit `email = Clear` | `Allowed = true`, `id/region/email = Clear`, rows `[1, 2, 3]` |
| `S9` | `S09_HiddenColumn` | Table has DENY masking rule on `email` + `Allow` grant | `Allowed = true`, `email = Deny`, `id/region = Clear` |
| `S10` | `S10_InListRowFilter` | `Allow` grant with `region IN ("EU", "UK")` row filter | `Allowed = true`, rows filtered to EU/UK (`[1, 2]`) |
| `S11` | `S11_EqualityRowFilter` | `Allow` grant with `region = "EU"` row filter | `Allowed = true`, rows filtered to EU (`[1]`) |
| `S12` | `S12_UnresolvedCallerAttributeDenies` | Virtual filter / consent referencing unresolved `@caller.department` | `Allowed = false` (or 0 rows / denied by virtual filter fail-closed) |
| `S13` | `S13_MaterializedProfileGrantMaskedPii` | Grant record materialized from approved access profile with `email = Mask` | `Allowed = true`, `email = Mask`, 1 grant ref |
| `S14` | `S14_ClusterAdminWithoutGrant` | Caller has `ClusterAdmin` role, no grant record | `Allowed = false`, `NO_GRANT` (roles never grant data access) |
| `S15` | `S15_ExpiredGrant` | `Allow` grant with `ValidTo < now` | `Allowed = false`, `NO_GRANT` |
| `S16` | `S16_RevokedGrantAfterEpochBump` | `Allow` grant marked `IsRevoked = true` + epoch invalidated | `Allowed = false`, `NO_GRANT` |
| `S17` | `S17_FutureGrantNotYetValid` | `Allow` grant with `ValidFrom > now` | `Allowed = false`, `NO_GRANT` |
| `S18` | `S18_OwnerGrant` | Active owner grant record (`GranteeSid = OwnerSid`) | `Allowed = true`, columns `Clear`, 1 grant ref |
| `S19` | `S19_AuthHookAddsRowFilter` | `Allow` grant + `IAuthPluginHost` hook returning `Restrict` with `region = 'EU'` | `Allowed = true`, rows narrowed to EU (`[1]`) |

#### Transports & Sources (`ParitySurface` enum, 15 values)

1. `GraphQl` (`AccessTransport.GraphQl` via `GraphQlDataSecurityMiddleware`)
2. `RestDatasets` (`AccessTransport.Rest` via `GatewayExecutionService`)
3. `OData` (`AccessTransport.OData` via `ODataQueryPolicyEnforcer`)
4. `WebSql` (`AccessTransport.WebSql` via `GovernedSqlRewriter`)
5. `Trino` (`AccessTransport.Trino` via `/v1/statement` / `GovernedSqlRewriter`)
6. `McpQueryData` (`AccessTransport.Mcp` via `AiDataGuardrailService`)
7. `Procedure` (`AccessTransport.Procedure` via `GovernedProcedureExecutionService`)
8. `AnalyticsAggregate` (`AccessTransport.Analytics` via `GovernedAggregationService`)
9. `OlapDuckDb` (`AccessTransport.Olap` via `DefaultCrossDomainAccessResolver`)
10. `FederatedJoin` (`AccessTransport.Federation` via `FederatedDuckDbExecutionService`)
11. `ArrowExport` (`AccessTransport.ArrowExport` via `ArrowExportEndpoints` / PDP check)
12. `EnvoyExtAuthz` (`AccessTransport.EnvoyExtAuthz` via `EnvoyExtAuthzService` — asserts allow/deny only, where any row/column restriction denies)
13. `Discovery` (`AccessTransport.Discovery` via `IAccessDecisionPoint.DecideManyAsync` — asserts column states only, never row data)
14. `HttpApiSource` (`AccessTransport.Rest` on an `HttpApi` data source table)
15. `DatabricksDelegated` (`AccessTransport.WebSql` on a `Databricks` delegated table with `EnforcementMode.Delegated` — asserts PDP denies before any source call is issued)

#### Required Tests in `CrossTransportAuthorizationParityTests.cs`

- `[Fact] public void Matrix_complete_for_all_transports_and_sources()`: asserts `Enum.GetValues<ParityScenario>().Length == 19`, `Enum.GetValues<ParitySurface>().Length == 15`, and `19 * 15 == 285` combinations are generated.
- `[Theory] [MemberData(nameof(AllCells))] public async Task Same_decision_on_every_transport(ParityScenario scenario, ParitySurface surface)`: executes the scenario on the target transport/source and compares `Allowed`, effective column states, filtered row keys, and `Grants` against `IAccessDecisionPoint.ExplainAsync`.
- `[Fact] public async Task Degraded_groups_without_principal_denies_across_pdp_and_olap()`: regression test for `SEC-AUTH-04c` verifying that a principal-less `SecurityPrincipalContext` with `GroupResolutionStatus = GroupResolutionStatus.Degraded` is denied with `GROUPS_DEGRADED` at `AccessDecisionPoint.DecideAsync`.

### 4.2 Route-Table Assertion in `RemovedAuthzKeysTests.RebacRoutes_Return404`

**File:** `tests/Autheris.Tests.Unit/Configuration/RemovedAuthzKeysTests.cs`

Extend `RebacRoutes_Return404()` to also build a minimal `EndpointRouteBuilder` / inspect mapped routes (or verify via `GatewayApplicationBuilderExtensions` route registration) that no `RouteEndpoint` pattern contains `/rebac` (`OrdinalIgnoreCase`), closing the PR 5 review follow-up.

### 4.3 Benchmark (`benchmarks/Autheris.Benchmarks/AccessDecisionPointBenchmark.cs`)

**File:** `benchmarks/Autheris.Benchmarks/AccessDecisionPointBenchmark.cs`

Create `AccessDecisionPointBenchmark` benchmarking cached and uncached `AccessDecisionPoint.DecideAsync` and `ExplainAsync` calls with an active consent and row filter, verifying cached p95 < 5 ms per object.

---

## 5. Requirement 4: Issue #730 (GET Guard for DML SQL Endpoints)

### 5.1 Threat & Root Cause Analysis

1. `SqlEndpointRoutes.MapSqlEndpoints` (`src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs:52-62`) maps both:
   - `GET /api/v1/queries/{name}` -> `HandleGetEndpoint`
   - `POST /api/v1/queries/{name}` -> `HandlePostEndpoint`
2. Both handlers invoke `ISqlEndpointExecutionService.ExecuteEndpointAsync(name, rawInputs, httpContext.User, tenantId, ct)` without passing the HTTP method or checking whether the endpoint's `RawSql` is a read-only `SELECT` statement.
3. Consequently:
   - A write-scoped token (`Data.ReadWrite`) or `ClusterAdmin` can trigger a registered DML SQL endpoint (`INSERT`, `UPDATE`, `DELETE`, `MERGE`) over `GET /api/v1/queries/{name}`, violating HTTP `GET` side-effect safety and bypassing `WebSqlEndpoints.RefuseDmlRequestAsync` (idempotency-key and POST-only enforcement).
   - Even worse, because `GET /api/v1/queries/{name}` is an HTTP `GET` route, `ReadOnlyTokenMiddleware` allows read-only tokens to reach `HandleGetEndpoint`!

### 5.2 Defense-in-Depth Design for Issue #730

#### Layer 1: Statement Classification Helper & Exception (`SqlEndpointExecutionService.cs`)

In `src/Autheris.Application/Sql/Endpoints/Interfaces/ISqlEndpointExecutionService.cs`:
- Add optional parameter `string httpMethod = "GET"` to `ExecuteEndpointAsync`:
  ```csharp
  Task<GovernedSqlResult> ExecuteEndpointAsync(
      string endpointName,
      IReadOnlyDictionary<string, object?>? rawParameters,
      ClaimsPrincipal user,
      TenantId tenantId = default,
      CancellationToken ct = default,
      string httpMethod = "GET");
  ```
- Define `SqlEndpointMethodNotAllowedException : SecurityException`:
  ```csharp
  public sealed class SqlEndpointMethodNotAllowedException : SecurityException
  {
      public string EndpointName { get; }
      public string AllowedMethod { get; }

      public SqlEndpointMethodNotAllowedException(string endpointName, string allowedMethod = "POST")
          : base($"SQL endpoint '{endpointName}' cannot be executed via HTTP GET; use {allowedMethod}.")
      {
          EndpointName = endpointName;
          AllowedMethod = allowedMethod;
      }
  }
  ```

In `src/Autheris.Application/Sql/Endpoints/Services/SqlEndpointExecutionService.cs`:
- Add static AST statement classifier `IsSelectOnlyStatement(string rawSql)`:
  ```csharp
  public static bool IsSelectOnlyStatement(string rawSql)
  {
      if (string.IsNullOrWhiteSpace(rawSql))
      {
          return false;
      }

      try
      {
          var normalized = SqlParameterExtractor.NormalizeForAst(rawSql.Trim());
          var (tree, _) = TrinoSqlEngine.FastSqlEngine.Default.Parse(normalized.AsMemory());
          if (tree.statement() is TrinoSqlEngine.SqlBaseParser.MergeContext)
          {
              return false;
          }

          return new TrinoSqlEngine.Analysis.SqlQueryAnalyzer().Analyze(tree).StatementType == TrinoSqlEngine.Analysis.SqlStatementType.Select;
      }
      catch (Exception)
      {
          return false;
      }
  }
  ```
- At the top of `ExecuteEndpointAsync` (immediately after resolving `definition` from `_registry`):
  ```csharp
  bool isGetOrHead = string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase)
      || string.Equals(httpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
  bool isSelectOnly = IsSelectOnlyStatement(definition.RawSql);

  if (isGetOrHead)
  {
      if (!string.IsNullOrWhiteSpace(definition.HttpMethod)
          && !string.Equals(definition.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
      {
          throw new SqlEndpointMethodNotAllowedException(endpointName, definition.HttpMethod);
      }

      if (!isSelectOnly)
      {
          throw new SqlEndpointMethodNotAllowedException(endpointName, "POST");
      }
  }
  else if (!isSelectOnly)
  {
      if (user.IsReadOnly() || !user.HasScope(Autheris.Domain.Security.TokenAccessScope.WriteScope))
      {
          throw new SecurityException("This token only permits read access; DML statements are rejected.");
      }

      if (!(_options.Value.WebSql?.AllowDmlStatements ?? false))
      {
          throw new SecurityException("DML statements are disabled.");
      }
  }
  ```

#### Layer 2: Route Guards in `SqlEndpointRoutes.cs`

In `src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs`:
1. **`HandleGetEndpoint`:**
   - Before calling `executionService.ExecuteEndpointAsync`, if `httpContext.RequestServices?.GetService<ISqlEndpointRegistry>()` resolves `endpointDefinition`:
     - If `(!string.IsNullOrWhiteSpace(endpointDefinition.HttpMethod) && !string.Equals(endpointDefinition.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)) || !SqlEndpointExecutionService.IsSelectOnlyStatement(endpointDefinition.RawSql)`:
       Set `httpContext.Response.Headers.Allow = "POST";` and write `405 Method Not Allowed` (`ApiProblems.WriteAsync(httpContext, StatusCodes.Status405MethodNotAllowed, "METHOD_NOT_ALLOWED", "This SQL endpoint cannot be executed via HTTP GET.", ct)`), returning before `executionService` is invoked.
   - Pass `httpMethod: "GET"` to `executionService.ExecuteEndpointAsync(name, rawInputs, httpContext.User, tenantId, ct, "GET")`.
   - Catch `SqlEndpointMethodNotAllowedException methodEx` before `SecurityException` and write `405 Method Not Allowed` (`httpContext.Response.Headers.Allow = methodEx.AllowedMethod`).
2. **`HandlePostEndpoint`:**
   - Pass `httpMethod: "POST"` to `executionService.ExecuteEndpointAsync(name, rawInputs, httpContext.User, tenantId, ct, "POST")`.
3. **`HandleRegisterEndpoint`:**
   - Check `bool isSelect = SqlEndpointExecutionService.IsSelectOnlyStatement(request.Sql);`.
   - If `!isSelect && string.Equals(request.HttpMethod?.Trim(), "GET", StringComparison.OrdinalIgnoreCase)`:
     Return `ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Non-SELECT SQL endpoints cannot be registered with HTTP method GET.", context);`.
   - Ensure `request.HttpMethod` (defaulting to `"POST"` when `!isSelect` and `"GET"` when `isSelect`) and `request.TimeoutSeconds` are applied to `definition` even when `endpointLoader != null`.

---

## 6. File-by-File Change Summary

| File | Action | Purpose |
| :--- | :--- | :--- |
| `src/Autheris.Domain/Security/SecurityPrincipalContext.cs` | Modify | In `FromPrincipal`, use `GroupResolutionClaims.Read(principal)` when `HasPipelineMarker(principal)` is true, and `IsMarkedDegraded(principal) ? Degraded : Complete` otherwise. |
| `src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs` | Modify | Add fail-closed `DecisionStep.Precondition` checks (`IDENTITY_INCOMPLETE`, `TENANT_UNRESOLVED`, `GROUPS_DEGRADED`, `WRITE_TRANSPORT_NOT_WIRED`, `TABLE_INACTIVE`) at top of `DecideAsync`; enrich `ExplainAsync` column state mapping over `request.Object.Columns`. |
| `src/Autheris.Application/Services/ConsentResolutionService.cs` | Modify | Populate `TableAccessDecision.Grants` (`DecisionGrantRef`) and `TableAccessDecision.Reasons` (`DecisionReason`: `GRANT_ALLOW`, `ROW_FILTER`, `COLUMN_MASK`, `COLUMN_DENY`, `GRANT_DENY`, `GRANT_EXPIRED`, `NO_GRANT`) without predicate literal values. |
| `src/Autheris.Api/Security/SecurityContextFactory.cs` | Modify | Set `Claims = user.Claims` and `Principal = user` in `BuildContext` (`#733`). |
| `src/Autheris.Api/Endpoints/AuthorizationExplainEndpoints.cs` | Modify | Add named `GrantsListResponse` record for `/api/v1/authz/objects/{objectName}/grants`. |
| `src/Autheris.Application/Sql/Endpoints/Interfaces/ISqlEndpointExecutionService.cs` | Modify | Add optional `string httpMethod = "GET"` parameter to `ExecuteEndpointAsync` and `SqlEndpointMethodNotAllowedException` (`#730`). |
| `src/Autheris.Application/Sql/Endpoints/Services/SqlEndpointExecutionService.cs` | Modify | Add `IsSelectOnlyStatement` AST guard and enforce `GET` read-only / `POST` write-scope rules in `ExecuteEndpointAsync` (`#730`). |
| `src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs` | Modify | Enforce `GET` guard (`405 Method Not Allowed` with `Allow: POST`) in `HandleGetEndpoint`, pass `httpMethod: "POST"` in `HandlePostEndpoint`, and reject `GET` registration of non-SELECT SQL in `HandleRegisterEndpoint` (`#730`). |
| `tests/Autheris.Tests.Unit/Policy/Pdp/AccessExplanationServiceTests.cs` | Modify | Set `GroupResolutionStatus = GroupResolutionStatus.Complete` in `Ctx` helper; add unit test verifying `DegradedExplainSubjectResolver` produces `GROUPS_DEGRADED` through real `AccessDecisionPoint`. |
| `tests/Autheris.Tests.Unit/Security/CallerLabelTests.cs` | Modify | Set `GroupResolutionStatus = GroupResolutionStatus.Complete` in `Subject` helper. |
| `tests/Autheris.Tests.Unit/Configuration/RemovedAuthzKeysTests.cs` | Modify | Add route-table check in `RebacRoutes_Return404`. |
| `tests/Autheris.Tests.Unit/Security/SqlEndpointDmlGetGuardTests.cs` | Create | Unit tests for `#730` covering `GET` rejection (`405`) of `INSERT`/`UPDATE`/`DELETE`/`MERGE`/DDL and `HttpMethod="POST"` endpoints for both write-scoped and read-only tokens, `POST` DML guard for read-only/unscoped tokens (`403`), and registration validation (`400`). |
| `tests/Autheris.Tests.Integration/Security/AuthModeEndToEndTests.cs` | Modify | Add `POST /api/v1/authz/explain` row-filter, mask, and column-state assertions across `Jwt`, `Basic`, and `ForwardAuth` (`#733`). |
| `tests/Autheris.Tests.Integration/Security/CrossTransportAuthorizationParityTests.cs` | Create | `WP-SIMPL-46` §6.3 parity matrix (`19 scenarios x 15 transports/sources`) + principal-less `Degraded` regression test (`SEC-AUTH-04c`). |
| `benchmarks/Autheris.Benchmarks/AccessDecisionPointBenchmark.cs` | Create | `WP-SIMPL-46` §6.4 PDP decision benchmark. |

---

## 7. Red-Green-Refactor TDD & Verification Plan

### 7.1 Local Filtered Unit & Architecture Tests (Single-Command Discipline, `-m:2 -nodeReuse:false`)

1. **Build once:**
   ```bash
   NUGET_PACKAGES=/work/nuget dotnet build Autheris.sln -c Release -p:AutherisIncludeTestAuth=true -m:2 -nodeReuse:false
   ```
2. **Run filtered PDP, Explain, RemovedAuthzKeys, and SQL Endpoint DML GET Guard unit tests:**
   ```bash
   NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --no-build --filter "FullyQualifiedName~AccessExplanationServiceTests|FullyQualifiedName~AccessDecisionContractsTests|FullyQualifiedName~PdpGroupsUnresolvedTests|FullyQualifiedName~CallerLabelTests|FullyQualifiedName~CallerLabelProvenanceTests|FullyQualifiedName~RemovedAuthzKeysTests|FullyQualifiedName~SqlEndpointDmlGetGuardTests|FullyQualifiedName~SqlEndpointsTests|FullyQualifiedName~SqlEndpointDynamicRegistrationTests" -m:2 -nodeReuse:false
   ```
3. **Run filtered Architecture tests (`AuthorizationArchitectureTests`, `ReadOnlyRouteAllowlistTests`, `ReadOnlyTokenGetRouteTests`, `CleanArchitectureTests`, `DocsIntegrityTests`):**
   ```bash
   NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --no-build --filter "FullyQualifiedName~AuthorizationArchitectureTests|FullyQualifiedName~ReadOnlyRouteAllowlistTests|FullyQualifiedName~ReadOnlyTokenGetRouteTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests" -m:2 -nodeReuse:false
   ```
4. **Compile-verify Integration & Benchmark projects locally (execution offloaded to CI for Testcontainers):**
   ```bash
   NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-restore -m:2 -nodeReuse:false
   NUGET_PACKAGES=/work/nuget dotnet build benchmarks/Autheris.Benchmarks/Autheris.Benchmarks.csproj -c Release --no-restore -m:2 -nodeReuse:false
   ```

---

## 8. Acceptance Criteria Checklist

- [ ] `AccessDecisionPoint.DecideAsync` denies any request where `subject.GroupResolutionStatus != GroupResolutionStatus.Complete` (or `GroupResolutionClaims.IsMarkedDegraded(subject.Principal)`) at `DecisionStep.Precondition` with `Code = "GROUPS_DEGRADED"` and message containing `identity.groups_unresolved`, even when `subject.Principal` is `null` (`SEC-AUTH-04c`).
- [ ] `SecurityContextFactory.BuildContext` attaches `Principal = user` and `Claims = user.Claims` so `POST /api/v1/authz/explain` resolves catalog visibility and caller attributes for all REST callers (`#733`).
- [ ] `ConsentResolutionService.ResolveAccess` populates `TableAccessDecision.Grants` and `TableAccessDecision.Reasons` without leaking row-filter predicate values; `AccessDecisionPoint.ExplainAsync` projects effective column states across `request.Object.Columns`.
- [ ] `AuthModeEndToEndTests.Same_user_same_groups_identical_security_context_across_modes` asserts `POST /api/v1/authz/explain` parity (SID, groups, `Complete` status, row filter reason, column mask reason, column states) across `Jwt`, `Basic`, and `ForwardAuth` (`#733`).
- [ ] `CrossTransportAuthorizationParityTests` covers all 19 scenarios (`S1`–`S19`) x 15 transports/sources and compiles/passes with `[Trait("Category", "Security")]`.
- [ ] `GET /api/v1/queries/{name}` rejects any non-`SELECT` statement (`INSERT`, `UPDATE`, `DELETE`, `MERGE`, DDL, unparsable) or `HttpMethod != "GET"` endpoint fail-closed with `405 Method Not Allowed` (`Allow: POST`) before execution for all callers, including write-scoped tokens (`#730`).
- [ ] Zero `WP-`, `SEC-`, or `OD-` strings added to `src/Autheris.Api` or `src/Autheris.Application`; `DocsIntegrityTests` passes with zero absolute URI links in `docs/`.

---

## 9. Phase 3 result (Security Review, `csharp-security-expert`)

- **Date:** 2026-11-14
- **Reviewer:** `arch-planner` (adversarial lens: PDP precondition ordering, explain-endpoint leaks, #730 bypasses, parity-matrix gaps)
- **Baseline:** `main` at `85b0a764`
- **Rule:** Where §2–§5 conflict with a binding condition below, the condition wins. The Hydrogen briefs in §10 already include every condition.

### 9.1 Verdicts per work package

| Work package | Verdict | Blocking conditions |
| :--- | :--- | :--- |
| A. PDP degraded-groups precondition (§2) | **CHANGES REQUIRED** | SEC-46-6-1, SEC-46-6-2, SEC-46-6-3 |
| B. #733 REST explain parity (§3) | **APPROVED WITH CONDITIONS** | SEC-46-6-4 … SEC-46-6-8 |
| C. Cross-transport parity matrix (§4) | **APPROVED WITH CONDITIONS** | SEC-46-6-15 … SEC-46-6-18 |
| D. #730 GET guard for DML SQL endpoints (§5) | **CHANGES REQUIRED** | SEC-46-6-9 … SEC-46-6-14 |

A and D are blocking because the plan code as written would either weaken an existing control (A) or not compile and leave a parser differential (D). B and C need tightening but the design is sound.

### 9.2 Findings

**F-1 (High, A): §2.2 relaxes `SecurityPrincipalContext.FromPrincipal` across all production transports.**
`FromPrincipal` (`src/Autheris.Domain/Security/SecurityPrincipalContext.cs`) currently calls `GroupResolutionClaims.Read(principal)`, which returns `Degraded` for any principal without the pipeline marker. §2.2 would turn every unmarked principal into `Complete`. `FromPrincipal` is called on production paths in 15 files, among them `GraphQlDataSecurityMiddleware`, `WebSocketAuthInterceptor`, `StreamRlsPolicyEnforcer`, `GatewayExecutionService`, `GovernedSqlRewriter`, `ODataQueryPolicyEnforcer`, `AiDataGuardrailService`, `FederatedDuckDbExecutionService`, `DefaultCrossDomainAccessResolver` and `GovernedProcedureExecutionService`. Any of these that receives a principal that skipped `IdentityPipeline` (WebSocket `connection_init`, stream subscribers, background jobs) would treat unverified token groups as complete. That is the exact bypass that `SEC-AUTH-04` closes. Unit-test ergonomics must not be bought with production behaviour.

**F-2 (Medium, A): precondition order leaks table state before identity is checked.**
§2.3 evaluates `TABLE_INACTIVE` (with the table name as the reason ref) before `IDENTITY_INCOMPLETE`, `TENANT_UNRESOLVED` and `GROUPS_DEGRADED`. An unauthenticated or degraded caller can then tell inactive tables from other tables through any surface that echoes the deny message.

**F-3 (Low, A): consent-bypass mode changes behaviour silently.**
Today the bypass path ignores group status. With the new top-of-method checks, bypass mode denies degraded and tenant-less callers. This is the correct fail-closed result, but it must be visible in tests and release notes rather than discovered.

**F-4 (High, B): self-explain projects raw catalog columns and leaks hidden column names.**
`AccessExplanationService.FindObjectAsync` returns the raw `tables.GetTableMetadataAsync` metadata, and the visibility result `visible` is only checked for null. §3.3 then projects **every** column of `request.Object.Columns`, and §3.2 adds `COLUMN_DENY` reasons that carry the column name. Together they list columns that catalog visibility hides from the same caller (hidden flags, DENY masking rules, column-level visibility). This breaks the existing rule "no existence oracle" and the catalog visibility contract (Q-SEC7-3).

**F-5 (Medium, B): the trace exposes deny-consent IDs and mask-rule details to non-privileged callers.**
`GRANT_DENY` / `COLUMN_DENY` / `ROW_DENY` reasons carry deny consent and filter IDs. `maskType = rule.RuleType` may carry rule configuration. For a non-privileged self-explain, the caller may learn *that* a deny applies, but not which governance object implements it.

**F-6 (Low, B): `SubjectResolved` is `true` for a degraded self.**
`AccessExplanationService` line 168 sets `resolved = isSelf || status == Complete`. A degraded caller explaining itself gets `subjectResolved: true` with `groups.status: Degraded`, which is contradictory.

**F-7 (Medium, B): `Principal = user` widens the inputs on REST paths that used to be principal-less.**
After §3.1, `DuckDbOlapEndpoints`, `GovernanceEndpoints.Catalog` and every other consumer of `HttpContext.Items[SecurityPrincipalContext.ItemKey]` start feeding `principal.GetAllUserSids()` and claims-based caller attributes into the PDP. This is the intended parity, but it must be proven by tests rather than assumed.

**F-8 (High, D): §5 references options that do not exist and duplicates the executor's write gate.**
`WebSqlOptions.AllowDmlStatements` does not exist; the real switch is `WebSql.AllowDml` (`GatewayOptions.IsWebSqlDmlAllowed`). `GovernedSqlRewriter` (line 257 ff.) already refuses non-`SELECT` statements unless `isDml && IsWebSqlDmlAllowed`, and refuses DML for `IsReadOnly()` or tokens without `TokenAccessScope.WriteScope`. The §5 Layer 1 copy of these checks is redundant at best and drifts at worst.

**F-9 (High, D): parser differential between the guard and the executed SQL.**
`IsSelectOnlyStatement` classifies `NormalizeForAst(RawSql.Trim())`. `SqlEndpointExecutionService` executes `StripComments(RawSql).Trim().TrimEnd(';').Trim()`, which `GovernedSqlRewriter` parses again. Two classifiers fed two different strings are a classic bypass shape: comment tricks, trailing statements, or dialect constructs that one parse accepts and the other reads differently. The authority must be the statement type that the executor computes for the exact string it executes.

**F-10 (High, D): the real #730 gap is narrower than §5.1 states, and one gap is missing from the plan.**
- Read-only tokens on `GET` are **already** refused for DML by `GovernedSqlRewriter`. §5.1 point 3, second bullet, overstates the issue.
- The real gap is a write-scoped token with `WebSql:AllowDml=true` running DML over `GET`. That breaks HTTP safety and opens a CSRF vector for cookie-backed upstream sessions (ForwardAuth): an `<img src>` can trigger the write.
- **Missing:** `POST /api/v1/queries/{name}` running DML skips `WebSqlEndpoints.RefuseDmlRequestAsync`, so it bypasses the `Idempotency-Key` requirement and the cluster-wide idempotency reservation that `DmlIdempotencyStoreStartupCheck` relies on.

**F-11 (Medium, D): the `Allow` header and registration accept arbitrary stored methods.**
§5.2 sets `Allow` from `definition.HttpMethod`, a free-form stored string. Registration (`request.HttpMethod ?? "GET"`) and file loading accept any value, so the header can carry anything, and a CR/LF value turns into a 500. The registration default `"GET"` is also wrong for DML.

**F-12 (Low, D): 405 vs 404 reveals that an endpoint exists and is DML.**
Any authenticated caller can probe names (`GET /{name}` → 404 or 405), while listing (`GET /api/v1/queries/`) requires list-admin. This is accepted as a residual risk because method semantics need the 405, but the response must not echo the endpoint name or SQL.

**F-13 (Medium, C): the parity matrix misses surfaces and lacks an independent oracle.**
- `AccessTransport` has 16 members. `Streaming` and `Lakehouse` are not in `ParitySurface`, and the declarative SQL endpoints surface (`SqlTransports.SqlEndpoints`, the #730 route itself) is not covered.
- The degraded-groups case is tested only on PDP and OLAP.
- S12's expected result is "denied **or** 0 rows", which is not deterministic.
- The cells are compared against `IAccessDecisionPoint.ExplainAsync`, so a PDP bug makes every surface agree and the matrix passes.

**F-14 (Info): checked and already safe.**
- `IdentityPipeline` strips inbound `GroupResolutionClaims.MarkerClaimType` / `StatusClaimType` claims (`IdentityPipeline.cs:50-51`), and the marker requires `Issuer == "Autheris:IdentityPipeline"`, so a token cannot forge the marker.
- `MERGE`, DDL, table-less statements, table functions and multi-statement input are already refused by `GovernedSqlRewriter` / single-statement parsing.
- PostgreSQL/Oracle reads now run inside `SET TRANSACTION READ ONLY` (SEC-DP-14, `3347e521`), which neutralises volatile-function writes inside a `SELECT` on those engines.

### 9.3 Binding conditions

**A. PDP degraded-groups precondition**
- **SEC-46-6-1** `SecurityPrincipalContext.FromPrincipal` stays strict and calls `GroupResolutionClaims.Read(principal)` unchanged. Drop §2.2. Unit tests that need a `Complete` principal get a **test-only** helper, `tests/Autheris.Tests.Unit/TestSupport/PipelinePrincipals.cs` (`MarkComplete(ClaimsPrincipal)` adds the marker and the `complete` status claim with `Issuer = IdentityClaimIssuers.Pipeline`). No such helper may exist in `src/`. Add the regression test `FromPrincipal_without_pipeline_marker_is_degraded`.
- **SEC-46-6-2** Precondition order in `AccessDecisionPoint.DecideAsync`: `IDENTITY_INCOMPLETE` → `TENANT_UNRESOLVED` → `GROUPS_DEGRADED` (status `!= Complete` **or** `IsMarkedDegraded(subject.Principal)`) → `TABLE_INACTIVE` → write-transport check. Every precondition deny has `EvaluatedByPdp = true`. The `TABLE_INACTIVE` reason ref is `null`; the table name appears only in server logs and audit.
- **SEC-46-6-3** Consent-bypass mode also denies degraded and tenant-less authenticated callers. Pin this with `Consent_bypass_still_denies_degraded_groups` and list it in the PR description as a behaviour change.

**B. #733 REST explain**
- **SEC-46-6-4** `FindObjectAsync` returns the caller-visible projection (`visible`), not the raw metadata, to `ExplainAsync`. The column projection in §3.3 and every reason that names a column are limited to the columns of that projection. Hidden columns never appear in `columns` or `trace`. Privileged explain of a foreign subject may use the raw metadata. Test: `Self_explain_never_lists_catalog_hidden_columns`.
- **SEC-46-6-5** For a non-privileged self-explain, the trace keeps reason **codes** and column names, but `Ref` is `null` for `GRANT_DENY`, `COLUMN_DENY`, `ROW_DENY`, `ROW_FILTER` and `GRANT_EXPIRED`. `maskType` is the mask-kind enum name only (`Kind.ToString()`), never rule parameters or expressions. No predicate literal and no row-filter SQL appears in any explain response. Test: `Self_explain_redacts_governance_object_ids` (assert on JSON, not just `ShouldNotContain("EU")`).
- **SEC-46-6-6** `Grants` and `Reasons` built in `ConsentResolutionService` are surfaced **only** by the explain endpoints and audit. No transport error body, GraphQL `extensions` or OData error may serialise them. Reuse the existing architecture-test style to assert that `DecisionReason` is not referenced from transport error mappers. Allocation cost is gated by the §4.3 benchmark: the cached p95 stays under 5 ms.
- **SEC-46-6-7** `SubjectResolved` = `subject.GroupResolutionStatus == Complete`, also for self.
- **SEC-46-6-8** Attaching `Principal = user` / `Claims = user.Claims` in `SecurityContextFactory.BuildContext` is covered by two tests:
  - `Http_context_security_context_matches_FromPrincipal` (same SIDs, groups, status, caller attributes for one principal through both factories).
  - `Http_context_without_pipeline_marker_stays_degraded` (keeps `IdentityPipelineTests.Principal_without_pipeline_marker_is_degraded` semantics).

**D. #730 GET guard**
- **SEC-46-6-9** The executor's own classification is the authority:
  - Add `bool ReadOnlyRequired` to `GovernedSqlQueryRequest`.
  - `GovernedSqlRewriter` refuses any `metadata.StatementType != SqlStatementType.Select` when it is set, with a new stable code `GovernedSqlErrorCodes.MethodNotAllowed`. The check runs after parsing the exact executed string and before any DML audit reservation or connection open.
  - `SqlEndpointExecutionService.ExecuteEndpointAsync` takes a `SqlEndpointInvocation` enum (`Get`, `Post`; no default value, so every caller must choose) and sets `ReadOnlyRequired = invocation == Get`.
  - Drop the §5.2 Layer 1 duplicate write-scope / `AllowDmlStatements` checks: the rewriter already enforces `IsWebSqlDmlAllowed`, `IsReadOnly()` and `WriteScope`.
- **SEC-46-6-10** The route pre-check in `HandleGetEndpoint` is defence in depth only. It shares **one** classifier with `WebSqlEndpoints.ClassifyStatement`, moved to Application as `SqlStatementClassifier.Classify(string sql)` returning `RequestStatementClass`, and fed the same normalised string the executor uses (`StripComments(RawSql).Trim().TrimEnd(';').Trim()`). `Unparsable` and `Write` → 405. Cache the class per `SqlEndpointDefinition` (keyed by name and SQL hash) so a GET never parses twice.
- **SEC-46-6-11** `GET` on an endpoint whose stored method is not `GET`, or whose class is not `Read` → `405`, `Allow: POST` (constant), uniform body `"This SQL endpoint cannot be executed via HTTP GET."`. No endpoint name, no SQL. This applies to every caller, including write-scoped tokens and `ClusterAdmin`.
- **SEC-46-6-12** `POST /api/v1/queries/{name}` running a `Write`-class endpoint goes through the same request guard as WebSQL:
  - Extract `RefuseDmlRequestAsync` into a shared `DmlRequestGuard`.
  - Require `Idempotency-Key` and a canonical caller identity.
  - Reserve in the cluster-wide store.
  - Disabled DML still answers through the executor refusal.
- **SEC-46-6-13** Registration and file loading normalise `HttpMethod` with `Trim().ToUpperInvariant()` and accept only `GET` or `POST`, otherwise `400`.
  - The default is `POST` for a `Write` class and `GET` for `Read`.
  - `GET` + `Write`, or any `Unparsable` SQL → `400`.
  - The loader path (`endpointLoader.ParseSqlContent`) applies the same rule, so the request `HttpMethod` cannot be dropped silently.
  - Endpoints loaded at startup that break the rule are logged and not registered (fail closed).
- **SEC-46-6-14** Tests in `SqlEndpointDmlGetGuardTests`:
  - `Get_on_dml_endpoint_returns_405_for_write_scoped_token`, `Get_on_dml_endpoint_returns_405_for_cluster_admin` and `Get_on_post_method_endpoint_returns_405`.
  - `Get_405_body_does_not_echo_endpoint_name`.
  - `Executor_refuses_dml_when_read_only_required_even_if_route_check_skipped`, which calls the service directly.
  - `Comment_and_trailing_statement_variants_classified_identically_by_guard_and_executor`, covering `/* */`, `--`, trailing `;`, `; DELETE …`, a CTE wrapping DML, and mixed-case verbs.
  - `Post_dml_without_idempotency_key_returns_400`.
  - `Register_get_with_dml_returns_400`, `Register_unknown_method_returns_400` and `Register_dml_without_method_defaults_to_post`.

**C. Parity matrix**
- **SEC-46-6-15** `ParitySurface` covers every `AccessTransport` member except `Internal`. Add `Streaming` (`StreamRlsPolicyEnforcer`), `Lakehouse`, and `SqlEndpointsGet` (`GET /api/v1/queries/{name}`, `SqlTransports.SqlEndpoints`). `Matrix_complete_for_all_transports_and_sources` asserts the set relation against `Enum.GetValues<AccessTransport>()`, not a literal 15, so a new transport fails the test until it is added.
- **SEC-46-6-16** Add scenarios `S20_DegradedGroups` (`GROUPS_DEGRADED`), `S21_TenantUnresolved` (`TENANT_UNRESOLVED`) and `S22_ForeignTenantObject` (uniform not-found / deny) to **every** surface, not only PDP and OLAP.
- **SEC-46-6-17** Every scenario has one deterministic expected outcome. S12 is `Allowed = false` with reason code `CALLER_ATTRIBUTE_UNRESOLVED` (or the existing virtual-filter fail-closed code; pick one and pin it). Surfaces with a reduced projection (Envoy allow/deny, Discovery column states) use a declared projection function per surface, never a skipped assertion.
- **SEC-46-6-18** The oracle is the hard-coded expected value in the scenario table: rows, column states, grant count, reason code. `ExplainAsync` is compared as one more surface, not as the reference.

### 9.4 Residual risks (accepted)

- **F-12:** endpoint-existence and DML-class probing via 405 for authenticated callers. The body is uniform and listing stays admin-only.
- **SQL Server, SQLite, Databricks:** volatile side effects inside a `SELECT` rely on the read-only login and the `SqlFunctionPolicy` denylist. SEC-DP-14 only covers PostgreSQL and Oracle.

---

## 10. Hydrogen task briefs

Both briefs: worktree from `main`, `NUGET_PACKAGES=/work/nuget`, English only, strict Red-Green-Refactor, **filtered** unit tests locally with `--blame-hang --blame-hang-timeout 120s -m:1 -nodeReuse:false`, integration and benchmark projects compile-only (`--no-dependencies`), full suites in CI. No `WP-`, `SEC-` or `OD-` strings in `src/Autheris.Api` / `src/Autheris.Application`. Report to `@coordinator` with commit, test counts, and any deviation.

### 10.1 PR 6a: PDP precondition + #733 principal attachment and explain

- **Branch / worktree:** `feat/simpl-46-pr6a-pdp-explain` in `/work/wt/simpl-46-pr6a`.
- **Scope (files):**
  - `src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs`
  - `src/Autheris.Application/Policy/Pdp/AccessExplanationService.cs`
  - `src/Autheris.Application/Services/ConsentResolutionService.cs`
  - `src/Autheris.Api/Security/SecurityContextFactory.cs`
  - `src/Autheris.Api/Endpoints/AuthorizationExplainEndpoints.cs`
  - tests listed below
  - **Do not touch** `SecurityPrincipalContext.FromPrincipal` (SEC-46-6-1).
- **Steps:**
  1. **Red:** add `tests/Autheris.Tests.Unit/TestSupport/PipelinePrincipals.cs` (test-only `MarkComplete`).
     Add failing tests:
     - `FromPrincipal_without_pipeline_marker_is_degraded`
     - `Principal_less_degraded_subject_denied_with_GROUPS_DEGRADED`
     - `Preconditions_evaluated_identity_tenant_groups_before_table_state`
     - `Consent_bypass_still_denies_degraded_groups`
  2. **Green:** implement the precondition block in `DecideAsync` in the SEC-46-6-2 order. Every deny gets `EvaluatedByPdp = true` and a `DecisionReason`; `TABLE_INACTIVE` has `Ref = null`.
  3. Fix existing unit tests that relied on unmarked principals by using `PipelinePrincipals.MarkComplete` or `GroupResolutionStatus = Complete` (`AccessExplanationServiceTests.Ctx`, `CallerLabelTests.Subject`, etc.). Never relax production code.
  4. Set `Principal = user` and `Claims = user.Claims` in `SecurityContextFactory.BuildContext` (§3.1), with the SEC-46-6-8 tests.
  5. Populate `Grants` and `Reasons` in `ConsentResolutionService` (§3.2), applying SEC-46-6-5 and SEC-46-6-6.
  6. `ExplainAsync` column projection (§3.3) over the **visible** projection (SEC-46-6-4); mask kind only.
     `AccessExplanationService`:
     - pass `visible` to `ExplainAsync`;
     - redact refs for non-privileged self-explain;
     - set `SubjectResolved` from group status (SEC-46-6-7).
  7. Add the `GrantsListResponse` record (§3.4).
  8. Extend `AuthModeEndToEndTests.Same_user_same_groups_identical_security_context_across_modes` (§3.5). Assert on parsed JSON that:
     - no `ref` is present for deny and filter reasons;
     - hidden columns are absent;
     - `NoGrant` → 404.
- **Local filter:** `AccessDecisionPoint|AccessExplanationService|ConsentResolutionService|PdpGroupsUnresolved|SecurityContextFactory|CallerLabel|IdentityPipelineTests|SecurityPrincipalContext`.
- **Done when:** the filtered unit tests are green, the integration project compiles, and the architecture tests (`AuthorizationArchitectureTests|CleanArchitectureTests|DocsIntegrityTests`) are green.

### 10.2 PR 6b: #730 GET guard + parity matrix tests

- **Branch / worktree:** `feat/simpl-46-pr6b-sqlendpoint-parity` in `/work/wt/simpl-46-pr6b`. It may start in parallel with 6a, but the parity-matrix part (steps 6–8) is rebased onto 6a before merge.
- **Scope (files):**
  - `src/Autheris.Application/Sql/Endpoints/**`
  - `src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs`
  - `GovernedSqlQueryRequest`
  - new `src/Autheris.Application/Sql/SqlStatementClassifier.cs`
  - new `DmlRequestGuard` (Api)
  - `src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs`
  - `src/Autheris.Api/Endpoints/WebSqlEndpoints.DmlGuard.cs` (extraction only, no behaviour change)
  - tests listed below
  - Do not touch PDP or explain files (6a).
- **Steps:**
  1. **Red:** add `tests/Autheris.Tests.Unit/Security/SqlEndpointDmlGetGuardTests.cs` with all SEC-46-6-14 tests.
  2. **Green (authority):** add `ReadOnlyRequired` to `GovernedSqlQueryRequest`. `GovernedSqlRewriter` refuses non-`Select` with `GovernedSqlErrorCodes.MethodNotAllowed` when it is set. Add the `SqlEndpointInvocation` parameter (no default) to `ExecuteEndpointAsync` and update all callers (SEC-46-6-9).
  3. Move `ClassifyStatement` to `SqlStatementClassifier` in Application. `WebSqlEndpoints` calls it, and the existing WebSQL tests stay green. Cache the class per endpoint definition (SEC-46-6-10).
  4. `HandleGetEndpoint`: defence-in-depth pre-check and map `MethodNotAllowed` → `405` with the constant `Allow: POST` and the uniform body (SEC-46-6-11).
     `HandlePostEndpoint`: the shared `DmlRequestGuard` (Idempotency-Key and reservation) for `Write`-class endpoints (SEC-46-6-12).
  5. Registration and loader method normalisation and validation (SEC-46-6-13).
  6. Add `tests/Autheris.Tests.Integration/Security/CrossTransportAuthorizationParityTests.cs` per §4.1, amended by SEC-46-6-15 … 18:
     - surfaces derived from `AccessTransport` plus `SqlEndpointsGet`;
     - S1–S22;
     - hard-coded expected outcomes;
     - a projection function per reduced surface.
  7. Route-table assertion in `RemovedAuthzKeysTests.RebacRoutes_Return404` (§4.2).
  8. `benchmarks/Autheris.Benchmarks/AccessDecisionPointBenchmark.cs` (§4.3).
- **Local filter:** `SqlEndpointDmlGetGuard|SqlEndpointsTests|SqlEndpointDynamicRegistration|WebSqlDml|GovernedSqlRewriter|RemovedAuthzKeys|SqlStatementClassifier`.
- **Done when:** the filtered unit tests are green, the integration and benchmark projects compile (the parity matrix runs in CI), the architecture tests (`ReadOnlyRouteAllowlistTests|ReadOnlyTokenGetRouteTests|CleanArchitectureTests|DocsIntegrityTests`) are green, and the CI parity job is green.
