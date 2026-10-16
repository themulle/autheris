# Implementation Plan: REQ-GOV-113 - Access Requests with Read-Only Tokens and Approver-Chosen Row Filters

**Document ID:** `PLAN-GOV-113` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-GOV-113, section 4](2026-10-16-req-issues-111-115-catalog-access-analytics.md) (FR-113-1..14, SI-113-1..6, AC 1..10, Q-113-1..5, section 8 owner decision Q-113-2)
**Branch:** increment A `feat/issue-113-a-self-service-requests`, increment B `feat/issue-113-b-approval-configuration` (both from `main` after their predecessors, see section 5); planning base `c78edbf5`
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement `REQ-GOV-113`; delivery tracker [PLAN-DELIVERY-W23](2026-10-16-plan-waves-2-3-delivery.md) (step P-8, implementation steps I-22 and I-24)
**Status:** PHASE 3 DONE (2026-10-16, `csharp-security-expert`): increment A APPROVED WITH CONDITIONS (SEC-113-01..13 binding); increment B CHANGES REQUIRED (architect revision 2 must close SEC-113-14..22 before Phase 4 of B); no standalone WP-0 for the three architect findings, one narrow optional WP-0 recommended (see Phase 3 review, section P3.2)

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
| 1 (Phase 3) | 2026-10-16 | Appended the Phase 3 security review (step S-8): verification of the pre-existing issues, SEC-113-01..22, binding conditions per increment, mandatory security tests T-S1..T-S28, recommendations for O-1..O-7. Verdicts: A APPROVED WITH CONDITIONS, B CHANGES REQUIRED. Plan body unchanged; the architect folds the conditions into revision 2. |

---

## Phase 3 Security Review (2026-10-16)

**Reviewer:** `csharp-security-expert` (step S-8 of PLAN-DELIVERY-W23). **Base:** worktree `plan-w23` at `210c98b8`; every architect claim relevant to security and the three pre-existing issues were re-read in the current code (line numbers below are from that read). **Related reviews:** PLAN-GOV-129 (subject binding, `GetUserSid()` fallback), PLAN-GOV-111 rev. 3 (`Gateway:Tenancy:Mode`, visibility predicate, WP-0), PLAN-SEC-115 (tool groups). **Owner rule applied throughout:** an agent must never widen its own reach; rights are checked by the backend from the authenticated principal.

### P3.1 Threat model

| # | Actor and goal | Entry | Control in the plan | Residual after conditions |
|---|---|---|---|---|
| TM-1 | **Prompt-injected agent** (instructions in a document or dataset text) files requests or tries to decide | MCP tools, GraphQL `requestTableAccess`, `approveConsentRequest` | read-only + self-service marker, closed root-field allowlist, decision guard, no MCP decision tool | agent can still file (bounded) requests the person never intended; mitigated by limits, visibility, labelling, human approval |
| TM-2 | **Agent widens its own reach**: request then approve through the same or a second token of the same person | mixed token, second token, scope-less auth scheme | request/review scope split, self-approval by identity set, four-eyes | a person holding an interactive review token and an agent token is still two tokens of one identity: caught only by identity-set self-approval (SEC-113-04) |
| TM-3 | **Agent text attacks the approver** (justification/context carrying instructions, bidi or control characters, markup) | `justification` (2000 chars), `context` (200 chars), shown in `pendingApprovalsForMe`, in approver UIs and possibly to an approver's own AI client | `context` sanitised; justification not covered | SEC-113-08 |
| TM-4 | **Flooding / quota exhaustion** of approver inboxes, ITSM tickets, or of the person's own quota by their agent | MCP, GraphQL | per-person caps | SEC-113-09 |
| TM-5 | **Existence oracle** for hidden datasets or other people's requests | submit, withdraw, approve/reject messages | neutral `FORBIDDEN`/`NOT_FOUND` | SEC-113-10 |
| TM-6 | **Approver-chosen filter abuse**: injection through column/value, steering rows by hidden columns, raising masked columns | increment B | validator + enforcing compiler, deny-column rule | SEC-113-14..19 |
| TM-7 | **Fail-open fallbacks**: invalid template, unresolved group or missing template silently yields the widest (unfiltered) consent | proposal service | none (the plan falls back to the built-in snapshot) | **SEC-113-14, CHANGES REQUIRED** |
| TM-8 | **Non-person subjects**: app-only or service-principal tokens filing or deciding as an application id | `GetUserSid()` fallback to `appid`/`client_id`/`azp` | person-subject gate | SEC-113-05, SEC-113-06 |
| TM-9 | **Race and partial state**: approve vs withdraw, approve step committed without consent, consent without grant-policy check | repositories | guarded updates (A), atomic activation (B) | SEC-113-11, SEC-113-12 |

### P3.2 Verification of the pre-existing issues

**P-1 Read-only scope trap (`EntraTokenPolicy.cs:82-87`): VERIFIED, design weakness, NOT a live exploit today.** `scopes.Count > 0 && readOnlyScopes.Count > 0 && scopes.All(in readOnlyScopes)` marks a token read-only only when **every** `scp` entry is listed; any other scope makes it a full token (`:82-87`; defaults `Agent.Read`, `App.Read`, `JwtOptions.cs:190-195`). The classification is "read-only unless proven otherwise" in the wrong direction: it is an inclusion test, so an unlisted scope silently removes the restriction. Why it is not live today: `scp` of a v2 token contains only the scopes of **this** API's app registration, and the gateway lists exactly the two agent scopes; a token holds an unlisted scope only if the operator granted the agent application another scope of this API (a conscious grant of a write-capable scope) or exposes a new scope and consents it to the agent app. No code path adds a scope on its own. It becomes a **real bug the moment `data.access.request` is issued** to the agent application (architect G-1 is correct) unless A-1 lands in the same change. Also noted: `TokenAccessScope.GetScopes` reads only `scp` and the mapped scope URI (`TokenAccessScope.cs:25-29,39-44`), while `GetScopeSet(IEnumerable<Claim>)` also reads `scope`; a token with only a `scope` claim has zero scopes and is never read-only (`EntraTokenPolicy.cs:28,36`); Entra v2 uses `scp`, so this is theoretical, but the two readers must agree (SEC-113-03). **Severity:** Medium, latent (configuration-dependent), becomes High if A-1 is not shipped with the first issuance of the new scope. **WP-0 recommendation: NO standalone WP-0.** The fix is A-1 itself and must be the first commit of A with test T-S1; shipping it alone would change nothing observable today. Interim operational rule for the release note: never consent an agent application to any scope other than the listed ones.

**P-2 Approve/reject GraphQL mutations without scope or read-only check (`MutationTypes.cs:222-370`, `:385-465`): VERIFIED, defence-in-depth gap, NOT a live bug today.** `ApproveConsentRequestAsync` evaluates tenant (`:263-270`), self-approval (`:273`), approver authorization (`:277-291`, claims `GovernanceAdmin`/canonical ClusterAdmin at `:259`, else ownership/delegation) and nothing about scopes or the read-only marker; `RejectConsentRequestAsync` (`:385-465`) the same minus self-approval. The only protection against a read-only token is `ReadOnlyOperationMiddleware` (`ReadOnlyOperationMiddleware.cs:27-47`, registered at `GatewayServiceCollectionExtensions.cs:581`), which rejects every document containing a mutation operation for a read-only principal, and `ReadOnlyTokenMiddleware` (`:56-97`) at HTTP level. I found **no other route** to these resolvers: the MCP `query_graphql` path rejects mutations (`GatewayMcpQueryExecutor.cs:175`), the ITSM webhook calls the repository directly (`ItsmWebhookHandler.cs:373,389`), no other caller of the resolver methods exists. All other mutations (`RevokeConsentAsync` `:481`, `ReloadSchemaAsync`, the syncs) rely on the same blanket gate, so approve/reject are not special today. The gap becomes **critical exactly when A-3 opens the pipeline** for the self-service allowlist: from then on the blanket gate is no longer total, and a classifier defect would reach resolvers that have no second line. **Severity:** Low today, High after A-3 without A-4(b). **WP-0 recommendation: NO standalone WP-0 for the scope part**; the resolver-level guard (A-4 b) is mandatory in A and must be in place before A-3 is merged (SEC-113-02).

**P-3 Non-atomic approval (`MutationTypes.cs:293` then `:356`): VERIFIED, an integrity/availability defect, not a confidentiality or privilege bug.** The step (`ApproveConsentRequestStepAsync`, own transaction, status `APPROVED`) commits at `:293`; `IGrantPolicy` is evaluated **after** it (`:313-337`) and the consent is created in a second transaction at `:356`. Consequences: (a) if the grant policy denies at activation (class maximum changed, table sensitivity raised between submission and approval) or `CreateConsentAsync` fails, the request is `APPROVED` with no consent and cannot be approved again (`ConsentApprovalPolicy.EnsureApprovableStatus` rejects non-pending status), the idempotency record is not stored (`:361`), so the requester waits forever and the approver sees an error for an already-final request; (b) the failure direction is **safe** (no access is granted), and no path creates a consent for such a request without the policy: `ActivateConsentAsync` is called only from the ITSM handler after its own step (`ItsmWebhookHandler.cs:398`). **Severity:** Medium (integrity/availability, no escalation); the new self-service volume and the withdrawal race (SEC-113-11) raise its operational weight. **WP-0 recommendation: OPTIONAL narrow WP-0 (S), not blocking A:** (1) evaluate `IGrantPolicy` **before** the final step (the request already carries `RequestedValidTo`, so the decision can be made on the pending request; a denial then leaves the request pending and rejectable); (2) a status guard in the approve/reject `UPDATE` (`SqliteGovernanceRepository.Consent.cs:1192,1267`: no `AND status = ...`). The full atomic fix stays in B (A-13). If the owner prefers not to touch this before #113, A must carry (1) and (2) itself (SEC-113-11).

**P-4 (found during review, not in the architect list): app-only decider.** `GetAuthenticatedUserSid` uses `GetUserSid()` (`MutationTypes.cs:49-69`, `Sid.cs:21-36`) with fallback to `appid`/`client_id`/`azp`. With `AppOnlyTokens=ReadWrite` (non-default, `JwtOptions.cs:198`) an application token carrying a `GovernanceAdmin` role claim can approve and reject today under its application id (`MutationTypes.cs:259,281`). Severity Low/Medium, operator-opt-in; it contradicts the owner rule only if automation is not intended to decide (owner flag O-2). Covered by SEC-113-06; the minimal decision-guard (read-only + service-principal denial) belongs in the optional WP-0 if the owner wants this closed before A.

**Summary:** no finding is a live, unauthenticated or privilege-escalating bug today. The recommended **standalone WP-0 is limited to P-3 (1)+(2) and, optionally, the P-4 guard**; P-1 and P-2 are closed by the planned A-1/A-4 provided the conditions below on ordering hold.

### P3.3 Findings

Severity scale: Critical / High / Medium / Low. "A" and "B" give the increment that must close the finding.

| ID | Sev | Inc | Finding | Evidence | Required change |
|---|---|---|---|---|---|
| SEC-113-01 | High | A | **A token with an agent-class scope plus the review scope is a full token.** After A-1 the read-only test is `scopes.All(in ReadOnlyScopes + SelfServiceScopes)`; `data.access.review` is in neither list, so `{Agent.Read, data.access.review}` is **not** read-only (`EntraTokenPolicy.cs:82-87`) and A-4 condition 1 does not fire; condition 4 only catches self-service + review. An agent application consented to `data.access.review` becomes a decider with the person's rights. | `EntraTokenPolicy.cs:82-87`; plan A-1 item 1, A-4 conditions 1-4 | `ConsentActorPolicy` `Decide` additionally denies when the token carries **any scope of `ReadOnlyScopes` or `SelfServiceScopes`** (agent class), marker or not (condition 4 becomes "agent-class scope AND review scope"). Startup validation already forbids review in `ReadOnlyScopes`; keep it and add a warning log when an application receives both classes |
| SEC-113-02 | High | A | **Resolver guard must precede the pipeline opening.** A-3 makes the blanket mutation gate non-total; `ApproveConsentRequestAsync` and `RejectConsentRequestAsync` have no second line (P-2). Plan allows (b) as "defence in depth" with DI resolution that can be missing (`MutationTypes.cs:313-320` style `GetService` with a **fallback to a default instance**, `:316-318`). | `MutationTypes.cs:222-370,385-465`; plan A-4 | (a) The resolver guard (A-4 b) lands in the **same commit or earlier** than the classifier; (b) it uses `GetRequiredService` (throw), never the `?? new` fallback seen at `:316-318`; (c) no resolver of a `Decide`/B-surface field may be registered without the guard (reflection test over all mutation fields: every field not in the self-service allowlist must either be unreachable for a read-only principal or call the guard, T-S6) |
| SEC-113-03 | Medium | A | **Scope readers disagree.** `GetScopes` reads `scp` + mapped URI only; `GetScopeSet(claims)` also `scope`. The new `ConsentActorPolicy` and the marker logic need one definition; `IsSameScope` strips resource prefixes and compares case-insensitively (`EntraTokenPolicy.cs:93-103`), so configured `api://other/data.access.review` matches a token scope `data.access.review` regardless of the resource. | `TokenAccessScope.cs:25-29,39-70`; `EntraTokenPolicy.cs:93-103` | One scope reader for policy and middleware; startup validation rejects review/request scope names containing a resource prefix different from the configured API audience, or normalises both sides with the same function; test with `scope`-only, mixed-case and prefixed spellings |
| SEC-113-04 | High | A | **Self-approval and four-eyes rely on identifier sets that include mutable claims and a per-app `sub`.** `ExtractPrincipalIdentifiers` collects primarysid, nameidentifier, objectSid, oid, **sub**, upn, email, name (`ConsentRequestService.cs:236-267`); matching is OR over all of them (`MutationTypes.cs:734-740`) and in the repository by SID and data-owner id (`SqliteGovernanceRepository.Consent.cs:1067-1088`). This errs on the safe side (false positives) but (1) `sub` is pairwise per client application in Entra, so it cannot link an agent token and an interactive token; only `oid`/SID can, and an agent token **without** `oid`/SID/upn would store only `sub` and never match the approver; (2) the requester key chosen by `GetPersonSid()` is the first immutable claim in `GetUserSid()` order; the approver key via `GetUserSid()` must agree. | `Sid.cs:41-52,110-135`; `ConsentRequestService.cs:236-267`; `MutationTypes.cs:273` | `GetPersonSid()` and the request's `RequesterIdentifiers` must contain the **same canonical key** (`oid` or on-prem SID) for the same person on every token class; a person token without `oid` and without a SID-class claim is **not** a person for self-service (fail closed, extends A-2); self-approval keeps matching by the full set; tests T-S9, T-S10 |
| SEC-113-05 | Medium | A | **Person gate depends on resolver heuristics.** `IIdentitySubjectResolver` classifies a delegated token without upn/email/preferred_username/unique_name as service principal; certificate/Kerberos principals have no `oid`. The plan rejects them (fail closed, good) but `GetPersonSid()` must not accept `sub`/`NameIdentifier` alone: a pairwise `sub` as requester key cannot be matched against any approver key. | `IdentitySubjectResolver.cs:10-80`; `Sid.cs:41-52` | `GetPersonSid()` accepts `oid`, on-prem SID claims only for the **self-service** path; `sub`/`NameIdentifier` alone is rejected for the self-service path (full tokens keep `GetUserSid()` unchanged) |
| SEC-113-06 | Medium | A | **Service principals may decide today (P-4)** and full app-only tokens may file requests (A-2 hardening only "owner flag O-2"). | `MutationTypes.cs:233,259,281`; `JwtOptions.cs:198` | Adopt A-4 condition 2 and the A-2 hardening **unconditionally** (not behind an owner flag); decision for the owner only if a documented automation approves requests (O-2) |
| SEC-113-07 | High | A | **Auto-approve would approve agent requests.** The insecure `IsAutoApproveEnabled` path activates every submission immediately (`ConsentRequestService.cs:140-151`), including self-service ones; in that mode an agent widens its own reach in one call. | `ConsentRequestService.cs:140-151`; `GatewayOptions.cs:109` | A self-service submission (`submitted_via = self-service`, or any submission from an agent-class token) is **never** auto-approved: it stays `PENDING`; startup validation logs an Error when `SelfServiceScopes` is non-empty together with `IsAutoApproveEnabled` and refuses to start unless the environment is the documented getting-started profile; test T-S12 |
| SEC-113-08 | Medium | A | **Justification is the larger injection surface than `context`.** `context` is sanitised (A-5) but `justification` (up to 2000 chars, `ConsentRequestService.cs:60-80`, only length checks) is equally agent-controlled and shown to approvers; no control-character, bidi (U+202A-202E, U+2066-2069), zero-width or NUL handling. MCP tool results echo the stored text back to agents. | `ConsentRequestService.cs:60-80`; plan A-5, A-9 | Apply the same normalisation to `justification` and `context`: NFC, reject control characters (except LF in justification), bidi override and zero-width characters, NUL; JSON-escape on output; cap per field; MCP/GraphQL read results mark both fields as untrusted user data (field name documented, no instruction-like wrapping by the gateway); `pendingApprovalsForMe` returns a `submittedVia` and the caller-application label so the approver sees that an **agent** filed the request; the text is never logged and never used for routing, approver choice or any tool argument |
| SEC-113-09 | Medium | A | **Limits are per person only and count-then-insert.** One prompt-injected agent can (a) fill the person's 25 pending slots with requests for the same table, starving the person's own requests, (b) flood approver inboxes/ITSM (`PENDING_EXTERNAL_APPROVAL` creates tickets). No duplicate suppression; no distinction between agent and person quota. | plan A-7; `ConsentRequestService.cs:86-93` | (1) Lower **self-service sub-cap** (default 5 open, 10 per hour) next to the person caps; (2) **at most one open request per (person, table)**: a second identical open request returns the existing request id (no new row, no ticket); (3) counts by canonical person key and tenant (A-7 is right); (4) ITSM dispatch happens only after the limit checks; (5) denial by limit writes a metric, not a Tier-A event per attempt (audit flood) |
| SEC-113-10 | Low | A | **Existence oracles remain on the decision side.** Approve/reject answer `NOT_FOUND` for unknown ids but `FORBIDDEN` with "belongs to another tenant" for foreign tenants (`MutationTypes.cs:263-270,440-446`) and, for non-approvers, a message that echoes the **table identifier** of any request id (`:283-288`). Request ids are GUIDs (not enumerable), so the risk is low; the new B surfaces repeat the pattern. Submit: hidden and unknown tables must stay in one code path with the same timing class (both skip the grant policy and all writes); the idempotency lookup (`:86-93`) precedes the table lookup but replays a stored result for the same caller only (own data, acceptable). | `MutationTypes.cs:263-270,283-288,440-446`; plan A-8 | Foreign-tenant and unknown ids answer the same `NOT_FOUND` on approve, reject and the new queries; the non-approver message drops the table identifier; A-8 order: input validation, visibility (`CatalogVisibility` `Discovery`, #111), then limits, then grant policy; hidden and unknown share one code path (T-S14) |
| SEC-113-11 | High | A | **Approve vs withdraw race and stale status writes.** The approve step and the reject write do `UPDATE CONSENT_REQUESTS SET status = ... WHERE id = @id` without a status guard (`SqliteGovernanceRepository.Consent.cs:1192,1267`); SQLite relies on a write transaction, PostgreSQL on `FOR UPDATE` (`PostgreSqlGovernanceRepository.Consent.cs:1210`). Once `WITHDRAWN` exists, an approval that read `PENDING` before the withdrawal can overwrite `WITHDRAWN` with `APPROVED` and create a consent for a request the person withdrew. Also `EnsureApprovableStatus` must treat `WITHDRAWN` as final. | `SqliteGovernanceRepository.Consent.cs:1056-1088,1192,1267`; `ConsentApprovalPolicy.cs:12-15,100-112` | The status guard (`AND status = @expected`, zero rows = conflict) on approve, reject **and** withdraw in all three engines in A (plan A-6 states it; make it a hard binding condition), `WITHDRAWN` added to the non-approvable set, and the GraphQL consent creation after the step must re-check status (until B makes it atomic). Fold P-3 (1): `IGrantPolicy` before the step. Tests T-S15, T-S16 |
| SEC-113-12 | Medium | A+B | **`IGrantPolicy` coverage per path.** Submission: evaluated (`ConsentRequestService.cs:106-120`, `GranteeType.User`, class maximum). GraphQL approval: evaluated **after** the step (`MutationTypes.cs:313-337`, P-3). ITSM activation and the auto-approve activation: **not** evaluated at activation, they copy `requested_valid_to` fixed at submission (`ItsmWebhookHandler.cs:398`; `ConsentRequestService.cs:140-151`), so a class maximum lowered after submission is not applied. | as cited | A: policy before the final step (see SEC-113-11). B: atomic with consent creation (A-13), with the repository second line **re-deriving the class from table metadata through the same `GrantClassifier`** (not from caller-supplied input); the ITSM overload and the (guarded, SEC-113-07) auto path evaluate `IGrantPolicy` at activation with `Now`; `ValidTo = null` or beyond maximum for User/ServicePrincipal grantees is refused in the repository; approver cannot extend `ValidTo` in the configuration (A-14 offers no duration, keep it so) |
| SEC-113-13 | Medium | A | **Audit.** The plan writes `ACCESS_REQUEST_SUBMITTED` after the row and compensates by delete (A-10): between insert and audit the request is visible and approvable; a delete failure leaves an unaudited request. `CONSENT_DENIED` per denied decision can be flooded by an injected agent (each audit write is `FailClosed`). Audit must carry enough to attribute agent-filed requests. | plan A-10; `ConsentRequestService.cs:172-191` | Submission row and audit entry in **one transaction** (repository method taking the audit entry, as the withdraw does), no compensation window; audit fields: canonical person key, token class, `submitted_via`, caller-application label (`CallerContext`), justification/context **length and SHA-256 prefix**, never text; decision denials aggregated per principal and minute (first denial audited, count in the metric) |
| SEC-113-14 | **High** | B | **Fail-open fallback to the widest consent.** A-15 and the fail-closed table (section 7) let an invalid/tampered template, an unavailable or empty group resolution, or a missing dataset default fall back to the **built-in default = column snapshot with no row filter** (all rows), and a non-admin may approve the built-in default unchanged (A-15 who-may-choose). A group template that was meant to narrow rows (for example a regional filter) is silently replaced by an unfiltered consent exactly when the resolver fails or the user is absent from `ROLE_MEMBERS`. This is the direction of an agent or outage widening reach. | plan A-15, A-16, section 7 rows "Group resolver fails", "Template stored JSON malformed" | If **any** template (group-bound or default) exists for the dataset and it cannot be applied (resolution `unavailable`, validation failed at proposal time, ambiguity without choice), the proposal is `BLOCKED`: the approval answers `CONFIGURATION_BLOCKED` and nothing is written; only a governance admin may then approve a free-form validated configuration, and the audit records the override. The built-in default is offered only for datasets with **no** templates at all and for `Public`/`Internal` sensitivity (the same ceiling as `AutoApproveMaxSensitivity`) |
| SEC-113-15 | High | B | **Group templates over `ROLE_MEMBERS` are weak as a trust source.** `GetTransitiveRolesAsync` is **not transitive** (a single join, `SqliteGovernanceRepository.Catalog.cs:992-1010`; PostgreSQL `:766`, SQL Server `:786`), ignores `member_type`, has **no tenant predicate**, and compares `member_sid` exactly. `ROLE_MEMBERS` has no application writer; rows are inserted only by schema seeding (`PostgreSqlGovernanceRepository.Schema.cs:286`, `SqlServerGovernanceRepository.Schema.cs:185`), so membership is whatever an administrator put into the database: stale (no sync with the directory) and unaudited. The plan restricts the lookup to the immutable subset of `RequesterIdentifiers` (A-15) but that list also holds upn/email/**name** (`ConsentRequestService.cs:252-258`): a mutable display name equal to a stored `member_sid` would match. | cited | (1) Group resolution uses **only** the canonical person key and immutable SID/oid claims stored at submission (`RequesterSid`, immutable subset), never upn/email/name; (2) a new tenant-scoped, `member_type`-aware read (do not reuse `GetTransitiveRolesAsync` as is; transitivity is not required, but then the plan and docs must not call it transitive); (3) group templates are **disabled by default** (`ConsentRequests:GroupTemplates:Enabled=false`) until an owner confirms how `ROLE_MEMBERS` is maintained (O-3); (4) approval audit stores matched keys, resolution state and the membership row version/timestamp; (5) a consent created from a group template is frozen: later group changes do not revoke it, documented as a property of the design, and `ValidTo` (class maximum) is the bound |
| SEC-113-16 | Medium | B | **Validator/enforcer parity details.** The enforcer upper-cases the operator (`TypedRowFilterBuilder.cs:57` region `op = filter.Operator.ToUpperInvariant()`) and builds identifiers by regex `^[a-zA-Z_][a-zA-Z0-9_]*$` (`:22-23`), values are policy parameters. Risks the plan has not fixed: (a) `LIKE '%'` or `IN` with every value, `NEQ` on a non-existent value are syntactically valid but **non-restrictive**; (b) an **empty filter list means "no restriction"** (`BuildCombined`), so a configuration whose filters were all dropped by normalisation silently widens; (c) the error message includes the column text (`:150-153`), a log-injection vector with approver-controlled input; (d) type check against the catalog type is claimed but `ValueType` mapping for `decimal`/`date` is not specified; (e) `USER_ATTRIBUTE` compiles to a constant (`:137-141`), must stay rejected on approval. | `TypedRowFilterBuilder.cs:22-27,130-165` | Validator persists **canonical** operator (upper) and catalog spelling of the column; a configuration that declares filters must still hold the same number of **compilable** filters after canonicalisation (no silent drop); a template or approver filter that is provably non-restrictive (`LIKE` pattern of only `%`/`_`, `IN` covering the full domain is not decidable, so only the `LIKE` case) is rejected as `FILTER_NOT_RESTRICTIVE`; error messages and logs carry no column or value text from the input; shared operator constant exposed as planned; corpus T-S19 |
| SEC-113-17 | Medium | B | **Admin definition and sensitive raising.** "Governance admin" for free-form configuration is the claim test of `MutationTypes.cs:259` (`GovernanceAdmin` role or canonical ClusterAdmin), including a **tenant-scoped** GovernanceAdmin. #111 rev. 3 restricts shared-table levers in `Multi` mode (`Gateway:Tenancy:Mode`); free-form `Mask` to `Clear` on a sensitive column is a stronger lever than a tier change. | `MutationTypes.cs:259`; PLAN-GOV-111 rev. 3 SEC-111-03 | Free-form configuration and any `Mask`-to-`Clear` raise of a sensitive/masked column require the admin class (claims) **and** always a second distinct approver (four-eyes forced for that approval, regardless of `requires_four_eyes`); in `Multi` mode a tenant-scoped admin may do this only for tables of their tenant per the #111 `CallerScopeResolver` rule, else `FORBIDDEN`; the check is by claim at the backend, never from the request |
| SEC-113-18 | Medium | B | **Frozen configuration and proposal hash.** `proposalHash` over source, template id/revision, column levels, filters, table id (A-16) omits the **request id, tenant and requester key**, so a hash computed for one request is valid for another with the same proposal (replay across requests of different persons); `CONFIGURATION_MISMATCH` compares hashes but the second step must also re-run authorization of the **second approver for the same configuration**. | plan A-16 | Hash input adds request id, tenant id, requester canonical key and the requested `ValidTo`; step 2 re-validates the stored frozen configuration with the validator at step-2 time and compares the live table metadata hash (columns changed between steps = `PROPOSAL_CHANGED`); same approver cannot give both steps (existing check, test with different claim spellings) |
| SEC-113-19 | Medium | B | **Template store integrity.** Template rows are re-validated at proposal time (good) but templates themselves hold `column_rules_json`/`row_filters_json` that a database writer can tamper; templates are only authored by admin API or manifest. A manifest applies **all-or-nothing** but wins over admin edits silently. Template changes do not bump the epoch (fine, nothing granted) but a template **deletion/edit between preview and approval** is handled by `PROPOSAL_CHANGED`. | plan A-15, A-19 | Template `PUT`/`DELETE` and manifest changes audited with before/after **hashes** (no filter values in log lines, as planned); `FailClosed`; tenant scope on every read; the validator runs on **read** for every template used (a tamper fails closed per SEC-113-14, not to default); role of the admin determined by claims |
| SEC-113-20 | Medium | B | **Auto-approve and ITSM paths.** A-17 applies the dataset-default template, else the built-in snapshot, for ITSM and auto-approve activation. A missing template again means unfiltered, and ITSM approval comes from an external board that never saw the configuration. | plan A-17 | ITSM and (guarded) auto activation apply the dataset-default template **only if one exists and validates**; if the dataset has any template and no valid default, the request stays pending with an alert (fail closed); the ITSM ticket text carries the configuration summary (hash and filter column names, no values) so the board approves what will be activated; sensitivity ceiling as planned |
| SEC-113-21 | Low | B | **Preview and proposal queries as oracles.** `proposedAccessConfiguration` returns candidates, group resolution and an effective-access preview (A-18); the preview uses the explanation service over a hypothetical consent and may disclose column names or filter values to anyone authorised to decide on that request. | plan A-18 | Guard with `Decide` **and** approver authorization of that request (planned); preview contains column names and levels only for the table of the request, never filter values of other requests or templates of other tables; not cached across callers; unknown/foreign request ids answer the same `NOT_FOUND` |
| SEC-113-22 | Low | B | **Approve resolver replacement.** Replacing the two-call path (A-13) changes the semantics for existing clients; the legacy overload without configuration must run through the **same** guards (`ConsentActorPolicy`, validator, built-in default only under SEC-113-14). A legacy call must not become a bypass of the new blocked state. | plan A-13, R-5 | Legacy overload = approve with `confirmProposed` against the current proposal; if the proposal is blocked or requires confirmation it fails the same way; test T-S27 |

### P3.4 Binding conditions

**Increment A (APPROVED WITH CONDITIONS)**

1. **Order of merge inside the PR (SEC-113-02):** resolver guard and `ConsentActorPolicy` before the classifier opens the allowlist; the policy resolves with `GetRequiredService`; the DI composition test and the all-mutations reflection test (T-S6) are part of the first red commit.
2. **Token classes (SEC-113-01, -03):** union rule only in `EntraTokenPolicy`; one scope reader; `Decide` denied for read-only, agent-class (any `ReadOnlyScopes` or `SelfServiceScopes` scope), non-person, missing review scope when required, and mixed tokens; startup validation exactly as A-1 plus the resource-prefix rule.
3. **Person gate (SEC-113-04, -05, -06):** requester key is the canonical `oid`/on-prem SID; `sub`-only tokens do not qualify for self-service; app-only and service-principal tokens neither request (self-service and full) nor decide, unconditionally.
4. **No auto-approval of agent requests (SEC-113-07).**
5. **Status guards and policy order (SEC-113-11, -12 part A):** guarded `UPDATE` on approve, reject and withdraw on three engines; `WITHDRAWN` non-approvable; `IGrantPolicy` before the final step; a withdrawn or no-longer-pending request never produces a consent.
6. **Text hygiene (SEC-113-08):** one normaliser for `justification` and `context`; `submittedVia` and caller label returned to approvers; text never logged, never routed.
7. **Limits (SEC-113-09):** self-service sub-cap, one open request per (person, table), fail-closed on repository failure, no ticket before limit checks.
8. **Visibility (SEC-113-10):** order input, visibility, limits, grant policy; same code path and text for hidden and unknown; depends on #111 WP-0/I-7 (A must not ship before); approve/reject messages drop table id and foreign-tenant wording.
9. **Audit (SEC-113-13):** submission and audit in one transaction; attribution fields; denial aggregation.
10. **MCP (A-9):** the marker is dropped when sent as a claim or `AdditionalClaims` (T-18 already); tools are listed only for person-bound sessions with the marker; no approve/reject tool; the tool descriptions state that requests are filed only on an explicit instruction of the user; tool results carry the request id, status and the neutral error text only (no echo of other people's data); sessions inherit the per-person limits.
11. **Notifications:** none are introduced by A; keep it so. Any later notification (email, chat) must send ids and counts only, never `justification` or `context` text, and must not deep-link with approval capability.

**Increment B (CHANGES REQUIRED; revision 2 of the plan must close the following before Phase 4 of B, then a narrow re-review)**

1. SEC-113-14: blocked-proposal semantics instead of fallback to the unfiltered default; admin override audited.
2. SEC-113-15: group templates disabled by default, immutable canonical key only, tenant-scoped membership read, honest naming (not transitive).
3. SEC-113-16, -17, -18, -19, -20: validator and enforcer parity corpus, forced four-eyes for sensitive raising and free-form, hash inputs, template integrity, ITSM/auto fail-closed.
4. SEC-113-12 (B part): atomic activation with repository second line re-deriving the class from table metadata; grant policy on ITSM and auto paths at activation.
5. SEC-113-21, -22: preview scope, legacy overload equivalence.
6. B depends on A merged, #107 complete (typed filter storage and `InsertConsentCoreAsync`) and on the #129 plan staying out of B (no `USER_ATTRIBUTE` filters on approval).

### P3.5 Mandatory security tests (in addition to the plan's list; red first)

**Increment A**

| ID | Test |
|---|---|
| T-S1 | Token class matrix including `{Agent.Read, data.access.request}` read-only+self-service and the **regression** that `{Agent.Read, data.access.review}` is full but cannot decide (SEC-113-01); feature off behaves as today |
| T-S2 | `{data.access.request, data.access.review}` and `{Agent.Read, data.access.review}` and `{App.Read, data.access.review}`: `Decide` denied with `DECISION_NOT_PERMITTED`, no repository call |
| T-S3 | Scope spellings: `scope`-only claim, mixed case, `api://x/data.access.review` in configuration vs bare token scope; policy and middleware agree |
| T-S4 | Read-only token calls `approveConsentRequest`/`rejectConsentRequest`: pipeline rejects; with the pipeline bypassed (direct resolver) the resolver guard rejects |
| T-S5 | Missing `IConsentActorPolicy` in DI makes the resolver throw, never allow |
| T-S6 | Reflection over every mutation field: each is outside the self-service allowlist and unreachable for a read-only principal, or explicitly allowlisted; adding a mutation without classification fails CI |
| T-S7 | Classifier corpus (aliases, fragments, cycles, unknown operation, batching, introspection on mutation root) ends in whole-document `READ_ONLY_TOKEN` |
| T-S8 | App-only token (`idtyp=app`, `appid`/`azp`/`client_id` only, `AppOnlyTokens=ReadWrite` with `GovernanceAdmin` role): request, withdraw, approve, reject, MCP tools all `FORBIDDEN`/`DECISION_NOT_PERMITTED`; requester key is never an application id |
| T-S9 | Self-approval across token classes: agent token (oid only) requests, the same person's interactive token (oid + primarysid + upn, review scope, owner rights) approves: rejected; reverse; different claim spellings; two steps by the same person under two spellings |
| T-S10 | A token with `sub` only is rejected for self-service; `GetPersonSid` and `GetUserSid` return the same key for oid tokens |
| T-S11 | Delegated token without human claim rejected; with `upn` and `oid` accepted |
| T-S12 | Auto-approve enabled: self-service request stays `PENDING`; startup validation reacts to the combination |
| T-S13 | Justification/context hygiene: bidi, zero-width, NUL, control characters rejected; JSON escaping; not logged (log capture); not part of routing; returned with `submittedVia` |
| T-S14 | Hidden and unknown dataset: byte-identical response and identical code path; limit check cannot distinguish them; approve/reject non-approver message has no table id; foreign tenant request id equals unknown |
| T-S15 | Approve racing withdraw on all three engines: exactly one terminal state; no consent for a `WITHDRAWN` request; approve after `WITHDRAWN` rejected |
| T-S16 | Grant policy denial at activation leaves the request pending (policy before step), no `APPROVED` without consent |
| T-S17 | Limits: self-service sub-cap, duplicate open request for the same table returns the existing id and creates no ticket, repository failure denies |
| T-S18 | Audit: submission and audit in one transaction (failure injection leaves nothing); denial aggregation; no text in any audit payload |
| T-S19 | MCP: marker injection through claims and `AdditionalClaims` dropped; approve/reject tool absent in `tools/list` and in the registry; request tool absent for non-person sessions |

**Increment B**

| ID | Test |
|---|---|
| T-S20 | Blocked proposal: invalid template, tampered template JSON, resolver `unavailable`, group templates present but no match and no valid default: non-admin approval `CONFIGURATION_BLOCKED`, nothing written; admin override audited |
| T-S21 | Group resolution uses the immutable canonical key only: a requester whose display name or email equals a stored member sid does not match; foreign tenant membership not used; group templates off by default |
| T-S22 | Injection and non-restrictive corpus (plan test 23 plus `LIKE '%'`, operator spelling variants, column canonicalisation, all filters dropped); validator-accepted configuration always compiles; no input text in error or log |
| T-S23 | Sensitive `Mask` to `Clear` and free-form need admin claims plus a forced second distinct approver; tenant-scoped admin in `Multi` mode limited to own-tenant tables |
| T-S24 | Hash replay: a hash for request R1 does not confirm request R2 with the same proposal; step 2 re-validates against live metadata |
| T-S25 | Atomicity with failure injection after step, consent, epoch bump on three engines; repository second line refuses null or oversized `ValidTo` and recomputes the class itself |
| T-S26 | ITSM and auto activation: dataset with templates but no valid default stays pending; grant policy evaluated at activation |
| T-S27 | Legacy approve overload follows the same blocked and confirmation rules |
| T-S28 | Template admin API: non-admin 403, tenant isolation, manifest wins, audit with hashes only, validator on read |

### P3.6 Recommendations for the owner flags

| Flag | Recommendation |
|---|---|
| **O-1** review scope default off | **Confirm** (off unless `SelfServiceScopes` or `ReviewScopes` is set). Add one hard rule: as soon as `SelfServiceScopes` is non-empty the review requirement cannot be switched off, and tokens without `scp` remain exempt only from condition 3 (not from read-only, person, agent-class). Document that scope-less schemes (Basic, Kerberos, ForwardAuth) are interactive-human channels; an agent must not hold such credentials |
| **O-2** service principals never decide, full app-only cannot file | **Confirm, unconditionally** (SEC-113-06). If an automation must approve, it needs a separate, reviewed design, not this path |
| **O-3** group templates from `ROLE_MEMBERS`, precedence group before dataset default | **Do not ship enabled in B.** `ROLE_MEMBERS` has no writer, is not transitive, has no tenant predicate (SEC-113-15). Ship the seam with the feature **off by default**; precedence group-bound before dataset default is correct (more specific wins) provided SEC-113-14 makes a failed resolution block instead of falling back |
| **O-4** ITSM/auto: dataset-default template only | **Confirm with changes**: only if a valid default exists, otherwise pending (SEC-113-20); auto-approve never for self-service (SEC-113-07) |
| **O-5** mixed request+review token cannot decide | **Confirm and widen** to "agent-class scope plus review scope" (SEC-113-01) |
| **O-6** visibility for every caller on submit | **Confirm** (closes an existence oracle that full tokens have today; aligned with the #111 WP-0 consent-based rule and with `BundledConsentRequestService`); ship with #111 persisted tiers (I-7) |
| **O-7** two MCP tools | **Confirm** (without them agents cannot use the feature), under SEC-113 conditions 10 and 11: person-bound sessions only, no decision tool, per-person limits, explicit-user-instruction wording in the tool description, untrusted-text labelling in results |

### P3.7 Verdicts

| Increment | Verdict | Basis |
|---|---|---|
| **A** (self-service request and withdraw, decision guard, MCP tools) | **APPROVED WITH CONDITIONS** | Design is sound and fail-closed: closed root-field allowlist, request/review scope split, person gate, tenant-scoped withdrawal with identical answers. Binding conditions: SEC-113-01..13 (section P3.4, A items 1-11). Highest risks: agent-class token holding the review scope (01), missing resolver-level guard when the pipeline opens (02), auto-approve of agent requests (07), approve/withdraw race (11). No standalone WP-0 required; optional narrow WP-0 for P-3 (1)+(2) and the P-4 guard. |
| **B** (approver-chosen row filters and column levels, templates, atomic approval) | **CHANGES REQUIRED** | The fallback to the unfiltered built-in default on any template, resolution or validation failure (SEC-113-14) and the weak trust source for group templates (SEC-113-15) are design-level fail-open behaviours; the plan must be revised (revision 2) and narrowly re-reviewed. SEC-113-12 (B part) and -16..-22 are binding. Start only after #107 and A are merged. |
