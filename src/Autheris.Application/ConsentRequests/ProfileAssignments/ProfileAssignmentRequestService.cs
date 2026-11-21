namespace Autheris.Application.ConsentRequests.ProfileAssignments;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;

/// <summary>
/// Orchestrates submission and per-item/whole-request approval of <see cref="ConsentRequestKind.ProfileAssignment"/>
/// requests through <see cref="IConsentDecisionPipeline"/> and <see cref="IProfileGrantMaterializer"/>.
/// </summary>
public sealed partial class ProfileAssignmentRequestService(
    IAccessProfileRepository profileRepository,
    ITableMetadataRepository metadataRepository,
    IConsentApprovalRepository approvalRepository,
    IDataOwnershipRepository ownershipRepository,
    IProfileAssignmentStore store,
    IProfileGrantMaterializer materializer,
    IGrantPolicy grantPolicy,
    IGrantValidityPolicy validityPolicy,
    IGrantWriter? grantWriter = null,
    IConsentWorkflowRouter? workflowRouter = null,
    IConsentEventOutbox? eventOutbox = null,
    TimeProvider? timeProvider = null,
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null,
    ISeparationOfDutiesService? separationOfDutiesService = null)
    : IProfileAssignmentRequestService, IProfileAssignmentDecisionHandler
{
    private readonly IAccessProfileRepository _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
    private readonly ITableMetadataRepository _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
    private readonly IConsentApprovalRepository _approvalRepository = approvalRepository ?? throw new ArgumentNullException(nameof(approvalRepository));
    private readonly IDataOwnershipRepository _ownershipRepository = ownershipRepository ?? throw new ArgumentNullException(nameof(ownershipRepository));
    private readonly IProfileAssignmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IProfileGrantMaterializer _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
    private readonly IGrantPolicy _grantPolicy = grantPolicy ?? throw new ArgumentNullException(nameof(grantPolicy));
    private readonly IGrantValidityPolicy _validityPolicy = validityPolicy ?? throw new ArgumentNullException(nameof(validityPolicy));
    private readonly IGrantWriter? _grantWriter = grantWriter;
    private readonly IConsentWorkflowRouter? _workflowRouter = workflowRouter;
    private readonly IConsentEventOutbox? _eventOutbox = eventOutbox;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ISeparationOfDutiesService _separationOfDuties = separationOfDutiesService ?? new SeparationOfDutiesService(groupStore, realmRegistry);

    public async Task<SubmitConsentRequestResult> SubmitAsync(ProfileAssignmentSubmission submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var (granteeType, granteeRef) = ResolveAndValidateGrantee(submission);
        var tenantId = ResolveTenant(submission);
        EnsureCallerAuthorizedToAssign(submission, granteeType, granteeRef);

        var profile = await LoadProfileOrThrowAsync(tenantId, submission.ProfileId, ct).ConfigureAwait(false);
        if (profile.TargetTables.Count == 0)
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "Access profile contains no target tables or patterns.");
        }

        var now = _time.GetUtcNow();
        var justification = ResolveJustification(submission, profile);
        var validTo = ResolveRequestedValidTo(submission, now);
        var validFrom = submission.RequestedValidFrom ?? now;

        var tenantTables = await LoadTenantTablesAsync(tenantId, ct).ConfigureAwait(false);
        var snapshotItems = BuildSnapshotItems(profile);
        var matchedTables = ResolveMatchedTables(snapshotItems, tenantTables);

        var grantDecision = _grantPolicy.Evaluate(new GrantRequest(
            granteeType,
            granteeRef,
            GrantKind.Consent,
            validTo,
            now,
            GrantClassifier.Classify(matchedTables),
            profile.MaskingMode,
            Requester: submission.CallerSid.Value,
            Justification: justification));
        if (!grantDecision.Allowed && !string.Equals(grantDecision.ErrorCode, GrantErrorCodes.FourEyesRequired, StringComparison.Ordinal))
        {
            throw new ConsentRequestException(grantDecision.ErrorCode ?? GrantErrorCodes.Indeterminate, grantDecision.Reason);
        }

        ConsentWorkflowMode workflowMode;
        Guid? externalSystemId;
        (workflowMode, externalSystemId, validTo) = await ResolveRouteAsync(
            tenantId, matchedTables.Select(t => t.Identifier).ToList(), submission, validFrom, validTo, ct).ConfigureAwait(false);

        var includesFutureObjects = snapshotItems.Any(static i => i.IsPattern);
        var snapshot = new ProfileAssignmentRequestContext(
            submission.ProfileId,
            profile.Name,
            profile.MaskingMode,
            profile.RowScope ?? [],
            snapshotItems,
            includesFutureObjects);

        var requestId = Guid.NewGuid();
        var itemRows = workflowMode == ConsentWorkflowMode.Internal
            ? await BuildInternalItemRowsAsync(tenantId, requestId, snapshotItems, tenantTables, now, ct).ConfigureAwait(false)
            : [];

        if (workflowMode == ConsentWorkflowMode.Internal && itemRows.Count == 0)
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "No active data owners exist for the profile's currently matched objects.");
        }

        var anchor = matchedTables.FirstOrDefault();
        var status = workflowMode == ConsentWorkflowMode.External ? "PENDING_EXTERNAL_APPROVAL" : "PENDING";
        var request = new ConsentRequest
        {
            Id = requestId,
            TableId = anchor?.Table.Id ?? Guid.Empty,
            TableIdentifier = anchor?.Identifier ?? default,
            RequesterSid = submission.CallerSid,
            RequestedGranteeType = granteeType,
            RequestedGranteeRef = granteeRef,
            BusinessJustification = justification,
            RequestedAt = now,
            RequestedValidFrom = validFrom,
            RequestedValidTo = validTo,
            UpdatedAt = now,
            Status = status,
            WorkflowMode = workflowMode,
            ExternalSystemId = externalSystemId,
            TenantId = tenantId,
            RequesterIdentifiers = ConsentRequestService.ExtractPrincipalIdentifiers(submission.Principal, submission.CallerSid.Value),
            ContextJson = JsonSerializer.Serialize(snapshot),
            RequestKind = ConsentRequestKind.ProfileAssignment,
            ProfileId = submission.ProfileId,
            RequestedWriteRules = snapshotItems.SelectMany(i => i.WriteRules).ToList(),
            IncludeReadAccess = true
        };

        var created = await _approvalRepository.CreateConsentRequestAsync(request, ct).ConfigureAwait(false);
        if (itemRows.Count > 0)
        {
            await _store.UpsertAssignmentItemsAsync(null, itemRows, ct).ConfigureAwait(false);
        }

        await EmitEventAsync(created, ConsentEventTypes.RequestCreated, now, ct).ConfigureAwait(false);

        return new SubmitConsentRequestResult
        {
            RequestId = created.Id,
            Status = created.Status,
            Message = includesFutureObjects
                ? "Profile assignment request created. Pattern items cover objects created later."
                : "Profile assignment request created."
        };
    }

    public async Task HandlePatternNewOwnerAsync(
        Consent patternConsent,
        TableMetadata newTable,
        IReadOnlyCollection<string> ownerSids,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patternConsent);
        ArgumentNullException.ThrowIfNull(newTable);
        if (patternConsent.ProfileAssignmentId is not { } assignmentId ||
            patternConsent.WorkflowMode != ConsentWorkflowMode.Internal ||
            ownerSids is not { Count: > 0 })
        {
            return;
        }

        var missingOwners = ownerSids
            .Where(s => !string.IsNullOrWhiteSpace(s) &&
                        !patternConsent.ApprovingOwners.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingOwners.Count == 0)
        {
            return;
        }

        var existingPending = await _approvalRepository.GetRequestsByRequesterSidAsync(
            patternConsent.GranteeSid ?? new Sid(patternConsent.RequestedGranteeOrSystem()),
            "PENDING",
            ct).ConfigureAwait(false);
        if (existingPending.Any(r =>
            r.TenantId == patternConsent.TenantId &&
            r.RequestKind == ConsentRequestKind.ProfileAssignment &&
            r.ProfileId == assignmentId &&
            r.TableIdentifier == newTable.Identifier))
        {
            return;
        }

        var itemNo = patternConsent.ProfileItemNo ?? 0;
        var objectPattern = patternConsent.ObjectPattern ?? newTable.Identifier.ToString();
        var context = new ProfileAssignmentRequestContext(
            assignmentId,
            "PatternCoverageExtension",
            MaskingPolicyMode.Default,
            patternConsent.RowScope,
            [new ProfileAssignmentSnapshotItem(itemNo, objectPattern, true, patternConsent.DataSource, patternConsent.WriteRules)],
            IncludesFutureObjects: true,
            ExtendingConsentId: patternConsent.Id,
            ExtendingItemNo: itemNo);

        var requestId = Guid.NewGuid();
        var requesterSid = patternConsent.GranteeSid ?? new Sid("SYSTEM");
        var justification = $"Pattern grant '{patternConsent.Id:D}' matched new object '{newTable.Identifier}' requiring owner approval.";
        var grantDecision = _grantPolicy.Evaluate(new GrantRequest(
            patternConsent.GranteeType,
            patternConsent.GranteeSid?.Value ?? string.Empty,
            GrantKind.Consent,
            patternConsent.ValidTo,
            now,
            GrantClassifier.Classify([newTable]),
            MaskingPolicyMode.Default,
            Requester: requesterSid.Value,
            Justification: justification));
        if (!grantDecision.Allowed && !string.Equals(grantDecision.ErrorCode, GrantErrorCodes.FourEyesRequired, StringComparison.Ordinal))
        {
            return;
        }

        var req = new ConsentRequest
        {
            Id = requestId,
            TableId = newTable.Table.Id,
            TableIdentifier = newTable.Identifier,
            RequesterSid = requesterSid,
            RequestedGranteeType = patternConsent.GranteeType,
            RequestedGranteeRef = patternConsent.GranteeSid?.Value ?? string.Empty,
            BusinessJustification = justification,
            Status = "PENDING",
            RequestedAt = now,
            RequestedValidFrom = now,
            RequestedValidTo = patternConsent.ValidTo,
            UpdatedAt = now,
            TenantId = patternConsent.TenantId,
            WorkflowMode = ConsentWorkflowMode.Internal,
            ContextJson = JsonSerializer.Serialize(context),
            RequestKind = ConsentRequestKind.ProfileAssignment,
            ProfileId = assignmentId
        };

        var created = await _approvalRepository.CreateConsentRequestAsync(req, ct).ConfigureAwait(false);
        var rows = missingOwners.Select(owner => new ProfileAssignmentItemRow(
            patternConsent.TenantId,
            created.Id,
            itemNo,
            newTable.Identifier.ToString(),
            owner,
            "PENDING")).ToList();
        await _store.UpsertAssignmentItemsAsync(null, rows, ct).ConfigureAwait(false);
        await EmitEventAsync(created, ConsentEventTypes.RequestCreated, now, ct).ConfigureAwait(false);
    }

    public Task<int> RevokeAssignmentAsync(
        TenantId tenantId,
        Guid profileAssignmentId,
        Sid actorSid,
        string reason,
        CancellationToken ct = default) =>
        _materializer.RevokeAssignmentAsync(tenantId, profileAssignmentId, actorSid, reason, ct);

    public async Task<ConsentDecisionResult> DecideAsync(
        ConsentDecisionCommand command,
        ConsentRequest req,
        Sid actorSid,
        IReadOnlyList<Sid> externalApproverSids,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(req);
        if (req.RequestKind != ConsentRequestKind.ProfileAssignment)
        {
            throw new InvalidOperationException("Request is not a profile assignment.");
        }

        var snapshot = ParseSnapshot(req);
        var now = _time.GetUtcNow();
        var tenantTables = await LoadTenantTablesAsync(req.TenantId, ct).ConfigureAwait(false);

        if (command.Channel == ConsentDecisionChannel.External)
        {
            return await DecideExternalAsync(command, req, snapshot, tenantTables, actorSid, externalApproverSids, now, ct).ConfigureAwait(false);
        }

        return await DecideInternalAsync(command, req, snapshot, tenantTables, actorSid, now, ct).ConfigureAwait(false);
    }

    private async Task<ConsentDecisionResult> DecideInternalAsync(
        ConsentDecisionCommand command,
        ConsentRequest req,
        ProfileAssignmentRequestContext snapshot,
        IReadOnlyList<TableMetadata> tenantTables,
        Sid actorSid,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (command.Decision == ConsentDecisionKind.Approve)
        {
            await EnsureSodAsync(req, command.Principal, actorSid, checkPriorApprovers: false, ct).ConfigureAwait(false);
        }

        var items = await _store.GetAssignmentItemsAsync(null, req.Id, ct).ConfigureAwait(false);
        var pendingRows = items.Where(i => string.Equals(i.OwnerDecision, "PENDING", StringComparison.OrdinalIgnoreCase)).ToList();
        var isSecondStepOnly = pendingRows.Count == 0 && string.Equals(req.Status, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase);
        if (pendingRows.Count == 0 && !isSecondStepOnly)
        {
            throw new ConsentDecisionException(ConsentDecisionErrorCodes.RequestNotDecidable, $"Request '{req.Id}' has no pending items.", statusCode: 409);
        }

        var isPrivAdmin = IsPrivilegedAdmin(command.Principal);
        var authorizedOwnerSids = isSecondStepOnly
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await ResolveAuthorizedPendingOwnerSidsAsync(pendingRows, snapshot, tenantTables, actorSid, isPrivAdmin, ct).ConfigureAwait(false);
        if (isSecondStepOnly
            ? !isPrivAdmin && !await IsAuthorizedForAnyMatchedTableAsync(snapshot, tenantTables, actorSid, ct).ConfigureAwait(false)
            : authorizedOwnerSids.Count == 0)
        {
            throw new ConsentDecisionException("FORBIDDEN", $"User '{actorSid}' is not an authorized owner or delegate for any pending item of request '{req.Id}'.", statusCode: 403);
        }

        var decisionText = command.Decision == ConsentDecisionKind.Approve ? "APPROVED" : "REJECTED";
        var rejectionReason = command.Decision == ConsentDecisionKind.Reject ? ValidateRejectionReason(command.Reason) : null;
        var approvedValidTo = command.Decision == ConsentDecisionKind.Approve
            ? ResolveApprovedValidToOrThrow(req, command.ApprovedValidTo, now)
            : req.RequestedValidTo;

        var combinedApprovers = req.ApprovalSteps
            .Where(s => string.Equals(s.Decision, "APPROVED", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ApproverSid.Value)
            .Append(actorSid.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requiresFourEyes = command.Decision == ConsentDecisionKind.Approve
            && RequiresFourEyes(snapshot, tenantTables, req, approvedValidTo, now, combinedApprovers);

        if (requiresFourEyes &&
            SeparationOfDutiesService.CollectSubjectIdentifiers(command.Principal, actorSid.Value)
                .Overlaps(SeparationOfDutiesService.CollectPriorApproverIdentifiers(req.ApprovalSteps)))
        {
            throw new ConsentDecisionException(ConsentDecisionErrorCodes.FourEyesSameApprover, "Four-eyes principle violated: this approver has already approved this request.", statusCode: 409);
        }

        IReadOnlyList<Consent> materialized = Array.Empty<Consent>();
        var invalidatedTables = new List<TableIdentifier>();
        string? finalStatus = null;

        await using (var tx = await _store.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            try
            {
                IReadOnlyList<ProfileAssignmentItemRow> updatedItems;
                if (isSecondStepOnly)
                {
                    updatedItems = items;
                }
                else
                {
                    var decideAll = isPrivAdmin && command.Decision == ConsentDecisionKind.Approve && authorizedOwnerSids.Count == pendingRows.Select(r => r.OwnerSid).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    await _store.DecideAssignmentItemsAsync(
                        tx.Transaction, req.Id, authorizedOwnerSids, decisionText, actorSid.Value, now, decideAll, ct).ConfigureAwait(false);
                    updatedItems = await _store.GetAssignmentItemsAsync(tx.Transaction, req.Id, ct).ConfigureAwait(false);
                }

                var stillPending = !isSecondStepOnly && updatedItems.Any(i => string.Equals(i.OwnerDecision, "PENDING", StringComparison.OrdinalIgnoreCase));
                var anyApproved = isSecondStepOnly
                    ? command.Decision == ConsentDecisionKind.Approve
                    : updatedItems.Any(i => string.Equals(i.OwnerDecision, "APPROVED", StringComparison.OrdinalIgnoreCase));
                var immediateReject = command.Decision == ConsentDecisionKind.Reject && (!anyApproved || isSecondStepOnly);

                if (stillPending && !immediateReject)
                {
                    await _store.ApplyRequestStepAndStatusInTransactionAsync(
                        tx.Transaction, req, newStatus: null, actorSid, decisionText, rejectionReason, now, ct).ConfigureAwait(false);
                }
                else if (anyApproved && requiresFourEyes && !isSecondStepOnly && CountDistinctApprovers(req, combinedApprovers) < 2)
                {
                    finalStatus = "PENDING_SECOND_APPROVAL";
                    await _store.ApplyRequestStepAndStatusInTransactionAsync(
                        tx.Transaction, req, finalStatus, actorSid, decisionText, rejectionReason, now, ct).ConfigureAwait(false);
                }
                else
                {
                    finalStatus = anyApproved ? "APPROVED" : "REJECTED";
                    await _store.ApplyRequestStepAndStatusInTransactionAsync(
                        tx.Transaction, req, finalStatus, actorSid, decisionText, rejectionReason, now, ct).ConfigureAwait(false);

                    if (anyApproved)
                    {
                        if (snapshot.ExtendingConsentId is { } targetConsentId)
                        {
                            var approvedOwners = updatedItems
                                .Where(i => string.Equals(i.OwnerDecision, "APPROVED", StringComparison.OrdinalIgnoreCase))
                                .Select(i => i.OwnerSid)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            var existingConsent = await _store.GetConsentByIdInTransactionAsync(tx.Transaction, targetConsentId, ct).ConfigureAwait(false);
                            if (existingConsent is not null)
                            {
                                var extended = await _materializer.ExtendApprovingOwnersAsync(
                                    tx.Transaction, existingConsent, req.Id, approvedOwners, actorSid, now, ct).ConfigureAwait(false);
                                materialized = [extended];
                            }

                            if (!string.IsNullOrWhiteSpace(req.TableIdentifier.TableName))
                            {
                                await _store.IncrementTableEpochInTransactionAsync(tx.Transaction, req.TableIdentifier, ct).ConfigureAwait(false);
                                invalidatedTables.Add(req.TableIdentifier);
                            }
                        }
                        else
                        {
                            materialized = await _materializer.MaterializeApprovedAsync(
                                tx.Transaction, req, snapshot, updatedItems, tenantTables, actorSid, [], approvedValidTo, now,
                                command.Description, command.WriteRules, approvedItemNos: null, command.ApprovedClearColumns, ct)
                                .ConfigureAwait(false);
                            invalidatedTables.AddRange(materialized.Where(c => !c.IsPatternGrant).Select(c => c.TableIdentifier).Distinct());
                        }
                    }
                    else if (_grantWriter is not null)
                    {
                        await _grantWriter.RecordRejectionAsync(
                            tx.Transaction, BuildRejectionInput(req, actorSid, rejectionReason ?? "REJECTED", now), ct).ConfigureAwait(false);
                    }
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        if (invalidatedTables.Count > 0)
        {
            await _store.InvalidateTablesAfterCommitAsync(invalidatedTables, ct).ConfigureAwait(false);
        }

        var updatedReq = finalStatus is null ? req : CloneWithStatus(req, finalStatus, now);
        var eventType = finalStatus is null || string.Equals(finalStatus, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase)
            ? ConsentEventTypes.RequestUpdated
            : ConsentEventTypes.RequestDecided;
        await EmitEventAsync(updatedReq, eventType, now, ct).ConfigureAwait(false);
        return new ConsentDecisionResult(updatedReq, materialized.FirstOrDefault());
    }

    private static (GranteeType Type, string Ref) ResolveAndValidateGrantee(ProfileAssignmentSubmission submission)
    {
        if (GranteeTypeParsing.IsRetiredRole(submission.GranteeType))
        {
            throw new ConsentRequestException(
                AccessProfileAssignmentRules.RoleSubjectUnsupported,
                "Role assignees are not supported; assign the profile to a group or a user instead.");
        }

        if (GranteeTypeParsing.TryParse(submission.GranteeType, out var parsedType))
        {
            var targetRef = string.IsNullOrWhiteSpace(submission.GranteeRef) ? submission.CallerSid.Value : submission.GranteeRef.Trim();
            return (parsedType, targetRef);
        }

        var granteeRef = string.IsNullOrWhiteSpace(submission.GranteeRef) ? submission.CallerSid.Value : submission.GranteeRef.Trim();
        if (granteeRef.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConsentRequestException(
                AccessProfileAssignmentRules.RoleSubjectUnsupported,
                "Role assignees are not supported; assign the profile to a group or a user instead.");
        }

        return (GranteeType.User, granteeRef);
    }

    private static void EnsureCallerAuthorizedToAssign(ProfileAssignmentSubmission submission, GranteeType granteeType, string granteeRef)
    {
        if (IsGovernanceOrClusterAdmin(submission.Principal))
        {
            return;
        }

        if (granteeType == GranteeType.User &&
            string.Equals(granteeRef, submission.CallerSid.Value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ConsentRequestException("FORBIDDEN", "Only a GovernanceAdmin can assign a profile to another user or group.");
    }

    private static bool IsGovernanceOrClusterAdmin(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return false;
        }

        if (ClusterAdminPolicy.IsCanonicalClusterAdmin(principal))
        {
            return true;
        }

        return principal.FindAll(ClaimTypes.Role)
            .Any(r => string.Equals(r.Value, "GovernanceAdmin", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPrivilegedAdmin(ClaimsPrincipal? principal) => IsGovernanceOrClusterAdmin(principal);

    private static TenantId ResolveTenant(ProfileAssignmentSubmission submission)
    {
        if (submission.ContextTenant is { } explicitTenant)
        {
            return explicitTenant;
        }

        var claim = TenantClaimReader.Read(submission.Principal);
        if (claim.Kind == ClaimTenantKind.Valid)
        {
            return claim.Tenant;
        }

        throw new ConsentRequestException("FORBIDDEN", "A valid tenant claim is required to assign an access profile.");
    }

    private async Task<AccessProfile> LoadProfileOrThrowAsync(TenantId tenantId, Guid profileId, CancellationToken ct)
    {
        var profile = await _profileRepository.GetProfileAsync(tenantId, profileId.ToString("D"), ct).ConfigureAwait(false)
            ?? await _profileRepository.GetProfileAsync(tenantId, profileId.ToString("N"), ct).ConfigureAwait(false);
        if (profile is null || profile.TenantId != tenantId)
        {
            throw new ConsentRequestException("NOT_FOUND", $"Access profile '{profileId}' was not found.");
        }

        return profile;
    }

    private static string ResolveJustification(ProfileAssignmentSubmission submission, AccessProfile profile)
    {
        var j = string.IsNullOrWhiteSpace(submission.Justification) ? profile.Justification : submission.Justification;
        if (string.IsNullOrWhiteSpace(j))
        {
            j = $"Access profile assignment for '{profile.Name}'.";
        }

        j = j.Trim();
        if (j.Length < 5 || j.Length > 2000)
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "Justification must be between 5 and 2000 characters.");
        }

        return j;
    }

    private DateTimeOffset ResolveRequestedValidTo(ProfileAssignmentSubmission submission, DateTimeOffset now)
    {
        try
        {
            return _validityPolicy.ResolveRequestedValidTo(
                now,
                submission.RequestedValidFrom,
                submission.RequestedValidTo,
                submission.RequestedDuration == null && submission.RequestedValidTo == null ? submission.DurationDays : null,
                submission.RequestedDuration);
        }
        catch (GrantValidityException ex)
        {
            throw new ConsentRequestException(ex.ErrorCode, ex.Message);
        }
    }

    private async Task<IReadOnlyList<TableMetadata>> LoadTenantTablesAsync(TenantId tenantId, CancellationToken ct)
    {
        var result = new List<TableMetadata>();
        string? after = null;
        for (var pageIndex = 0; pageIndex < 1000; pageIndex++)
        {
            var page = await _metadataRepository.QueryTablesAsync(
                new Autheris.Application.Catalog.CatalogTableQuery(
                    After: after,
                    Limit: Autheris.Application.Catalog.CatalogTableQuery.MaxLimit),
                ct).ConfigureAwait(false);
            foreach (var t in page.Items)
            {
                if (!t.Table.DiscoveryHidden &&
                    (t.OwnerTenantId is null || string.Equals(t.OwnerTenantId, tenantId.Value, StringComparison.Ordinal)))
                {
                    result.Add(t);
                }
            }

            if (page.NextAfter is null)
            {
                break;
            }

            after = page.NextAfter;
        }

        return result
            .OrderBy(t => t.Identifier.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    private static List<ProfileAssignmentSnapshotItem> BuildSnapshotItems(AccessProfile profile)
    {
        var items = new List<ProfileAssignmentSnapshotItem>(profile.TargetTables.Count);
        for (int i = 0; i < profile.TargetTables.Count; i++)
        {
            var raw = profile.TargetTables[i]?.Trim() ?? string.Empty;
            var isPattern = raw.Contains('*', StringComparison.Ordinal);
            string normalized = raw;
            string? dataSource = null;

            if (isPattern)
            {
                if (!ObjectPatternSpec.TryParse(raw, out var spec, out var error))
                {
                    throw new ConsentRequestException("VALIDATION_ERROR", $"Profile pattern '{raw}' is invalid: {error}");
                }

                normalized = spec!.NormalizedPattern;
                dataSource = spec.DataSource;
            }
            else if (TableIdentifier.TryParse(raw, out var exact))
            {
                normalized = exact.ToString();
                dataSource = exact.Domain;
            }
            else
            {
                throw new ConsentRequestException("VALIDATION_ERROR", $"Profile target '{raw}' must be 'domain.schema.table' or a valid pattern.");
            }

            var matchingWriteRules = (profile.WriteRules ?? [])
                .Where(w => isPattern
                    ? AccessProfile.MatchesPattern(normalized, w.TableIdentifier)
                    : string.Equals(w.TableIdentifier.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                .Select(w => new WriteRule(w.Id, w.Operation, w.Columns, w.RowScope, w.RowFilter, w.MaxRowsPerStatement))
                .ToList();

            items.Add(new ProfileAssignmentSnapshotItem(i, normalized, isPattern, dataSource, matchingWriteRules));
        }

        return items;
    }

    private static List<TableMetadata> ResolveMatchedTables(
        IReadOnlyList<ProfileAssignmentSnapshotItem> items,
        IReadOnlyList<TableMetadata> tenantTables)
    {
        var seen = new HashSet<TableIdentifier>();
        var list = new List<TableMetadata>();
        foreach (var item in items)
        {
            foreach (var table in ProfileGrantMaterializer.MatchTablesForItem(item, tenantTables))
            {
                if (seen.Add(table.Identifier))
                {
                    list.Add(table);
                }
            }
        }

        return list;
    }

    private async Task<(ConsentWorkflowMode Mode, Guid? ExternalSystemId, DateTimeOffset ValidTo)> ResolveRouteAsync(
        TenantId tenantId,
        IReadOnlyList<TableIdentifier> tables,
        ProfileAssignmentSubmission submission,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        CancellationToken ct)
    {
        if (_workflowRouter is null)
        {
            return (ConsentWorkflowMode.Internal, null, validTo);
        }

        var resolution = await _workflowRouter.ResolveForTablesAsync(tenantId, tables, ct).ConfigureAwait(false);
        var maxEnd = GrantTimeMath.AddSaturating(validFrom, GrantTimeMath.DaysToSpan(Math.Max(1, resolution.MaxDurationDays)));
        if (validTo > maxEnd)
        {
            if (submission.RequestedValidTo.HasValue || submission.RequestedDuration is not null || submission.DurationDays > 0)
            {
                throw new ConsentRequestException("VALIDITY_EXCEEDS_MAXIMUM", $"Requested duration exceeds route maximum of {resolution.MaxDurationDays} days.");
            }

            validTo = maxEnd;
        }

        return (resolution.Mode, resolution.Mode == ConsentWorkflowMode.External ? resolution.ExternalSystemId : null, validTo);
    }

    private async Task<List<ProfileAssignmentItemRow>> BuildInternalItemRowsAsync(
        TenantId tenantId,
        Guid requestId,
        IReadOnlyList<ProfileAssignmentSnapshotItem> items,
        IReadOnlyList<TableMetadata> tenantTables,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rows = new List<ProfileAssignmentItemRow>();
        var seen = new HashSet<(int ItemNo, string OwnerSid)>();

        foreach (var item in items)
        {
            var matched = ProfileGrantMaterializer.MatchTablesForItem(item, tenantTables);
            foreach (var table in matched)
            {
                var directOwners = await _ownershipRepository.GetDataOwnersForTableAsync(table.Identifier, ct).ConfigureAwait(false);
                var activeDirectSids = directOwners
                    .Where(o => o.IsActive && !string.IsNullOrWhiteSpace(o.AdSid.Value))
                    .Select(o => o.AdSid.Value)
                    .ToList();
                var approvers = activeDirectSids.Count > 0
                    ? activeDirectSids
                    : (await _ownershipRepository.GetAuthorizedApproverSidsForTableAsync(table.Identifier, now, ct).ConfigureAwait(false)).ToList();

                foreach (var ownerSid in approvers)
                {
                    if (seen.Add((item.ItemNo, ownerSid)))
                    {
                        rows.Add(new ProfileAssignmentItemRow(tenantId, requestId, item.ItemNo, item.Target, ownerSid, "PENDING"));
                    }
                }
            }
        }

        return rows;
    }

    private async Task<HashSet<string>> ResolveAuthorizedPendingOwnerSidsAsync(
        IReadOnlyList<ProfileAssignmentItemRow> pendingRows,
        ProfileAssignmentRequestContext snapshot,
        IReadOnlyList<TableMetadata> tenantTables,
        Sid actorSid,
        bool isPrivilegedAdmin,
        CancellationToken ct)
    {
        if (isPrivilegedAdmin)
        {
            return pendingRows.Select(r => r.OwnerSid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemsByNo = snapshot.Items.ToDictionary(i => i.ItemNo);
        var authTableCache = new Dictionary<TableIdentifier, bool>();

        foreach (var row in pendingRows)
        {
            if (string.Equals(row.OwnerSid, actorSid.Value, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(row.OwnerSid);
                continue;
            }

            if (!itemsByNo.TryGetValue(row.ItemNo, out var item))
            {
                continue;
            }

            foreach (var table in ProfileGrantMaterializer.MatchTablesForItem(item, tenantTables))
            {
                if (!authTableCache.TryGetValue(table.Identifier, out var isAuth))
                {
                    isAuth = await _ownershipRepository.IsAuthorizedApproverForTableAsync(table.Identifier, actorSid, ct).ConfigureAwait(false);
                    authTableCache[table.Identifier] = isAuth;
                }

                if (isAuth)
                {
                    var owners = await _ownershipRepository.GetDataOwnersForTableAsync(table.Identifier, ct).ConfigureAwait(false);
                    if (owners.Any(o => string.Equals(o.AdSid.Value, row.OwnerSid, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(row.OwnerSid);
                    }
                }
            }
        }

        return result;
    }

    private async Task EnsureSodAsync(ConsentRequest req, ClaimsPrincipal? principal, Sid actorSid, bool checkPriorApprovers, CancellationToken ct)
    {
        var sod = await _separationOfDuties.CheckConsentApprovalAsync(
            req.TenantId, req, principal, actorSid, null, checkPriorApprovers, ct: ct).ConfigureAwait(false);
        if (!sod.Allowed)
        {
            throw new ConsentDecisionException(
                sod.Kind == SodViolationKind.SameApprover ? ConsentDecisionErrorCodes.FourEyesSameApprover : ConsentDecisionErrorCodes.SelfApprovalForbidden,
                sod.Reason,
                statusCode: sod.Kind == SodViolationKind.SameApprover ? 409 : 403);
        }
    }

    private async Task<bool> IsAuthorizedForAnyMatchedTableAsync(ProfileAssignmentRequestContext snapshot, IReadOnlyList<TableMetadata> tenantTables, Sid actorSid, CancellationToken ct)
    {
        foreach (var table in snapshot.Items.SelectMany(i => ProfileGrantMaterializer.MatchTablesForItem(i, tenantTables)).DistinctBy(t => t.Identifier))
        {
            if (await _ownershipRepository.IsAuthorizedApproverForTableAsync(table.Identifier, actorSid, ct).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private static int CountDistinctApprovers(ConsentRequest req, IReadOnlyList<string> combinedApprovers) =>
        SeparationOfDutiesService.CountDistinctIndependentApprovers(
            combinedApprovers,
            req.RequesterSid.Value,
            req.RequestedGranteeType is GranteeType.User or GranteeType.ServicePrincipal ? req.RequestedGranteeRef : null);

    private bool RequiresFourEyes(
        ProfileAssignmentRequestContext snapshot,
        IReadOnlyList<TableMetadata> tenantTables,
        ConsentRequest req,
        DateTimeOffset approvedValidTo,
        DateTimeOffset now,
        IReadOnlyList<string>? approvers = null)
    {
        var matched = snapshot.Items.SelectMany(i => ProfileGrantMaterializer.MatchTablesForItem(i, tenantTables)).ToList();
        if (matched.Any(t => t.Table.IsHighlySensitive || t.Table.RequiresFourEyes) ||
            (snapshot.MaskingMode == MaskingPolicyMode.Unmasked && req.RequestedGranteeType is GranteeType.User or GranteeType.ServicePrincipal))
        {
            return true;
        }

        if (req.RequestedGranteeType is not (GranteeType.User or GranteeType.ServicePrincipal) || matched.Count == 0)
            return false;

        var masking = snapshot.MaskingMode;
        var justification = string.IsNullOrWhiteSpace(req.BusinessJustification) ? snapshot.ProfileName : req.BusinessJustification;
        foreach (var table in matched)
        {
            var d = _grantPolicy.Evaluate(new GrantRequest(
                req.RequestedGranteeType, req.RequestedGranteeRef, GrantKind.Consent, approvedValidTo, now,
                GrantClassifier.Classify([table]), masking, Requester: req.RequesterSid.Value, Approvers: approvers, Justification: justification));
            if (!d.Allowed && !string.Equals(d.ErrorCode, GrantErrorCodes.FourEyesRequired, StringComparison.Ordinal))
                throw new ConsentDecisionException(d.ErrorCode ?? GrantErrorCodes.Indeterminate, d.Reason, statusCode: 400);
            if (d.RequiresFourEyes || string.Equals(d.ErrorCode, GrantErrorCodes.FourEyesRequired, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private DateTimeOffset ResolveApprovedValidToOrThrow(ConsentRequest req, DateTimeOffset? approvedValidTo, DateTimeOffset now)
    {
        try
        {
            return _validityPolicy.ResolveApprovedValidTo(req, approvedValidTo, now);
        }
        catch (GrantValidityException ex)
        {
            throw new ConsentDecisionException(ex.ErrorCode, ex.Message, ex.StatusCode);
        }
    }

    private static string ValidateRejectionReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Rejection reason is required.", statusCode: 400);
        }

        return reason.Trim();
    }

    internal static ProfileAssignmentRequestContext ParseSnapshot(ConsentRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.ContextJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ProfileAssignmentRequestContext>(req.ContextJson);
                if (parsed is { Items.Count: > 0 })
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
            }
        }

        var fallbackTarget = req.TableIdentifier.ToString();
        return new ProfileAssignmentRequestContext(
            req.ProfileId ?? req.Id,
            "AccessProfile",
            MaskingPolicyMode.Default,
            [],
            [new ProfileAssignmentSnapshotItem(0, fallbackTarget, fallbackTarget.Contains('*', StringComparison.Ordinal), req.TableIdentifier.Domain, req.RequestedWriteRules ?? [])]);
    }

    private static GrantRecordInput BuildRejectionInput(ConsentRequest req, Sid actorSid, string reason, DateTimeOffset now) =>
        new(
            req.TenantId.Value,
            "{}",
            req.RequesterSid.Value,
            JsonSerializer.Serialize(new { Action = "ProfileAssignmentRejected", DecidedBy = actorSid.Value, Reason = reason }),
            now,
            ObjectId: !string.IsNullOrWhiteSpace(req.TableIdentifier.TableName) ? req.TableIdentifier.ToString() : (req.ProfileId ?? req.Id).ToString("D"),
            GranteeType: req.RequestedGranteeType.ToString(),
            GranteeRef: req.RequestedGranteeRef,
            RequestId: req.Id,
            GrantSource: ConsentGrantSource.AccessProfile,
            ProfileAssignmentId: req.Id,
            RecordType: GrantRecordType.Rejected);

    private async Task EmitEventAsync(ConsentRequest request, string eventType, DateTimeOffset occurredAt, CancellationToken ct)
    {
        if (_eventOutbox is null)
        {
            return;
        }

        var payload = ConsentEventPayloads.ForRequest(request, occurredAt);
        await _eventOutbox.AppendAsync(
            new ConsentEventWrite(
                request.TenantId.Value,
                Guid.NewGuid(),
                eventType,
                request.Id.ToString("D"),
                ConsentEventPayloads.Serialize(payload),
                occurredAt,
                request.ExternalSystemId),
            transaction: null,
            ct: ct).ConfigureAwait(false);
    }

    private static ConsentRequest CloneWithStatus(ConsentRequest req, string status, DateTimeOffset updatedAt) => new()
    {
        Id = req.Id,
        TableId = req.TableId,
        TableIdentifier = req.TableIdentifier,
        RequesterSid = req.RequesterSid,
        RequestedGranteeType = req.RequestedGranteeType,
        RequestedGranteeRef = req.RequestedGranteeRef,
        BusinessJustification = req.BusinessJustification,
        Status = status,
        RequestedAt = req.RequestedAt,
        RequestedValidTo = req.RequestedValidTo,
        TenantId = req.TenantId,
        ContextJson = req.ContextJson,
        RequesterIdentifiers = [.. req.RequesterIdentifiers],
        ApprovalSteps = [.. req.ApprovalSteps],
        RequestedWriteRules = req.RequestedWriteRules,
        IncludeReadAccess = req.IncludeReadAccess,
        Version = req.Version,
        WorkflowMode = req.WorkflowMode,
        ExternalSystemId = req.ExternalSystemId,
        ExternalReference = req.ExternalReference,
        RequestedValidFrom = req.RequestedValidFrom,
        UpdatedAt = updatedAt,
        RequestKind = req.RequestKind,
        ProfileId = req.ProfileId
    };
}

internal static class ConsentExtensionsForProfileMonitor
{
    public static string RequestedGranteeOrSystem(this Consent consent) =>
        consent.GranteeSid?.Value ?? "SYSTEM";
}
