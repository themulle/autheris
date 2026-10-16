# Implementation Plan: REQ-GOV-113 - Access Requests with Read-Only Tokens and Approver-Chosen Row Filters

**Document ID:** `PLAN-GOV-113` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-GOV-113, section 4](2026-10-16-req-issues-111-115-catalog-access-analytics.md) (FR-113-1..14, SI-113-1..6, AC 1..10, Q-113-1..5, section 8 owner decision Q-113-2)
**Branch:** increment A `feat/issue-113-a-self-service-requests`, increment B `feat/issue-113-b-approval-configuration` (both from `main` after their predecessors, see section 5); planning base `c78edbf5`
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement `REQ-GOV-113`; delivery tracker [PLAN-DELIVERY-W23](2026-10-16-plan-waves-2-3-delivery.md) (step P-8, implementation steps I-22 and I-24)
**Status:** PROPOSED (Phase 2 only; Phase 3 security review pending, a separate verdict is required for increment A and for increment B)

**Binding rules applied:** the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server is extended in place, the `0001` checksum constants stay unchanged, the shape snapshots under `src/Autheris.Infrastructure/Persistence/Migrations/Shape/` and their tests are updated (the way #107 added `TABLE_ROW_FILTER_STRATEGIES`); no upgrade or repair migrations; fail closed; rights are checked by the backend from the authenticated principal, never from request content or the calling agent; **an agent must never widen its own reach**: a read-only token may only request and withdraw, never decide; **owner decision Q-113-2: two separate scopes**, `data.access.request` (file and withdraw a request; this is what `SelfServiceScopes` lists) and `data.access.review` (decide a request; required in addition to the approver role), separating requesting from deciding (four-eyes); Q-113-1 (no app-only tokens), Q-113-3 (templates only for non-admin owners), Q-113-4 (templates via admin API plus manifest, manifest wins), Q-113-5 (requester-group template derived at approval time, recorded in the audit) follow the PRD recommendation; every approval created by this plan goes through `IGrantPolicy`; Phase-4 developer is Sonnet, **one sequential developer**, A first, B after #107.

---

## 1. Verification of the PRD Against the Code (`c78edbf5`)

### 1.1 Read-only tokens and the mutation gate

| PRD claim | Result |
|---|---|
| `requestTableAccess` is a mutation rejected for read-only tokens | **Confirmed.** `Mutation.RequestTableAccessAsync` (`src/Autheris.GraphQL/Types/MutationTypes.cs:156`). `ReadOnlyOperationMiddleware.InvokeAsync` (`src/Autheris.GraphQL/Interceptors/ReadOnlyOperationMiddleware.cs:27-40`) rejects any document where `IsMutation(document, operationName)` is true and the principal `IsReadOnly()` with `READ_ONLY_TOKEN`. `IsMutation` (`:43-47`) looks at **operation types only**; it does not look at root fields. Without an operation name every mutation counts (fail closed). The middleware is registered in the GraphQL pipeline at `GatewayServiceCollectionExtensions.cs:581`. |
| Read-only classification | **Confirmed with a trap the PRD does not mention.** `EntraTokenPolicy.Apply` (`src/Autheris.Api/Security/EntraTokenPolicy.cs:82-87`) marks a delegated token read-only only when **all** its scopes are in `ReadOnlyScopes` (`scopes.All(...)`, default `Agent.Read`, `App.Read`: `JwtOptions.cs:190-195`). A token that carries `Agent.Read` **plus** an unlisted scope such as `data.access.request` is therefore **not read-only at all** and keeps every right of the person. Adding `data.access.request` to the end of a token naively would silently turn an agent token into a full write token (A-1 fixes this). App-only tokens are classified at `:47-80` (default `AppOnlyTokenAccess.ReadOnly`, `JwtOptions.cs:198`). |
| Marker cannot be forged | **Confirmed.** `TokenAccessScope` (`src/Autheris.Domain/Security/TokenAccessScope.cs:12-34`): fixed `Issuer`, checked in `IsReadOnly` (`:24-25`). MCP strips the marker from forwarded claims (`McpEndpoints.cs:221-235`, `McpProtocolHandler.cs:387-399`) and carries the fact as `McpSessionContext.IsReadOnly` (`src/Autheris.Domain/Model/McpModels.cs:54`). |
| HTTP layer | `ReadOnlyTokenMiddleware` (`src/Autheris.Api/Middleware/ReadOnlyTokenMiddleware.cs:56-97`) lets read-only tokens POST to the GraphQL path, `/api/v1/sql`, `/api/sql`, `/api/v1/queries/{name}`, `/api/v1/export/arrow`; everything else is `READ_ONLY_TOKEN`. No REST change is needed for A. REST mutation paths (`/api/v1/consents/bulk` etc.) stay closed to read-only tokens. |
| MCP tools for requests | **None exist.** `McpToolRegistry` registers dataset, admin, doc and semantic tools only (`McpToolRegistry.cs:134-152`). The MCP `query_graphql` path accepts queries only (`GatewayMcpQueryExecutor.cs:409`), curated tools with a mutation target are rejected for read-only principals (`:172-180`). An agent working over MCP therefore **cannot file a request today even with a self-service scope**; increment A adds two MCP tools (A-9). |
| `myAccessRequests`, `pendingApprovalsForMe` | **Confirmed**, `QueryTypes.cs:100` and `:152`. Both are queries (usable by read-only tokens). `pendingApprovalsForMe` lists only `PENDING` and `PENDING_SECOND_APPROVAL` requests of tables the caller **owns or has a delegation on** (`SqliteGovernanceRepository.Consent.cs:984-1008`, PG/SQL Server analogues); **admins without ownership see nothing** there, and the query has no tenant predicate. Both map to `ConsentRequestDto` (`src/Autheris.Domain/Model/ConsentDtoModels.cs:9-21`) which has no `context` field. |
| `withdraw` mutation | **Confirmed absent.** `grep -rn "withdraw" src` finds no consent request withdrawal; no `WITHDRAWN` status exists. Status values in use: `PENDING`, `PENDING_SECOND_APPROVAL`, `PENDING_EXTERNAL_APPROVAL`, `APPROVED`, `REJECTED` (`ConsentApprovalPolicy.cs:12-15`). **The PRD's `SUBMITTED` and `ESCALATED` are never written**; they only appear in a status comment (`GovernanceModels.cs:42`) and in a read-side check (`CatalogVisibility.cs:46-53`, which treats any status starting with `PENDING` as pending, so a new `WITHDRAWN` is correctly **not** pending). |

### 1.2 Submission, subject resolution, grant policy

| Item | Result |
|---|---|
| Single service | `ConsentRequestService.SubmitAsync` (`src/Autheris.Application/ConsentRequests/ConsentRequestService.cs:56-213`): input validation `:60-80`, tenant `:84,104`, idempotency (fingerprint over tenant, table, justification, duration `:86-93`, key space per user and operation `:289-303`), unknown table answers neutral `FORBIDDEN` (`:96-101`), **`IGrantPolicy` evaluated at submission** with `GrantKind.Consent`, `GranteeType.User` and the table class (`:106-120`), ITSM hand-off `:122,153-203`, insecure auto-approve `:140-151`. The request carries no `context`. |
| **Subject key is unsafe for tokens** | **Confirmed (same finding as PLAN-GOV-129).** The mutation resolver takes the requester from `GetAuthenticatedUserSid` (`MutationTypes.cs:50-69`) which calls `GetUserSid()` (`src/Autheris.Domain/Common/Sid.cs:21-36`); that method falls back to `sub`, **`appid`, `client_id`, `azp`** (`:32-34`). An app-only token would be filed as a request **by its application id**. `IIdentitySubjectResolver.ResolveSubject` (`src/Autheris.Application/Services/IdentitySubjectResolver.cs:10-80`) marks `idtyp=app` or an app id without a human claim (upn/email/preferred_username/unique_name) as `SubjectType.ServicePrincipal`. Immutable person claims are listed in `Sid.cs:41-52` (`GetAllUserSids`, `:110-135`); app ids are deliberately excluded there. Q-113-1 therefore needs a **person-subject gate**, not `GetUserSid()` (A-2). |
| Grant policy on all paths | `IGrantPolicy` is used at: `ConsentRequestService.cs:108` (submission), the GraphQL approval at **activation time** (`MutationTypes.cs:313-337`, role/group grantees skip it inside the policy), `BundledConsentRequestService.cs:163`, `AccessPlanningService.cs:69`, `DeclarativeConsentApplyService.cs:121`, `ConsentRecertificationWorkflowService.cs:192`, bulk profile assignment (`GovernanceEndpoints.Access.cs:219`). **Not evaluated:** the ITSM activation (`ActivateConsentCoreAsync`, `SqliteGovernanceRepository.Consent.cs:743-870`: copies `requested_valid_to` fixed at submission, which the submission evaluation already bounded) and the insecure auto-approve activation. Neither is changed here except that B routes their configuration through the proposal service (A-17); the submission evaluation remains their bound. |
| ITSM mode | With `Itsm.Enabled` and a dispatcher, requests start as `PENDING_EXTERNAL_APPROVAL` (`:133`) and can be approved **only** by the signed ITSM webhook (`ConsentApprovalPolicy.EnsureApprovableStatus`, `:100-112`). The GraphQL approval and `data.access.review` never apply to them. |

### 1.3 Approval, four-eyes, approver roles

| Item | Result |
|---|---|
| Approval flow | GraphQL `ApproveConsentRequestAsync` (`MutationTypes.cs:222-370`): load request `:248`; tenant check `:263-270`; **self-approval** `req.RequesterSid == approverSid || IsSameIdentity(...)` (`:273`, identity set from `ExtractPrincipalIdentifiers`, `ConsentRequestService.cs:236-267`, `MutationTypes.cs:720-742`); approver authorization `:277-291`: `GovernanceAdmin` or canonical ClusterAdmin **claims** (`:259`) or `IsAuthorizedApproverForTableAsync` (data owner or valid delegate or DB role `GovernanceAdmin`/`ClusterAdmin`, `SqliteGovernanceRepository.Catalog.cs:958-990`). There is **no check of any scope and no check of a read-only flag** in this path (blocked today only because the whole mutation is rejected for read-only tokens). |
| Repository step | `ApproveConsentRequestStepAsync` (`SqliteGovernanceRepository.Consent.cs:1047-1163`, PG `:1153`, SQL Server analogue): re-checks status (`ConsentApprovalPolicy.EnsureApprovableStatus`), self-approval by SID and by data owner id (`:1056-1088`), authorization, four-eyes (`requires_four_eyes` or sensitivity rank >= 3 means two different approvers, `ConsentApprovalPolicy.cs:119-132`), inserts `APPROVAL_STEPS` and updates the status in one transaction with an audit entry `CONSENT_APPROVAL_STEP` (`BuildStepAudit`, `ConsentApprovalPolicy.cs:137-155`). The `UPDATE CONSENT_REQUESTS SET status` has **no `AND status = @expected` guard** (SQLite relies on the write scope, PG on `FOR UPDATE` at `PostgreSqlGovernanceRepository.Consent.cs:1210`). |
| **Consent creation is a separate transaction (correction of the PRD)** | The PRD says approval "activates the consent created at submission". **In the GraphQL path the consent is created after the step**, by a second call `consentRepository.CreateConsentAsync` (`MutationTypes.cs:293` then `:356`) with the metadata column snapshot (`ConsentColumnSnapshot.FromMetadata`, `src/Autheris.Domain/Model/ConsentColumnSnapshot.cs:12-26`: sensitive or masked columns `Mask`, others `Clear`) and **no row filter**. The step is committed before; a failure in between leaves a request `APPROVED` without a consent, and `ActivateConsentAsync` (only the ITSM/auto path) would then create one **without filters**. SI-113-4 (atomic activation) is **not met today** and is a precondition of increment B (A-13). ITSM activation creates its consent inside the step's activation transaction with the same snapshot and no filters (`ActivateConsentCoreAsync`, `:743-870`). |
| `CONSENT_ROW_FILTERS` | Written only by `InsertConsentCoreAsync` (`:1344-1380`, three engines; called from `CreateConsentAsync` `:1282-1297`, which also bumps the table epoch in the same transaction and then calls `InvalidateEpochAsync`). Immutable per consent; carries the #107 key-set columns (`ConsentModels.cs:41-70`). Write-time validation does **not** exist for filters: the manifest path copies fields as given (`DeclarativeConsentApplyService.cs:216-236`); the typed builder validates at **read time** (`TypedRowFilterBuilder.cs:24-27` operator allowlist, identifier regex `:22-23,144-147`, JSON value check `:157-165`). A `USER_ATTRIBUTE` source compiles to a constant (`1 = 0` / DENY polarity, `:137-141`) until #129. |
| Approver roles | `EndpointSecurity.IsApprover` (`DataSteward`, `DataOwner`, `GovernanceAdmin`, `ClusterAdmin`; `src/Autheris.Api/Endpoints/EndpointSecurity.cs:73`) is used by the HitL endpoints only, **not** by consent approval. "Approver role" for a consent request means: owner or delegate of the table, or `GovernanceAdmin`/canonical ClusterAdmin. |
| Notifications | **None.** No notification component exists for consent requests; approvers poll `pendingApprovalsForMe`; the only outbound channel is the ITSM dispatcher. This plan adds no notification mechanism (the PRD asks for none); the new `context` is shown through the existing queries. |
| Audit | No Tier-A event is written for submission by the service or the resolver (`CONSENT_REQUESTED` is used only by HitL/plan endpoints, `GovernancePlanEndpoints.cs:43`, `HitLEndpoints.cs:52`). The repository writes `CONSENT_APPROVAL_STEP` and `CONSENT_GRANTED` inside the transaction (`InsertConsentCoreAsync` end; `RecordAuditEventInternalAsync`). `AuditPolicyTable.cs:59-64` makes the consent events `FailClosed`; new event types need an entry. |
| Time-bound grants | `IGrantPolicy`/`GrantPolicy` (`src/Autheris.Application/Governance/Grants/GrantPolicy.cs:11-80`): individual (User/ServicePrincipal) grants need an explicit expiry within the class maximum; group and role grants are standing; any exception denies (`:19-27`). |

### 1.4 PRD gaps found (closed by this plan)

| # | Gap | Resolution |
|---|---|---|
| G-1 | The scope classification trap (1.1): `Agent.Read data.access.request` would be a **full** token | A-1: the read-only test becomes "all scopes in `ReadOnlyScopes` union `SelfServiceScopes`"; tests 1-3 |
| G-2 | `IsMutation` is operation-type based; a closed field allowlist needs root-field analysis (aliases, fragments, several operations) | A-3: `MutationRootFieldClassifier`, fail closed on anything not a plain allowlisted field |
| G-3 | Nothing stops a read-only token from **deciding** once `requestTableAccess` is let through; the approve mutation has no scope check | A-4: decision guard (review scope, person subject, not read-only, no request scope on the same token) in the pipeline **and** in the resolvers |
| G-4 | `GetUserSid()` falls back to the app id | A-2: person-subject gate with the immutable claim list, no `appid`/`client_id`/`azp` |
| G-5 | Group templates (Q-113-5) need the requester's groups **at approval time**; the approver's token has the approver's groups, and the gateway has no directory lookup (groups come from token claims only, `ClaimsNormalizer.cs:128-140`) | A-15: resolution through the server-side role/group membership store (`IDataOwnershipRepository.GetTransitiveRolesAsync(requesterSid)`, `IGovernanceRepository.cs:247`, no application caller today); no match means no group template (narrower default), ambiguity needs an explicit choice |
| G-6 | Approval is not atomic with consent creation | A-13: one repository transaction for the final step and the consent (B) |
| G-7 | PRD statuses `SUBMITTED`/`ESCALATED` do not exist; `PENDING_EXTERNAL_APPROVAL` belongs to the change board | A-6: withdrawable states are exactly `PENDING` and `PENDING_SECOND_APPROVAL` |
| G-8 | `pendingApprovalsForMe` is owner-based, tenant-blind and hides requests from admins | not changed in A (out of scope); the approver inbox gains `context`; a tenant predicate is added (T-A-14) because the new free text must not cross tenants |
| G-9 | PRD FR-113-9 lists "dataset template, then group template"; a dataset default can never be more specific than a group-bound template | A-15: group-bound match first, then dataset default, then built-in default (owner flag O-3) |
| G-10 | ITSM and auto-approve activation create consents without any configuration (no filter) | A-17: they apply only the dataset-default template (never free-form, never a group template), else the built-in snapshot (owner flag O-4) |

---

## 2. Architecture Decisions

### Increment A: self-service request and withdraw

#### A-1 Token classes and the two scopes (Q-113-2)
Three new options on `EntraIdAuthOptions` (`JwtOptions.cs`, next to `ReadOnlyScopes`): `SelfServiceScopes` (nullable list, empty or null = **feature off**, sample value `["data.access.request"]`), `ReviewScopes` (nullable list, sample `["data.access.review"]`). No default values for `SelfServiceScopes` (PRD FR-113-1); `ReviewScopes` null means `["data.access.review"]` **only when the review requirement is active** (below).

`EntraTokenPolicy.Apply` changes in the delegated branch only:
1. `readOnlyOrSelf = ReadOnlyScopes ∪ SelfServiceScopes`; the token is read-only when `scopes.All(in readOnlyOrSelf)` and the union is non-empty (today: `ReadOnlyScopes` only). A token that has **any scope outside the union** keeps today's behaviour (full token, **not** read-only, no self-service marker needed).
2. When the token is read-only, `scopes.Any(in SelfServiceScopes)`, `scopes.Count > 0` (delegated) **and** the subject is a person (A-2), the policy adds the self-service marker (`TokenAccessScope.MarkSelfService`, fixed issuer, same pattern as `MarkReadOnly`, new constant `autheris:access_self_service`). The marker is **additive to** `autheris:access_mode=read`: the token stays read-only.
3. The review requirement (`ReviewScopeRequired`) is active when `SelfServiceScopes` is non-empty **or** `ReviewScopes` is explicitly non-empty. Off by default so existing deployments are unchanged (owner flag O-1).

Startup validation (`GatewayEmptyListConfigurationValidator` pattern plus a new `SelfServiceScopeConfigurationValidator`; fails the start): scope names trimmed and non-empty; **no scope appears in both `SelfServiceScopes` and `ReviewScopes`**; **no review scope appears in `ReadOnlyScopes`** (a read-only review token is a contradiction); if `SelfServiceScopes` is non-empty, the effective review scopes are non-empty. An explicit empty `SelfServiceScopes: []` is logged like `ReadOnlyScopes` (Warning, feature off).

#### A-2 Person-subject gate (Q-113-1)
New `PersonSubject.TryResolve(ClaimsPrincipal, IIdentitySubjectResolver)` in Application returning the requester `Sid` or failure: requires `Identity.IsAuthenticated`, `ResolveSubject(...).Type == SubjectType.User`, **and** at least one claim of `Sid.cs`'s immutable user list (`ImmutableUserSidClaimTypes`, `:41-52`). The requester `Sid` is the first immutable claim in the same order as `GetUserSid()` **excluding** `appid`/`client_id`/`azp` (new `ClaimsPrincipalExtensions.GetPersonSid()` next to `GetUserSid`; `GetUserSid()` itself is **not** changed, it has many callers; the #129 plan introduces its own `CallerSubjectFactory`, the two must agree on the claim order, test 9). Failure (app-only, service principal, certificate subject, no immutable claim) means **no self-service** and `FORBIDDEN` with the neutral text `Access requests require a signed-in person.`; delegated tokens that carry no human claim (no upn/mail/preferred_username/unique_name, so the resolver classifies them as service principal) are rejected too (fail closed, documented in Phase 6). The gate applies to: a **read-only** principal on `requestTableAccess`, `withdrawMyAccessRequest` and the MCP tools. For full tokens the existing behaviour (`GetUserSid()`) is kept for `requestTableAccess` (no regression for existing clients) but the gate result is **also** required to be present when the token is app-only (`AppOnlyTokens=ReadWrite`): an app-only full token filing requests is out of scope and rejected as `FORBIDDEN` (hardening, owner flag O-2).

#### A-3 Closed allowlist in the GraphQL pipeline (FR-113-2, PRD veto 1)
`ReadOnlyOperationMiddleware` keeps its place and contract; before returning `READ_ONLY_TOKEN` it asks the new `MutationRootFieldClassifier` (Application-free, `Autheris.GraphQL`):
- Selected operation: by `OperationName`; if the name is null and the document has more than one operation, **all** operations are considered (as today); an operation name that matches no definition counts as "unknown" and the request is rejected.
- Root selections are collected by walking the selection set of each mutation operation: plain `FieldNode` (use **`Name`, never the alias**), `InlineFragmentNode` (a type condition other than `Mutation` or absent is rejected), `FragmentSpreadNode` resolved against the document's fragment definitions with a visited set and depth limit 8 (cycle, missing fragment, or depth overflow rejects). `__typename` is ignored. Directives `@skip`/`@include` do not change classification (a conditionally skipped field is still counted).
- The document is **self-service-only** iff every collected root field name is in the **code constant** `{ requestTableAccess, withdrawMyAccessRequest }` (case-sensitive ordinal) **and** at least one exists. A single other field makes the whole document `READ_ONLY_TOKEN`.
- The exception requires `principal.IsReadOnly()` **and** `TokenAccessScope.IsSelfServiceRequester(principal)` **and** the person gate (A-2). Without the marker, behaviour is exactly as today.
- Adding a name to the constant is a code change with a security review (FR-113-2); a source-level test pins the set.
Subscriptions are untouched (the classifier only looks at mutation operations). GraphQL batching: each batch item is its own `RequestContext` through the same middleware; there is no shared state.

#### A-4 Decision guard (G-3, review scope)
New `ConsentActorPolicy` (Application, pure, injectable; options snapshot of `EntraIdAuthOptions` plus `IIdentitySubjectResolver`):
`Evaluate(ClaimsPrincipal, ConsentAction { Request, Withdraw, Decide })`. For `Decide` (approve, reject; in B also configuration confirmation) it denies (`FORBIDDEN`, code `DECISION_NOT_PERMITTED`) when **any** holds:
1. the principal is read-only (`IsReadOnly()`), whatever its scopes;
2. the subject is not a person (A-2) (a service principal never decides: owner flag O-2);
3. `ReviewScopeRequired` and the token is scope-bearing (`scp` present, `TokenAccessScope.GetScopes(principal).Count > 0`) and carries **no** scope of the effective review list; tokens without `scp` (Basic, Kerberos, ForwardAuth, ADFS: they cannot carry the scope) are not subject to 3 but still to 1 and 2;
4. the token carries **both** a self-service scope and a review scope (mixed token; requesting and deciding never share a token; owner flag O-5).
It is evaluated at two points: (a) in the new `ConsentDecisionMiddleware` registered **next to** the read-only middleware (it uses the same root-field walk: a mutation operation containing `approveConsentRequest` or `rejectConsentRequest`, plus the B fields, runs the policy before any resolver); (b) inside the resolvers `ApproveConsentRequestAsync` and `RejectConsentRequestAsync` at the top, by resolving `IConsentActorPolicy` from `HttpContext.RequestServices` (precedent: `MutationTypes.cs:313-320` resolves `IGrantPolicy` this way). (b) is defence in depth for callers that bypass the pipeline (tests, future in-process callers); a missing service in (b) is a composition error caught by a DI composition test, not a silent allow. The policy **adds to**, never replaces, the existing checks (ownership/admin authorization, self-approval by identity set, repository re-checks, four-eyes).
Self-approval stays as today (`MutationTypes.cs:273`, repository `:1056-1088`); the policy adds nothing there but test 6 pins it for the new token classes. Requesting and deciding are therefore separated twice: by scope (token level) and by person (identity level).

#### A-5 Request record: `context`, token class, withdrawal
`CONSENT_REQUESTS` (baseline in place) gets `context` (nullable, max 500 stored, option cap 200), `withdrawn_at` (nullable), `submitted_via` (`full` or `self-service`, fixed vocabulary). `ConsentRequest` (`GovernanceModels.cs:33`) gets `Context`, `WithdrawnAt`, `SubmittedVia` (init). Repositories write them in `CreateConsentRequestAsync` and read them in `GetConsentRequestAsync`, `GetRequestsByRequesterSidAsync`, `GetPendingRequestsForApproverAsync` (three engines). `ConsentRequestDto` gets `Context`, `WithdrawnAt`. `context` is plain text: control characters rejected, trimmed, length-capped (`MaxContextLength`, default 200), **never** used for routing, authorization, approver selection or SQL; output through GraphQL is JSON-escaped; the audit stores only its length (SI-113-6). It is part of the idempotency fingerprint (`ConsentRequestService.cs:86-88`, append `context`; a key reuse with another context is a different request, not a replay).

#### A-6 `withdrawMyAccessRequest` (FR-113-3)
New `IConsentWithdrawalService.WithdrawAsync(requestId, principal, tenant)` (Application). Rules, in this order, all tenant-scoped: person gate (read-only principals need the self-service marker, A-3); load the request; unknown id, foreign tenant, or a request whose `RequesterSid` is **not** in `principal.GetAllUserSids()` all answer the **identical** `NOT_FOUND` (same message, no oracle); status must be `PENDING` or `PENDING_SECOND_APPROVAL`, else `ACCESS_REQUEST_NOT_PENDING` (this includes `PENDING_EXTERNAL_APPROVAL`: the ticket belongs to the change board, G-7, and `APPROVED`, `REJECTED`, `WITHDRAWN`). The repository applies **one conditional statement**: `UPDATE CONSENT_REQUESTS SET status = 'WITHDRAWN', withdrawn_at = @now WHERE id = @id AND tenant_id = @t AND requester_sid = @sid AND status IN ('PENDING','PENDING_SECOND_APPROVAL')` and an audit entry in the same transaction; zero rows means the state changed concurrently and answers `ACCESS_REQUEST_NOT_PENDING`. The approve and reject UPDATEs in the three repositories get the matching `AND status = @expected` guard so an approval and a withdrawal that race end in exactly one terminal state (test 22). No consent exists for a withdrawn request (the `WHERE status = 'APPROVED'` activation conditions already exclude it). Available to every authenticated person (full and self-service tokens), not to app-only tokens (A-2).

#### A-7 Limits (FR-113-5)
`ConsentRequests:MaxPendingPerPerson` (25), `ConsentRequests:MaxSubmissionsPerHour` (30), both counted in the store by **immutable requester sid and tenant** (`CountOpenRequestsAsync`, `CountSubmissionsSinceAsync` on `IConsentApprovalRepository`, default interface members throwing `NotSupportedException`, the precedent `GetRowFilterAsync`): pending = status in `PENDING`, `PENDING_SECOND_APPROVAL`, `PENDING_EXTERNAL_APPROVAL`. A repository failure **denies** the submission (`ACCESS_REQUEST_LIMIT_INDETERMINATE`, fail closed, nothing created). The count-then-insert is not atomic across engines; a burst can exceed the cap by at most the number of concurrent submissions, which is acceptable for an anti-spam limit and documented (R-4); the limit is not a security boundary. Exceeding returns `ACCESS_REQUEST_LIMIT_EXCEEDED`. The limits apply to **all** submissions (self-service and full) so the agent path cannot flood approvers while a person path stays open.

#### A-8 Hidden datasets and neutral answers (FR-113-6)
After the metadata lookup `SubmitAsync` applies the visibility predicate delivered by #111 (`CatalogVisibility` with the `Discovery` surface; PLAN-GOV-111): a table that is `Hidden` for the caller (caller neither admin, nor owner/delegate, nor holder of a grant) answers the **same** `FORBIDDEN` as an unknown table (`ConsentRequestService.cs:96-101`, same code, same text). The check runs **before** the grant policy and before any write. A visibility lookup failure denies. This depends on I-7 (#111): without persisted tiers every table is `Hidden` for non-owners, so A must not ship before I-7 (the tracker already orders it so). The same predicate guards the MCP request tool. Behaviour change for full tokens documented as owner flag O-6.

#### A-9 MCP tools for agents
Two built-in tools, registered in `McpToolRegistry` and listed in `McpToolGroups` group `Access` (PLAN-SEC-115; the pinning test fails CI for an unmapped new tool): `request_dataset_access` (`dataset`, `justification`, `durationDays`, optional `context`; calls `IConsentRequestService.SubmitAsync`) and `withdraw_access_request` (`requestId`; calls `IConsentWithdrawalService`). They call the services directly (the GraphQL pipeline is not involved over MCP), so the service layer enforces the same gate: a new `bool IsSelfService` is carried like `IsReadOnly` (`McpCaller` `McpEndpoints.cs:38,221-235`, `IMcpProtocolHandler.CreateSession`, `IMcpSessionStore.CreateSession`, `McpSessionContext` `McpModels.cs:54`, `McpProtocolHandler.cs:58-61,397-399`, `McpSessionStore.cs:48,95`, `GatewayMcpServer.cs:337`), and the principal rebuild re-adds the marker; both marker claim types are skipped when forwarding token claims (`McpEndpoints.cs:230`, `McpProtocolHandler.cs:390`). Tool visibility: not listed for sessions that are read-only without the marker or not person-bound (precedent `McpAdminTools.CanUseAdminTools`, `McpAdminTools.cs:45-51`). Over MCP there is **no** approve or reject tool and none is added (an agent must never decide). Owner flag O-7 (the PRD names only GraphQL).

#### A-10 Audit, metrics
New Tier-A event types `ACCESS_REQUEST_SUBMITTED` and `ACCESS_REQUEST_WITHDRAWN` (`AuditEventTypes.cs`, `FailClosed` in `AuditPolicyTable.cs`). Written through `IAuditWriter.RecordRequiredAsync` (abort on failure): for submission **after** the request row exists and compensated by deleting it when the audit write fails (precedent `DeleteConsentRequestAsync`, `ConsentRequestService.cs:172-191`); for withdrawal inside the repository transaction. Details: requester sid, table, token class (`read-only+self-service` or `full`), request id, requested valid-to, context **length** (never text), caller application header value as `CallerContext` already carries it (normalisation per REQ-OBS-114 is applied once #114 lands; until then the existing field). Also `DECISION_NOT_PERMITTED` denials by `ConsentActorPolicy` write a Tier-A `CONSENT_DENIED` entry (`AuditEventTypes.cs:70`) with the reason class (read-only, not person, missing review scope, mixed token). Metrics: `autheris_access_requests_total{outcome,token_class}` (outcome from a fixed set), `autheris_access_request_denied_total{reason}`; labels are fixed vocabularies, never ids.

#### A-11 Idempotency and errors
Error codes (all `ConsentRequestException`/GraphQL `SetCode`): existing `READ_ONLY_TOKEN`, `FORBIDDEN`, `NOT_FOUND`, `INVALID_ARGUMENT`; new `ACCESS_REQUEST_LIMIT_EXCEEDED`, `ACCESS_REQUEST_LIMIT_INDETERMINATE`, `ACCESS_REQUEST_NOT_PENDING`, `DECISION_NOT_PERMITTED`. Withdraw is naturally idempotent for the requester only through status (a second call answers `ACCESS_REQUEST_NOT_PENDING`); no idempotency key.

### Increment B: approver-chosen configuration on approval

#### A-12 Scope of B and what it reuses
B adds an **approval configuration** (column levels plus typed row filters) chosen by the approver or proposed from a template, validated at write time, frozen per request, and activated atomically with the consent. It reuses `ConsentRowFilter` (`SimpleColumnPredicate` only, `LITERAL` only; correlated and cross-source filters and `USER_ATTRIBUTE` are **not** offered on approval: #129 comes later in the delivery order and the correlated forms carry the #107 strategy surface that has its own admin API), `ConsentColumnRule` and `TypedRowFilterBuilder` for enforcement. It does not invent a filter dialect and does not duplicate F-GOV-11 access profiles (templates are an approver convenience, not a second policy store: they are consulted only while a request is being decided and never grant anything by themselves).

#### A-13 Atomic approve-and-activate (SI-113-4, G-6)
New `IConsentApprovalRepository.ApproveConsentRequestWithConfigurationAsync(requestId, approverSid, ApprovalConfiguration config, ConsentActivationPlan activation, ct)` (default member throwing `NotSupportedException`; implemented on the three engines by factoring the existing step logic into an internal core that accepts an optional activation): in **one transaction** it re-checks status, authorization, self-approval and four-eyes (as the existing step), inserts the step with the config hash, writes or checks the frozen configuration row, updates the status, and **when the new status is `APPROVED`** inserts the consent through the existing `InsertConsentCoreAsync` (consent row, column rules, row filters, `CONSENT_GRANTED` audit), bumps the table epoch (`IncrementTableEpochInternalAsync`, same transaction), and commits; only after the commit `InvalidateEpochAsync` (exactly as `CreateConsentAsync`, `:1282-1297`). Any failure rolls the step back: the request stays pending. `IGrantPolicy.Evaluate` runs in the application service **before** the call, at activation time with `Now` (as `MutationTypes.cs:313-337`), and a denied decision aborts before any write; the repository additionally refuses an activation plan whose individual-grant `ValidTo` is null or beyond a re-derived class maximum (second line, test 33). The existing two-call path in `MutationTypes.cs:293-356` is replaced by this call for **all** approvals (with and without explicit configuration), which also fixes the A-phase gap for existing clients; the legacy overload without configuration keeps working through the built-in default configuration.

#### A-14 Approval configuration model and validation (FR-113-8, FR-113-10, SI-113-5)
`ApprovalConfiguration` = column levels (`Deny`, `Mask`, `Clear` per column; the GraphQL names `DENY`, `MASKED`, `FULL` map to the existing `ColumnAccessLevel`) plus up to `MaxRowFilters` (default 8) typed filters. `ApprovalConfigurationValidator` (Application, pure plus the table metadata) rejects the **whole** approval (`INVALID_ROW_FILTER`, `INVALID_COLUMN_LEVEL`; nothing is written, the request stays pending) unless:
- every column named exists in the catalog metadata of the table (case-insensitive exact, canonicalised to the catalog spelling), appears once, and every catalog column gets a level (missing columns default to the built-in snapshot level, never to `Clear`);
- no non-admin approver raises any column above the built-in snapshot level (`Mask` to `Clear` for a sensitive or masked column is **governance admin only**; `Deny` is always allowed); a four-eyes table keeps its two steps, and the second approver must submit the **same** configuration hash (A-16);
- every filter: `FilterType = SimpleColumnPredicate`, `ValueSource = LITERAL`, operator in the allowlist of `TypedRowFilterBuilder` (`EQ NEQ LT GT LTE GTE LIKE IN`, exposed as one shared internal constant so the validator cannot drift from the enforcer), column identifier matches the same regex, value JSON parses and its element types match `ValueType` and the catalog type of the column (`int`, `decimal`, `date`, `string`), `IN` has 1..`MaxInValues` (default 100) elements, `LIKE` patterns at most 128 characters, string values at most 256 characters, no NUL or control characters, `USER_ATTRIBUTE`, subquery and cross-source properties **must be null**;
- a **dry compile** with `TypedRowFilterBuilder.Build(filters, table)` succeeds (the validator calls the enforcing compiler, so a filter that would fail at read time, which would deny the table to the grantee, is rejected at write time);
- the filter references **no sensitive column in a way that discloses it**: a filter on a column whose level is `Deny` is rejected (a hidden column must not steer visible rows); a filter on a `Mask` column is allowed (the filter applies to the stored value, as for every consent filter).
Values are persisted as `ValueJson` and bound as parameters by the existing builder; nothing is concatenated. The validator never logs values.

#### A-15 Templates (FR-113-9, Q-113-3, Q-113-4, Q-113-5)
Storage `TABLE_ACCESS_TEMPLATES` (separate from `TABLES`: ingestion upserts rewrite `TABLES`, the reason F-GOV-21 and #107 used separate tables), tenant-scoped. A template = column levels + typed filters in the same shape as A-14 (validated at write time by the **same** validator, run against the current catalog metadata) + optional `group_key`. Maintained through the governance admin REST API and the declarative manifest (F-GOV-15, `DeclarativeConsentApplyService`); a row has `source` (`admin` or `manifest`); **the manifest wins**: applying a manifest replaces the row for the same (tenant, table, group key) and marks it `manifest`; an admin `PUT` on a `manifest` row answers `409 TEMPLATE_MANAGED_BY_MANIFEST` (the admin must change the manifest). Template revisions increment; the approval stores template id and revision.
Proposal order per request (the **proposal service**, evaluated at approval time, never at request time): (1) group-bound templates whose `group_key` is in the requester's server-side group set (below), (2) the dataset-default template (`group_key = ''`), (3) the built-in default (current snapshot, no row filter). Exactly one match in step 1 is proposed; **more than one** is ambiguous: the proposal is source `NONE_UNIQUE`, lists the candidates, and the approver must choose one by `templateId` (no silent pick, no precedence guess). The requester's group set comes from `IDataOwnershipRepository.GetTransitiveRolesAsync(requesterSid)` for **each** immutable requester id (`ConsentRequest.RequesterIdentifiers` restricted to the immutable subset) through a new `IRequesterGroupResolver` seam (default implementation over that method; a future SCIM/Role-Resolver implementation replaces it without touching the service); the approver's own groups are never consulted. A resolver failure or an empty set means **no group template matches** (the proposal falls to the dataset default or built-in default, both of which are narrower or equal in the shipped templates only if the administrator keeps them so: owner flag O-3 discusses this) and is recorded as `group_resolution = unavailable|none|matched`. The matched group keys, the resolution state and a hash of the resolved group set are written into the approval audit (Q-113-5).
Who may choose what (Q-113-3): a **non-admin** approver (owner, delegate) may approve only (a) a proposed template unchanged (`templateId` plus `proposalHash`) or (b) the built-in default; any free-form column level or filter by a non-admin is `FORBIDDEN_FREE_FORM_CONFIGURATION`. A **governance admin** (claims `GovernanceAdmin` or canonical ClusterAdmin, the same test as `MutationTypes.cs:259`) may submit a free-form typed configuration (validated by A-14). Template authoring (admin API, manifest) is governance-admin-only.

#### A-16 Confirmation, proposal hash, frozen configuration (FR-113-8, FR-113-13)
`proposalHash` = SHA-256 over the canonical JSON of (proposal source, template id, template revision, column levels, filters, table id). Approving **without** a configuration argument is accepted only when the resolved proposal is the built-in default **and** the table is not four-eyes and not above `AutoApproveMaxSensitivity` semantics (below); otherwise `CONFIGURATION_CONFIRMATION_REQUIRED` carries the current proposal hash and nothing is written (no silent activation for four-eyes datasets or when a template exists). `confirmProposed: true` plus `proposalHash` is accepted only if the backend recomputes the **same** hash now (a template edited between preview and approve answers `PROPOSAL_CHANGED`). The first approval step stores the configuration (table `CONSENT_REQUEST_APPROVAL_CONFIGS`, one row per request: `config_json`, `config_hash`, template id and revision, source, proposer sid, resolution record); `APPROVAL_STEPS` gets a `config_hash` column. A second step must present the **same** hash (or `confirmProposed` against the stored frozen configuration), else `CONFIGURATION_MISMATCH`: a later step cannot change what an earlier step approved; changing means reject and re-request. The frozen row is what the activation reads; the consent therefore carries exactly what both approvers saw.

#### A-17 Auto-approve, ITSM, bundled requests (FR-113-12)
`IsAutoApproveEnabled` (insecure getting-started flag, `GatewayOptions.cs:109`): the path applies **only** the dataset-default template (never a group template, never free-form) through the same atomic activation; a dataset without template **and** with sensitivity rank above `ConsentRequests:AutoApproveMaxSensitivity` (default `Internal`, resolved with `Table.SensitivityRank`, `TableModels.cs:60`) is **not** activated (the request stays `PENDING`, `ConsentRequestService` returns the pending result with a message), fail closed. ITSM activation (`ActivateConsentCoreAsync`): the webhook handler resolves the dataset-default template and passes it as an `ApprovalConfiguration` to a new overload; without a template the built-in snapshot applies unchanged (owner flag O-4). `BundledConsentRequestService` requests are decided by the same GraphQL approval and therefore get B automatically; their creation is **not** opened to read-only tokens (REST path, closed by `ReadOnlyTokenMiddleware`).

#### A-18 GraphQL surface for B
Additive: `approveConsentRequest(requestId, idempotencyKey, configuration: ApprovalConfigurationInput)` with `ApprovalConfigurationInput { templateId, proposalHash, confirmProposed, columnLevels: [{column, level}], rowFilters: [{column, operator, valueType, values: [String!]!}] }` (a legacy call without the argument keeps compiling); query `proposedAccessConfiguration(requestId)` returning `{ source, templateId, templateRevision, proposalHash, columnLevels, rowFilters, candidates, requiresConfirmation, preview }`; `preview` is the **effective access** the requester would get, computed with the existing `EffectiveAccessExplanationService` over a **hypothetical consent** (no write, FR-113-14). Both are guarded by `ConsentActorPolicy` `Decide` and the approver authorization of the request (non-approvers get `FORBIDDEN`, unknown or foreign-tenant ids `NOT_FOUND`). `pendingApprovalsForMe` and `myAccessRequests` additionally return `context`, `withdrawnAt` and (to approvers) the proposal source. Idempotency fingerprint of approve includes the configuration hash.

#### A-19 Admin REST and manifest for templates (Q-113-4)
`GET|PUT|DELETE /api/v1/governance/access-templates/{datasetId}` and `/{datasetId}/groups/{groupKey}` (`GatewayPolicies.GovernanceAdmin`, `WithAudit(AuditLevel.Full, AccessTemplateChanged/Deleted)`, new `FailClosed` event types, before/after in the audit, no filter values in the log line; dataset resolution reuses the catalog resolution of #111). The manifest gains an `accessTemplates` section (F-GOV-15 shape) applied by `DeclarativeConsentApplyService` with the same validator; a validation error rejects the whole manifest (the existing all-or-nothing behaviour). Template changes do **not** bump the table epoch (a template grants nothing); they are visible to the next proposal only.

---

## 3. Contracts (fixed up front; first commit "contracts" per increment)

### C-1 Options and token marker (A; `Autheris.Domain`)
```csharp
// Options/Security/JwtOptions.cs (EntraIdAuthOptions)
public List<string>? SelfServiceScopes { get; init; }   // null or empty = off
public List<string>? ReviewScopes { get; init; }
public IReadOnlyList<string> GetEffectiveSelfServiceScopes();
public IReadOnlyList<string> GetEffectiveReviewScopes(); // null => ["data.access.review"]
public bool ReviewScopeRequired { get; }                  // SelfService non-empty OR ReviewScopes explicitly non-empty

// Security/TokenAccessScope.cs
public const string SelfServiceClaimType = "autheris:access_self_service";
public static bool IsSelfServiceRequester(this ClaimsPrincipal? p);   // same fixed Issuer check as IsReadOnly
public static void MarkSelfService(ClaimsIdentity identity);

// Options/ConsentRequestOptions.cs, GatewayOptions.ConsentRequests
MaxContextLength = 200 (1..500); MaxPendingPerPerson = 25 (1..1000);
MaxSubmissionsPerHour = 30 (1..10000);
AutoApproveMaxSensitivity = "Internal" (B); MaxRowFilters = 8 (1..32) (B); MaxInValues = 100 (1..1000) (B)

// Common/Sid.cs
public static Sid? GetPersonSid(this ClaimsPrincipal? p);  // immutable claim list only; never appid/client_id/azp
```

### C-2 Domain model (A)
```csharp
// GovernanceModels.cs ConsentRequest: + string? Context, DateTimeOffset? WithdrawnAt, string SubmittedVia = "full"
// ConsentDtoModels.cs ConsentRequestDto: + string? Context, DateTimeOffset? WithdrawnAt
// ConsentRequestSubmission (IConsentRequestService.cs): + string? Context = null (trailing optional)
public enum ConsentAction { Request, Withdraw, Decide }
```

### C-3 Application (A)
```csharp
public interface IConsentActorPolicy { ConsentActorDecision Evaluate(ClaimsPrincipal? principal, ConsentAction action); }
public sealed record ConsentActorDecision(bool Allowed, string? ErrorCode, string Reason, string TokenClass, string DenyReasonClass);

public interface IConsentWithdrawalService
{ Task WithdrawAsync(Guid requestId, ClaimsPrincipal principal, TenantId tenant, CancellationToken ct); } // ConsentRequestException on failure

// Repositories (IConsentApprovalRepository; default members throw NotSupportedException)
Task<int> CountOpenRequestsAsync(Sid requester, TenantId tenant, CancellationToken ct);
Task<int> CountSubmissionsSinceAsync(Sid requester, TenantId tenant, DateTimeOffset since, CancellationToken ct);
Task<bool> WithdrawConsentRequestAsync(Guid requestId, TenantId tenant, IReadOnlySet<Sid> requesterSids, DateTimeOffset now, AuditLogEntry audit, CancellationToken ct);
```
`GraphQL`: `internal static class MutationRootFieldClassifier { public static RootFieldSet Classify(DocumentNode doc, string? operationName); }` with `RootFieldSet(IReadOnlySet<string> Fields, bool Indeterminate)`.

### C-4 Governance DDL (A; in place in `0001`, checksums unchanged, shapes regenerated)
```sql
-- CONSENT_REQUESTS: add three columns
context        TEXT/varchar(500)/nvarchar(500) NULL,
withdrawn_at   TEXT/timestamptz/datetimeoffset NULL,
submitted_via  TEXT/varchar(32)/nvarchar(32) NOT NULL DEFAULT 'full'
-- index for the limits (no FK, no CHECK, consistent with TABLE_ROW_FILTER_STRATEGIES)
CREATE INDEX ix_consent_requests_requester ON CONSENT_REQUESTS (tenant_id, requester_sid, status);
```
(B)
```sql
CREATE TABLE TABLE_ACCESS_TEMPLATES (
  id uuid-text PK, tenant_id NOT NULL, table_id NOT NULL,
  group_key NOT NULL DEFAULT '',          -- '' = dataset default
  name NOT NULL, column_rules_json NOT NULL, row_filters_json NOT NULL,
  source NOT NULL,                        -- 'admin' | 'manifest'
  revision INT NOT NULL,
  created_by NOT NULL, created_at NOT NULL, updated_by NOT NULL, updated_at NOT NULL);
CREATE UNIQUE INDEX ux_access_templates_key ON TABLE_ACCESS_TEMPLATES (tenant_id, table_id, group_key);
CREATE TABLE CONSENT_REQUEST_APPROVAL_CONFIGS (
  request_id PK, tenant_id NOT NULL, config_json NOT NULL, config_hash NOT NULL,
  template_id NULL, template_revision INT NULL, source NOT NULL,
  proposed_by NOT NULL, resolution_json NOT NULL, created_at NOT NULL);
-- APPROVAL_STEPS: add config_hash NULL
```
Types per engine follow the neighbouring tables (SQLite TEXT/INTEGER, PostgreSQL uuid/timestamptz/jsonb-as-text, SQL Server uniqueidentifier/datetimeoffset/nvarchar(max)); read-only runtime login grants include the new tables.

### C-5 Approval contracts (B)
```csharp
public sealed record ApprovalConfiguration(IReadOnlyList<ApprovalColumnLevel> ColumnLevels, IReadOnlyList<ConsentRowFilter> RowFilters, string Hash);
public sealed record ApprovalProposal(string Source, Guid? TemplateId, int? TemplateRevision, ApprovalConfiguration Configuration,
    IReadOnlyList<ApprovalTemplateCandidate> Candidates, GroupResolution Resolution, bool RequiresConfirmation);
public interface IApprovalProposalService { Task<ApprovalProposal> ProposeAsync(ConsentRequest request, ClaimsPrincipal approver, CancellationToken ct); }
public interface IApprovalConfigurationValidator { ApprovalValidation Validate(ApprovalConfiguration cfg, TableMetadata table, ApproverClass approver); }
public interface IRequesterGroupResolver { Task<GroupResolution> ResolveAsync(ConsentRequest request, CancellationToken ct); }
public interface IAccessTemplateRepository { Get / List / Upsert(source) / Delete }  // 3 engines, tenant-scoped
Task<ConsentRequest> ApproveConsentRequestWithConfigurationAsync(Guid requestId, Sid approver, ApprovalConfiguration cfg,
    ConsentActivationPlan activation, CancellationToken ct);   // A-13
```

---

## 4. Files per Layer and Work Package (ONE sequential developer, A then B)

Ownership is exclusive per work package; the single developer works the packages in order and does not build in between (project rule: the lead compiles and tests once per increment).

### Increment A (M)

| WP | Files (new unless marked "changed") | Notes |
|---|---|---|
| **WP-A1 Contracts, options, DDL, models** | changed: `src/Autheris.Domain/Options/Security/JwtOptions.cs`, `src/Autheris.Domain/Security/TokenAccessScope.cs`, `src/Autheris.Domain/Common/Sid.cs`, `src/Autheris.Domain/Options/GatewayOptions.cs`, `src/Autheris.Domain/Model/GovernanceModels.cs`, `ConsentDtoModels.cs`, `src/Autheris.Domain/Audit/AuditEventTypes.cs`, `src/Autheris.Application/Audit/AuditPolicyTable.cs`, `Migrations/{Sqlite,PostgreSql,SqlServer}BaselineSchema.cs`, the three `Shape/governance-shape.*.json` and their tests; new: `ConsentRequestOptions.cs`, `SelfServiceScopeConfigurationValidator.cs` (registered with the options validators), changed `GatewayEmptyListConfigurationValidator.cs` (rule for `SelfServiceScopes`) | first commit "contracts" |
| **WP-A2 Token policy and person gate** | changed: `src/Autheris.Api/Security/EntraTokenPolicy.cs`; new: `src/Autheris.Application/Services/PersonSubject.cs`, `Governance/ConsentRequests/ConsentActorPolicy.cs` (+ `IConsentActorPolicy`), DI in `GatewayGovernanceServiceExtensions.cs` | token class matrix tests first |
| **WP-A3 GraphQL gate** | new: `src/Autheris.GraphQL/Interceptors/MutationRootFieldClassifier.cs`, `ConsentDecisionMiddleware.cs`; changed: `ReadOnlyOperationMiddleware.cs`, `GatewayServiceCollectionExtensions.cs:581` (registration order: read-only gate, then decision gate), `MutationTypes.cs` (guard call at the top of approve and reject, `RequestTableAccessAsync` passes `context`, new `WithdrawMyAccessRequestAsync`), `QueryTypes.cs` (DTO fields, tenant predicate on `pendingApprovalsForMe`) | classifier is a pure function: test it exhaustively |
| **WP-A4 Services** | changed: `ConsentRequestService.cs` (context, person gate, limits, visibility, audit + compensation, fingerprint), `IConsentRequestService.cs`; new: `ConsentWithdrawalService.cs`, `IConsentWithdrawalService.cs` | |
| **WP-A5 Repositories (3 engines)** | changed: `{Sqlite,PostgreSql,SqlServer}GovernanceRepository.Consent.cs` (create/read of the three columns, counts, `WithdrawConsentRequestAsync`, guarded status updates in approve/reject), `IGovernanceRepository.cs` (`IConsentApprovalRepository` members), in-memory test double if one exists | one contract-test base class run on all three engines |
| **WP-A6 MCP** | changed: `McpEndpoints.cs`, `IMcpProtocolHandler.cs`, `McpProtocolHandler.cs`, `IMcpSessionStore.cs`, `McpSessionStore.cs`, `McpModels.cs`, `GatewayMcpServer.cs`, `McpToolRegistry.cs`, `McpToolGroups.cs` (#115), `McpToolExecutionHandler.cs`; new: `src/Autheris.Application/Mcp/Tools/McpAccessRequestTools.cs` | no approve/reject tool |
| **WP-A7 Wrap-up** | metrics, docs pointer to the master plan row (the architect does not update `docs/`), cleanup of obsolete code | |

### Increment B (L; starts only after #107 is complete and A is merged)

| WP | Files | Notes |
|---|---|---|
| **WP-B1 Contracts, DDL, models** | changed: baseline x3, shapes x3 + tests, `ConsentRequestOptions.cs`, `AuditEventTypes.cs`, `AuditPolicyTable.cs`; new: `ApprovalConfiguration.cs`, `ApprovalProposal.cs`, interfaces of C-5 | first commit "contracts" |
| **WP-B2 Validator and templates** | new: `ApprovalConfigurationValidator.cs`, `AccessTemplateRepository` x3 (`*GovernanceRepository.AccessTemplates.cs`), `AccessTemplateService.cs`; changed: `TypedRowFilterBuilder.cs` (expose the operator allowlist constant only, no behaviour change) | validator calls the enforcing compiler |
| **WP-B3 Proposal and group resolution** | new: `ApprovalProposalService.cs`, `RequesterGroupResolver.cs`; changed: `DeclarativeConsentApplyService.cs` (manifest `accessTemplates`) | |
| **WP-B4 Atomic approval (3 engines)** | changed: `{Sqlite,PostgreSql,SqlServer}GovernanceRepository.Consent.cs` (`ApproveConsentRequestWithConfigurationAsync`, frozen config row, `config_hash` on steps, activation inside the transaction, ITSM overload), `IGovernanceRepository.cs`, `ItsmWebhookHandler.cs` (template overload only) | the heaviest package; contract-test base class |
| **WP-B5 GraphQL and auto-approve** | changed: `MutationTypes.cs` (approve with configuration, replaced two-call path), `QueryTypes.cs` (`proposedAccessConfiguration`), `ConsentRequestService.cs` (auto-approve: template only, fail closed) | |
| **WP-B6 Admin REST and wrap-up** | new: `src/Autheris.Api/Endpoints/AccessTemplateEndpoints.cs`; changed: endpoint registration, metrics | |

---

## 5. Sequencing

1. **A** (I-22) after #111 (I-7): needs the visibility predicate (A-8). One branch, one PR, its own Phase 3 verdict, Phase 5, Phase 6.
2. #114 (I-23) follows A (shared caller-label normalisation).
3. **B** (I-24) after #107 (I-3) and A: needs the typed filter storage and the `InsertConsentCoreAsync` path as merged by #107, and the `status` guard of A-6. One branch, one PR, its own Phase 3 verdict.
4. #129 (I-25) follows B; B therefore offers no `USER_ATTRIBUTE` filters (A-12) and #129 later extends the validator by one rule.
Within each increment the developer follows WP order; contracts are the first commit and are not edited afterwards without architect consent.

---

## 6. Test Strategy (TDD list, red first)

Unit tests live in `tests/Autheris.Tests.Unit`; engine contract tests in a shared base class on the three governance engines; the GraphQL end-to-end cases in `tests/Autheris.Tests.Integration` (`DOTNET_USE_POLLING_FILE_WATCHER=true` in WSL).

### Increment A (write these first, they fail before WP-A2..A5)

| # | Test (AC / SI) |
|---|---|
| 1 | Token class matrix in `EntraTokenPolicy`: `{Agent.Read}` read-only no marker; `{Agent.Read, data.access.request}` read-only **and** self-service (the G-1 regression: before the change it is a full token); `{data.access.request}` alone read-only+self-service; `{Agent.Read, Other.Write}` full; feature off (`SelfServiceScopes` empty) behaves exactly as before |
| 2 | **Read-only token without a listed scope cannot request**: `requestTableAccess` answers `READ_ONLY_TOKEN` (AC 2) |
| 3 | A read-only self-service token runs `requestTableAccess` and `withdrawMyAccessRequest` (AC 1); **every other mutation** (`approveConsentRequest`, data mutations, catalog sync) answers `READ_ONLY_TOKEN` (AC 3, SI-113-1) |
| 4 | **Mixed documents rejected as a whole**: allowlisted + other field; aliased forms (`a: approveConsentRequest` and an alias that names an allowlisted field around another); inline fragment; fragment spread; fragment cycle; unknown operation name; multi-operation document without name; `__typename` only; the allowlist constant equals exactly `{requestTableAccess, withdrawMyAccessRequest}` |
| 5 | **Request token cannot approve**: read-only+self-service token calling `approveConsentRequest` or `rejectConsentRequest` is rejected by the pipeline and, with the pipeline bypassed (direct resolver call), by `ConsentActorPolicy` in the resolver; no repository call happens (SI-113-3) |
| 6 | **Self-approval rejected**: person P files with a self-service token, P approves with a full token holding `data.access.review` and owner rights: rejected by the identity set (`FORBIDDEN`, separation of duties); same with different claim spellings (oid vs primarysid); repository-level rejection as well |
| 7 | **Approver without `data.access.review` rejected**: delegated token without the scope (`ReviewScopeRequired`), owner rights present, `DECISION_NOT_PERMITTED`, no step written; with the scope: allowed; tokens without `scp` (Basic/Kerberos) are not subject to the scope test; `ReviewScopes` unset and `SelfServiceScopes` empty: legacy behaviour unchanged |
| 8 | **Mixed token** (`data.access.request` + `data.access.review`) cannot decide; scope present in both lists fails startup; review scope in `ReadOnlyScopes` fails startup; non-empty `SelfServiceScopes` with empty effective review scopes fails startup |
| 9 | **App-only token rejected** (Q-113-1): `idtyp=app`, `appid` only, `client_id`/`azp` only, certificate subject, `AppOnlyTokens=ReadWrite`: `requestTableAccess`, `withdraw`, MCP tools and decisions all answer `FORBIDDEN`; the requester sid is never an application id; `GetPersonSid` and the #129 subject key agree on the claim order |
| 10 | Delegated token without any human claim (resolver says service principal) is rejected (fail closed); delegated token with `upn` accepted |
| 11 | `context` (AC 1, FR-113-4): persisted, returned in `myAccessRequests` and (tenant-filtered) `pendingApprovalsForMe`, JSON-escaped, 201st character rejected `INVALID_ARGUMENT`, control characters rejected, part of the idempotency fingerprint (same key, other context is a new request), audit stores length only |
| 12 | `withdrawMyAccessRequest`: own `PENDING` and `PENDING_SECOND_APPROVAL` request becomes `WITHDRAWN` with `withdrawn_at` and a Tier-A entry (AC 4); other person's id, unknown id, other tenant's id answer **byte-identical** `NOT_FOUND`; `APPROVED`, `REJECTED`, `WITHDRAWN`, `PENDING_EXTERNAL_APPROVAL` answer `ACCESS_REQUEST_NOT_PENDING`; no consent exists afterwards |
| 13 | Concurrency: approve-final-step racing a withdrawal ends in exactly one terminal state on every engine; the status guard in approve/reject rejects a request that is no longer pending |
| 14 | Limits (AC 5): 26th pending request `ACCESS_REQUEST_LIMIT_EXCEEDED`; hourly cap; limits per immutable sid and tenant (another person and another tenant unaffected); repository failure denies with `ACCESS_REQUEST_LIMIT_INDETERMINATE` and creates nothing |
| 15 | Hidden dataset (AC 6): a request for a `Hidden` table and for an unknown table produce identical code and text, before any write and before the grant policy; admin/owner/grant holder may request; visibility lookup failure denies |
| 16 | Grant policy: a request whose expiry exceeds the class maximum is rejected (`GrantErrorCodes`), as today; a self-service request goes through the same call (test pins `IGrantPolicy.Evaluate` is invoked for the self-service path) |
| 17 | Audit: `ACCESS_REQUEST_SUBMITTED`/`WITHDRAWN` are `FailClosed`; an audit failure aborts a withdrawal (rolled back) and compensates a submission (request deleted); `CONSENT_DENIED` entry for a denied decision with the reason class; token class values `read-only+self-service` and `full` |
| 18 | MCP: tools present in the registry and mapped in `McpToolGroups` (pinning test); a self-service session can request and withdraw; an unmarked read-only session cannot; the marker cannot be injected through `AdditionalClaims` or a token claim (`autheris:access_self_service` sent as a claim is dropped); no approve/reject tool exists; `tools/list` hides them for non-eligible sessions |
| 19 | HTTP layer: REST mutation paths and WebSql DML stay `READ_ONLY_TOKEN` for self-service tokens (SI-113-1 veto 2) |
| 20 | `pendingApprovalsForMe` does not list a foreign tenant's requests (new tenant predicate) |
| 21 | Three-engine contract tests for create/read of the new columns, counts, `WithdrawConsentRequestAsync`, guarded updates, shape snapshot tests updated |

### Increment B (write these first)

| # | Test (AC / SI) |
|---|---|
| 22 | Approval with a row filter (AC 7): two filters on two requests, the requester's next query returns only the rows of each filter (integration, SQLite plus the PG/SQL Server contract path); policy epoch bumped in the same transaction; cache invalidated after commit |
| 23 | **Filter template injection**: column names such as `a;DROP TABLE x`, `a" OR "1"="1`, `a--`, `a/*`, unicode lookalikes and a column that is not in the catalog are rejected (`INVALID_ROW_FILTER`); values containing quotes, `';--`, `%` patterns, NUL, 10,000 characters, nested JSON, a JSON array for `EQ`, a scalar for `IN`, more than `MaxInValues` items are either bound as parameters (the SQL text of the compiled predicate contains none of the value characters) or rejected, never concatenated; a template whose stored JSON was tampered with directly in the store is re-validated at proposal time and refused; `USER_ATTRIBUTE`, subquery and cross-source filters are rejected on approval |
| 24 | **Invalid filter** (AC 8): approval is rejected, no step, no consent, request still pending; a filter that the validator accepts compiles with `TypedRowFilterBuilder` (the validator and the enforcer share the operator constant: a test fails if one changes) |
| 25 | A filter on a `Deny` column is rejected; a non-admin cannot raise a sensitive column `Mask` to `Clear`; a governance admin can (and four-eyes tables still need a second distinct approver) |
| 26 | **Free-form by non-admin rejected** (Q-113-3): owner submitting free-form levels or filters gets `FORBIDDEN_FREE_FORM_CONFIGURATION`; owner confirming a template unchanged succeeds; admin free-form succeeds after validation |
| 27 | Proposal order (G-9): group-bound match, dataset default, built-in default; **two matching group templates** produce no auto-proposal and require an explicit `templateId`; resolver failure or empty group set falls back and is recorded (`unavailable`/`none`); the approver's own groups never influence the result; the matched group keys and resolution state are in the approval audit (Q-113-5) |
| 28 | Confirmation: approve without configuration on a four-eyes dataset or with an existing template answers `CONFIGURATION_CONFIRMATION_REQUIRED` and writes nothing; `confirmProposed` with a stale hash answers `PROPOSAL_CHANGED`; with the current hash activates exactly the proposed configuration (AC 9) |
| 29 | **Frozen configuration** (FR-113-13): step 2 with a different hash answers `CONFIGURATION_MISMATCH`; step 2 with the same hash activates; the consent equals the stored configuration; the template edited between steps changes nothing for this request |
| 30 | **Approver without `data.access.review` rejected** and **request token cannot approve** also on the new surfaces: `proposedAccessConfiguration`, `approveConsentRequest(configuration)` |
| 31 | **Self-approval rejected** with configuration present (AC 10): requester cannot approve own request even when choosing a stricter configuration; the same person cannot be both approvers |
| 32 | Atomicity (SI-113-4): failure injected after the step insert, after the consent insert, after the epoch bump: nothing persists (status unchanged, no consent, no config row) on all three engines; a crash between commit and cache invalidation leaves a correct store (epoch already bumped) |
| 33 | `IGrantPolicy` on the approval path: individual grants beyond the class maximum are denied at activation before any write; the repository second line refuses an activation plan with null or oversized `ValidTo`; group/role grantee requests are standing as today |
| 34 | Auto-approve (FR-113-12): applies only the dataset-default template; no template and sensitivity above `AutoApproveMaxSensitivity` leaves the request `PENDING`; never a group template or free-form |
| 35 | ITSM activation applies the dataset-default template when present, the built-in snapshot otherwise (A-17); ITSM requests are not approvable through GraphQL (unchanged) |
| 36 | Templates: admin API CRUD (GovernanceAdmin only; non-admin 403), tenant isolation (other tenant's template invisible and not applicable), manifest wins (`409 TEMPLATE_MANAGED_BY_MANIFEST` for admin edits of a manifest row), validator runs on write, audit before/after without values, no epoch bump |
| 37 | Effective-access preview (FR-113-14) matches the access the activated consent really yields (the same explanation service, hypothetical consent not persisted) |
| 38 | Three-engine contract tests for templates, frozen config rows, atomic approve, shape snapshots |

---

## 7. Fail-Closed Behaviour

| Situation | Behaviour |
|---|---|
| `SelfServiceScopes` empty or null | feature off; read-only tokens behave exactly as today |
| Token with a scope outside `ReadOnlyScopes` union `SelfServiceScopes` | full token, not read-only (as today); never gains the marker |
| Self-service marker absent, forged claim, or token without person subject | `READ_ONLY_TOKEN` / `FORBIDDEN`; marker claim from outside is dropped (fixed issuer) |
| Document with any non-allowlisted mutation field, unresolvable fragment, unknown operation name, indeterminate root fields | `READ_ONLY_TOKEN` for the whole document |
| Read-only token, service principal, mixed request/review token, missing review scope (when required) on approve or reject | `DECISION_NOT_PERMITTED`, no repository access, Tier-A denial entry |
| Policy service missing in a resolver | composition test fails; at runtime the resolver throws (never allows) |
| Unknown, foreign, other-person, or hidden request or dataset | identical neutral answer (`NOT_FOUND` for withdraw, `FORBIDDEN` for submit) |
| Limit lookup fails | submission denied (`ACCESS_REQUEST_LIMIT_INDETERMINATE`), nothing created |
| Visibility lookup fails | submission denied |
| Audit write fails | withdrawal rolled back; submission compensated (request deleted); decision denial still returned |
| Withdraw races with approve | exactly one terminal state (guarded `UPDATE`) |
| Invalid filter, column, level, ambiguous or stale proposal, hash mismatch | whole approval rejected, nothing activated, request pending |
| Any repository error during approve-and-activate | transaction rolled back, request unchanged |
| Group resolver fails or returns nothing | no group template matches; fallback recorded in the audit |
| Auto-approve without template above the sensitivity ceiling | request stays pending |
| Template stored JSON malformed or failing the validator at proposal time | template ignored for the proposal, Error log without values, built-in default offered with `requiresConfirmation` |
| `IGrantPolicy` indeterminate or denies | no activation (as everywhere else) |

---

## 8. Risks

| # | Risk | Mitigation |
|---|---|---|
| R-1 | The classification trap (G-1) is reintroduced by a later scope change | Test 1 pins the matrix; the union is computed in one place |
| R-2 | Root-field classification misses a GraphQL construct (fragments, aliases, directives) | Fail closed on everything but plain fields; exhaustive pure-function tests (test 4); schema-level test that the allowlist names exist |
| R-3 | Review scope breaks existing interactive approvers when `SelfServiceScopes` is configured | Enforcement is tied to the feature or an explicit `ReviewScopes`; release notes; tokens without `scp` unaffected (owner flag O-1) |
| R-4 | Pending cap overshoots under concurrency | Anti-spam only, documented; fixed bound by concurrency; hourly cap as second guard |
| R-5 | Replacing the two-call approval changes behaviour of existing clients and tests | Legacy overload retained; same observable results; the atomicity fix is a defect correction; tests for the legacy path are adapted in WP-B5 |
| R-6 | Group resolution has no real directory behind it today | Seam plus fail-safe fallback; a Role-Resolver (scale plan WP-SC-05/07) plugs in later; owner flag O-3 |
| R-7 | Template or filter content steers rows by a column the approver may not see | Deny-column rule (test 25); admin-only authoring; values never logged |
| R-8 | Three engines drift | One contract-test base class, shape snapshots |
| R-9 | Dependency drift: #111 or #107 land in another shape than planned | A and B each start with a "delta check" step (WP-A1/WP-B1) against the merged code; contracts are the first commit |
| R-10 | The decision guard blocks the ITSM signed webhook accidentally | The webhook calls the repository directly, not GraphQL; test 35 pins it |

## 9. Owner Flags

- **O-1** Review scope enforcement defaults to **off** unless `SelfServiceScopes` is configured or `ReviewScopes` is set explicitly (existing approver tokens do not carry `data.access.review`). Confirm, or require it unconditionally (breaking for existing approver clients).
- **O-2** Service principals never decide and (hardening) full app-only tokens cannot file requests. Confirm that no automation approves consent requests through GraphQL.
- **O-3** Group templates are resolved from the role/group membership store; the token groups of the requester are not available at approval time. Until a Role-Resolver exists the group template only matches users present in `ROLE_MEMBERS`. Also: precedence group-bound before dataset default deviates from the PRD wording (G-9).
- **O-4** ITSM and auto-approve activation apply only the dataset-default template (or the unchanged snapshot).
- **O-5** A token carrying both the request and the review scope cannot decide (not in the PRD; closes the one-token request-and-review gap).
- **O-6** Submission now applies catalog visibility to every caller, not only to agents (consistent with `BundledConsentRequestService`, but a change for full tokens).
- **O-7** Two MCP tools are added so agents can use the feature at all (MCP cannot run mutations). Drop them if agents should use GraphQL only.

## 10. Security Notes for Phase 3 (separate verdict for A and for B)

**Increment A**
1. Token classification (A-1): union rule, marker issuer, review/request scope separation, startup validation; try to obtain a full token from a read-only one, and to obtain the marker without a listed scope; behaviour with unusual `scp` spellings and the `IsSameScope` bare-name logic (`EntraTokenPolicy.cs:93-103`).
2. Closed allowlist (A-3): every GraphQL document construct (aliases, fragments, directives, multiple operations, batching, persisted/trusted documents, `__typename`, introspection on mutation root); the MCP and REST routes that bypass the pipeline.
3. Decision guard (A-4): both enforcement points, the four conditions, the mixed-token rule, tokens without `scp`; self-approval across token classes and claim spellings; no code path where a read-only principal reaches `ApproveConsentRequestStepAsync`.
4. Person gate (A-2): `GetUserSid()` fallback to `appid`/`client_id`/`azp`, resolver misclassification of delegated tokens without human claims, certificate subjects.
5. Withdrawal (A-6): identical `NOT_FOUND`, conditional update, tenant filter, race with approval, `PENDING_EXTERNAL_APPROVAL` exclusion.
6. Free text (A-5): `context` as an agent-controlled string that approvers and agents read (prompt injection surface): length, characters, escaping, never logged in full, never used for routing.
7. Anti-flooding (A-7) and tenant leakage in `pendingApprovalsForMe`.
8. Audit completeness and failure policy (A-10); MCP session marker plumbing (A-9).
9. Visibility oracle (A-8): identical answers for hidden and unknown datasets, including timing-neutral order of checks.

**Increment B**
1. Atomic activation (A-13): rollback paths on three engines, the second-line grant check, epoch/cache ordering, replacement of the two-call path.
2. Validator (A-14) versus enforcer parity: injection corpus, shared operator allowlist, dry compile, no secrets or values in logs; the `Deny`-column steering rule; stored-template tampering.
3. Who may grant what (A-15): non-admin template-only rule, admin determination by claims, raising `Mask` to `Clear`, four-eyes interplay, manifest-wins and admin edits.
4. Group resolution (A-15, G-5): trust in `ROLE_MEMBERS`, ambiguity handling, resolver failure direction, audit content.
5. Confirmation and frozen configuration (A-16): hash construction, TOCTOU on templates, step-2 mismatch, no path that activates without both approvers' identical configuration.
6. Auto-approve and ITSM paths (A-17): template-only, ceiling fail closed, no group template.
7. Admin REST for templates (A-19): GovernanceAdmin only, tenant isolation, audit before/after.
8. `IGrantPolicy` on every path created here (expiry classes), including that group/role grantees remain standing as designed.

## 11. LOC Estimate

| Increment | Area | Production | Tests |
|---|---|---|---|
| A | WP-A1 contracts, options, DDL x3, shapes (generated, excluded) | ~330 | ~300 |
| A | WP-A2 token policy, person gate, actor policy | ~260 | ~520 |
| A | WP-A3 classifier, middlewares, resolvers, queries | ~330 | ~620 |
| A | WP-A4 submit changes, withdrawal service | ~320 | ~420 |
| A | WP-A5 repositories x3 | ~480 | ~430 |
| A | WP-A6 MCP plumbing and two tools | ~330 | ~330 |
| A | WP-A7 metrics, wrap-up | ~40 | ~20 |
| **A total** | | **~2,100** | **~2,650** |
| B | WP-B1 contracts, DDL x3 | ~280 | ~140 |
| B | WP-B2 validator, template store x3, template service | ~900 | ~1,150 |
| B | WP-B3 proposal, group resolver, manifest section | ~420 | ~620 |
| B | WP-B4 atomic approval x3, ITSM overload | ~780 | ~1,100 |
| B | WP-B5 GraphQL surface, auto-approve | ~380 | ~620 |
| B | WP-B6 admin REST, metrics | ~330 | ~480 |
| **B total** | | **~3,100** | **~4,100** |

Size: A = M (matches the PRD, driven by the three-engine repositories and the MCP plumbing), B = L (atomic approval on three engines and the validator are the bulk). Shape JSON diffs (~200 lines per engine and increment) are excluded.

## 12. Changelog

| Revision | Date | Change |
|---|---|---|
| 1 | 2026-10-16 | Initial Phase 2 plan (PROPOSED) for increments A and B, based on `c78edbf5`. Records owner decision Q-113-2 (scopes `data.access.request` / `data.access.review`). |
