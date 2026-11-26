using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Notifications;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Governance.Requests;

public sealed partial class PermissionRequestEngine
{
    public async Task<PermissionSubmitResult> SubmitAsync(
        PermissionRequestSubmission submission,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return await SubmitSingleInternalAsync(submission, enforcePendingCap: true, bundleIdOverride: submission.BundleId, ct).ConfigureAwait(false);
    }

    public async Task<PermissionSubmitResult> SubmitBundleAsync(
        ClaimsPrincipal caller,
        TenantId tenantId,
        IReadOnlyList<PermissionRequestSubmission> items,
        SecurityPrincipalContext? callerContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "Bundle must contain at least one request item.");
        }

        var validTenant = ValidateTenant(tenantId);
        var callerCtx = ValidateAuthenticatedCaller(caller, callerContext, validTenant);
        var policy = await GetTenantPolicyAsync(validTenant, ct).ConfigureAwait(false);
        var maxBundle = policy?.MaxBundleItems ?? 50;
        if (items.Count > maxBundle)
        {
            throw new ConsentRequestException("BUNDLE_LIMIT_EXCEEDED", $"Bundle exceeds maximum of {maxBundle} items.");
        }

        var maxPending = policy?.MaxPendingRequestsPerRequester ?? 100;
        var existingPending = await _approvalRepository.CountPendingRequestsForRequesterAsync(validTenant, callerCtx.UserSid, ct).ConfigureAwait(false);
        if (existingPending + items.Count > maxPending)
        {
            throw new ConsentRequestException("PENDING_REQUEST_LIMIT_EXCEEDED", $"Pending requests per requester cannot exceed {maxPending}.");
        }

        var bundleId = Guid.NewGuid();
        var created = new List<ConsentRequest>(items.Count);
        foreach (var item in items)
        {
            var normalizedItem = item with
            {
                Caller = caller,
                TenantId = validTenant,
                BundleId = bundleId,
                CallerContext = callerCtx
            };
            var single = await SubmitSingleInternalAsync(normalizedItem, enforcePendingCap: false, bundleIdOverride: bundleId, ct).ConfigureAwait(false);
            created.Add(single.Request);
        }

        return new PermissionSubmitResult(created[0], created, bundleId);
    }

    private async Task<PermissionSubmitResult> SubmitSingleInternalAsync(
        PermissionRequestSubmission submission,
        bool enforcePendingCap,
        Guid? bundleIdOverride,
        CancellationToken ct)
    {
        ConsentSubmissionGuards.EnsureSelfServiceEnabled(_gatewayOptions);
        var validTenant = ValidateTenant(submission.TenantId);
        var callerCtx = ValidateAuthenticatedCaller(submission.Caller, submission.CallerContext, validTenant);

        if (!Enum.IsDefined(submission.RequestKind) || !Enum.IsDefined(submission.GranteeType))
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "Unsupported request kind or grantee type.");
        }

        if (string.IsNullOrWhiteSpace(submission.GranteeRef))
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "Grantee reference is required.");
        }

        if (submission.RequestKind != ConsentRequestKind.Data && submission.RequestedWriteRules is { Count: > 0 })
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "Write rules are supported for Data requests only.");
        }

        await ConsentSubmissionGuards.CheckAccessRequestRateAsync(
            validTenant, callerCtx.UserSid, _rateLimiter, _gatewayOptions, _discoveryOptions, ct).ConfigureAwait(false);

        var normalizedJustification = JustificationPolicy.Validate(submission.Justification);

        var policy = await GetTenantPolicyAsync(validTenant, ct).ConfigureAwait(false);
        if (enforcePendingCap)
        {
            var maxPending = policy?.MaxPendingRequestsPerRequester ?? 100;
            var currentPending = await _approvalRepository.CountPendingRequestsForRequesterAsync(validTenant, callerCtx.UserSid, ct).ConfigureAwait(false);
            if (currentPending >= maxPending)
            {
                throw new ConsentRequestException("PENDING_REQUEST_LIMIT_EXCEEDED", $"Pending requests per requester cannot exceed {maxPending}.");
            }
        }

        var (tableId, tableIdentifier, normalizedColumns, containerKind, containerId, profileId) =
            await ResolveAndValidateTargetAsync(validTenant, callerCtx, submission, ct).ConfigureAwait(false);

        var now = _time.GetUtcNow().ToUniversalTime();
        var validFrom = (submission.ValidFrom ?? now).ToUniversalTime();
        var requestedValidTo = ResolveRequestedValidToCore(submission, now, validFrom);
        if (requestedValidTo < DateTimeOffset.MaxValue.AddDays(-1))
        {
            var upperCapacity = submission.RequestKind == ConsentRequestKind.ProfileAssignment
                ? ApproverCapacity.Governance
                : ApproverCapacity.DataOwner;

            var eval = _grantPolicy.Evaluate(new GrantRequest(
                submission.GranteeType,
                submission.GranteeRef.Trim(),
                MapPolicyGrantKind(submission.RequestKind),
                requestedValidTo,
                now,
                RequestKind: submission.RequestKind,
                Capacity: upperCapacity,
                SelfGrant: false,
                TenantPolicy: policy,
                Justification: normalizedJustification));

            if (!eval.Allowed)
            {
                throw new ConsentRequestException(eval.ErrorCode ?? GrantErrorCodes.Indeterminate, eval.Reason);
            }
        }

        await EnsureNoDuplicatePendingAsync(
            validTenant,
            callerCtx.UserSid,
            submission,
            tableIdentifier,
            normalizedColumns,
            containerKind,
            containerId,
            profileId,
            ct).ConfigureAwait(false);

        await EnsureNotAlreadyCoveredByActiveUnmaskGrantAsync(
            validTenant,
            submission,
            tableIdentifier,
            normalizedColumns,
            now,
            validFrom,
            requestedValidTo,
            ct).ConfigureAwait(false);

        var (workflowMode, externalSystemId) = await ResolveWorkflowRouteAsync(validTenant, tableIdentifier, ct).ConfigureAwait(false);
        var grantKind = MapGrantKind(submission.RequestKind);
        var requesterIds = SeparationOfDutiesService.CollectSubjectIdentifiers(submission.Caller, callerCtx.UserSid.Value).ToList();

        var draft = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = tableId,
            TableIdentifier = tableIdentifier,
            RequesterSid = callerCtx.UserSid,
            RequestedGranteeType = submission.GranteeType,
            RequestedGranteeRef = submission.GranteeRef.Trim(),
            BusinessJustification = normalizedJustification,
            // an externally routed request waits for the external system (same status as every other external submission path)
            Status = workflowMode == ConsentWorkflowMode.External ? "PENDING_EXTERNAL_APPROVAL" : "PENDING",
            RequestedAt = now,
            RequestedValidFrom = validFrom,
            RequestedValidTo = requestedValidTo,
            UpdatedAt = now,
            TenantId = validTenant,
            RequesterIdentifiers = requesterIds,
            RequestedWriteRules = submission.RequestedWriteRules ?? [],
            IncludeReadAccess = submission.IncludeReadAccess,
            Version = 1,
            WorkflowMode = workflowMode,
            ExternalSystemId = externalSystemId,
            RequestKind = submission.RequestKind,
            GrantKind = grantKind,
            ProfileId = profileId,
            TargetColumns = normalizedColumns,
            ContainerKind = containerKind,
            ContainerId = containerId,
            BundleId = bundleIdOverride ?? submission.BundleId
        };

        var created = await _approvalRepository.CreateConsentRequestAsync(draft, ct).ConfigureAwait(false);
        var actorKind = PermissionAuditDetails.ResolveActorKind(callerCtx);

        var auditEntry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = now,
            EventType = AuditEventTypes.PermissionRequested,
            ActorSid = callerCtx.UserSid,
            TargetTable = FormatTargetObject(created),
            Decision = "PENDING",
            TraceId = GetTraceId(),
            TenantId = validTenant,
            DetailsJson = new PermissionAuditDetails(
                Actor: callerCtx.UserSid.Value,
                Subject: created.RequestedGranteeRef,
                TargetObject: FormatTargetObject(created),
                ValidFrom: created.RequestedValidFrom,
                ValidTo: created.RequestedValidTo,
                Justification: normalizedJustification,
                Capacity: null,
                SelfGrant: false,
                RequestId: created.Id,
                ActorKind: actorKind,
                GrantKind: grantKind.ToSqlString()).ToDetailsJson()
        };

        try
        {
            await _auditWriter.RecordRequiredAsync(auditEntry, ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _approvalRepository.DeleteConsentRequestAsync(created.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve fail-closed audit failure.
            }

            throw;
        }

        // the external workflow systems and event-feed consumers learn about a new request from this event
        await AppendRequestEventAsync(
            Autheris.Application.ConsentRequests.Events.ConsentEventTypes.RequestCreated,
            created,
            Autheris.Application.ConsentRequests.Events.ConsentEventPayloads.ForRequest(created, now),
            transaction: null,
            ct).ConfigureAwait(false);

        _logger?.LogInformation(
            "Created permission request {RequestId} of kind {RequestKind} in tenant {TenantId}.",
            created.Id,
            created.RequestKind,
            validTenant.Value);

        if (_notificationDispatcher is not null)
        {
            await _notificationDispatcher.DispatchAsync(
                new GovernanceNotificationMessage(
                    AuditEventTypes.PermissionRequested,
                    validTenant.Value,
                    string.Empty,
                    created.RequestedGranteeRef,
                    $"Permission request {created.Id:D} ({created.RequestKind}) submitted.",
                    now,
                    ObjectName: FormatTargetObject(created),
                    Justification: normalizedJustification),
                ct).ConfigureAwait(false);
        }

        if (await TryInsecureAutoApproveAsync(created, callerCtx, normalizedJustification, ct).ConfigureAwait(false) is { } approved)
        {
            return new PermissionSubmitResult(approved, BundleId: approved.BundleId, AutoApproved: true);
        }

        return new PermissionSubmitResult(created, BundleId: created.BundleId);
    }

    /// <summary>
    /// Insecure getting-started switch (<c>Insecure:warn_auto_approve_access_requests</c>, refused at startup outside Development): an
    /// internally routed Data request of a single submission is activated at once. External routes, other request kinds and bundles never are.
    /// </summary>
    private async Task<ConsentRequest?> TryInsecureAutoApproveAsync(
        ConsentRequest created, SecurityPrincipalContext callerCtx, string justification, CancellationToken ct)
    {
        if (_gatewayOptions?.IsAutoApproveEnabled != true ||
            created.WorkflowMode != ConsentWorkflowMode.Internal ||
            created.RequestKind != ConsentRequestKind.Data ||
            created.BundleId is not null)
        {
            return null;
        }

        // the activation writes a grant: it must pass the grant policy as an approval would; a refusal leaves the request pending
        var now = _time.GetUtcNow().ToUniversalTime();
        var policyDecision = _grantPolicy.Evaluate(new GrantRequest(
            created.RequestedGranteeType,
            created.RequestedGranteeRef,
            MapPolicyGrantKind(created.RequestKind),
            created.RequestedValidTo,
            now,
            RequestKind: created.RequestKind,
            Capacity: ApproverCapacity.DataOwner,
            SelfGrant: false,
            TenantPolicy: await GetTenantPolicyAsync(created.TenantId, ct).ConfigureAwait(false),
            Justification: justification));
        if (!policyDecision.Allowed)
        {
            return null;
        }

        // Issue #1121: a grant on secret data needs two distinct approvals; the getting-started switch never activates it.
        if (_protectionProvider is not null &&
            !string.IsNullOrWhiteSpace(created.TableIdentifier.TableName) &&
            SecretFourEyesRule.Applies(created.RequestKind, await ResolveMaxClassAsync(created, ct).ConfigureAwait(false)))
        {
            return null;
        }

        await _approvalRepository.ActivateConsentForAutoApproveAsync(created.Id, ct).ConfigureAwait(false);
        var approved = await _approvalRepository.GetConsentRequestAsync(created.Id, ct).ConfigureAwait(false) ?? created;
        await _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = now,
            EventType = AuditEventTypes.PermissionApproved,
            ActorSid = callerCtx.UserSid,
            TargetTable = FormatTargetObject(approved),
            Decision = "APPROVED",
            TraceId = GetTraceId(),
            TenantId = approved.TenantId,
            DetailsJson = new PermissionAuditDetails(
                Actor: callerCtx.UserSid.Value,
                Subject: approved.RequestedGranteeRef,
                TargetObject: FormatTargetObject(approved),
                ValidFrom: approved.RequestedValidFrom,
                ValidTo: approved.RequestedValidTo,
                Justification: justification,
                Capacity: null,
                SelfGrant: false,
                RequestId: approved.Id,
                ActorKind: PermissionAuditDetails.ResolveActorKind(callerCtx),
                GrantKind: approved.GrantKind.ToSqlString()).ToDetailsJson()
        }, ct).ConfigureAwait(false);

        _logger?.LogWarning("Permission request {RequestId} was auto-approved by the insecure getting-started switch.", approved.Id);
        return approved;
    }

    private SecurityPrincipalContext ValidateAuthenticatedCaller(
        ClaimsPrincipal? principal,
        SecurityPrincipalContext? overrideContext,
        TenantId targetTenant)
    {
        if (principal is null || principal.Identity?.IsAuthenticated != true)
        {
            throw new ConsentRequestException("UNAUTHENTICATED", "Authenticated principal is required.");
        }

        var ctx = overrideContext ?? SecurityPrincipalContext.FromPrincipal(principal);
        if (!ctx.IsAuthenticated ||
            ctx.TenantClaimInvalid ||
            string.IsNullOrWhiteSpace(ctx.UserSid.Value) ||
            string.Equals(ctx.UserSid.Value.Trim(), "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConsentRequestException("UNAUTHENTICATED", "Authenticated subject from a validated realm is required.");
        }

        if (_subjectResolver is not null)
        {
            var resolved = _subjectResolver.ResolveSubject(principal);
            if (resolved is null || string.IsNullOrWhiteSpace(resolved.Id))
            {
                throw new ConsentRequestException("UNAUTHENTICATED", "Caller subject could not be resolved.");
            }
        }

        if (!string.IsNullOrWhiteSpace(ctx.TenantId.Value) && ctx.TenantId != targetTenant)
        {
            throw new ConsentRequestException("FORBIDDEN", "Caller tenant does not match request tenant.");
        }

        return ctx;
    }

    private static TenantId ValidateTenant(TenantId tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            throw new ConsentRequestException("INVALID_TENANT", "A valid non-reserved tenant identifier is required.");
        }

        try
        {
            return new TenantId(StoredTenant.RequireWritable(tenant));
        }
        catch (ArgumentException)
        {
            throw new ConsentRequestException("INVALID_TENANT", "A valid non-reserved tenant identifier is required.");
        }
    }

    private async Task<(
        Guid TableId,
        TableIdentifier TableIdentifier,
        IReadOnlyList<string>? Columns,
        ContainerKind? ContainerKind,
        Guid? ContainerId,
        Guid? ProfileId)> ResolveAndValidateTargetAsync(
        TenantId tenant,
        SecurityPrincipalContext caller,
        PermissionRequestSubmission submission,
        CancellationToken ct)
    {
        var target = submission.Target ?? throw new ConsentRequestException("VALIDATION_ERROR", "Target is required.");

        if (submission.RequestKind is ConsentRequestKind.Data or ConsentRequestKind.Unmask)
        {
            if (!target.HasTable)
            {
                throw new ConsentRequestException("TARGET_NOT_FOUND", "Target was not found.");
            }

            var table = target.Table!.Value;
            var meta = _metadataRepository is not null
                ? await _metadataRepository.GetTableMetadataAsync(table, ct).ConfigureAwait(false)
                : null;

            if (_metadataRepository is not null)
            {
                if (!CatalogTenantOwnership.IsEligibleTargetInTenant(meta, tenant))
                {
                    throw new ConsentRequestException("TARGET_NOT_FOUND", "Target was not found.");
                }
            }

            if (submission.RequestKind == ConsentRequestKind.Data && meta is not null && submission.RequestedWriteRules is { Count: > 0 } writeRules)
            {
                Autheris.Application.ConsentRequests.WriteRuleWorkflowHelper.ValidateRulesAgainstMetadata(meta, writeRules, table);
                await ValidateWriteRulesAgainstTablePolicyAsync(tenant, meta, writeRules, ct).ConfigureAwait(false);
            }

            ContainerKind? resolvedContainerKind = target.ContainerKind;
            Guid? resolvedContainerId = target.ContainerId;

            if (_protectionProvider is not null)
            {
                var protection = await _protectionProvider.GetAsync(tenant, table, ct).ConfigureAwait(false);
                if (protection.Container is { } containerRef)
                {
                    resolvedContainerKind ??= containerRef.Kind;
                    resolvedContainerId ??= containerRef.Id;
                }

                if ((_catalogVisibility is not null || _catalogVisibilityRepository is not null) &&
                    protection.Visibility == CatalogVisibilityMode.OnRequest)
                {
                    var visible = await HasCatalogVisibilityForTargetAsync(tenant, caller, protection.Container, table, ct).ConfigureAwait(false);
                    if (!visible)
                    {
                        throw new ConsentRequestException("TARGET_NOT_FOUND", "Target was not found.");
                    }
                }
            }

            IReadOnlyList<string>? normalizedCols = null;
            if (target.Columns is { Count: > 0 })
            {
                if (target.Columns.Any(string.IsNullOrWhiteSpace))
                {
                    throw new ConsentRequestException("VALIDATION_ERROR", "Target columns must not contain empty or whitespace entries.");
                }

                normalizedCols = target.Columns
                    .Select(c => c.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (meta is null)
                {
                    throw new ConsentRequestException("COLUMN_METADATA_UNAVAILABLE", "Table column metadata is unavailable for column-scoped permission requests.");
                }

                if (meta.Columns.Count == 0)
                {
                    throw new ConsentRequestException("TARGET_NOT_FOUND", "Target was not found.");
                }

                var knownCols = new HashSet<string>(meta.Columns.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase);
                if (normalizedCols.Any(c => !knownCols.Contains(c)))
                {
                    throw new ConsentRequestException("TARGET_NOT_FOUND", "Target was not found.");
                }
            }

            return (meta?.Table.Id ?? Guid.NewGuid(), table, normalizedCols, resolvedContainerKind, resolvedContainerId, null);
        }

        if (submission.RequestKind == ConsentRequestKind.Catalog)
        {
            if (!target.HasContainer)
            {
                throw new ConsentRequestException("TARGET_NOT_FOUND", "Target was not found.");
            }

            var kind = target.ContainerKind!.Value;
            var containerId = target.ContainerId!.Value;
            if (kind == ContainerKind.Domain && _domainRepository is not null)
            {
                var domain = await _domainRepository.GetAsync(tenant, containerId, ct).ConfigureAwait(false);
                if (domain is null)
                {
                    throw new ConsentRequestException("TARGET_NOT_FOUND", "Target was not found.");
                }
            }
            else if (kind == ContainerKind.DataProduct && _productRepository is not null)
            {
                var product = await _productRepository.GetProductByIdAsync(tenant, containerId, ct).ConfigureAwait(false);
                if (product is null)
                {
                    throw new ConsentRequestException("TARGET_NOT_FOUND", "Target was not found.");
                }
            }

            return (Guid.Empty, default, null, kind, containerId, null);
        }

        if (!target.HasProfile)
        {
            throw new ConsentRequestException("TARGET_NOT_FOUND", "Target was not found.");
        }

        var profileId = target.ProfileId!.Value;
        if (_accessProfileRepository is not null)
        {
            var profile = await _accessProfileRepository.GetProfileAsync(tenant, profileId.ToString("D"), ct).ConfigureAwait(false);
            if (profile is null)
            {
                throw new ConsentRequestException("TARGET_NOT_FOUND", "Target was not found.");
            }
        }

        return (Guid.Empty, default, null, null, null, profileId);
    }

    private async Task ValidateWriteRulesAgainstTablePolicyAsync(
        TenantId tenant,
        TableMetadata meta,
        IReadOnlyList<WriteRule> writeRules,
        CancellationToken ct)
    {
        var tablePolicy = _writePolicyRepository is not null
            ? await _writePolicyRepository.GetAsync(tenant, meta.Table.Id, ct).ConfigureAwait(false)
            : null;

        if (tablePolicy is null)
        {
            return;
        }

        if (!tablePolicy.AllowsWrites)
        {
            throw new ConsentRequestException(
                "TARGET_NOT_WRITABLE",
                "The target table is marked read-only by its table write policy.");
        }

        HashSet<string>? allowedPolicyCols = tablePolicy.WritableColumns is { Count: > 0 }
            ? new HashSet<string>(tablePolicy.WritableColumns, StringComparer.OrdinalIgnoreCase)
            : null;
        HashSet<string>? stampedCols = tablePolicy.ServerStampedColumns is { Count: > 0 }
            ? new HashSet<string>(tablePolicy.ServerStampedColumns.Keys, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var rule in writeRules)
        {
            if (!tablePolicy.AllowsOperation(rule.Operation))
            {
                throw new ConsentRequestException(
                    "TARGET_NOT_WRITABLE",
                    "One or more requested write operations are not permitted by the table write policy.");
            }

            if (rule.Columns is { Count: > 0 })
            {
                if (allowedPolicyCols is not null && rule.Columns.Any(c => !allowedPolicyCols.Contains(c)))
                {
                    throw new ConsentRequestException(
                        "TARGET_NOT_WRITABLE",
                        "One or more requested write columns are not permitted by the table write policy.");
                }

                if (stampedCols is not null &&
                    rule.Operation == WriteOperation.Update &&
                    rule.Columns.Any(c => stampedCols.Contains(c)))
                {
                    throw new ConsentRequestException(
                        "TARGET_NOT_WRITABLE",
                        "Server-stamped columns cannot be requested as writable for updates.");
                }
            }
        }
    }

    private async Task<bool> HasCatalogVisibilityForTargetAsync(
        TenantId tenant, SecurityPrincipalContext caller, ContainerRef container, TableIdentifier table, CancellationToken ct)
    {
        if (caller.GroupResolutionStatus != GroupResolutionStatus.Complete ||
            GroupResolutionClaims.HasAnyDegradedClaim(caller.Claims) ||
            SeparationOfDutiesService.IsDegradedGroupOrRoleResolution(caller.Principal))
        {
            return false;
        }

        if (_catalogVisibility is not null && caller.Principal is not null)
        {
            if (await _catalogVisibility.FindVisibleAsync(new CatalogCaller(caller.Principal, tenant, CatalogChannel.Rest), table, ct).ConfigureAwait(false) is not null)
                return true;
            return _capabilities?.IsItAdmin(caller.Principal, tenant) == true;
        }

        if (_capabilities is not null &&
            (_capabilities.IsGovernance(caller.Principal, tenant) || _capabilities.IsItAdmin(caller.Principal, tenant) ||
             (container.Id != Guid.Empty && await _capabilities.IsOwnerMemberAsync(caller, container, ct).ConfigureAwait(false))))
        {
            return true;
        }

        var subjects = new HashSet<Sid>(caller.GroupSids) { caller.UserSid };
        if (caller.Principal is not null) subjects.UnionWith(caller.Principal.GetAllUserSids());
        var now = _time.GetUtcNow();
        if (_catalogVisibilityRepository is not null && container.Id != Guid.Empty)
        {
            var grants = await _catalogVisibilityRepository.ListActiveForSubjectsAsync(tenant, subjects, now, ct).ConfigureAwait(false);
            if (grants.Any(g => g.ContainerKind == container.Kind && g.ContainerId == container.Id)) return true;
        }

        if (_consentRepository is not null)
        {
            var consents = await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, table, now, tenant, ct).ConfigureAwait(false);
            return !consents.Any(c => c.Effect == ConsentEffect.Deny && c.GrantKind == PermissionGrantKind.Data && !c.IsRevoked && c.IsActive(now)) &&
                   consents.Any(c => c.Effect == ConsentEffect.Allow && c.GrantKind == PermissionGrantKind.Data && !c.IsRevoked && c.IsActive(now));
        }

        return false;
    }

    private DateTimeOffset ResolveRequestedValidToCore(
        PermissionRequestSubmission submission,
        DateTimeOffset now,
        DateTimeOffset validFrom)
    {
        var hasFiniteTarget = submission.ValidTo.HasValue
            || submission.DurationDays.HasValue
            || !string.IsNullOrWhiteSpace(submission.RequestedDuration);

        if (!hasFiniteTarget)
        {
            if (submission.RequestKind is ConsentRequestKind.Data or ConsentRequestKind.Unmask or ConsentRequestKind.ProfileAssignment)
            {
                throw new ConsentRequestException(GrantErrorCodes.EndDateRequired, "Grant end date (validTo) is required.");
            }

            return DateTimeOffset.MaxValue;
        }

        return ConsentSubmissionGuards.ResolveRequestedValidTo(
            _validityPolicy,
            now,
            validFrom,
            submission.ValidTo,
            submission.DurationDays,
            submission.RequestedDuration).ToUniversalTime();
    }

    private async Task EnsureNoDuplicatePendingAsync(
        TenantId tenant,
        Sid requesterSid,
        PermissionRequestSubmission submission,
        TableIdentifier table,
        IReadOnlyList<string>? normalizedColumns,
        ContainerKind? containerKind,
        Guid? containerId,
        Guid? profileId,
        CancellationToken ct)
    {
        var pending = await _approvalRepository.GetRequestsByRequesterSidAsync(requesterSid, tenant, "PENDING", ct).ConfigureAwait(false);
        // A request awaiting its second approval (#1121) is still open: a duplicate of it is refused like a duplicate of a pending one.
        var awaitingSecond = await _approvalRepository.GetRequestsByRequesterSidAsync(
            requesterSid, tenant, SecretFourEyesRule.PendingSecondApproval, ct).ConfigureAwait(false);
        var existing = (pending ?? []).Concat(awaitingSecond ?? []).ToList();
        if (existing.Count == 0)
        {
            return;
        }

        var granteeNorm = submission.GranteeRef.Trim();
        var colSet = normalizedColumns is { Count: > 0 }
            ? new HashSet<string>(normalizedColumns, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var req in existing)
        {
            if (req.RequestKind != submission.RequestKind
                || req.RequestedGranteeType != submission.GranteeType
                || !string.Equals(req.RequestedGranteeRef, granteeNorm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (submission.RequestKind is ConsentRequestKind.Data or ConsentRequestKind.Unmask)
            {
                if (req.TableIdentifier != table)
                {
                    continue;
                }

                var reqCols = req.TargetColumns is { Count: > 0 }
                    ? new HashSet<string>(req.TargetColumns, StringComparer.OrdinalIgnoreCase)
                    : null;
                if (colSet is null && reqCols is null)
                {
                    throw new ConsentRequestException("DUPLICATE_PENDING_REQUEST", "A duplicate pending permission request already exists.");
                }

                if (colSet is not null && reqCols is not null && colSet.SetEquals(reqCols))
                {
                    throw new ConsentRequestException("DUPLICATE_PENDING_REQUEST", "A duplicate pending permission request already exists.");
                }
            }
            else if (submission.RequestKind == ConsentRequestKind.Catalog)
            {
                if (req.ContainerKind == containerKind && req.ContainerId == containerId)
                {
                    throw new ConsentRequestException("DUPLICATE_PENDING_REQUEST", "A duplicate pending permission request already exists.");
                }
            }
            else if (submission.RequestKind == ConsentRequestKind.ProfileAssignment && req.ProfileId == profileId)
            {
                throw new ConsentRequestException("DUPLICATE_PENDING_REQUEST", "A duplicate pending permission request already exists.");
            }
        }
    }

    private async Task EnsureNotAlreadyCoveredByActiveUnmaskGrantAsync(
        TenantId tenant,
        PermissionRequestSubmission submission,
        TableIdentifier table,
        IReadOnlyList<string>? normalizedColumns,
        DateTimeOffset now,
        DateTimeOffset validFrom,
        DateTimeOffset requestedValidTo,
        CancellationToken ct)
    {
        if (submission.RequestKind != ConsentRequestKind.Unmask || _consentRepository is null)
        {
            return;
        }

        var granteeNorm = submission.GranteeRef.Trim();
        var activeGrants = await _consentRepository.GetActiveUnmaskGrantsForSubjectsAsync(
            [new Sid(granteeNorm)],
            table,
            now,
            tenant,
            ct).ConfigureAwait(false);

        if (activeGrants is null || activeGrants.Count == 0)
        {
            return;
        }

        var matchingGrants = activeGrants
            .Where(g => g.GrantKind == PermissionGrantKind.Unmask
                && g.Effect == ConsentEffect.Allow
                && !g.IsRevoked
                && g.GranteeType == submission.GranteeType
                && g.GranteeSid is { } sid
                && string.Equals(sid.Value.Trim(), granteeNorm, StringComparison.OrdinalIgnoreCase)
                && g.ValidFrom <= validFrom
                && g.ValidTo >= requestedValidTo
                && g.ValidTo > now)
            .OrderByDescending(g => g.ValidTo)
            .ThenBy(g => g.Id)
            .ToList();

        if (matchingGrants.Count == 0)
        {
            return;
        }

        if (normalizedColumns is null || normalizedColumns.Count == 0)
        {
            var tableWide = matchingGrants.FirstOrDefault(g => g.TargetColumns is null || g.TargetColumns.Count == 0);
            if (tableWide is not null)
            {
                throw new ConsentRequestException(
                    "ALREADY_COVERED",
                    $"The requested unmask access is already covered by active unmask grant '{tableWide.Id:D}'.",
                    coveringGrantId: tableWide.Id);
            }

            return;
        }

        var requestedSet = new HashSet<string>(normalizedColumns, StringComparer.OrdinalIgnoreCase);

        foreach (var grant in matchingGrants)
        {
            if (grant.TargetColumns is null || grant.TargetColumns.Count == 0)
            {
                throw new ConsentRequestException(
                    "ALREADY_COVERED",
                    $"The requested columns are already covered by active unmask grant '{grant.Id:D}'.",
                    coveringGrantId: grant.Id);
            }

            var grantColumns = new HashSet<string>(grant.TargetColumns, StringComparer.OrdinalIgnoreCase);
            if (requestedSet.IsSubsetOf(grantColumns))
            {
                throw new ConsentRequestException(
                    "ALREADY_COVERED",
                    $"The requested columns are already covered by active unmask grant '{grant.Id:D}'.",
                    coveringGrantId: grant.Id);
            }
        }

        var coveredColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Guid? firstCoveringGrantId = null;
        foreach (var grant in matchingGrants)
        {
            if (grant.TargetColumns is not { Count: > 0 })
            {
                continue;
            }

            var contributed = false;
            foreach (var col in grant.TargetColumns)
            {
                if (requestedSet.Contains(col) && coveredColumns.Add(col))
                {
                    contributed = true;
                }
            }

            if (contributed && firstCoveringGrantId is null)
            {
                firstCoveringGrantId = grant.Id;
            }

            if (coveredColumns.Count == requestedSet.Count && firstCoveringGrantId is { } coveringId)
            {
                throw new ConsentRequestException(
                    "ALREADY_COVERED",
                    $"The requested columns are already covered by active unmask grant '{coveringId:D}'.",
                    coveringGrantId: coveringId);
            }
        }
    }

    /// <summary>
    /// The route that directs a new request: the same resolution as the external decision uses (domain, then tenant; removed or unknown
    /// scope kinds are inert). Requests without a table target fall back to the tenant route.
    /// </summary>
    private async Task<(ConsentWorkflowMode Mode, Guid? ExternalSystemId)> ResolveWorkflowRouteAsync(
        TenantId tenant,
        TableIdentifier table,
        CancellationToken ct)
    {
        if (_workflowRouter is not null && table.TableName is not null)
        {
            var resolution = await _workflowRouter.ResolveForTableAsync(tenant, table, ct).ConfigureAwait(false);
            return resolution is { Mode: ConsentWorkflowMode.External, ExternalSystemId: { } systemId }
                ? (ConsentWorkflowMode.External, systemId)
                : (ConsentWorkflowMode.Internal, null);
        }

        return await ResolveTenantOnlyWorkflowRouteAsync(tenant, ct).ConfigureAwait(false);
    }

    private async Task<(ConsentWorkflowMode Mode, Guid? ExternalSystemId)> ResolveTenantOnlyWorkflowRouteAsync(
        TenantId tenant,
        CancellationToken ct)
    {
        if (_workflowConfigRepository is null)
        {
            return (ConsentWorkflowMode.Internal, null);
        }

        if (_workflowConfigRepository is Autheris.Application.ConsentRequests.Routing.IConsentWorkflowAdminRepository adminRepository)
        {
            var routes = await adminRepository.ListWorkflowRoutesAsync(tenant, ct).ConfigureAwait(false);
            // Removed or unknown route scopes are inert: only tenant-scoped routes may direct a new request.
            var tenantRoute = routes.FirstOrDefault(r => r.ScopeKind == WorkflowRouteScopeKind.Tenant);
            if (tenantRoute is null || tenantRoute.Mode != ConsentWorkflowMode.External || !tenantRoute.ExternalSystemId.HasValue)
            {
                return (ConsentWorkflowMode.Internal, null);
            }

            return (ConsentWorkflowMode.External, tenantRoute.ExternalSystemId);
        }

        var fallbackRoute = await _workflowConfigRepository.ResolveWorkflowRouteAsync(tenant, default, ct).ConfigureAwait(false);
        if (fallbackRoute is null ||
            fallbackRoute.ScopeKind != WorkflowRouteScopeKind.Tenant ||
            fallbackRoute.Mode != ConsentWorkflowMode.External ||
            !fallbackRoute.ExternalSystemId.HasValue)
        {
            return (ConsentWorkflowMode.Internal, null);
        }

        return (ConsentWorkflowMode.External, fallbackRoute.ExternalSystemId);
    }

    private async Task<TenantAccessPolicy?> GetTenantPolicyAsync(TenantId tenant, CancellationToken ct) =>
        _accessPolicyProvider is not null
            ? await _accessPolicyProvider.GetEffectiveAsync(tenant, ct).ConfigureAwait(false)
            : null;
}
