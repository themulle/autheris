namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed class DeclarativeConsentApplyService(
    ITableMetadataRepository metadataRepository,
    IConsentRepository consentRepository,
    IAuditLogRepository auditLogRepository) : IDeclarativeConsentApplyService
{
    public async Task<DeclarativeApplyResult> ApplyManifestAsync(
        ClaimsPrincipal caller,
        DeclarativeConsentManifest manifest,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(manifest);

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
                effect = ConsentEffect.Allow;
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

            // Load existing active consents for table
            var subjects = granteeSid != null ? new[] { granteeSid.Value } : Array.Empty<Sid>();
            var roles = roleName != null ? new[] { roleName } : Array.Empty<string>();

            var existingConsents = await consentRepository.GetActiveConsentsForSubjectsAsync(
                subjects, tableId, now, tenantId, ct).ConfigureAwait(false);

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
            if (!dryRun)
            {
                // Revoke old obsolete active consents
                foreach (var oldConsent in activeExisting)
                {
                    await consentRepository.RevokeConsentAsync(
                        oldConsent.Id,
                        actorSid,
                        $"Superseded by GitOps declarative apply (version: {manifest.Version})",
                        ct).ConfigureAwait(false);
                    revoked++;
                }

                // Construct new Consent
                var validityDays = policy.ValidityDays is > 0 ? policy.ValidityDays.Value : 365;
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
                    ValidTo = now.AddDays(validityDays),
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

                await consentRepository.CreateConsentAsync(newConsent, ct).ConfigureAwait(false);

                // Record WORM audit event
                await auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredAt = now,
                    EventType = "GITOPS_CONSENT_APPLY",
                    ActorSid = actorSid,
                    TargetTable = tableId.ToString(),
                    Decision = "APPLIED",
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { Reason = $"Applied declarative policy version {manifest.Version}: {policy.Description ?? "No description"}" }),
                    TenantId = tenantId
                }, ct).ConfigureAwait(false);
            }
            else
            {
                if (activeExisting.Count > 0)
                {
                    revoked += activeExisting.Count;
                }
            }

            applied++;
            details.Add($"{(dryRun ? "[DryRun] Would apply" : "Applied")} policy for '{tableId}' -> '{policy.GranteeRef}'.");
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
        }

        return true;
    }
}
