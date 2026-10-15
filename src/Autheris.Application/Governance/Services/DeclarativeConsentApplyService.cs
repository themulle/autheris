namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed class DeclarativeConsentApplyService(
    ITableMetadataRepository metadataRepository,
    IConsentRepository consentRepository,
    IAuditLogRepository auditLogRepository,
    IGrantPolicy? grantPolicy = null) : IDeclarativeConsentApplyService
{
    // WP-SC-16: the policy is stateless; without an injected instance the built-in maxima apply (never "no policy").
    private readonly IGrantPolicy _grantPolicy = grantPolicy ?? new GrantPolicy();

    public async Task<DeclarativeApplyResult> ApplyManifestAsync(
        ClaimsPrincipal caller,
        DeclarativeConsentManifest manifest,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(manifest);

        if (caller.Identity?.IsAuthenticated != true)
        {
            throw new System.Security.SecurityException("Caller must be authenticated to apply declarative consent manifests.");
        }

        var isPrivileged = caller.IsInRole("GOVERNANCE_ADMIN") ||
                           caller.IsInRole("CLUSTER_ADMIN") ||
                           caller.IsInRole("DATA_GOVERNANCE_REVIEWER") ||
                           caller.HasClaim(c => c.Type == "scope" && c.Value.Contains("governance:admin"));
        if (!isPrivileged)
        {
            throw new System.Security.SecurityException("Caller lacks required governance permissions to apply declarative consent manifests.");
        }

        var actorSid = caller.GetUserSid() ?? new Sid("S-1-5-21-GITOPS-SYNC");
        var tenantId = caller.GetTenantId();
        var now = DateTimeOffset.UtcNow;

        int totalEvaluated = 0;
        int applied = 0;
        int unchanged = 0;
        int revoked = 0;
        var details = new List<string>();

        if (manifest.Policies == null || manifest.Policies.Count == 0)
        {
            return new DeclarativeApplyResult(0, 0, 0, 0, dryRun, ["Empty manifest; 0 policies processed."]);
        }

        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in manifest.Policies)
        {
            var key = $"{p.Domain}|{p.Schema}|{p.TableName}|{(string.Equals(p.GranteeType, "Role", StringComparison.OrdinalIgnoreCase) ? "Role" : "Sid")}|{p.GranteeRef}";
            if (!seenTargets.Add(key))
            {
                throw new ArgumentException(
                    $"Manifest contains more than one policy for '{p.Domain}.{p.Schema}.{p.TableName}' -> '{p.GranteeRef}'; refusing an ambiguous apply.", nameof(manifest));
            }
        }

        var revokeIds = new List<Guid>();
        var createList = new List<Consent>();
        var auditEntries = new List<AuditLogEntry>();

        foreach (var policy in manifest.Policies)
        {
            totalEvaluated++;
            var tableId = new TableIdentifier(policy.Domain, policy.Schema, policy.TableName);
            var metadata = await metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
            if (metadata == null)
            {
                details.Add($"Table '{tableId}' not found in catalog; skipped policy.");
                continue;
            }

            if (!Enum.TryParse<GranteeType>(policy.GranteeType, ignoreCase: true, out var granteeType))
            {
                granteeType = GranteeType.User;
            }

            if (!Enum.TryParse<ConsentEffect>(policy.Effect, ignoreCase: true, out var effect))
            {
                throw new ArgumentException($"Invalid consent effect '{policy.Effect}' for table '{tableId}'. Effect must be explicitly specified (e.g. 'Allow' or 'Deny').", nameof(manifest));
            }

            Sid? granteeSid = null;
            string? roleName = null;
            Guid? roleId = null;

            if (granteeType == GranteeType.Role)
            {
                roleName = policy.GranteeRef;
            }
            else
            {
                granteeSid = new Sid(policy.GranteeRef);
            }

            // WP-SC-16: individual grants need an absolute, class-bounded validUntil (also in dry-run). Group/role grants are standing.
            var individual = granteeType is GranteeType.User or GranteeType.ServicePrincipal;
            DateTimeOffset validTo;
            if (individual)
            {
                var decision = _grantPolicy.Evaluate(new GrantRequest(
                    granteeType,
                    policy.GranteeRef,
                    GrantKind.ManifestPolicy,
                    policy.ValidUntil,
                    now,
                    GrantClassifier.Classify([metadata]),
                    Justification: policy.Description ?? "GitOps manifest",
                    Effect: effect));
                GrantPolicyViolationException.ThrowIfDenied(decision);

                // A deny needs no expiry; without one it is stored as long-lived (deny rules are never clamped).
                validTo = policy.ValidUntil ?? now.AddYears(100);
            }
            else
            {
                validTo = now.AddDays(policy.ValidityDays is > 0 ? policy.ValidityDays.Value : 365);
            }

            // WP-SC-02: role consents have no subject SID, so they are looked up by role name (the subject lookup never returns them).
            IReadOnlyList<Consent> existingConsents = granteeType == GranteeType.Role
                ? await consentRepository.GetActiveRoleConsentsAsync(tableId, roleName!, tenantId, now, ct).ConfigureAwait(false)
                : await consentRepository.GetActiveConsentsForSubjectsAsync(new[] { granteeSid!.Value }, tableId, now, tenantId, ct).ConfigureAwait(false);

            var activeExisting = existingConsents
                .Where(c => c.IsActive(now) && c.TenantId == tenantId && MatchesGrantee(c, granteeType, granteeSid, roleName))
                .ToList();

            // Check if existing consent matches desired policy
            var matchingConsent = activeExisting.FirstOrDefault(c => IsIdentical(c, effect, policy));
            if (matchingConsent != null)
            {
                unchanged++;
                details.Add($"Policy for '{tableId}' -> '{policy.GranteeRef}' is unchanged.");
                continue;
            }

            // Needs apply / change
            revoked += activeExisting.Count;
            if (!dryRun)
            {
                revokeIds.AddRange(activeExisting.Select(c => c.Id));

                // Construct new Consent
                var consentId = Guid.NewGuid();
                var newConsent = new Consent
                {
                    Id = consentId,
                    TableId = metadata.Table.Id,
                    TableIdentifier = tableId,
                    TenantId = tenantId,
                    Effect = effect,
                    GranteeType = granteeType,
                    GranteeSid = granteeSid,
                    RoleName = roleName,
                    RoleId = roleId,
                    ValidFrom = now,
                    ValidTo = validTo,
                    IsRevoked = false
                };

                // Build column rules
                if (policy.ColumnRules != null && policy.ColumnRules.Count > 0)
                {
                    var colRules = new List<ConsentColumnRule>();
                    foreach (var cr in policy.ColumnRules)
                    {
                        var colMeta = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, cr.ColumnName, StringComparison.OrdinalIgnoreCase));
                        var accessLevel = Enum.TryParse<ColumnAccessLevel>(cr.AccessLevel, ignoreCase: true, out var lvl) ? lvl : ColumnAccessLevel.Deny;
                        colRules.Add(new ConsentColumnRule
                        {
                            Id = Guid.NewGuid(),
                            ConsentId = consentId,
                            TableColumnId = colMeta?.Id ?? Guid.NewGuid(),
                            ColumnName = cr.ColumnName,
                            AccessLevel = accessLevel,
                            ApplicableScopes = cr.ApplicableScopes ?? Array.Empty<string>()
                        });
                    }
                    newConsent.ColumnRules = colRules;
                }

                // Build row filters
                if (policy.RowFilters != null && policy.RowFilters.Count > 0)
                {
                    var rowFilters = new List<ConsentRowFilter>();
                    foreach (var rf in policy.RowFilters)
                    {
                        var colMeta = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, rf.ColumnName, StringComparison.OrdinalIgnoreCase));
                        rowFilters.Add(new ConsentRowFilter
                        {
                            Id = Guid.NewGuid(),
                            ConsentId = consentId,
                            FilterGroup = 0,
                            TableColumnId = colMeta?.Id ?? Guid.NewGuid(),
                            ColumnName = rf.ColumnName,
                            Operator = rf.Operator,
                            ValueType = rf.ValueType,
                            ValueJson = rf.ValueJson,
                            ValueSource = rf.ValueSource ?? "LITERAL",
                            UserAttribute = rf.UserAttribute
                        });
                    }
                    newConsent.RowFilters = rowFilters;
                }

                createList.Add(newConsent);
                auditEntries.Add(new AuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredAt = now,
                    EventType = "GITOPS_CONSENT_APPLY",
                    ActorSid = actorSid,
                    TargetTable = tableId.ToString(),
                    Decision = "APPLIED",
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { Reason = $"Applied declarative policy version {manifest.Version}: {policy.Description ?? "No description"}" }),
                    TenantId = tenantId
                });
            }

            applied++;
            details.Add($"{(dryRun ? "[DryRun] Would apply" : "Applied")} policy for '{tableId}' -> '{policy.GranteeRef}'.");
        }

        // WP-SC-02: one transaction and one epoch bump for the whole manifest; a fault leaves nothing changed and writes no audit.
        if (!dryRun && (createList.Count > 0 || revokeIds.Count > 0))
        {
            await consentRepository.ReplaceConsentsAsync(
                revokeIds,
                createList,
                actorSid,
                $"Superseded by GitOps declarative apply (version: {manifest.Version})",
                ct).ConfigureAwait(false);

            foreach (var entry in auditEntries)
            {
                await auditLogRepository.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
            }
        }

        return new DeclarativeApplyResult(totalEvaluated, applied, unchanged, revoked, dryRun, details);
    }

    private static bool MatchesGrantee(Consent consent, GranteeType type, Sid? sid, string? roleName)
    {
        if (consent.GranteeType != type) return false;
        if (type == GranteeType.Role)
        {
            return string.Equals(consent.RoleName, roleName, StringComparison.OrdinalIgnoreCase);
        }
        return consent.GranteeSid == sid;
    }

    private static bool IsIdentical(Consent existing, ConsentEffect desiredEffect, DeclarativeConsentPolicy policy)
    {
        if (existing.Effect != desiredEffect) return false;

        // Check column rules
        var desiredColRules = policy.ColumnRules ?? Array.Empty<DeclarativeColumnRule>();
        if (existing.ColumnRules.Count != desiredColRules.Count) return false;

        foreach (var desiredCr in desiredColRules)
        {
            var match = existing.ColumnRules.FirstOrDefault(c => string.Equals(c.ColumnName, desiredCr.ColumnName, StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;
            var desiredLevel = Enum.TryParse<ColumnAccessLevel>(desiredCr.AccessLevel, ignoreCase: true, out var lvl) ? lvl : ColumnAccessLevel.Deny;
            if (match.AccessLevel != desiredLevel) return false;

            var existingScopes = match.ApplicableScopes ?? Array.Empty<string>();
            var desiredScopes = desiredCr.ApplicableScopes ?? Array.Empty<string>();
            if (existingScopes.Count != desiredScopes.Count) return false;
            if (!existingScopes.All(s => desiredScopes.Contains(s, StringComparer.OrdinalIgnoreCase))) return false;
        }

        // Check row filters
        var desiredRowFilters = policy.RowFilters ?? Array.Empty<DeclarativeRowFilter>();
        if (existing.RowFilters.Count != desiredRowFilters.Count) return false;

        foreach (var desiredRf in desiredRowFilters)
        {
            var match = existing.RowFilters.FirstOrDefault(r => string.Equals(r.ColumnName, desiredRf.ColumnName, StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;
            if (!string.Equals(match.Operator, desiredRf.Operator, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(match.ValueJson, desiredRf.ValueJson, StringComparison.Ordinal)) return false;
            if (!string.Equals(match.UserAttribute, desiredRf.UserAttribute, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(match.ValueSource ?? "LITERAL", desiredRf.ValueSource ?? "LITERAL", StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}
