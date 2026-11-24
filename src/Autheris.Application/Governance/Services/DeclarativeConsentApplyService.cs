namespace Autheris.Application.Governance.Services;

using Autheris.Application.Governance.Ownership;
using Autheris.Domain.Identity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Governance.Validation;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

public sealed partial class DeclarativeConsentApplyService(
    ITableMetadataRepository metadataRepository,
    IConsentRepository consentRepository,
    IAuditLogRepository auditLogRepository,
    IGrantPolicy? grantPolicy = null,
    IDatasetCatalogAdminService? datasetCatalog = null,
    IOptions<GovernanceTextOptions>? textOptions = null,
    IGatewayRoleEvaluator? roleEvaluator = null,
    IContainerOwnership? dataOwnershipRepository = null,
    IGrantValidityPolicy? grantValidityPolicy = null) : IDeclarativeConsentApplyService, IDeclarativeManifestPlanner
{
    public const int MaxDeclarativePruneCount = 10_000;
    public const string PrunedByManifestReason = "PRUNED_BY_MANIFEST";

    private sealed class KeyedLockEntry : IDisposable
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
        public void Dispose() => Semaphore.Dispose();
    }

    private static readonly object GranteeLocksGate = new();
    private static readonly Dictionary<string, KeyedLockEntry> GranteeLocks = new(StringComparer.Ordinal);
    // Declarative grants are authored by Data Governance only; IT administration never applies a manifest directly.
    private static readonly GatewayRole[] RequiredApplyRoles = [GatewayRole.GovernanceAdmin];

    // the policy is stateless; without an injected instance the test-default validity maxima apply (never "no policy").
    // Kept for constructor/DI compatibility: the APPLIED audit entries are written by ReplaceConsentsAsync inside its transaction, never separately.
    private readonly IAuditLogRepository _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));

    private readonly IGrantPolicy _grantPolicy = grantPolicy ?? new GrantPolicy(validityPolicy: grantValidityPolicy);
    private readonly IGrantValidityPolicy _grantValidityPolicy = grantValidityPolicy ?? GrantValidityPolicy.CreateDefault();
    private readonly IGatewayRoleEvaluator _roleEvaluator = roleEvaluator ?? new GatewayRoleEvaluator();
    private readonly IContainerOwnership? _dataOwnershipRepository =
        dataOwnershipRepository ?? (metadataRepository as IContainerOwnership) ?? (consentRepository as IContainerOwnership);

    private readonly int _descriptionMaxLength = (textOptions?.Value ?? new GovernanceTextOptions()).ConsentDescriptionMaxLength;

    public async Task<DeclarativeApplyResult> ApplyManifestAsync(
        ClaimsPrincipal caller,
        DeclarativeConsentManifest manifest,
        bool dryRun = false,
        SecurityPrincipalContext? callerContext = null,
        DeclarativePruneMode pruneMode = DeclarativePruneMode.False,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(manifest);

        AuthorizeCaller(caller, manifest, out _, out var tenantId);
        await using var granteeLocks = await AcquireGranteeLocksAsync(dryRun ? [] : LockKeys(manifest, tenantId), ct).ConfigureAwait(false);

        var build = await BuildAsync(caller, manifest, dryRun, callerContext, pruneMode, ct).ConfigureAwait(false);
        if (!dryRun)
        {
            await CommitAsync(build, caller, ct).ConfigureAwait(false);
        }

        return build.Result;
    }

    /// <inheritdoc />
    public async Task<DeclarativeManifestPlan> ValidateAndDiffAsync(
        ClaimsPrincipal caller,
        DeclarativeConsentManifest manifest,
        SecurityPrincipalContext? callerContext = null,
        DeclarativePruneMode pruneMode = DeclarativePruneMode.False,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(manifest);

        // Side-effect free: nothing is written, no lock is taken, no audit entry is produced.
        var build = await BuildAsync(caller, manifest, dryRun: true, callerContext, pruneMode, ct).ConfigureAwait(false);
        return new DeclarativeManifestPlan(manifest, pruneMode, callerContext, build.TenantId, build.ActorSid, build.StateHash, build.Result);
    }

    /// <inheritdoc />
    public async Task<DeclarativeApplyResult> ApplyAsync(
        DeclarativeManifestPlan plan,
        ClaimsPrincipal caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(caller);

        AuthorizeCaller(caller, plan.Manifest, out var actorSid, out var tenantId);
        if (tenantId != plan.TenantId || actorSid != plan.ActorSid)
        {
            throw new ConsentApplyForbiddenException();
        }

        await using var granteeLocks = await AcquireGranteeLocksAsync(LockKeys(plan.Manifest, tenantId), ct).ConfigureAwait(false);

        // The plan is re-validated against the current state under the grantee locks: a grant set that changed since the diff makes the plan stale.
        var build = await BuildAsync(caller, plan.Manifest, dryRun: false, plan.CallerContext, plan.PruneMode, ct).ConfigureAwait(false);
        if (!string.Equals(build.StateHash, plan.StateHash, StringComparison.Ordinal))
        {
            throw new DeclarativePlanStaleException();
        }

        await CommitAsync(build, caller, ct).ConfigureAwait(false);
        return build.Result;
    }

    private static string[] LockKeys(DeclarativeConsentManifest manifest, TenantId tenantId) =>
        manifest.Policies is { Count: > 0 } policies
            ? policies
                .Where(p => !string.IsNullOrWhiteSpace(p.GranteeRef))
                .Select(p => $"{tenantId.Value.ToUpperInvariant()}|{p.GranteeRef.Trim().ToUpperInvariant()}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray()
            : [];

    private sealed record BuildResult(
        DeclarativeApplyResult Result,
        TenantId TenantId,
        Sid ActorSid,
        string StateHash,
        string ManifestVersion,
        List<Guid> RevokeIds,
        List<Consent> CreateList,
        List<ConsentDescriptionUpdate> DescriptionUpdates,
        List<AuditLogEntry> AuditEntries,
        HashSet<Guid> PrunedIds,
        List<(string Id, DatasetCatalogEdit Edit)> DatasetEdits,
        int SkippedDatasets,
        int PolicyEvaluatedGrants);

    private async Task<BuildResult> BuildAsync(
        ClaimsPrincipal caller,
        DeclarativeConsentManifest manifest,
        bool dryRun,
        SecurityPrincipalContext? callerContext,
        DeclarativePruneMode pruneMode,
        CancellationToken ct)
    {
        AuthorizeCaller(caller, manifest, out var actorSid, out var tenantId);
        var now = DateTimeOffset.UtcNow;
        var stateParts = new List<string>();

        // SR-M-10: the self-grant check compares against every identifier form of the actor and the groups and roles it holds.
        var actorIdentifiers = ActorIdentifiers.Collect(caller, actorSid, callerContext);
        // The group set is only as good as its resolution: a Degraded status (set by the identity pipeline, never by a token claim) refuses a
        // group Allow instead of skipping the self-grant check.
        var groupsDegraded = callerContext is not null
            ? callerContext.GroupResolutionStatus == GroupResolutionStatus.Degraded
              || GroupResolutionClaims.IsMarkedDegraded(caller)
              || GroupResolutionClaims.IsMarkedDegraded(callerContext.Principal)
            : GroupResolutionClaims.IsMarkedDegraded(caller);

        int totalEvaluated = 0;
        int applied = 0;
        int unchanged = 0;
        int revoked = 0;
        int updated = 0;
        var policyEvaluatedGrants = 0;
        var details = new List<string>();

        var hasDatasets = manifest.Datasets is { Count: > 0 };
        if ((manifest.Policies == null || manifest.Policies.Count == 0) && !hasDatasets)
        {
            return new BuildResult(
                new DeclarativeApplyResult(
                    0,
                    0,
                    0,
                    0,
                    dryRun,
                    ["Empty manifest; 0 policies processed."],
                    PrunedCount: 0,
                    Pruned: pruneMode != DeclarativePruneMode.False ? Array.Empty<DeclarativePrunedConsentDto>() : null),
                tenantId,
                actorSid,
                ComputeStateHash(stateParts),
                manifest.Version,
                [], [], [], [], [], [], 0, 0);
        }

        var policies = manifest.Policies ?? [];

        // The document is rejected as a whole before anything is read or written (dry-run included): a retired role grantee or any
        // other unsupported grantee type is never skipped and never mapped to a user or group.
        for (var i = 0; i < policies.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(policies[i].GranteeType) && !GranteeTypeParsing.TryParse(policies[i].GranteeType, out _))
            {
                throw new DeclarativeManifestValidationException(i, DeclarativeManifestViolation.GranteeType);
            }
        }

        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in policies)
        {
            var key = $"{p.Domain}|{p.Schema}|{p.TableName}|{p.GranteeRef}";
            if (!seenTargets.Add(key))
            {
                throw new ArgumentException(
                    $"Manifest contains more than one policy for '{p.Domain}.{p.Schema}.{p.TableName}' -> '{p.GranteeRef}'; refusing an ambiguous apply.", nameof(manifest));
            }
        }

        // INC1 decision 6: normalise every description and check every scope entry before anything is read or written (also in dry-run);
        // a violation rejects the whole manifest with a neutral message that names only the policy index.
        var descriptions = new string?[policies.Count];
        for (var i = 0; i < policies.Count; i++)
        {
            // Every entry carries a business justification (no fallback text): normalised as untrusted text and at least as long as the
            // justification of a request, so a manifest grant is never less justified than a requested one.
            var check = UntrustedText.Normalize(policies[i].Description, _descriptionMaxLength, allowLineFeed: false);
            if (!check.IsValid
                || string.IsNullOrWhiteSpace(check.Value)
                || !JustificationPolicy.TryValidate(check.Value, out _, out _))
            {
                throw new DeclarativeManifestValidationException(i);
            }

            descriptions[i] = check.Value;

            // The end date is mandatory and bounded for every entry, also for tables that are skipped later, so the whole manifest is refused.
            _grantValidityPolicy.ValidateDeclarativeValidTo(policies[i].ValidUntil, now);

            // A blank scope entry is stored as no scope and would turn a scope rule into a base rule for every scope (fail closed).
            foreach (var rule in policies[i].ColumnRules ?? [])
            {
                if (!ConsentRuleScopeValidator.AreValid(rule.ApplicableScopes))
                {
                    throw new DeclarativeManifestValidationException(i, DeclarativeManifestViolation.ScopeEntry);
                }
            }
        }

        var revokeIds = new List<Guid>();
        var descriptionUpdates = new List<ConsentDescriptionUpdate>();
        var createList = new List<Consent>();
        var auditEntries = new List<AuditLogEntry>();
        var manifestGrantees = new HashSet<(GranteeType Type, Sid Sid)>();
        var declaredPairs = new HashSet<(GranteeType Type, Sid Sid, TableIdentifier Table)>();

        for (var policyIndex = 0; policyIndex < policies.Count; policyIndex++)
        {
            var policy = policies[policyIndex];
            var description = descriptions[policyIndex];
            totalEvaluated++;
            var tableId = new TableIdentifier(policy.Domain, policy.Schema, policy.TableName);
            var metadata = await metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
            if (metadata == null)
            {
                if (GranteeTypeParsing.TryParse(policy.GranteeType, out var parsedType))
                {
                    if (!string.IsNullOrWhiteSpace(policy.GranteeRef))
                    {
                        var skippedSid = new Sid(policy.GranteeRef);
                        manifestGrantees.Add((parsedType, skippedSid));
                        declaredPairs.Add((parsedType, skippedSid, tableId));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(policy.GranteeRef))
                {
                    var skippedSid = new Sid(policy.GranteeRef);
                    manifestGrantees.Add((GranteeType.User, skippedSid));
                    declaredPairs.Add((GranteeType.User, skippedSid, tableId));
                }

                details.Add($"Table '{tableId}' not found in catalog; skipped policy.");
                continue;
            }

            if (!GranteeTypeParsing.TryParse(policy.GranteeType, out var granteeType))
            {
                granteeType = GranteeType.User;
            }

            if (!Enum.TryParse<ConsentEffect>(policy.Effect, ignoreCase: true, out var effect))
            {
                throw new ArgumentException($"Invalid consent effect '{policy.Effect}' for table '{tableId}'. Effect must be explicitly specified (e.g. 'Allow' or 'Deny').", nameof(manifest));
            }

            Sid? granteeSid = null;
            granteeSid = new Sid(policy.GranteeRef);
            manifestGrantees.Add((granteeType, granteeSid.Value));
            declaredPairs.Add((granteeType, granteeSid.Value, tableId));

            // every policy needs an absolute validUntil within the declarative maximum (also in dry-run); individual grants are also class-bounded.
            var individual = granteeType is GranteeType.User or GranteeType.ServicePrincipal;

            // SR-M-10 (SoD): no self-grant, neither directly (any identifier form) nor through a group or role the actor holds.
            // A deny towards an own group or role only restricts access and stays allowed; an individual policy for the actor is refused for any effect.
            if ((individual || effect == ConsentEffect.Allow) && ActorIdentifiers.IsActorSubject(actorIdentifiers, policy.GranteeRef))
            {
                throw new GrantPolicyViolationException(GrantDecision.Deny(
                    GrantErrorCodes.FourEyesRequired,
                    $"Self-grant prohibited: Actor '{actorSid}' cannot grant consent to themselves or to a group or role they belong to via declarative manifest."));
            }

            // With unresolved groups (for example an Entra group overage without Graph) the group list of the actor is incomplete, so a grant to
            // an own group cannot be ruled out: an Allow towards a group is refused (fail closed) instead of relying on an incomplete list.
            if (granteeType == GranteeType.Group && effect == ConsentEffect.Allow && groupsDegraded)
            {
                throw new GrantPolicyViolationException(GrantDecision.Deny(
                    GrantErrorCodes.FourEyesRequired,
                    "Group grant refused: the group memberships of the actor could not be resolved completely (group resolution degraded), so a self-grant through a group cannot be ruled out. Use a token with a resolvable group list or the consent request/approval workflow."));
            }

            var validTo = policy.ValidUntil!.Value;
            {
                var sensitivity = GrantClassifier.Classify([metadata]);
                var hasClearColumnRule = HasClearColumnRule(policy);

                // The same class rules and configured maxima as for requests apply to every grantee type (group grants included).
                var decision = _grantPolicy.Evaluate(new GrantRequest(
                    granteeType,
                    policy.GranteeRef,
                    GrantKind.ManifestPolicy,
                    policy.ValidUntil,
                    now,
                    sensitivity,
                    hasClearColumnRule ? MaskingPolicyMode.Unmasked : MaskingPolicyMode.Default,
                    Requester: actorSid.Value,
                    Justification: description,
                    Effect: effect));
                GrantPolicyViolationException.ThrowIfDenied(decision);
                policyEvaluatedGrants++;

                // The manifest activates the consent immediately and carries no approvals, so an Allow on a sensitive table or with a
                // Clear column rule (or any class the policy marks as four-eyes) is refused here (also in dry-run) and must use the consent
                // request/approval workflow. Sensitivity and Clear are checked directly because the policy classifies every service
                // principal as GrantClass.ServicePrincipal, which carries no four-eyes flag. The manifest never sets a privileged right.
                if (individual && effect == ConsentEffect.Allow &&
                    (sensitivity == GrantSensitivity.Sensitive || hasClearColumnRule || decision.RequiresFourEyes))
                {
                    throw new GrantPolicyViolationException(GrantDecision.Deny(
                        GrantErrorCodes.FourEyesRequired,
                        "A declarative manifest cannot activate a sensitive or unmasked individual grant: it requires independent four-eyes approval. Use the consent request/approval workflow.",
                        decision.Class,
                        decision.MaxValidTo));
                }
            }

            var existingConsents = await consentRepository.GetActiveConsentsForSubjectsAsync(new[] { granteeSid!.Value }, tableId, now, tenantId, ct).ConfigureAwait(false);

            // Issue #806: the subject read also returns the grantee's pattern grants of the whole domain. Only grants for exactly this table are
            // what the policy replaces: a pattern grant must neither count as the identical consent nor be revoked by the change path.
            var activeExisting = existingConsents
                .Where(c => c.IsActive(now) && c.TenantId == tenantId && !c.IsPatternGrant && c.TableIdentifier.Equals(tableId) && MatchesGrantee(c, granteeType, granteeSid))
                .ToList();

            foreach (var existing in activeExisting)
            {
                stateParts.Add($"E|{existing.Id}|{existing.ValidTo.UtcTicks}");
            }

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
            var matchingConsent = activeExisting.FirstOrDefault(c => IsIdentical(c, effect, policy, validTo));
            if (matchingConsent != null)
            {
                // Decision 6: a description-only difference is an in-place update (same id, no revoke, no epoch bump); every semantic
                // difference already failed IsIdentical and takes the revoke/create path.
                await ConsentDescriptionLoader.LoadAsync(consentRepository, [matchingConsent], ct).ConfigureAwait(false);
                var storedDescription = UntrustedText.Normalize(matchingConsent.Description, GovernanceTextOptions.ConsentDescriptionCeiling, allowLineFeed: false);
                var storedNormalized = storedDescription.IsValid ? storedDescription.Value : matchingConsent.Description;
                if (!string.Equals(storedNormalized, description, StringComparison.Ordinal))
                {
                    updated++;
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

                    details.Add($"{(dryRun ? "[DryRun] Would update" : "Updated")} description for '{tableId}' -> '{policy.GranteeRef}'.");
                    continue;
                }

                unchanged++;
                details.Add($"Policy for '{tableId}' -> '{policy.GranteeRef}' is unchanged.");
                continue;
            }

            // Needs apply / change
            revoked += activeExisting.Count;
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
                Description = description,
                ValidFrom = now,
                ValidTo = validTo,
                IsRevoked = false,
                GrantSource = ConsentGrantSource.Declarative
            };

            // Build column rules
            if (policy.ColumnRules != null && policy.ColumnRules.Count > 0)
            {
                var colRules = new List<ConsentColumnRule>();
                foreach (var cr in policy.ColumnRules)
                {
                    var colMeta = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, cr.ColumnName, StringComparison.OrdinalIgnoreCase));
                    colRules.Add(new ConsentColumnRule
                    {
                        Id = Guid.NewGuid(),
                        ConsentId = consentId,
                        TableColumnId = colMeta?.Id ?? Guid.NewGuid(),
                        ColumnName = cr.ColumnName,
                        AccessLevel = ParseAccessLevel(cr.AccessLevel),
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

            applied++;
            details.Add($"{(dryRun ? "[DryRun] Would apply" : "Applied")} policy for '{tableId}' -> '{policy.GranteeRef}'.");
        }

        // Issue #111 (A-7, BC-11): the dataset entries run the service dry run first (same authorization, tenant rule and validation as the
        // apply), so one rejected entry stops the whole manifest before anything is written.
        var datasetEdits = new List<(string Id, DatasetCatalogEdit Edit)>();
        var skippedDatasets = 0;
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
                    var edit = new DatasetCatalogEdit(entry.Tags, null, null);
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

                // The dry-run pass writes no per-entry probe audit; a real apply writes one aggregated entry per manifest (throttled; see CommitAsync).
                skippedDatasets = skipped;
            }
        }

        var authority = ConsentRevocationAuthority.FromPrincipal(caller);
        var prunedDtos = new List<DeclarativePrunedConsentDto>();
        var prunedIdSet = new HashSet<Guid>();
        if (pruneMode != DeclarativePruneMode.False && manifestGrantees.Count > 0)
        {
            var manifestSids = manifestGrantees.Select(g => g.Sid).Distinct().ToList();
            var activeForGrantees = await consentRepository
                .GetAllActiveConsentsForSubjectsAsync(manifestSids, roles: null, atTime: now, tenantId: tenantId, ct)
                .ConfigureAwait(false);

            var activeDeclarative = activeForGrantees
                .Where(c => c.IsActive(now)
                            && c.TenantId == tenantId
                            && c.GrantSource == ConsentGrantSource.Declarative
                            && c.GranteeSid is { } sid
                            && manifestGrantees.Contains((c.GranteeType, sid)))
                .ToList();

            var pruneCandidates = activeDeclarative
                .Where(c => !declaredPairs.Contains((c.GranteeType, c.GranteeSid!.Value, c.TableIdentifier)))
                .OrderBy(c => c.TableIdentifier.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.GranteeSid!.Value.Value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Id)
                .ToList();

            foreach (var candidate in pruneCandidates)
            {
                stateParts.Add($"P|{candidate.Id}|{candidate.ValidTo.UtcTicks}");
            }

            if (pruneCandidates.Count > 0)
            {
                if (!authority.CanAccessTenant(tenantId))
                {
                    throw new ConsentRevocationForbiddenException();
                }

                if (!authority.AllowsAdminForTenant(tenantId))
                {
                    foreach (var table in pruneCandidates.Select(c => c.TableIdentifier).Distinct())
                    {
                        if (_dataOwnershipRepository is null ||
                            !await _dataOwnershipRepository.IsAuthorizedApproverForTableAsync(table, actorSid, ct).ConfigureAwait(false))
                        {
                            throw new ConsentRevocationForbiddenException();
                        }
                    }
                }

                if (pruneCandidates.Count > MaxDeclarativePruneCount)
                {
                    throw new DeclarativePruneCapExceededException(pruneCandidates.Count, MaxDeclarativePruneCount);
                }

                if (pruneMode == DeclarativePruneMode.True &&
                    activeDeclarative.Count >= 2 &&
                    pruneCandidates.Count * 2 > activeDeclarative.Count)
                {
                    throw new DeclarativePruneThresholdExceededException(pruneCandidates.Count, activeDeclarative.Count);
                }

                var manifestSha256 = ComputeManifestSha256(manifest);
                foreach (var candidate in pruneCandidates)
                {
                    prunedDtos.Add(new DeclarativePrunedConsentDto(
                        candidate.Id,
                        candidate.GranteeSid!.Value.Value,
                        candidate.TableIdentifier.Domain,
                        candidate.TableIdentifier.Schema,
                        candidate.TableIdentifier.TableName));
                    details.Add($"{(dryRun ? "[DryRun] Would prune" : "Pruned")} declarative consent '{candidate.Id}' for '{candidate.TableIdentifier}' -> '{candidate.GranteeSid!.Value.Value}'.");

                    prunedIdSet.Add(candidate.Id);
                    auditEntries.Add(new AuditLogEntry
                    {
                        Id = Guid.NewGuid(),
                        OccurredAt = now,
                        EventType = AuditEventTypes.ConsentRevoked,
                        ActorSid = actorSid,
                        TargetTable = candidate.TableIdentifier.ToString(),
                        Decision = "PRUNED",
                        DetailsJson = JsonSerializer.Serialize(new
                        {
                            ConsentId = candidate.Id,
                            Grantee = candidate.GranteeSid!.Value.Value,
                            TableId = candidate.TableIdentifier.ToString(),
                            Reason = PrunedByManifestReason,
                            ManifestSha256 = manifestSha256
                        }),
                        TenantId = tenantId
                    });
                }
            }
        }

        var result = new DeclarativeApplyResult(
            totalEvaluated,
            applied,
            unchanged,
            revoked,
            dryRun,
            details,
            updated,
            PrunedCount: prunedDtos.Count,
            Pruned: pruneMode != DeclarativePruneMode.False ? prunedDtos : null);

        return new BuildResult(
            result,
            tenantId,
            actorSid,
            ComputeStateHash(stateParts),
            manifest.Version,
            revokeIds,
            createList,
            descriptionUpdates,
            auditEntries,
            prunedIdSet,
            datasetEdits,
            skippedDatasets,
            policyEvaluatedGrants);
    }

    private static string ComputeStateHash(List<string> parts)
    {
        parts.Sort(StringComparer.Ordinal);
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', parts));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private async Task CommitAsync(BuildResult build, ClaimsPrincipal caller, CancellationToken ct)
    {
        if (build.CreateList.Count > build.PolicyEvaluatedGrants)
        {
            throw new InvalidOperationException("A declarative plan is written only after every created grant was evaluated by the IGrantPolicy.");
        }

        var (revokeIds, createList, descriptionUpdates, auditEntries, prunedIdSet) =
            (build.RevokeIds, build.CreateList, build.DescriptionUpdates, build.AuditEntries, build.PrunedIds);
        var actorSid = build.ActorSid;
        var authority = ConsentRevocationAuthority.FromPrincipal(caller);

        if (build.SkippedDatasets > 0 && datasetCatalog is not null)
        {
            await datasetCatalog.RecordManifestProbesAsync(caller, build.SkippedDatasets, ct).ConfigureAwait(false);
        }

        // one transaction and one epoch bump for the whole manifest; a fault leaves nothing changed and writes no audit.
        // M2: the APPLIED audit entries travel into the same transaction, so a committed change always has its (Tier-A, anchored) record and a failing
        // epoch invalidation after the commit cannot lose it.
        if ((createList.Count > 0 || revokeIds.Count > 0 || descriptionUpdates.Count > 0 || prunedIdSet.Count > 0))
        {
            var supersededReason = $"Superseded by GitOps declarative apply (version: {build.ManifestVersion})";
            if (prunedIdSet.Count == 0)
            {
                if (descriptionUpdates.Count == 0)
                {
                    await consentRepository.ReplaceConsentsAsync(revokeIds, createList, actorSid, supersededReason, authority, auditEntries, ct).ConfigureAwait(false);
                }
                else
                {
                    var descriptionWriter = consentRepository as IConsentDescriptionRepository
                        ?? throw new InvalidOperationException("The consent repository does not support in-place description updates.");
                    await descriptionWriter.ReplaceConsentsAsync(revokeIds, createList, actorSid, supersededReason, authority, auditEntries, descriptionUpdates, ct).ConfigureAwait(false);
                }
            }
            else
            {
                var combinedRevokeIds = revokeIds.Concat(prunedIdSet).Distinct().ToList();
                if (descriptionUpdates.Count == 0)
                {
                    await consentRepository.ReplaceConsentsAsync(
                        combinedRevokeIds,
                        createList,
                        actorSid,
                        supersededReason,
                        authority,
                        auditEntries,
                        prunedIdSet,
                        PrunedByManifestReason,
                        ct).ConfigureAwait(false);
                }
                else
                {
                    var descriptionWriter = consentRepository as IConsentDescriptionRepository
                        ?? throw new InvalidOperationException("The consent repository does not support in-place description updates.");
                    await descriptionWriter.ReplaceConsentsAsync(
                        combinedRevokeIds,
                        createList,
                        actorSid,
                        supersededReason,
                        authority,
                        auditEntries,
                        descriptionUpdates,
                        prunedIdSet,
                        PrunedByManifestReason,
                        ct).ConfigureAwait(false);
                }
            }
        }

        // Not atomic with the consent commit above: validation is all-or-nothing, a storage fault in the middle is not rolled back across the two stores.
        {
            foreach (var (datasetId, edit) in build.DatasetEdits)
            {
                var result = await datasetCatalog!.ApplyAsync(caller, datasetId, edit, "manifest", ct).ConfigureAwait(false);
                if (!result.IsSuccess && result.Outcome != DatasetCatalogOutcome.NotFound)
                {
                    throw new DeclarativeDatasetCatalogException(result.Outcome);
                }
            }
        }
    }
}

/// <summary>Raised when a caller is not authorized to apply a declarative consent manifest for the resolved tenant.</summary>
public sealed class ConsentApplyForbiddenException()
    : Exception("You do not have permission to apply declarative consent manifests.");

/// <summary>Issue #111: a dataset entry of the manifest was rejected by the admin service; the whole manifest is refused with a stable code.</summary>
public sealed class DeclarativeDatasetCatalogException(DatasetCatalogOutcome outcome) : Exception("A dataset catalog entry of the manifest was rejected.")
{
    public DatasetCatalogOutcome Outcome { get; } = outcome;

    public string ErrorCode => Outcome switch
    {
        DatasetCatalogOutcome.InvalidTag => "INVALID_TAG",
        DatasetCatalogOutcome.TagLimitExceeded => "TAG_LIMIT_EXCEEDED",
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

/// <summary>Raised when a declarative prune exceeds the hard per-run cap.</summary>
public sealed class DeclarativePruneCapExceededException(int candidateCount, int maxAllowed)
    : Exception($"Declarative consent prune candidate count ({candidateCount}) exceeds the maximum allowed per run ({maxAllowed}).")
{
    public int CandidateCount { get; } = candidateCount;
    public int MaxAllowed { get; } = maxAllowed;
}

/// <summary>Raised when a declarative prune exceeds 50% of a grantee's active declarative consents without prune=force.</summary>
public sealed class DeclarativePruneThresholdExceededException(int candidateCount, int totalCount)
    : Exception($"Prune would revoke {candidateCount} of {totalCount} active declarative consents (> 50%); retry with prune=force to confirm.")
{
    public int CandidateCount { get; } = candidateCount;
    public int TotalCount { get; } = totalCount;
}

/// <summary>Raised when the grant set changed between the validated plan and its apply; the plan must be validated again (maps to 409).</summary>
public sealed class DeclarativePlanStaleException()
    : Exception("The grant set changed since the manifest was validated; validate the manifest again.");
