namespace Autheris.Application.Governance.Ownership;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Products;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>One relation as the external system states it.</summary>
public sealed record DeputyRelationEntry(string? ExternalId, string? Principal, string? Deputy, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo);

/// <summary>An entry the reconciliation rejected, with a stable code.</summary>
public sealed record DeputyRelationRejection(string? ExternalId, string Code, string Message);

/// <summary>Outcome of a deputy relation call.</summary>
public enum DeputyRelationOutcome
{
    Ok = 0,

    /// <summary>The caller holds neither the deputy sync right nor Data Governance (or a read without scope).</summary>
    Forbidden = 1,

    /// <summary>The request as a whole is invalid (source, size, single entry rejected).</summary>
    Invalid = 2,

    /// <summary>The stored state changed concurrently; nothing was applied.</summary>
    Conflict = 3,

    /// <summary>The relation does not exist (incremental delete).</summary>
    NotFound = 4
}

/// <summary>Result of a reconciliation, an incremental change or a read.</summary>
public sealed record DeputyRelationResult(
    DeputyRelationOutcome Outcome,
    IReadOnlyList<DeputyRelation> Created,
    IReadOnlyList<(DeputyRelation Before, DeputyRelation After)> Updated,
    IReadOnlyList<DeputyRelation> Ended,
    int Unchanged,
    IReadOnlyList<DeputyRelationRejection> Rejected,
    bool DryRun,
    IReadOnlyList<DeputyRelation>? Relations = null,
    string? ErrorCode = null,
    string? Message = null)
{
    public static DeputyRelationResult Failed(DeputyRelationOutcome outcome, string? code = null, string? message = null) =>
        new(outcome, [], [], [], 0, [], false, null, code, message);

    public static DeputyRelationResult Read(IReadOnlyList<DeputyRelation> relations) =>
        new(DeputyRelationOutcome.Ok, [], [], [], 0, [], false, relations);
}

/// <summary>Read filter of <see cref="DeputyRelationService.ListAsync"/>.</summary>
public sealed record DeputyRelationQuery(
    string? Principal = null,
    string? Deputy = null,
    string? Source = null,
    bool ActiveOnly = false,
    ContainerRef? Container = null);

/// <summary>Stable error codes of deputy relation reconciliation.</summary>
public static class DeputyRelationErrorCodes
{
    public const string SourceInvalid = "DEPUTY_SOURCE_INVALID";
    public const string TooManyEntries = "DEPUTY_SYNC_TOO_LARGE";
    public const string ExternalIdInvalid = "DEPUTY_EXTERNAL_ID_INVALID";
    public const string DuplicateExternalId = "DEPUTY_EXTERNAL_ID_DUPLICATE";
    public const string PrincipalUnresolvable = "DEPUTY_PRINCIPAL_UNRESOLVABLE";
    public const string PrincipalAmbiguous = "DEPUTY_PRINCIPAL_AMBIGUOUS";
    public const string DeputyUnresolvable = "DEPUTY_DEPUTY_UNRESOLVABLE";
    public const string DeputyAmbiguous = "DEPUTY_DEPUTY_AMBIGUOUS";
    public const string SelfRelation = "DEPUTY_SELF_RELATION";
    public const string WindowInvalid = "DEPUTY_WINDOW_INVALID";
    public const string ConcurrentChange = "DEPUTY_CONCURRENT_CHANGE";
}

/// <summary>
/// Reconciles externally sourced deputy relations (issue #1126). The full state of one source replaces the stored relations of that source
/// in the caller's tenant (create, update, end missing) in one transaction together with one audit entry per change; other sources and
/// tenants are never touched. A dry run returns the same diff without writing. Callers: the tenant-bound deputy sync right, or Data
/// Governance of the tenant; IT administration has no access. Entries whose identities cannot be resolved (or that break a rule) are
/// rejected and reported per entry; a rejected entry counts as absent, so an existing relation with its external id ends (fail closed).
/// </summary>
public sealed partial class DeputyRelationService
{
    /// <summary>Maximum length of one relation window (default and ceiling, 90 days).</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(90);

    /// <summary>Upper bound of entries in one full-state reconciliation (and of stored relations of one source).</summary>
    public const int MaxEntriesPerSource = 5000;

    /// <summary>Upper bound of relations returned by one read.</summary>
    public const int MaxReadRows = 1000;

    /// <summary>Reason recorded when a relation ends because the source no longer states it.</summary>
    public const string EndedMissingFromSource = "MISSING_FROM_SOURCE";

    private static readonly Sid DeprovisioningActor = new("SYSTEM:DEPROVISIONING");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDeputyRelationRepository _repository;
    private readonly IDeputyIdentityResolver _identities;
    private readonly IGovernanceCapabilities _capabilities;
    private readonly IDeputyAuthority? _authority;
    private readonly IGovernanceDomainRepository? _domains;
    private readonly IDataProductRepository? _products;
    private readonly IGovernanceWriteInvalidator? _invalidator;
    private readonly TimeProvider _time;

    public DeputyRelationService(
        IDeputyRelationRepository repository,
        IDeputyIdentityResolver identities,
        IGovernanceCapabilities capabilities,
        IDeputyAuthority? authority = null,
        IGovernanceDomainRepository? domains = null,
        IDataProductRepository? products = null,
        IGovernanceWriteInvalidator? invalidator = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _authority = authority;
        _domains = domains;
        _products = products;
        _invalidator = invalidator;
        _time = timeProvider ?? TimeProvider.System;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SourcePattern();

    /// <summary>True when <paramref name="source"/> is a valid source name.</summary>
    public static bool IsValidSource(string? source) => source is not null && SourcePattern().IsMatch(source);

    /// <summary>Full-state reconciliation of <paramref name="source"/> in <paramref name="tenant"/>.</summary>
    public async Task<DeputyRelationResult> ReconcileAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        bool holdsSyncRight,
        string source,
        IReadOnlyList<DeputyRelationEntry> entries,
        bool dryRun,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (SyncCapacity(caller, tenant, holdsSyncRight) is not { } capacity)
        {
            return DeputyRelationResult.Failed(DeputyRelationOutcome.Forbidden);
        }

        if (!IsValidSource(source))
        {
            return DeputyRelationResult.Failed(DeputyRelationOutcome.Invalid, DeputyRelationErrorCodes.SourceInvalid, "The source name is invalid.");
        }

        if (entries.Count > MaxEntriesPerSource)
        {
            return DeputyRelationResult.Failed(
                DeputyRelationOutcome.Invalid, DeputyRelationErrorCodes.TooManyEntries, $"A source holds at most {MaxEntriesPerSource} relations.");
        }

        var existing = await _repository.ListAsync(tenant, new DeputyRelationFilter(Source: source), MaxEntriesPerSource + 1, ct).ConfigureAwait(false) ?? [];
        var now = _time.GetUtcNow();
        var (desired, rejected) = await BuildDesiredAsync(caller, tenant, source, entries, existing, now, ct).ConfigureAwait(false);
        return await ApplyDiffAsync(caller, tenant, capacity, existing.Where(e => e.Source == source && e.TenantId == tenant).ToList(), desired, rejected, dryRun, now, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Incremental upsert of one relation of <paramref name="source"/>.</summary>
    public async Task<DeputyRelationResult> UpsertAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        bool holdsSyncRight,
        string source,
        DeputyRelationEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (SyncCapacity(caller, tenant, holdsSyncRight) is not { } capacity)
        {
            return DeputyRelationResult.Failed(DeputyRelationOutcome.Forbidden);
        }

        if (!IsValidSource(source))
        {
            return DeputyRelationResult.Failed(DeputyRelationOutcome.Invalid, DeputyRelationErrorCodes.SourceInvalid, "The source name is invalid.");
        }

        var existing = (await _repository.ListAsync(tenant, new DeputyRelationFilter(Source: source), MaxEntriesPerSource + 1, ct).ConfigureAwait(false) ?? [])
            .Where(e => e.TenantId == tenant && e.Source == source && string.Equals(e.ExternalId, entry.ExternalId?.Trim(), StringComparison.Ordinal))
            .ToList();
        var now = _time.GetUtcNow();
        var (desired, rejected) = await BuildDesiredAsync(caller, tenant, source, [entry], existing, now, ct).ConfigureAwait(false);
        if (rejected.Count > 0)
        {
            // A single rejected entry changes nothing; the stored relation (if any) is left as it is.
            return DeputyRelationResult.Failed(DeputyRelationOutcome.Invalid, rejected[0].Code, rejected[0].Message) with { Rejected = rejected };
        }

        return await ApplyDiffAsync(caller, tenant, capacity, existing, desired, rejected, dryRun: false, now, ct).ConfigureAwait(false);
    }

    /// <summary>Incremental end of one relation of <paramref name="source"/>.</summary>
    public async Task<DeputyRelationResult> DeleteAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        bool holdsSyncRight,
        string source,
        string externalId,
        CancellationToken ct = default)
    {
        if (SyncCapacity(caller, tenant, holdsSyncRight) is not { } capacity)
        {
            return DeputyRelationResult.Failed(DeputyRelationOutcome.Forbidden);
        }

        if (!IsValidSource(source))
        {
            return DeputyRelationResult.Failed(DeputyRelationOutcome.Invalid, DeputyRelationErrorCodes.SourceInvalid, "The source name is invalid.");
        }

        var existing = (await _repository.ListAsync(tenant, new DeputyRelationFilter(Source: source), MaxEntriesPerSource + 1, ct).ConfigureAwait(false) ?? [])
            .Where(e => e.TenantId == tenant && e.Source == source && string.Equals(e.ExternalId, externalId?.Trim(), StringComparison.Ordinal))
            .ToList();
        if (existing.Count == 0)
        {
            return DeputyRelationResult.Failed(DeputyRelationOutcome.NotFound);
        }

        return await ApplyDiffAsync(caller, tenant, capacity, existing, [], [], dryRun: false, _time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads relations. The deputy sync right and Data Governance read every relation of the tenant; an owner-group member reads the
    /// effective deputies of a container it owns (active relations whose principal is an owner-group member of that container).
    /// </summary>
    public async Task<DeputyRelationResult> ListAsync(
        SecurityPrincipalContext caller, TenantId tenant, bool holdsSyncRight, DeputyRelationQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IsPersonOrSyncInTenant(caller, tenant))
        {
            return DeputyRelationResult.Failed(DeputyRelationOutcome.Forbidden);
        }

        var now = _time.GetUtcNow();
        var full = SyncCapacity(caller, tenant, holdsSyncRight) is not null;
        Sid? ownerGroup = null;
        if (!full)
        {
            if (query.Container is not { } container ||
                await OwnerGroupOfAsync(tenant, container, ct).ConfigureAwait(false) is not { } group ||
                IsDegraded(caller) ||
                !await _capabilities.IsOwnerMemberAsync(caller, container, ct).ConfigureAwait(false) ||
                _authority is null)
            {
                return DeputyRelationResult.Failed(DeputyRelationOutcome.Forbidden);
            }

            ownerGroup = group;
        }

        var principalKeys = await KeysOfAsync(tenant, query.Principal, ct).ConfigureAwait(false);
        var deputyKeys = await KeysOfAsync(tenant, query.Deputy, ct).ConfigureAwait(false);
        if ((query.Principal is not null && principalKeys.Count == 0) || (query.Deputy is not null && deputyKeys.Count == 0) ||
            (query.Source is not null && !IsValidSource(query.Source)))
        {
            return DeputyRelationResult.Read([]);
        }

        var filter = new DeputyRelationFilter(
            Source: query.Source,
            DeputyKeys: deputyKeys.Count > 0 ? deputyKeys : null,
            PrincipalKeys: principalKeys.Count > 0 ? principalKeys : null,
            ActiveAt: query.ActiveOnly || !full ? now : null);
        var rows = (await _repository.ListAsync(tenant, filter, MaxReadRows, ct).ConfigureAwait(false) ?? []).Where(r => r.TenantId == tenant).ToList();
        if (ownerGroup is { } og)
        {
            var effective = new List<DeputyRelation>();
            foreach (var row in rows)
            {
                if (await _authority!.IsPrincipalOwnerMemberAsync(tenant, row, og, ct).ConfigureAwait(false))
                {
                    effective.Add(row);
                }
            }

            rows = effective;
        }

        return DeputyRelationResult.Read(rows);
    }

    /// <summary>
    /// SCIM deprovisioning path: ends every relation (any source, any window) in which <paramref name="subject"/> is the deputy or the
    /// principal, atomically with one audit entry each. Returns the number ended; throws when nothing could be applied so the
    /// deprovisioning is retried idempotently.
    /// </summary>
    public async Task<int> EndAllForSubjectAsync(TenantId tenant, Sid subject, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            throw new ArgumentException("A valid tenant is required.", nameof(tenant));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subject.Value);
        var key = subject.Value.Contains('|', StringComparison.Ordinal) ? DeputyKey.For(subject.Value) : string.Empty;
        if (key.Length == 0)
        {
            // Stored identities are realm-qualified; the deprovisioning passes the realm-qualified forms as well.
            return 0;
        }

        var rows = (await _repository.ListAsync(tenant, new DeputyRelationFilter(SubjectKeys: [key]), MaxEntriesPerSource, ct).ConfigureAwait(false) ?? [])
            .Where(r => r.TenantId == tenant)
            .ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        var now = _time.GetUtcNow();
        var audit = rows.Select(r => Audit(AuditEventTypes.DeputyRelationEnded, "ENDED", DeprovisioningActor, "DEPROVISIONING", r, r, null, now, reason)).ToList();
        if (!await _repository.ApplyAsync(tenant, new DeputyRelationChanges([], [], rows), audit, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The deputy relations of the deprovisioned subject changed concurrently; the deprovisioning must be retried.");
        }

        await InvalidateAsync(tenant, now, ct).ConfigureAwait(false);
        return rows.Count;
    }

    private async Task<(Dictionary<string, DeputyRelation> Desired, List<DeputyRelationRejection> Rejected)> BuildDesiredAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        string source,
        IReadOnlyList<DeputyRelationEntry> entries,
        IReadOnlyList<DeputyRelation> existing,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var desired = new Dictionary<string, DeputyRelation>(StringComparer.Ordinal);
        var rejected = new List<DeputyRelationRejection>();
        var duplicates = entries
            .Select(e => e?.ExternalId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .GroupBy(id => id!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        var resolved = new Dictionary<string, (ResolvedDeputyIdentity? Identity, DeputyIdentityFailure Failure)>(StringComparer.Ordinal);
        var byExternalId = existing.ToDictionary(e => e.ExternalId, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var externalId = entry?.ExternalId?.Trim();
            if (entry is null || string.IsNullOrEmpty(externalId) || externalId.Length > DeputyKey.MaxLength || externalId.Any(char.IsControl))
            {
                rejected.Add(new DeputyRelationRejection(externalId, DeputyRelationErrorCodes.ExternalIdInvalid, "externalId is required (at most 256 characters)."));
                continue;
            }

            if (duplicates.Contains(externalId))
            {
                rejected.Add(new DeputyRelationRejection(externalId, DeputyRelationErrorCodes.DuplicateExternalId, "The externalId occurs more than once."));
                continue;
            }

            if (entry.ValidTo is not { } validTo || (entry.ValidFrom ?? now) is var validFrom && validTo <= validFrom)
            {
                rejected.Add(new DeputyRelationRejection(externalId, DeputyRelationErrorCodes.WindowInvalid, "validTo is required and must be after validFrom."));
                continue;
            }

            var from = (entry.ValidFrom ?? byExternalId.GetValueOrDefault(externalId)?.ValidFrom ?? now).ToUniversalTime();
            var to = validTo.ToUniversalTime();
            if (to - from > MaxWindow)
            {
                rejected.Add(new DeputyRelationRejection(
                    externalId, DeputyRelationErrorCodes.WindowInvalid, $"A deputy relation lasts at most {(int)MaxWindow.TotalDays} days."));
                continue;
            }

            if (to <= now)
            {
                // An ended relation grants nothing and is not stored; an existing one with this id ends.
                continue;
            }

            var principal = await ResolveCachedAsync(tenant, entry.Principal, resolved, ct).ConfigureAwait(false);
            if (principal.Identity is null)
            {
                rejected.Add(new DeputyRelationRejection(
                    externalId,
                    principal.Failure == DeputyIdentityFailure.Ambiguous ? DeputyRelationErrorCodes.PrincipalAmbiguous : DeputyRelationErrorCodes.PrincipalUnresolvable,
                    "The principal cannot be resolved to exactly one active user."));
                continue;
            }

            var deputy = await ResolveCachedAsync(tenant, entry.Deputy, resolved, ct).ConfigureAwait(false);
            if (deputy.Identity is null)
            {
                rejected.Add(new DeputyRelationRejection(
                    externalId,
                    deputy.Failure == DeputyIdentityFailure.Ambiguous ? DeputyRelationErrorCodes.DeputyAmbiguous : DeputyRelationErrorCodes.DeputyUnresolvable,
                    "The deputy cannot be resolved to exactly one active user."));
                continue;
            }

            if (string.Equals(principal.Identity.Key, deputy.Identity.Key, StringComparison.Ordinal))
            {
                rejected.Add(new DeputyRelationRejection(externalId, DeputyRelationErrorCodes.SelfRelation, "A person cannot deputize for itself."));
                continue;
            }

            var previous = byExternalId.GetValueOrDefault(externalId);
            desired[externalId] = new DeputyRelation
            {
                Id = previous?.Id ?? Guid.NewGuid(),
                TenantId = tenant,
                Source = source,
                ExternalId = externalId,
                PrincipalRef = principal.Identity.Reference,
                PrincipalKey = principal.Identity.Key,
                PrincipalAltKey = principal.Identity.AltKey,
                DeputyRef = deputy.Identity.Reference,
                DeputyKey = deputy.Identity.Key,
                DeputyAltKey = deputy.Identity.AltKey,
                ValidFrom = from,
                ValidTo = to,
                CreatedBy = previous?.CreatedBy ?? caller.UserSid,
                CreatedAt = previous?.CreatedAt ?? now,
                UpdatedBy = previous is null ? (Sid?)null : caller.UserSid,
                UpdatedAt = previous is null ? null : now
            };
        }

        return (desired, rejected);
    }

    private async Task<DeputyRelationResult> ApplyDiffAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        string capacity,
        IReadOnlyList<DeputyRelation> existing,
        Dictionary<string, DeputyRelation> desired,
        List<DeputyRelationRejection> rejected,
        bool dryRun,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var created = new List<DeputyRelation>();
        var updated = new List<(DeputyRelation Before, DeputyRelation After)>();
        var ended = new List<DeputyRelation>();
        var unchanged = 0;
        foreach (var current in existing)
        {
            if (!desired.TryGetValue(current.ExternalId, out var wanted))
            {
                ended.Add(current);
            }
            else if (current.SameContentAs(wanted))
            {
                unchanged++;
            }
            else
            {
                updated.Add((current, wanted));
            }
        }

        var existingIds = existing.Select(e => e.ExternalId).ToHashSet(StringComparer.Ordinal);
        created.AddRange(desired.Values.Where(d => !existingIds.Contains(d.ExternalId)).OrderBy(d => d.ExternalId, StringComparer.Ordinal));

        var result = new DeputyRelationResult(DeputyRelationOutcome.Ok, created, updated, ended, unchanged, rejected, dryRun);
        var changes = new DeputyRelationChanges(created, updated, ended);
        if (dryRun || changes.IsEmpty)
        {
            return result;
        }

        var actor = caller.UserSid;
        var audit = new List<AuditLogEntry>(created.Count + updated.Count + ended.Count);
        audit.AddRange(created.Select(c => Audit(AuditEventTypes.DeputyRelationCreated, "CREATED", actor, capacity, c, null, c, now)));
        audit.AddRange(updated.Select(u => Audit(AuditEventTypes.DeputyRelationUpdated, "UPDATED", actor, capacity, u.After, u.Before, u.After, now)));
        audit.AddRange(ended.Select(e => Audit(AuditEventTypes.DeputyRelationEnded, "ENDED", actor, capacity, e, e, null, now, EndedMissingFromSource)));
        if (!await _repository.ApplyAsync(tenant, changes, audit, ct).ConfigureAwait(false))
        {
            return DeputyRelationResult.Failed(
                DeputyRelationOutcome.Conflict, DeputyRelationErrorCodes.ConcurrentChange, "The relations of the source changed concurrently; nothing was applied.");
        }

        await InvalidateAsync(tenant, now, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<(ResolvedDeputyIdentity? Identity, DeputyIdentityFailure Failure)> ResolveCachedAsync(
        TenantId tenant, string? identifier, Dictionary<string, (ResolvedDeputyIdentity?, DeputyIdentityFailure)> cache, CancellationToken ct)
    {
        var key = identifier?.Trim() ?? string.Empty;
        if (!cache.TryGetValue(key, out var hit))
        {
            hit = await _identities.ResolveAsync(tenant, identifier, ct).ConfigureAwait(false);
            cache[key] = hit;
        }

        return hit;
    }

    private async Task<IReadOnlyCollection<string>> KeysOfAsync(TenantId tenant, string? identifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return [];
        }

        var (identity, _) = await _identities.ResolveAsync(tenant, identifier, ct).ConfigureAwait(false);
        return identity is null ? [] : [identity.Key];
    }

    private async Task<Sid?> OwnerGroupOfAsync(TenantId tenant, ContainerRef container, CancellationToken ct)
    {
        Sid? group = container.Kind switch
        {
            ContainerKind.Domain when _domains is not null => (await _domains.GetAsync(tenant, container.Id, ct).ConfigureAwait(false))?.OwnerGroupSid,
            ContainerKind.DataProduct when _products is not null => (await _products.GetProductByIdAsync(tenant, container.Id, ct).ConfigureAwait(false))?.OwnerGroupSid,
            _ => null
        };
        return group is { } g && !string.IsNullOrWhiteSpace(g.Value) ? g : (Sid?)null;
    }

    /// <summary>
    /// The capacity a caller reconciles with: the tenant-bound deputy sync right (checked by the caller of this service against the
    /// authenticated tenant) or Data Governance of the tenant. Null otherwise; IT administration has no capacity here.
    /// </summary>
    private string? SyncCapacity(SecurityPrincipalContext? caller, TenantId tenant, bool holdsSyncRight)
    {
        if (!IsPersonOrSyncInTenant(caller, tenant))
        {
            return null;
        }

        if (holdsSyncRight)
        {
            return "DEPUTY_SYNC";
        }

        return !IsDegraded(caller!) && _capabilities.IsGovernance(caller!.Principal, tenant) ? "GOVERNANCE" : null;
    }

    private static bool IsPersonOrSyncInTenant(SecurityPrincipalContext? caller, TenantId tenant) =>
        caller is not null && caller.IsAuthenticated && !caller.TenantClaimInvalid && caller.Principal is not null &&
        !string.IsNullOrWhiteSpace(caller.UserSid.Value) &&
        !string.IsNullOrWhiteSpace(tenant.Value) && !TenantId.IsReserved(tenant) &&
        caller.ResolvedTenant is { } resolved && string.Equals(resolved.Value, tenant.Value, StringComparison.OrdinalIgnoreCase);

    private static bool IsDegraded(SecurityPrincipalContext caller) =>
        caller.GroupResolutionStatus != GroupResolutionStatus.Complete ||
        GroupResolutionClaims.HasAnyDegradedClaim(caller.Claims) ||
        SeparationOfDutiesService.IsDegradedGroupOrRoleResolution(caller.Principal);

    private async Task InvalidateAsync(TenantId tenant, DateTimeOffset now, CancellationToken ct)
    {
        if (_invalidator is not null)
        {
            await _invalidator.InvalidateWideAsync(tenant, occurredAtUtc: now, ct: ct).ConfigureAwait(false);
        }
    }

    private static AuditLogEntry Audit(
        string eventType,
        string decision,
        Sid actor,
        string capacity,
        DeputyRelation subject,
        DeputyRelation? before,
        DeputyRelation? after,
        DateTimeOffset now,
        string? reason = null) => new()
    {
        Id = Guid.NewGuid(),
        OccurredAt = now,
        EventType = eventType,
        ActorSid = actor,
        TargetTable = $"DEPUTY_RELATION:{subject.Source}:{subject.ExternalId}",
        Decision = decision,
        TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
        TenantId = subject.TenantId,
        DetailsJson = JsonSerializer.Serialize(
            new
            {
                RelationId = subject.Id,
                subject.Source,
                subject.ExternalId,
                Capacity = capacity,
                Reason = reason,
                Before = before is null ? null : Snapshot(before),
                After = after is null ? null : Snapshot(after)
            },
            JsonOptions)
    };

    private static object Snapshot(DeputyRelation relation) => new
    {
        Principal = relation.PrincipalRef,
        Deputy = relation.DeputyRef,
        relation.ValidFrom,
        relation.ValidTo
    };
}
