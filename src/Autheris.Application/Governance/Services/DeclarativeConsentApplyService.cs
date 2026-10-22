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
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

public sealed class DeclarativeConsentApplyService(
    ITableMetadataRepository metadataRepository,
    IConsentRepository consentRepository,
    IAuditLogRepository auditLogRepository,
    IGrantPolicy? grantPolicy = null,
    IDatasetCatalogAdminService? datasetCatalog = null,
    IOptions<GovernanceTextOptions>? textOptions = null) : IDeclarativeConsentApplyService
{
    // WP-SC-16: the policy is stateless; without an injected instance the built-in maxima apply (never "no policy").
    // Kept for constructor/DI compatibility: the APPLIED audit entries are written by ReplaceConsentsAsync inside its transaction, never separately.
    private readonly IAuditLogRepository _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));

    private readonly IGrantPolicy _grantPolicy = grantPolicy ?? new GrantPolicy();

    private readonly int _descriptionMaxLength = (textOptions?.Value ?? new GovernanceTextOptions()).ConsentDescriptionMaxLength;

    public async Task<DeclarativeApplyResult> ApplyManifestAsync(
        ClaimsPrincipal caller,
        DeclarativeConsentManifest manifest,
        bool dryRun = false,
        SecurityPrincipalContext? callerContext = null,
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

        // SR-M-10: the self-grant check compares against every identifier form of the actor and the groups and roles it holds.
        var actorIdentifiers = ActorIdentifiers.Collect(caller, actorSid, callerContext);
        var hasGroupOverage = HasGroupOverage(caller);

        int totalEvaluated = 0;
        int applied = 0;
        int unchanged = 0;
        int revoked = 0;
        int updated = 0;
        var details = new List<string>();

        var hasDatasets = manifest.Datasets is { Count: > 0 };
        if ((manifest.Policies == null || manifest.Policies.Count == 0) && !hasDatasets)
        {
            return new DeclarativeApplyResult(0, 0, 0, 0, dryRun, ["Empty manifest; 0 policies processed."]);
        }

        var policies = manifest.Policies ?? [];
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in policies)
        {
            var key = $"{p.Domain}|{p.Schema}|{p.TableName}|{(string.Equals(p.GranteeType, "Role", StringComparison.OrdinalIgnoreCase) ? "Role" : "Sid")}|{p.GranteeRef}";
            if (!seenTargets.Add(key))
            {
                throw new ArgumentException(
                    $"Manifest contains more than one policy for '{p.Domain}.{p.Schema}.{p.TableName}' -> '{p.GranteeRef}'; refusing an ambiguous apply.", nameof(manifest));
            }
        }

        // PLAN-GOV-158-INC1 decision 6: normalise every description before anything is read or written (also in dry-run); a violation
        // rejects the whole manifest with a neutral message that names only the policy index (SEC-GOV158I1-03).
        var descriptions = new string?[policies.Count];
        for (var i = 0; i < policies.Count; i++)
        {
            var check = UntrustedText.Normalize(policies[i].Description, _descriptionMaxLength, allowLineFeed: false);
            if (!check.IsValid)
            {
                throw new DeclarativeManifestValidationException(i);
            }

            descriptions[i] = check.Value;
        }

        var revokeIds = new List<Guid>();
        var descriptionUpdates = new List<ConsentDescriptionUpdate>();
        var createList = new List<Consent>();
        var auditEntries = new List<AuditLogEntry>();

        for (var policyIndex = 0; policyIndex < policies.Count; policyIndex++)
        {
            var policy = policies[policyIndex];
            var description = descriptions[policyIndex];
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

            // SR-M-10 (SoD): no self-grant, neither directly (any identifier form) nor through a group or role the actor holds.
            // A deny towards an own group or role only restricts access and stays allowed; an individual policy for the actor is refused for any effect.
            if ((individual || effect == ConsentEffect.Allow) && ActorIdentifiers.IsActorSubject(actorIdentifiers, policy.GranteeRef))
            {
                throw new GrantPolicyViolationException(GrantDecision.Deny(
                    GrantErrorCodes.FourEyesRequired,
                    $"Self-grant prohibited: Actor '{actorSid}' cannot grant consent to themselves or to a group or role they belong to via declarative manifest."));
            }

            // With an Entra group overage the token does not list every group of the actor, so a grant to an own group cannot be
            // ruled out: an Allow towards a group is refused (fail closed) instead of relying on an incomplete group list.
            if (granteeType == GranteeType.Group && effect == ConsentEffect.Allow && hasGroupOverage)
            {
                throw new GrantPolicyViolationException(GrantDecision.Deny(
                    GrantErrorCodes.FourEyesRequired,
                    "Group grant refused: the actor's token carries an Entra group overage marker, so its group memberships are incomplete and a self-grant through a group cannot be ruled out. Use a token with the full group list or the consent request/approval workflow."));
            }

            DateTimeOffset validTo;
            if (individual)
            {
                var sensitivity = GrantClassifier.Classify([metadata]);
                var hasClearColumnRule = HasClearColumnRule(policy);
                var decision = _grantPolicy.Evaluate(new GrantRequest(
                    granteeType,
                    policy.GranteeRef,
                    GrantKind.ManifestPolicy,
                    policy.ValidUntil,
                    now,
                    sensitivity,
                    hasClearColumnRule ? MaskingPolicyMode.Unmasked : MaskingPolicyMode.Default,
                    Requester: actorSid.Value,
                    Justification: description ?? "GitOps manifest",
                    Effect: effect));
                GrantPolicyViolationException.ThrowIfDenied(decision);

                // The manifest activates the consent immediately and carries no approvals, so an Allow on a sensitive table or with a
                // Clear column rule (or any class the policy marks as four-eyes) is refused here (also in dry-run) and must use the consent
                // request/approval workflow. Sensitivity and Clear are checked directly because the policy classifies every service
                // principal as GrantClass.ServicePrincipal, which carries no four-eyes flag. The manifest never sets a privileged right.
                if (effect == ConsentEffect.Allow &&
                    (sensitivity == GrantSensitivity.Sensitive || hasClearColumnRule || decision.RequiresFourEyes))
                {
                    throw new GrantPolicyViolationException(GrantDecision.Deny(
                        GrantErrorCodes.FourEyesRequired,
                        "A declarative manifest cannot activate a sensitive or unmasked individual grant: it requires independent four-eyes approval. Use the consent request/approval workflow.",
                        decision.Class,
                        decision.MaxValidTo));
                }

                // A deny needs no expiry; without one it is stored as long-lived (deny rules are never clamped).
                validTo = policy.ValidUntil ?? now.AddYears(100);
            }
            else
            {
                var days = policy.ValidityDays is > 0 ? Math.Min(policy.ValidityDays.Value, 365) : 365;
                validTo = now.AddDays(days);
            }

            // WP-SC-02: role consents have no subject SID, so they are looked up by role name (the subject lookup never returns them).
            IReadOnlyList<Consent> existingConsents = granteeType == GranteeType.Role
                ? await consentRepository.GetActiveRoleConsentsAsync(tableId, roleName!, tenantId, now, ct).ConfigureAwait(false)
                : await consentRepository.GetActiveConsentsForSubjectsAsync(new[] { granteeSid!.Value }, tableId, now, tenantId, ct).ConfigureAwait(false);

            var activeExisting = existingConsents
                .Where(c => c.IsActive(now) && c.TenantId == tenantId && MatchesGrantee(c, granteeType, granteeSid, roleName))
                .ToList();

            // Issue #116: scope-only column rules never grant access (fail closed); surface that as a non-blocking warning.
            if (policy.ColumnRules != null)
            {
                foreach (var scopeOnlyColumn in policy.ColumnRules
                    .GroupBy(r => r.ColumnName, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.All(r => r.ApplicableScopes != null && r.ApplicableScopes.Count > 0))
                    .Select(g => g.Key))
                {
                    details.Add($"WARNING: Column '{scopeOnlyColumn}' of '{tableId}' -> '{policy.GranteeRef}' has only scope-specific rules; without a rule that applies to all scopes it stays denied for callers whose scopes are not listed (add a rule without scopes to set the base level).");
                }
            }

            // Check if existing consent matches desired policy
            var matchingConsent = activeExisting.FirstOrDefault(c => IsIdentical(c, effect, policy, individual ? policy.ValidUntil : null));
            if (matchingConsent != null)
            {
                // Decision 6: a description-only difference is an in-place update (same id, no revoke, no epoch bump); every semantic
                // difference already failed IsIdentical and takes the revoke/create path (SEC-GOV158I1-07).
                await ConsentDescriptionLoader.LoadAsync(consentRepository, [matchingConsent], ct).ConfigureAwait(false);
                var storedDescription = UntrustedText.Normalize(matchingConsent.Description, GovernanceTextOptions.ConsentDescriptionCeiling, allowLineFeed: false);
                var storedNormalized = storedDescription.IsValid ? storedDescription.Value : matchingConsent.Description;
                if (!string.Equals(storedNormalized, description, StringComparison.Ordinal))
                {
                    updated++;
                    if (!dryRun)
                    {
                        descriptionUpdates.Add(new ConsentDescriptionUpdate(matchingConsent.Id, tenantId, description));
                        var (length, prefix) = ConsentDescriptionText.Fingerprint(description);
                        auditEntries.Add(new AuditLogEntry
                        {
                            Id = Guid.NewGuid(),
                            OccurredAt = now,
                            EventType = "GITOPS_CONSENT_APPLY",
                            ActorSid = actorSid,
                            TargetTable = tableId.ToString(),
                            Decision = "APPLIED",
                            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { Version = manifest.Version, Update = "description", DescriptionLength = length, DescriptionSha256Prefix = prefix }),
                            TenantId = tenantId
                        });
                    }

                    details.Add($"{(dryRun ? "[DryRun] Would update" : "Updated")} description for '{tableId}' -> '{policy.GranteeRef}'.");
                    continue;
                }

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
                    Description = description,
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
                var (fingerprintLength, fingerprintPrefix) = ConsentDescriptionText.Fingerprint(description);
                auditEntries.Add(new AuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredAt = now,
                    EventType = "GITOPS_CONSENT_APPLY",
                    ActorSid = actorSid,
                    TargetTable = tableId.ToString(),
                    Decision = "APPLIED",
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Reason = $"Applied declarative policy version {manifest.Version}",
                        Version = manifest.Version,
                        DescriptionLength = fingerprintLength,
                        DescriptionSha256Prefix = fingerprintPrefix
                    }),
                    TenantId = tenantId
                });
            }

            applied++;
            details.Add($"{(dryRun ? "[DryRun] Would apply" : "Applied")} policy for '{tableId}' -> '{policy.GranteeRef}'.");
        }

        // Issue #111 (A-7, BC-11): the dataset entries run the service dry run first (same authorization, tenant rule and validation as the
        // apply), so one rejected entry stops the whole manifest before anything is written.
        var datasetEdits = new List<(string Id, DatasetCatalogEdit Edit)>();
        if (hasDatasets)
        {
            if (datasetCatalog is null)
            {
                details.Add($"Dataset catalog settings are not available; {manifest.Datasets!.Count} dataset entries were not applied.");
            }
            else
            {
                var skipped = 0;
                foreach (var entry in manifest.Datasets!)
                {
                    totalEvaluated++;
                    var datasetId = new TableIdentifier(entry.Domain, entry.Schema, entry.TableName).ToString();
                    var edit = new DatasetCatalogEdit(entry.Tags, null, null, entry.Visibility);
                    var planned = await datasetCatalog.ApplyAsync(caller, datasetId, edit, "manifest", ct, dryRun: true).ConfigureAwait(false);
                    switch (planned.Outcome)
                    {
                        case DatasetCatalogOutcome.NotFound:
                            details.Add($"Dataset '{datasetId}' not found in catalog; skipped catalog settings.");
                            skipped++;
                            break;
                        case DatasetCatalogOutcome.Applied:
                            applied++;
                            datasetEdits.Add((datasetId, edit));
                            details.Add($"{(dryRun ? "[DryRun] Would apply" : "Applied")} catalog settings for '{datasetId}'.");
                            break;
                        case DatasetCatalogOutcome.Unchanged:
                            unchanged++;
                            details.Add($"Catalog settings for '{datasetId}' are unchanged.");
                            break;
                        default:
                            throw new DeclarativeDatasetCatalogException(planned.Outcome);
                    }
                }

                // The dry-run pass writes no per-entry probe audit; a real apply writes one aggregated entry per manifest (throttled). A dry run writes no audit.
                if (skipped > 0 && !dryRun)
                {
                    await datasetCatalog.RecordManifestProbesAsync(caller, skipped, ct).ConfigureAwait(false);
                }
            }
        }

        // WP-SC-02: one transaction and one epoch bump for the whole manifest; a fault leaves nothing changed and writes no audit.
        // M2: the APPLIED audit entries travel into the same transaction, so a committed change always has its (Tier-A, anchored) record and a failing
        // epoch invalidation after the commit cannot lose it.
        if (!dryRun && (createList.Count > 0 || revokeIds.Count > 0 || descriptionUpdates.Count > 0))
        {
            var supersededReason = $"Superseded by GitOps declarative apply (version: {manifest.Version})";
            if (descriptionUpdates.Count == 0)
            {
                await consentRepository.ReplaceConsentsAsync(revokeIds, createList, actorSid, supersededReason, auditEntries, ct).ConfigureAwait(false);
            }
            else
            {
                var descriptionWriter = consentRepository as IConsentDescriptionRepository
                    ?? throw new InvalidOperationException("The consent repository does not support in-place description updates.");
                await descriptionWriter.ReplaceConsentsAsync(revokeIds, createList, actorSid, supersededReason, auditEntries, descriptionUpdates, ct).ConfigureAwait(false);
            }
        }

        // Not atomic with the consent commit above: validation is all-or-nothing, a storage fault in the middle is not rolled back across the two stores.
        if (!dryRun)
        {
            foreach (var (datasetId, edit) in datasetEdits)
            {
                var result = await datasetCatalog!.ApplyAsync(caller, datasetId, edit, "manifest", ct).ConfigureAwait(false);
                if (!result.IsSuccess && result.Outcome != DatasetCatalogOutcome.NotFound)
                {
                    throw new DeclarativeDatasetCatalogException(result.Outcome);
                }
            }
        }

        return new DeclarativeApplyResult(totalEvaluated, applied, unchanged, revoked, dryRun, details, updated);
    }

    /// <summary>
    /// Entra group overage: with too many groups the token carries "hasgroups" (implicit flow) or a "_claim_names" entry for "groups"
    /// instead of the group list, so the group claims are incomplete.
    /// </summary>
    private static bool HasGroupOverage(ClaimsPrincipal caller) =>
        caller.HasClaim(c => string.Equals(c.Type, "hasgroups", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(c.Value?.Trim(), "false", StringComparison.OrdinalIgnoreCase)) ||
        caller.HasClaim(c => string.Equals(c.Type, "_claim_names", StringComparison.OrdinalIgnoreCase)
                             && c.Value != null
                             && c.Value.Contains("groups", StringComparison.OrdinalIgnoreCase));

    // A Clear column rule exposes the column in clear text: the grant is classified like an Unmasked one.
    private static bool HasClearColumnRule(DeclarativeConsentPolicy policy) =>
        policy.ColumnRules?.Any(r => Enum.TryParse<ColumnAccessLevel>(r.AccessLevel, ignoreCase: true, out var level) && level == ColumnAccessLevel.Clear) == true;

    private static bool MatchesGrantee(Consent consent, GranteeType type, Sid? sid, string? roleName)
    {
        if (consent.GranteeType != type) return false;
        if (type == GranteeType.Role)
        {
            return string.Equals(consent.RoleName, roleName, StringComparison.OrdinalIgnoreCase);
        }
        return consent.GranteeSid == sid;
    }

    /// <param name="desiredIndividualValidTo">
    /// The explicit validUntil of an individual grant. An EARLIER expiry than the stored one is a change (the manifest shortens the grant); a later or equal one is not
    /// (a longer validity is a renewal, which needs its own approval, so the manifest cannot extend a grant).
    /// </param>
    private static bool IsIdentical(Consent existing, ConsentEffect desiredEffect, DeclarativeConsentPolicy policy, DateTimeOffset? desiredIndividualValidTo = null)
    {
        if (existing.Effect != desiredEffect) return false;
        if (desiredIndividualValidTo is { } desired && desired < existing.ValidTo) return false;

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

/// <summary>Issue #111: a dataset entry of the manifest was rejected by the admin service; the whole manifest is refused with a stable code.</summary>
public sealed class DeclarativeDatasetCatalogException(DatasetCatalogOutcome outcome) : Exception("A dataset catalog entry of the manifest was rejected.")
{
    public DatasetCatalogOutcome Outcome { get; } = outcome;

    public string ErrorCode => Outcome switch
    {
        DatasetCatalogOutcome.InvalidTag => "INVALID_TAG",
        DatasetCatalogOutcome.TagLimitExceeded => "TAG_LIMIT_EXCEEDED",
        DatasetCatalogOutcome.InvalidVisibility => "INVALID_VISIBILITY",
        DatasetCatalogOutcome.Forbidden => "FORBIDDEN",
        DatasetCatalogOutcome.Unavailable => "UNAVAILABLE",
        _ => "INVALID_REQUEST"
    };

    public int StatusCode => Outcome switch
    {
        DatasetCatalogOutcome.Forbidden => 403,
        DatasetCatalogOutcome.Unavailable => 503,
        _ => 400
    };
}
