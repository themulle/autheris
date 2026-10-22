namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Security;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TrinoSqlEngine;

public static partial class GovernanceEndpoints
{
    private static readonly string[] SelfGrantClaimTypes =
    [
        ClaimTypes.PrimarySid, ClaimTypes.NameIdentifier, "objectSid", "onprem_sid", "oid", "sub",
        ClaimTypes.Upn, ClaimTypes.Email, ClaimTypes.Name, "preferred_username", "upn", "email", "name",
        ClaimTypes.Role, "role", "roles"
    ];

    private static void AddIdentifierForms(HashSet<string> set, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var v = value.Trim();
        set.Add(v);
        var slash = v.LastIndexOf('\\');
        if (slash >= 0 && slash < v.Length - 1)
        {
            set.Add(v[(slash + 1)..]);
        }

        var at = v.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            set.Add(v[..at]);
        }
    }

    /// <summary>SR-M-10: True when the subject is the caller (any identifier form) or a group the caller belongs to.</summary>
    internal static bool IsSelfGrantSubject(HttpContext context, string subject)
    {
        var callerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in context.User.Claims)
        {
            if (SelfGrantClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            {
                AddIdentifierForms(callerIds, claim.Value);
            }
        }

        AddIdentifierForms(callerIds, context.User.GetUserSid()?.Value);
        AddIdentifierForms(callerIds, context.User.Identity?.Name);

        var security = EndpointSecurity.GetSecurityContext(context);
        AddIdentifierForms(callerIds, security.UserSid.Value);
        foreach (var group in security.GroupSids)
        {
            AddIdentifierForms(callerIds, group.Value);
        }
        foreach (var role in security.TenantRoles)
        {
            AddIdentifierForms(callerIds, role);
        }
        foreach (var role in security.ClusterRoles)
        {
            AddIdentifierForms(callerIds, role);
        }

        var subjectForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(subjectForms, subject);
        return subjectForms.Any(f => callerIds.Contains(f));
    }

    internal static async Task<IResult> CreateBulkConsentAsync(
        BulkConsentRequest req,
        HttpContext context,
        IAccessProfileRepository accessProfileRepo,
        CancellationToken ct)
    {
        var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin])
            || context.User.IsInRole("SecurityAdmin")
            || context.User.IsInRole("ClusterAdmin");
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (req == null)
        {
            return Results.BadRequest(new { error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(req.Subject))
        {
            return Results.BadRequest(new { error = "Subject is required." });
        }

        var trimmedSubject = req.Subject.Trim();
        var callerSid = context.User.GetUserSid()?.Value ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.Identity?.Name ?? string.Empty;

        // Segregation of Duties (SoD): self-grant is strictly forbidden. SR-M-10: compare against ALL caller identifiers
        // (SID, name, UPN, e-mail, oid, sub, ...), their account-name forms and the caller's groups, case-insensitively.
        if (IsSelfGrantSubject(context, trimmedSubject))
        {
            return Results.BadRequest(new { error = "Segregation of Duties (SoD) violation: Self-grant of access profiles is strictly prohibited." });
        }

        // Justification validation (minimum 15 characters)
        if (string.IsNullOrWhiteSpace(req.Justification) || req.Justification.Trim().Length < 15)
        {
            return Results.BadRequest(new { error = "Justification is required and must be at least 15 characters long." });
        }

        // ValidDays validation (1 to 180 days)
        if (req.ValidDays < 1 || req.ValidDays > 180)
        {
            return Results.BadRequest(new { error = "validDays must be between 1 and 180." });
        }

        // MaskingMode validation. Enum.TryParse also accepts any numeric string (e.g. "7"), so an undefined value is rejected explicitly:
        // it would otherwise be stored and ranked/applied as an unknown mode.
        if (!Enum.TryParse<MaskingPolicyMode>(req.MaskingMode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            return Results.BadRequest(new { error = $"Invalid maskingMode '{req.MaskingMode}'. Supported values: Default, Unmasked, Strict." });
        }

        // Tables validation
        if (req.Tables == null || req.Tables.Count == 0)
        {
            return Results.BadRequest(new { error = "At least one target table pattern must be provided in 'tables'." });
        }

        // Overbroad wildcard restriction: *.* or * is forbidden for Unmasked
        if (mode == MaskingPolicyMode.Unmasked)
        {
            if (req.Tables.Any(t => string.IsNullOrWhiteSpace(t) || t.Trim() is "*.*" or "*"))
            {
                return Results.BadRequest(new { error = "Overbroad wildcard '*.*' is prohibited for Unmasked access profiles. Minimum schema scope required (e.g. 'tem.*')." });
            }
        }

        // SR-M-10: clear-text (Unmasked) access must pass the Four-Eyes approval workflow of single consents;
        // the bulk path activates profiles immediately and therefore never creates them.
        if (mode == MaskingPolicyMode.Unmasked)
        {
            return Results.Json(
                new { error = "Unmasked access profiles cannot be created via bulk consent because they require approval by a different administrator. Use the consent request/approval workflow." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        // RowFilter sandbox AST validation
        if (!string.IsNullOrWhiteSpace(req.RowFilter))
        {
            var trimmedFilter = req.RowFilter.Trim();
            if (trimmedFilter.Contains(';') || trimmedFilter.Contains("--") || trimmedFilter.Contains("/*") || trimmedFilter.Contains("*/"))
            {
                return Results.BadRequest(new { error = "Row filter predicate contains prohibited SQL constructs (semicolons or comments)." });
            }

            try
            {
                var parser = FastSqlEngine.Default;
                _ = parser.ParseExpression(trimmedFilter.AsMemory(), SqlTokenSecurityOptions.None, ct);
            }
            catch (Exception ex)
            {
                SafeErrorResults.LogFailure(context, ex, "ROW_FILTER_SYNTAX_INVALID");
                var isDevelopment = context.RequestServices?.GetService<IHostEnvironment>()?.IsDevelopment() ?? false;
                return SafeErrorResults.BadRequest(
                    "ROW_FILTER_SYNTAX_INVALID",
                    isDevelopment ? $"Invalid row filter syntax: {ex.Message}" : "The row filter predicate is not valid SQL.",
                    context);
            }
        }

        var tenantId = EndpointSecurity.GetRequestTenant(context);
        var now = DateTimeOffset.UtcNow;
        var validTo = now.AddDays(req.ValidDays);
        var profileId = $"prof-{trimmedSubject.ToLowerInvariant()}-{mode.ToString().ToLowerInvariant()}-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var targetTables = req.Tables.Select(t => t.Trim()).ToList();

        // Calculate matched tables
        int tablesMatched = 0;
        IReadOnlyList<TableMetadata>? matchedTables = null; // null = classification unavailable (fail closed to Sensitive)
        var metaRepo = context.RequestServices.GetService<ITableMetadataRepository>();
        if (metaRepo != null)
        {
            try
            {
                var allTables = await metaRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
                matchedTables = allTables.Where(t => targetTables.Any(pattern => AccessProfile.MatchesPattern(pattern, t.Identifier))).ToList();
                tablesMatched = matchedTables.Count;
            }
            catch
            {
                // Fallback to table count if metadata repository query fails
                tablesMatched = targetTables.Count;
            }
        }
        else
        {
            tablesMatched = targetTables.Count;
        }

        var subjectType = string.IsNullOrWhiteSpace(req.SubjectType) ? "User" : req.SubjectType.Trim();

        // WP-SC-16: every individual grant is time-bound within the class maximum (also for bulk grants).
        if (!Enum.TryParse<GranteeType>(subjectType, ignoreCase: true, out var bulkGranteeType) || !Enum.IsDefined(bulkGranteeType))
        {
            return Results.BadRequest(new { error = $"Invalid subjectType '{subjectType}'. Supported values: User, Group, Role, ServicePrincipal." });
        }

        var grantPolicy = context.RequestServices.GetService<IGrantPolicy>() ?? new GrantPolicy();
        var grantDecision = grantPolicy.Evaluate(new GrantRequest(
            bulkGranteeType,
            trimmedSubject,
            GrantKind.BulkGrant,
            validTo,
            now,
            GrantClassifier.Classify(matchedTables),
            mode,
            Requester: callerSid,
            Justification: req.Justification.Trim()));
        if (!grantDecision.Allowed)
        {
            return Results.Json(
                new { error = grantDecision.ErrorCode, message = grantDecision.Reason, maxValidTo = grantDecision.MaxValidTo },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var profile = new AccessProfile
        {
            ProfileId = profileId,
            TenantId = tenantId,
            Name = $"BulkConsent_{trimmedSubject}_{mode}",
            MaskingMode = mode,
            TargetTables = targetTables,
            RowFilterPredicate = string.IsNullOrWhiteSpace(req.RowFilter) ? null : req.RowFilter.Trim(),
            AssignedSubjects = [],
            CreatedAt = now,
            ValidTo = validTo,
            Justification = req.Justification.Trim(),
            CreatedBy = callerSid
        };

        await accessProfileRepo.UpsertProfileAsync(profile, ct).ConfigureAwait(false);

        // WP-SC-03: the assignment goes through the strict assignment API (time-bound individual grants, Unmasked and break-glass rules).
        // A rejection leaves no unassigned profile behind.
        var assignmentSubjectType = bulkGranteeType switch
        {
            GranteeType.Group => AccessProfileSubjectType.Group,
            GranteeType.Role => AccessProfileSubjectType.Role,
            _ => AccessProfileSubjectType.User
        };
        try
        {
            await accessProfileRepo.AssignProfileAsync(tenantId, profileId, new AccessProfileAssignment
            {
                Subject = trimmedSubject,
                SubjectType = assignmentSubjectType,
                ExpiresAt = validTo,
                CreatedBy = callerSid,
                Justification = profile.Justification
            }, ct).ConfigureAwait(false);
        }
        catch (AccessProfileAssignmentRejectedException ex)
        {
            await accessProfileRepo.DeleteProfileAsync(tenantId, profileId, CancellationToken.None).ConfigureAwait(false);
            return Results.Json(new { error = ex.Code, message = "The access profile assignment violates the grant rules." }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch
        {
            await accessProfileRepo.DeleteProfileAsync(tenantId, profileId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // Tier-A audit anchoring (CONSENT_GRANTED)
        var auditEvent = new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = AuditEventTypes.ConsentGranted,
            ActorSid = context.User.GetUserSid() ?? new Sid(callerSid),
            TargetTable = string.Join(",", targetTables),
            Decision = "ALLOW",
            TraceId = context.TraceIdentifier,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                profileId,
                subject = trimmedSubject,
                subjectType,
                maskingMode = mode.ToString(),
                tables = targetTables,
                rowFilter = profile.RowFilterPredicate,
                validDays = req.ValidDays,
                validTo,
                justification = profile.Justification,
                tablesMatched
            })
        };

        var auditRepo = context.RequestServices.GetRequiredService<Autheris.Application.Audit.IAuditWriter>();
        await auditRepo.RecordRequiredAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);

        // Cluster-wide cache invalidation (AR-01)
        var accessProfileCache = context.RequestServices.GetService<Autheris.Application.Policy.Interfaces.IAccessProfileCache>();
        if (accessProfileCache != null)
        {
            try
            {
                await accessProfileCache.InvalidateTenantAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failAudit = new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = AuditEventTypes.ProfileInvalidationFailed,
                    ActorSid = context.User.GetUserSid() ?? new Sid(callerSid),
                    Decision = "DENY",
                    TraceId = context.TraceIdentifier,
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        reason = ex.Message,
                        tenant = tenantId.Value,
                        subject = trimmedSubject
                    })
                };
                await auditRepo.RecordRequiredAsync(failAudit, CancellationToken.None).ConfigureAwait(false);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }
        else
        {
            var clusterState = context.RequestServices.GetService<IDistributedClusterStateProvider>();
            if (clusterState != null)
            {
                var bumped = await clusterState.IncrementAsync($"profile_epoch:{tenantId.Value}", 1, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
                if (!bumped.HasValue)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }
        }

        var message = mode == MaskingPolicyMode.Unmasked
            ? "Bulk consent and unmasked access profile successfully created and activated."
            : "Bulk consent and access profile successfully created and activated.";

        return Results.Created($"/api/v1/profiles/{profileId}", new
        {
            profileId,
            subject = trimmedSubject,
            maskingMode = mode.ToString(),
            tablesMatched,
            rowFilter = profile.RowFilterPredicate,
            validTo,
            auditEventId = auditEvent.Id,
            message
        });
    }

    internal static async Task<IResult> GetProfilesAsync(
        HttpContext context,
        IAccessProfileRepository accessProfileRepo,
        CancellationToken ct)
    {
        var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor])
            || context.User.IsInRole("SecurityAdmin")
            || context.User.IsInRole("ClusterAdmin");
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var tenantId = EndpointSecurity.GetRequestTenant(context);
        var profiles = await accessProfileRepo.GetAllProfilesAsync(tenantId, ct).ConfigureAwait(false);
        return Results.Ok(profiles);
    }

    internal static async Task<IResult> GetProfileByIdAsync(
        string id,
        HttpContext context,
        IAccessProfileRepository accessProfileRepo,
        CancellationToken ct)
    {
        var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor])
            || context.User.IsInRole("SecurityAdmin")
            || context.User.IsInRole("ClusterAdmin");
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var tenantId = EndpointSecurity.GetRequestTenant(context);
        var profile = await accessProfileRepo.GetProfileAsync(tenantId, id, ct).ConfigureAwait(false);
        if (profile == null)
        {
            return Results.NotFound(new { error = $"Profile '{id}' not found." });
        }

        return Results.Ok(profile);
    }

    /// <summary>
    /// WP-SC-03 / SEC-SC-04: the second person approves an access-profile assignment (typically a standing Unmasked group/role mapping or an individual
    /// Unmasked grant). Four-eyes: the approver is never the creator (requester) nor the grantee; for an individual Unmasked grant the approver must be the data
    /// owner (or a delegate) of every matched table. The approval is a Tier-A audit event and bumps the profile epoch (an unavailable epoch store is a 503).
    /// </summary>
    internal static async Task<IResult> ApproveProfileAssignmentAsync(
        string id,
        ApproveProfileAssignmentRequest req,
        HttpContext context,
        IAccessProfileRepository accessProfileRepo,
        CancellationToken ct)
    {
        if (!GatewayPolicies.HasAnyRole(context.User, [GatewayRole.ClusterAdmin, GatewayRole.GovernanceAdmin]))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (req == null || string.IsNullOrWhiteSpace(req.Subject) || !Enum.TryParse<AccessProfileSubjectType>(req.SubjectType ?? "User", ignoreCase: true, out var subjectType) ||
            !Enum.IsDefined(subjectType))
        {
            return Results.BadRequest(new { error = "subject and a valid subjectType (User, Group, Role) are required." });
        }

        var callerSid = context.User.GetUserSid()?.Value ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.Identity?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(callerSid))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var tenantId = EndpointSecurity.GetRequestTenant(context);
        var profile = await accessProfileRepo.GetProfileAsync(tenantId, id, ct).ConfigureAwait(false);
        var subject = req.Subject.Trim();
        var assignment = profile?.Assignments.FirstOrDefault(a => a.SubjectType == subjectType && string.Equals(a.Subject, subject, StringComparison.OrdinalIgnoreCase));
        if (profile == null || assignment == null)
        {
            return Results.NotFound(new { error = "The assignment does not exist." });
        }

        if (string.Equals(assignment.CreatedBy?.Trim(), callerSid, StringComparison.OrdinalIgnoreCase) || IsSelfGrantSubject(context, subject))
        {
            return Results.Json(new { error = AccessProfileAssignmentRules.SameApprover, message = "Four-eyes: the approver must differ from the requester and the grantee." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Route (b): an individual Unmasked grant needs the data owner (or delegate) of every matched table.
        if (profile.MaskingMode == MaskingPolicyMode.Unmasked && subjectType == AccessProfileSubjectType.User && assignment.Kind == AccessProfileAssignmentKind.Standard)
        {
            // EP-01: Individual unmasked assignment may only be approved for concrete patterns (no wildcard)
            if (profile.HasWildcardPattern)
            {
                return Results.Json(new { error = "DATA_OWNER_APPROVAL_REQUIRED", message = "Individual unmasked assignments cannot be approved for wildcard target tables." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var owners = context.RequestServices.GetService<IDataOwnershipRepository>();
            var metaRepo = context.RequestServices.GetService<ITableMetadataRepository>();
            if (owners == null || metaRepo == null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); // fail closed: ownership cannot be verified
            }

            var matched = (await metaRepo.GetAllTablesAsync(ct).ConfigureAwait(false))
                .Where(t => profile.MatchesTable(t.Identifier))
                .ToList();

            // EP-01: At least one table must match
            if (matched.Count == 0)
            {
                return Results.Json(new { error = "DATA_OWNER_APPROVAL_REQUIRED", message = "No catalogued tables match the profile target tables." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            foreach (var table in matched)
            {
                if (!await owners.IsAuthorizedApproverForTableAsync(table.Identifier, new Sid(callerSid), ct).ConfigureAwait(false))
                {
                    return Results.Json(new { error = "DATA_OWNER_APPROVAL_REQUIRED", message = $"The approver is not the data owner or a delegate of '{table.Identifier}'." },
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }
        }

        AccessProfileAssignment approved;
        try
        {
            approved = await accessProfileRepo.ApproveAssignmentAsync(tenantId, id, subjectType, subject, callerSid, ct).ConfigureAwait(false);
        }
        catch (AccessProfileAssignmentRejectedException ex)
        {
            return Results.Json(new { error = ex.Code, message = "The approval violates the grant rules." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var auditRepo = context.RequestServices.GetRequiredService<Autheris.Application.Audit.IAuditWriter>();
        var audit = new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = AuditEventTypes.ConsentApproved,
            ActorSid = context.User.GetUserSid() ?? new Sid(callerSid),
            TargetTable = string.Join(",", profile.TargetTables),
            Decision = "ALLOW",
            TraceId = context.TraceIdentifier,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                profileId = id, subject, subjectType = subjectType.ToString(), maskingMode = profile.MaskingMode.ToString(), approvedBy = callerSid, createdBy = assignment.CreatedBy
            })
        };
        await auditRepo.RecordRequiredAsync(audit, CancellationToken.None).ConfigureAwait(false);

        var cache = context.RequestServices.GetService<Autheris.Application.Policy.Interfaces.IAccessProfileCache>();
        if (cache != null)
        {
            try
            {
                await cache.InvalidateTenantAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await auditRepo.RecordRequiredAsync(new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = AuditEventTypes.ProfileInvalidationFailed,
                    ActorSid = context.User.GetUserSid() ?? new Sid(callerSid),
                    Decision = "DENY",
                    TraceId = context.TraceIdentifier,
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { reason = ex.GetType().Name, tenant = tenantId.Value, profileId = id, subject })
                }, CancellationToken.None).ConfigureAwait(false);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        return Results.Ok(new { profileId = id, subject, subjectType = subjectType.ToString(), approvedBy = approved.ApprovedBy, approvedAt = approved.ApprovedAt });
    }

    internal static async Task<IResult> DeleteProfileAsync(
        string id,
        HttpContext context,
        IAccessProfileRepository accessProfileRepo,
        CancellationToken ct)
    {
        var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin])
            || context.User.IsInRole("SecurityAdmin")
            || context.User.IsInRole("ClusterAdmin");
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var tenantId = EndpointSecurity.GetRequestTenant(context);
        var profile = await accessProfileRepo.GetProfileAsync(tenantId, id, ct).ConfigureAwait(false);
        if (profile == null)
        {
            return Results.NotFound(new { error = $"Profile '{id}' not found." });
        }

        await accessProfileRepo.DeleteProfileAsync(tenantId, id, ct).ConfigureAwait(false);

        var callerSid = context.User.GetUserSid()?.Value ?? context.User.Identity?.Name ?? "UNKNOWN";
        var auditEvent = new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = AuditEventTypes.ConsentRevoked,
            ActorSid = context.User.GetUserSid() ?? new Sid(callerSid),
            TargetTable = string.Join(",", profile.TargetTables),
            Decision = "DENY",
            TraceId = context.TraceIdentifier,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                profileId = id,
                profile.Name,
                profile.MaskingMode,
                profile.TargetTables,
                profile.AssignedSubjects,
                revokedBy = callerSid
            })
        };

        var auditRepo = context.RequestServices.GetRequiredService<Autheris.Application.Audit.IAuditWriter>();
        await auditRepo.RecordRequiredAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);

        // Cluster-wide cache invalidation (AR-01)
        var accessProfileCache = context.RequestServices.GetService<Autheris.Application.Policy.Interfaces.IAccessProfileCache>();
        if (accessProfileCache != null)
        {
            try
            {
                await accessProfileCache.InvalidateTenantAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failAudit = new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = AuditEventTypes.ProfileInvalidationFailed,
                    ActorSid = context.User.GetUserSid() ?? new Sid(callerSid),
                    Decision = "DENY",
                    TraceId = context.TraceIdentifier,
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        reason = ex.Message,
                        tenant = tenantId.Value,
                        profileId = id.ToString()
                    })
                };
                await auditRepo.RecordRequiredAsync(failAudit, CancellationToken.None).ConfigureAwait(false);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }
        else
        {
            var clusterState = context.RequestServices.GetService<IDistributedClusterStateProvider>();
            if (clusterState != null)
            {
                var bumped = await clusterState.IncrementAsync($"profile_epoch:{tenantId.Value}", 1, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
                if (!bumped.HasValue)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }
        }

        return Results.Ok(new { message = $"Profile '{id}' successfully revoked.", auditEventId = auditEvent.Id });
    }

    private static string ResolveTenantBoundClientId(HttpContext context, string clientId, out bool isForbidden)
    {
        isForbidden = false;
        var reqTenant = EndpointSecurity.GetRequestTenant(context).Value;
        var isCanonicalClusterAdmin = EndpointSecurity.IsCanonicalClusterAdmin(context.User);

        var colonIdx = clientId.IndexOf(':');
        if (colonIdx > 0)
        {
            var targetTenant = clientId[..colonIdx];
            if (!isCanonicalClusterAdmin && !string.Equals(targetTenant, reqTenant, StringComparison.OrdinalIgnoreCase))
            {
                isForbidden = true;
                return clientId;
            }
            return clientId;
        }

        return string.IsNullOrWhiteSpace(reqTenant) ? clientId : $"{reqTenant}:{clientId}";
    }
}
