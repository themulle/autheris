# Plan: `Gateway:VirtualFilters:RequireApproval=false` seems to have no effect (Issue #923)

- Issue: [#923](https://github.com/themulle/autheris/issues/923). Phase 2 plan, 2026-11-19. Code facts verified on `origin/main` after the v1.9.0 tag.
- Type: the behaviour is by design, but the documentation and the API response do not explain it. The fix makes the behaviour explicit. The approval rule does not change.
- Phase 3: **required (light)**. The change touches the virtual-filter approval workflow. The recommended option keeps the gate unchanged; option C below would need a full Phase 3.

## 1. Problem and evidence

1. The option is read correctly. `src/Autheris.Application/VirtualFilters/VirtualFilterAdministrationService.cs:136` binds `GatewayOptions.VirtualFilters`, and the default is `false` (`src/Autheris.Domain/Options/Security/RlsOptions.cs:25`).
2. `SaveFilterAsync` (`VirtualFilterAdministrationService.cs:198`) sets `needsApproval = !actor.IsSync && (_options.RequireApproval || filter.Sql != null)`. A **Stage-2 SQL filter** (`sql: from … where … target.<col>`, `src/Autheris.Domain/Model/VirtualFilterModels.cs:100-104`) is therefore always stored as `PendingApproval`, whatever the option says. Structured filters (`from`/`where`/`joins`) become `Active` directly when the option is `false` (lines 253-271).
3. This is intended and pinned by `tests/Autheris.Tests.Unit/Policy/RowScopes/RowPolicyApprovalTests.cs:134` ("Even with RequireApproval=false, SQL policies start PendingApproval").
4. Approval of a SQL filter needs two things:
   - an approver distinct from the creator (`VirtualFilterAdministrationService.cs:303`);
   - an approver who is an owner or delegate of **every** table the SQL references (`:313-320`). This applies to GovernanceAdmin too.
   With a single admin and no ownership metadata, only the sync path (`actor.IsSync`) activates such a filter. That matches the observation in the issue.
5. The reporter's filter uses the `from … where … target.<col>` form, the same form as in #921, so it is a Stage-2 SQL filter.
6. The documentation does not mention the exception:
   - `docs/reference/configuration.md:1152` and the XML doc at `RlsOptions.cs:22-24` say only "manual changes … require four-eyes approval".
   - The `PUT` response (`src/Autheris.Api/Endpoints/VirtualFilterEndpoints.Crud.cs:51-52`) returns `status: PendingApproval` with no reason.
7. Access profiles (`VirtualFilterAdministrationService.Evaluation.cs:120`) follow the option with no SQL exception. That is correct.

## 2. Options

| Option | Description | Verdict |
|---|---|---|
| A | Make `RequireApproval=false` also skip approval for SQL filters | Rejected. A SQL filter can join other tables, and owner consent for every referenced table is the control. Skipping it would widen what an admin can disclose. |
| B | Keep the rule. Make it explicit: (1) the option docs say that SQL filters always need owner approval; (2) the `PUT` response carries `approval_reason`; (3) the pending audit event carries the same reason | **Recommended.** No change to the security model. Closes the issue's "or document it" branch. |
| C | Development-only bypass: in `Development` with `RequireApproval=false`, SQL filters become active directly, with an audit event `VIRTUAL_FILTER_SQL_APPROVAL_BYPASSED` | Owner option only. It adds an environment-dependent gate, so it needs a full Phase 3 and a startup-validator ratchet. |

## 3. Decisions (option B)
1. D-923-1: the approval rule stays: `needsApproval = !IsSync && (RequireApproval || Sql != null)`.
2. D-923-2: new enum `VirtualFilterApprovalReason { None, RequireApprovalOption, SqlDefinitionNeedsOwnerApproval }` in `src/Autheris.Domain/Model/VirtualFilterModels.cs`. `SaveFilterAsync` computes it next to `needsApproval`. When both apply, `SqlDefinitionNeedsOwnerApproval` wins.
3. D-923-3: the `PUT /virtual-filters/{name}` view (`FilterView` in `VirtualFilterEndpoints.Crud.cs`) adds `approval_reason` (snake_case: `none`, `require_approval_option`, `sql_definition_needs_owner_approval`). It is computed from the stored filter, so `GET` returns the same value. Do not persist it; derive it from `Status`, the draft and `Sql`.
4. D-923-4: the `VIRTUAL_FILTER_PENDING_APPROVAL` audit details (lines 226-227 and 248-249) gain `reason`.
5. D-923-5: docs (reference only, no progress notes): update `docs/reference/configuration.md:1152`, the XML doc at `RlsOptions.cs:22-24` and `docs/features/f-gov-09-virtual-filters.md`. Say that SQL (Stage 2) filters always need approval by a distinct owner or delegate of every referenced table, or activation through the sync path.

## 4. Work packages (one PR)

### WP-923-1 (S): approval reason in service, view and audit
- Files:
  - `src/Autheris.Domain/Model/VirtualFilterModels.cs`: the enum.
  - `src/Autheris.Application/VirtualFilters/VirtualFilterAdministrationService.cs`: add `internal static VirtualFilterApprovalReason ApprovalReason(VirtualFilter filter, VirtualFilterOptions options, VirtualFilterActor actor)` and use it at line 198. Audit details at lines 226-227 and 248-249.
  - `src/Autheris.Api/Endpoints/VirtualFilterEndpoints.Crud.cs`: `FilterView` gains `approval_reason`.
- Unit tests (filter `RowPolicyApprovalTests|VirtualFilterApprovalReasonTests`):
  - New `tests/Autheris.Tests.Unit/Policy/RowScopes/VirtualFilterApprovalReasonTests.cs`, with `[Trait("Category", "Security")]` on the first two:
    - `SqlFilter_RequireApprovalFalse_ReasonIsSqlOwnerApproval_StatusPending`
    - `StructuredFilter_RequireApprovalFalse_IsActive_ReasonNone`
    - `StructuredFilter_RequireApprovalTrue_ReasonIsOption`
    - `SyncActor_SqlFilter_ReasonNone_Active`
  - Keep `RowPolicyApprovalTests.cs:134` unchanged; it is the ratchet for D-923-1.
- Acceptance: the `PUT` of a SQL filter with the option `false` returns `PendingApproval` and `approval_reason = sql_definition_needs_owner_approval`.

### WP-923-2 (XS): reference docs
- Files: `docs/reference/configuration.md:1152`, `RlsOptions.cs:22-24`, `docs/features/f-gov-09-virtual-filters.md` (approval section). Description text only.
- Acceptance: the configuration description test or snapshot, if any (`git grep -n "RequireApproval" tests/Autheris.Tests.Architecture`), stays green. `secure-defaults.txt:81` is unchanged.

### Order
WP-923-1, then WP-923-2. Independent of #922. The four-eyes approval of structured filters with `RequireApproval=true` still needs a second Governance.Approve user on PostgreSQL, which #922 unblocks.

## 5. Integration test impact
- `tests/Autheris.Tests.Integration/Governance/VirtualFilterEndpointsTests.cs` (around line 199): add `PutSqlFilter_RequireApprovalFalse_ReturnsPendingWithReason`. Check for strict anonymous-object or JSON snapshot assertions on the filter view and add the new field.
- `tests/Autheris.Tests.Integration/Governance/VirtualFilterAdministrationServiceTests.cs:372,396`: no change expected; re-run in CI.

## 6. Owner questions
- Q-923-1: keep mandatory owner approval for SQL filters even with `RequireApproval=false` (option B), or add a Development-only bypass (option C)? **Default taken: option B.**
- Q-923-2: rename the option to make its scope clear (for example `RequireApprovalForStructuredFilters`)? **Default taken: no; the docs fix is enough and avoids a configuration break.**
