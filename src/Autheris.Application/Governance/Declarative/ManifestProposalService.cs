namespace Autheris.Application.Governance.Declarative;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Notifications;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Amendment 6 (issue #1009): IT administration submits a declarative consent manifest as a proposal, which has no effect until one Data
/// Governance member confirms it. Submit runs the full validation as a dry run and stores the manifest, the diff and the hash of the grant state
/// the diff was computed against. Confirm re-validates against the current state (a changed state is stale and must be confirmed again) and applies
/// the manifest exactly like a Data Governance apply. The confirmer is never the submitter nor a beneficiary; the first decision wins.
/// </summary>
public sealed class ManifestProposalService(
    IDeclarativeManifestPlanner planner,
    IManifestProposalRepository repository,
    IAuditWriter auditWriter,
    IGatewayRoleEvaluator? roleEvaluator = null,
    IGovernanceNotificationDispatcher? notifications = null,
    TimeProvider? timeProvider = null)
{
    public const string DefaultScopeName = "default";
    public const int MaxScopeNameLength = 128;
    public const int MaxRejectReasonLength = 512;

    /// <summary>A proposal stays open this long for a Data Governance decision (Amendment 6: 30 days).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private const int ExpireBatchSize = 100;

    private static readonly GatewayRole[] DecideRoles = [GatewayRole.GovernanceAdmin];
    private static readonly GatewayRole[] ReadRoles = [GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin];
    private static readonly char[] PatternCharacters = ['*', '?', '%', '[', ']'];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDeclarativeManifestPlanner _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    private readonly IManifestProposalRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IAuditWriter _audit = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
    private readonly IGatewayRoleEvaluator _roles = roleEvaluator ?? new GatewayRoleEvaluator();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Validates and stores a manifest as a pending proposal; a newer proposal for the same scope supersedes a pending one.</summary>
    public async Task<ManifestProposalView> SubmitAsync(
        ClaimsPrincipal submitter,
        DeclarativeConsentManifest manifest,
        SecurityPrincipalContext? callerContext = null,
        DeclarativePruneMode pruneMode = DeclarativePruneMode.False,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submitter);
        ArgumentNullException.ThrowIfNull(manifest);

        var plan = await PreviewAsync(submitter, manifest, callerContext, pruneMode, ct).ConfigureAwait(false);
        var scope = NormalizeScope(manifest.Name);
        var now = _time.GetUtcNow().ToUniversalTime();
        var proposal = new ManifestProposal
        {
            Id = Guid.NewGuid(),
            TenantId = plan.TenantId,
            ScopeName = scope,
            SubmittedBy = plan.ActorSid,
            SubmittedAt = now,
            ManifestJson = JsonSerializer.Serialize(new ManifestEnvelope(pruneMode, manifest), JsonOptions),
            DiffJson = SerializeDiff(plan.Diff),
            StateHash = plan.StateHash,
            Status = ManifestProposalStatus.Pending,
            ExpiresAt = now.Add(Lifetime)
        };

        var superseded = await _repository.CreateAsync(proposal, ct).ConfigureAwait(false);
        try
        {
            foreach (var oldId in superseded)
            {
                await _audit.RecordRequiredAsync(
                    Entry(AuditEventTypes.ManifestProposalExpired, plan.TenantId, plan.ActorSid, "SUPERSEDED", new { ProposalId = oldId, SupersededBy = proposal.Id, Scope = scope }, now),
                    ct).ConfigureAwait(false);
            }

            await _audit.RecordRequiredAsync(
                Entry(
                    AuditEventTypes.ManifestProposalSubmitted,
                    plan.TenantId,
                    plan.ActorSid,
                    "SUBMITTED",
                    new
                    {
                        ProposalId = proposal.Id,
                        Scope = scope,
                        manifest.Version,
                        PruneMode = pruneMode.ToString(),
                        plan.Preview.Applied,
                        plan.Preview.Updated,
                        plan.Preview.Revoked,
                        plan.Preview.PrunedCount,
                        plan.StateHash
                    },
                    now),
                ct).ConfigureAwait(false);
        }
        catch
        {
            // An unaudited proposal is closed at once so it can never be confirmed (fail closed).
            await _repository.TryTransitionAsync(plan.TenantId, proposal.Id, ManifestProposalStatus.Pending, ManifestProposalStatus.Expired, null, now, null, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        if (notifications is not null)
        {
            await notifications.DispatchAsync(
                new GovernanceNotificationMessage(
                    AuditEventTypes.ManifestProposalSubmitted,
                    plan.TenantId.Value,
                    string.Empty,
                    plan.ActorSid.Value,
                    $"Manifest proposal {proposal.Id:D} for scope '{scope}' awaits a Data Governance decision ({plan.Diff.Count(d => d.Action is not (DeclarativeDiffAction.Unchanged or DeclarativeDiffAction.Skipped))} change(s)). Review: /api/v1/governance/consents/proposals/{proposal.Id:D}",
                    now,
                    ObjectName: scope),
                ct).ConfigureAwait(false);
        }

        return View(proposal, plan.Diff, pruneMode);
    }

    /// <summary>Validates a manifest as a proposal without storing it (submit with <c>dryRun</c>).</summary>
    public async Task<DeclarativeManifestPlan> PreviewAsync(
        ClaimsPrincipal submitter,
        DeclarativeConsentManifest manifest,
        SecurityPrincipalContext? callerContext = null,
        DeclarativePruneMode pruneMode = DeclarativePruneMode.False,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submitter);
        ArgumentNullException.ThrowIfNull(manifest);

        RejectPatterns(manifest);
        _ = NormalizeScope(manifest.Name);
        return await _planner.ValidateProposalAsync(submitter, manifest, callerContext, pruneMode, ct).ConfigureAwait(false);
    }

    /// <summary>Returns a proposal of the caller's tenant (IT administration or Data Governance).</summary>
    public async Task<ManifestProposalView> GetAsync(ClaimsPrincipal caller, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var tenant = AuthorizeTenantRole(caller, ReadRoles, out _);
        var proposal = await _repository.GetAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new ManifestProposalNotFoundException();

        // A read has no side effect: a pending proposal past its lifetime is shown as expired; the transition is written by a decision or the sweep.
        if (proposal.Status == ManifestProposalStatus.Pending && _time.GetUtcNow() >= proposal.ExpiresAt)
        {
            proposal.Status = ManifestProposalStatus.Expired;
        }

        var envelope = ReadEnvelope(proposal);
        return View(proposal, ReadDiff(proposal), envelope.PruneMode);
    }

    /// <summary>
    /// Confirms a pending proposal: re-validates it against the current state as the confirmer and applies it in one transaction. A changed state
    /// stores the recomputed diff and throws <see cref="ManifestProposalStaleException"/>.
    /// </summary>
    public async Task<DeclarativeApplyResult> ConfirmAsync(
        ClaimsPrincipal confirmer,
        Guid id,
        SecurityPrincipalContext? callerContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmer);
        var tenant = AuthorizeTenantRole(confirmer, DecideRoles, out var confirmerSid);
        var proposal = await LoadPendingAsync(tenant, id, ct).ConfigureAwait(false);

        // The submitter never confirms their own proposal, in any identifier form, even when they also hold Data Governance.
        var confirmerIdentifiers = ActorIdentifiers.Collect(confirmer, confirmerSid, callerContext);
        if (ActorIdentifiers.IsActorSubject(confirmerIdentifiers, proposal.SubmittedBy.Value))
        {
            throw new ManifestProposalSeparationOfDutiesException(ManifestProposalSeparationOfDutiesException.SubmitterReason);
        }

        // Without a complete group list a grant to a group the confirmer holds cannot be ruled out: confirmation is refused.
        if (IsGroupResolutionDegraded(confirmer, callerContext))
        {
            throw new ManifestProposalSeparationOfDutiesException(ManifestProposalSeparationOfDutiesException.DegradedReason);
        }

        var envelope = ReadEnvelope(proposal);

        // Full re-validation as the confirmer: class rules, maxima, prune authority and threshold, and the self-grant refusal, which refuses any
        // grant to the confirmer (directly in any identifier form or through a group they hold).
        var plan = await _planner.ValidateAndDiffAsync(confirmer, envelope.Manifest, callerContext, envelope.PruneMode, ct).ConfigureAwait(false);
        if (!string.Equals(plan.StateHash, proposal.StateHash, StringComparison.Ordinal))
        {
            throw await MarkStaleAsync(proposal, plan, confirmerSid, ct).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow().ToUniversalTime();
        if (!await _repository.TryTransitionAsync(tenant, id, ManifestProposalStatus.Pending, ManifestProposalStatus.Confirmed, confirmerSid, now, null, ct).ConfigureAwait(false))
        {
            throw await ClosedAsync(tenant, id, ct).ConfigureAwait(false);
        }

        DeclarativeApplyResult result;
        try
        {
            result = await _planner.ApplyAsync(plan with { ProposalId = id }, confirmer, ct).ConfigureAwait(false);
        }
        catch (DeclarativePlanStaleException)
        {
            await ReopenAsync(tenant, id).ConfigureAwait(false);
            var fresh = await _planner.ValidateAndDiffAsync(confirmer, envelope.Manifest, callerContext, envelope.PruneMode, ct).ConfigureAwait(false);
            throw await MarkStaleAsync(proposal, fresh, confirmerSid, ct).ConfigureAwait(false);
        }
        catch
        {
            await ReopenAsync(tenant, id).ConfigureAwait(false);
            throw;
        }

        await _audit.RecordRequiredAsync(
            Entry(
                AuditEventTypes.ManifestProposalConfirmed,
                tenant,
                confirmerSid,
                "CONFIRMED",
                new { ProposalId = id, Scope = proposal.ScopeName, SubmittedBy = proposal.SubmittedBy.Value, result.Applied, result.Updated, result.Revoked, result.PrunedCount },
                now),
            ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Rejects a pending proposal (the reason is optional); the first decision wins.</summary>
    public async Task<ManifestProposalView> RejectAsync(ClaimsPrincipal decider, Guid id, string? reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decider);
        var tenant = AuthorizeTenantRole(decider, DecideRoles, out var deciderSid);
        string? normalizedReason = null;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var check = UntrustedText.Normalize(reason, MaxRejectReasonLength, allowLineFeed: false);
            if (!check.IsValid)
            {
                throw new ArgumentException("The rejection reason is invalid.", nameof(reason));
            }

            normalizedReason = check.Value;
        }

        var proposal = await LoadPendingAsync(tenant, id, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().ToUniversalTime();
        if (!await _repository.TryTransitionAsync(tenant, id, ManifestProposalStatus.Pending, ManifestProposalStatus.Rejected, deciderSid, now, normalizedReason, ct).ConfigureAwait(false))
        {
            throw await ClosedAsync(tenant, id, ct).ConfigureAwait(false);
        }

        try
        {
            await _audit.RecordRequiredAsync(
                Entry(AuditEventTypes.ManifestProposalRejected, tenant, deciderSid, "REJECTED", new { ProposalId = id, Scope = proposal.ScopeName, SubmittedBy = proposal.SubmittedBy.Value, ReasonGiven = normalizedReason is not null }, now),
                ct).ConfigureAwait(false);
        }
        catch
        {
            await _repository.TryTransitionAsync(tenant, id, ManifestProposalStatus.Rejected, ManifestProposalStatus.Pending, null, null, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        proposal.Status = ManifestProposalStatus.Rejected;
        proposal.DecidedBy = deciderSid;
        proposal.DecidedAt = now;
        proposal.RejectReason = normalizedReason;
        return View(proposal, ReadDiff(proposal), ReadEnvelope(proposal).PruneMode);
    }

    /// <summary>Closes every pending proposal whose lifetime ended; returns how many were expired.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().ToUniversalTime();
        var due = await _repository.ListExpiredPendingAsync(now, ExpireBatchSize, ct).ConfigureAwait(false);
        var expired = 0;
        foreach (var proposal in due)
        {
            if (await ExpireCoreAsync(proposal, now, ct).ConfigureAwait(false))
            {
                expired++;
            }
        }

        return expired;
    }

    private async Task<ManifestProposal> LoadPendingAsync(TenantId tenant, Guid id, CancellationToken ct)
    {
        var proposal = await _repository.GetAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new ManifestProposalNotFoundException();
        proposal = await ExpireIfDueAsync(proposal, ct).ConfigureAwait(false);
        if (proposal.Status != ManifestProposalStatus.Pending)
        {
            throw new ManifestProposalClosedException(proposal.Status);
        }

        return proposal;
    }

    private async Task<ManifestProposal> ExpireIfDueAsync(ManifestProposal proposal, CancellationToken ct)
    {
        var now = _time.GetUtcNow().ToUniversalTime();
        if (proposal.Status != ManifestProposalStatus.Pending || now < proposal.ExpiresAt)
        {
            return proposal;
        }

        if (await ExpireCoreAsync(proposal, now, ct).ConfigureAwait(false))
        {
            proposal.Status = ManifestProposalStatus.Expired;
            proposal.DecidedAt = now;
            return proposal;
        }

        return await _repository.GetAsync(proposal.TenantId, proposal.Id, ct).ConfigureAwait(false) ?? throw new ManifestProposalNotFoundException();
    }

    private async Task<bool> ExpireCoreAsync(ManifestProposal proposal, DateTimeOffset now, CancellationToken ct)
    {
        if (!await _repository.TryTransitionAsync(proposal.TenantId, proposal.Id, ManifestProposalStatus.Pending, ManifestProposalStatus.Expired, null, now, null, ct).ConfigureAwait(false))
        {
            return false;
        }

        await _audit.RecordRequiredAsync(
            Entry(AuditEventTypes.ManifestProposalExpired, proposal.TenantId, proposal.SubmittedBy, "EXPIRED", new { ProposalId = proposal.Id, Scope = proposal.ScopeName, proposal.ExpiresAt }, now),
            ct).ConfigureAwait(false);
        return true;
    }

    private async Task<ManifestProposalStaleException> MarkStaleAsync(ManifestProposal proposal, DeclarativeManifestPlan fresh, Sid actor, CancellationToken ct)
    {
        await _repository.UpdateDiffAsync(proposal.TenantId, proposal.Id, SerializeDiff(fresh.Diff), fresh.StateHash, ct).ConfigureAwait(false);
        await _audit.RecordRequiredAsync(
            Entry(
                AuditEventTypes.ManifestProposalStale,
                proposal.TenantId,
                actor,
                "STALE",
                new { ProposalId = proposal.Id, Scope = proposal.ScopeName, PreviousStateHash = proposal.StateHash, fresh.StateHash },
                _time.GetUtcNow().ToUniversalTime()),
            ct).ConfigureAwait(false);
        return new ManifestProposalStaleException(proposal.Id, fresh.Diff);
    }

    private async Task<ManifestProposalClosedException> ClosedAsync(TenantId tenant, Guid id, CancellationToken ct)
    {
        var current = await _repository.GetAsync(tenant, id, ct).ConfigureAwait(false);
        return new ManifestProposalClosedException(current?.Status ?? ManifestProposalStatus.Expired);
    }

    private Task ReopenAsync(TenantId tenant, Guid id) =>
        _repository.TryTransitionAsync(tenant, id, ManifestProposalStatus.Confirmed, ManifestProposalStatus.Pending, null, null, null, CancellationToken.None);

    private TenantId AuthorizeTenantRole(ClaimsPrincipal caller, GatewayRole[] roles, out Sid actorSid)
    {
        if (caller.Identity?.IsAuthenticated != true)
        {
            throw new ConsentApplyForbiddenException();
        }

        TenantId tenant;
        try
        {
            tenant = caller.GetTenantId();
        }
        catch (GatewaySecurityException)
        {
            throw new ConsentApplyForbiddenException();
        }

        if (!TenantId.TryParse(tenant.Value, out _) || !_roles.HasAnyRole(caller, roles, tenant.Value))
        {
            throw new ConsentApplyForbiddenException();
        }

        actorSid = caller.GetUserSid() ?? throw new ConsentApplyForbiddenException();
        return tenant;
    }

    private static bool IsGroupResolutionDegraded(ClaimsPrincipal caller, SecurityPrincipalContext? callerContext) =>
        GroupResolutionClaims.IsMarkedDegraded(caller)
        || (callerContext is not null
            && (callerContext.GroupResolutionStatus == GroupResolutionStatus.Degraded || GroupResolutionClaims.IsMarkedDegraded(callerContext.Principal)));

    /// <summary>A proposal names every object explicitly: a pattern could reach a <c>secret</c> object that was never named (fail closed).</summary>
    private static void RejectPatterns(DeclarativeConsentManifest manifest)
    {
        var policies = manifest.Policies ?? [];
        for (var i = 0; i < policies.Count; i++)
        {
            var policy = policies[i];
            if (IsPattern(policy.Domain) || IsPattern(policy.Schema) || IsPattern(policy.TableName))
            {
                throw new DeclarativeManifestValidationException(i, DeclarativeManifestViolation.Pattern);
            }
        }
    }

    private static bool IsPattern(string? name) => name is not null && name.IndexOfAny(PatternCharacters) >= 0;

    private static string NormalizeScope(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DefaultScopeName;
        }

        var check = UntrustedText.Normalize(name, MaxScopeNameLength, allowLineFeed: false);
        if (!check.IsValid || string.IsNullOrWhiteSpace(check.Value))
        {
            throw new DeclarativeManifestNameException();
        }

        return check.Value;
    }

    private static string SerializeDiff(IReadOnlyList<DeclarativeDiffEntry> diff) => JsonSerializer.Serialize(diff, JsonOptions);

    private static IReadOnlyList<DeclarativeDiffEntry> ReadDiff(ManifestProposal proposal) =>
        JsonSerializer.Deserialize<List<DeclarativeDiffEntry>>(proposal.DiffJson, JsonOptions) ?? [];

    private static ManifestEnvelope ReadEnvelope(ManifestProposal proposal) =>
        JsonSerializer.Deserialize<ManifestEnvelope>(proposal.ManifestJson, JsonOptions) is { Manifest: not null } envelope
            ? envelope
            : throw new InvalidOperationException("The stored manifest proposal is unreadable.");

    private static ManifestProposalView View(ManifestProposal proposal, IReadOnlyList<DeclarativeDiffEntry> diff, DeclarativePruneMode pruneMode) => new(
        proposal.Id,
        proposal.ScopeName,
        proposal.SubmittedBy.Value,
        proposal.SubmittedAt,
        proposal.Status.ToString(),
        proposal.ExpiresAt,
        proposal.DecidedBy?.Value,
        proposal.DecidedAt,
        proposal.RejectReason,
        pruneMode.ToString(),
        diff);

    private static AuditLogEntry Entry(string eventType, TenantId tenant, Sid actor, string decision, object details, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        OccurredAt = now,
        EventType = eventType,
        ActorSid = actor,
        TargetTable = string.Empty,
        Decision = decision,
        DetailsJson = JsonSerializer.Serialize(details, JsonOptions),
        TenantId = tenant
    };

    private sealed record ManifestEnvelope(DeclarativePruneMode PruneMode, DeclarativeConsentManifest Manifest);
}

/// <summary>A manifest proposal as Data Governance reviews it: the diff per grant with grantee, object, period and justification.</summary>
public sealed record ManifestProposalView(
    Guid Id,
    string Scope,
    string SubmittedBy,
    DateTimeOffset SubmittedAt,
    string Status,
    DateTimeOffset ExpiresAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? RejectReason,
    string PruneMode,
    IReadOnlyList<DeclarativeDiffEntry> Diff);

/// <summary>The manifest name (the proposal scope) is not valid untrusted text of at most 128 characters (maps to 400).</summary>
public sealed class DeclarativeManifestNameException() : ArgumentException("The manifest name is invalid.");

/// <summary>No proposal with this id exists in the caller's tenant (maps to 404).</summary>
public sealed class ManifestProposalNotFoundException() : Exception("The manifest proposal was not found.");

/// <summary>The proposal is no longer pending: decided, expired or superseded (maps to 409).</summary>
public sealed class ManifestProposalClosedException(ManifestProposalStatus status)
    : Exception("The manifest proposal is no longer pending.")
{
    public ManifestProposalStatus Status { get; } = status;
}

/// <summary>The grant state changed since the diff was computed; the recomputed diff is stored and must be confirmed again (maps to 409).</summary>
public sealed class ManifestProposalStaleException(Guid proposalId, IReadOnlyList<DeclarativeDiffEntry> recomputedDiff)
    : Exception("The grant state changed since the manifest proposal was submitted; review the recomputed diff and confirm again.")
{
    public Guid ProposalId { get; } = proposalId;

    public IReadOnlyList<DeclarativeDiffEntry> RecomputedDiff { get; } = recomputedDiff;
}

/// <summary>The confirmer is the submitter, or their group memberships could not be resolved completely (maps to 403).</summary>
public sealed class ManifestProposalSeparationOfDutiesException(string reason) : Exception(reason)
{
    public const string SubmitterReason = "The submitter of a manifest proposal cannot confirm it.";
    public const string DegradedReason = "The group memberships of the confirmer could not be resolved completely; a grant to a group the confirmer holds cannot be ruled out.";
}
