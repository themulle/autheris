namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Security;
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
        ClaimTypes.Upn, ClaimTypes.Email, ClaimTypes.Name, "preferred_username", "upn", "email", "name"
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

        // MaskingMode validation
        if (!Enum.TryParse<MaskingPolicyMode>(req.MaskingMode, ignoreCase: true, out var mode))
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
        var metaRepo = context.RequestServices.GetService<ITableMetadataRepository>();
        if (metaRepo != null)
        {
            try
            {
                var allTables = await metaRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
                tablesMatched = allTables.Count(t => targetTables.Any(pattern => AccessProfile.MatchesPattern(pattern, t.Identifier)));
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
        var profile = new AccessProfile
        {
            ProfileId = profileId,
            TenantId = tenantId,
            Name = $"BulkConsent_{trimmedSubject}_{mode}",
            MaskingMode = mode,
            TargetTables = targetTables,
            RowFilterPredicate = string.IsNullOrWhiteSpace(req.RowFilter) ? null : req.RowFilter.Trim(),
            AssignedSubjects = [trimmedSubject],
            CreatedAt = now,
            ValidTo = validTo,
            Justification = req.Justification.Trim(),
            CreatedBy = callerSid
        };

        await accessProfileRepo.UpsertProfileAsync(profile, ct).ConfigureAwait(false);

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
