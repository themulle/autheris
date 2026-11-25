using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Ownership;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Security;

namespace Autheris.Tests.Unit.Governance.Requests;

/// <summary>Shared fixtures for the deputy relation tests (issue #1126).</summary>
internal static class DeputyTestSupport
{
    public const string Realm = "corp";
    public static readonly TenantId TenantA = new("tenant-a");
    public static readonly TenantId TenantB = new("tenant-b");
    public static readonly Guid DomainId = Guid.Parse("7f2f7c1e-1a2b-4c3d-8e9f-001122334455");
    public static readonly Sid OwnerGroupSid = new("S-1-5-21-10-20-30-5000");
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T10:00:00Z");

    /// <summary>A person of the directory: SCIM user key and object SID.</summary>
    public sealed record Person(string UserKey, string ObjectSid)
    {
        public string Reference => Realm + "|" + UserKey;
    }

    public static readonly Person Alice = new("alice-key", "S-1-5-21-10-20-30-1001"); // deputy (A)
    public static readonly Person Bob = new("bob-key", "S-1-5-21-10-20-30-1002");     // owner member (B)
    public static readonly Person Carol = new("carol-key", "S-1-5-21-10-20-30-1003"); // owner member, unrelated
    public static readonly Person Dave = new("dave-key", "S-1-5-21-10-20-30-1004");   // not an owner member

    public static SecurityPrincipalContext ContextFor(
        Person person,
        string? role = null,
        Sid[]? groups = null,
        TenantId? tenant = null,
        GroupResolutionStatus groupStatus = GroupResolutionStatus.Complete,
        bool withRealm = true) =>
        ContextFor(person.ObjectSid, role, groups, tenant, groupStatus, withRealm);

    public static SecurityPrincipalContext ContextFor(
        string sid,
        string? role = null,
        Sid[]? groups = null,
        TenantId? tenant = null,
        GroupResolutionStatus groupStatus = GroupResolutionStatus.Complete,
        bool withRealm = true)
    {
        var resolved = tenant ?? TenantA;
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new("tenant_id", resolved.Value) };
        if (withRealm)
        {
            claims.Add(new Claim("autheris:realm", Realm));
        }

        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = resolved,
            GroupSids = groups?.ToHashSet() ?? new HashSet<Sid>(),
            GroupResolutionStatus = groupStatus,
            TenantRoles = role is null ? new HashSet<string>() : new HashSet<string>([role], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };
    }

    public static DeputyRelation Relation(
        Person deputy,
        Person principal,
        DateTimeOffset from,
        DateTimeOffset to,
        string source = "hr",
        string? externalId = null,
        TenantId? tenant = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant ?? TenantA,
        Source = source,
        ExternalId = externalId ?? deputy.UserKey + "-for-" + principal.UserKey,
        PrincipalRef = principal.Reference,
        PrincipalKey = DeputyKey.Qualified(Realm, principal.UserKey),
        PrincipalAltKey = DeputyKey.Qualified(Realm, principal.ObjectSid),
        DeputyRef = deputy.Reference,
        DeputyKey = DeputyKey.Qualified(Realm, deputy.UserKey),
        DeputyAltKey = DeputyKey.Qualified(Realm, deputy.ObjectSid),
        ValidFrom = from,
        ValidTo = to,
        CreatedBy = new Sid("hr-sync"),
        CreatedAt = from
    };

    /// <summary>
    /// A directory where Bob and Carol are effective members of the owner group; locked users and inactive users can be configured.
    /// </summary>
    public static IIdentityStoreRepository Directory(
        ISet<string>? ownerMembers = null,
        ISet<string>? locked = null,
        IReadOnlyCollection<Person>? users = null,
        ISet<string>? inactive = null)
    {
        ownerMembers ??= new HashSet<string>(StringComparer.Ordinal) { Bob.UserKey, Carol.UserKey };
        locked ??= new HashSet<string>(StringComparer.Ordinal);
        inactive ??= new HashSet<string>(StringComparer.Ordinal);
        users ??= [Alice, Bob, Carol, Dave];
        ScimUser? Find(Func<Person, bool> match) =>
            users.FirstOrDefault(match) is { } p
                ? new ScimUser(p.UserKey, null, p.UserKey + "@corp.example", null, null, !inactive.Contains(p.UserKey), null, null, null, ObjectSid: p.ObjectSid)
                : null;
        return NullFake.Create<IIdentityStoreRepository>((m, args) => m.Name switch
        {
            nameof(IScimGroupStore.GetEffectiveGroupSidsForUserAsync) => Task.FromResult(new EffectiveScimGroupsResult(
                ownerMembers.Contains((string)args![2]!)
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OwnerGroupSid.Value }
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                MaxDepthExceeded: false)),
            nameof(IScimProvisioningStore.IsSubjectLockedAsync) => Task.FromResult(locked.Contains((string)args![2]!)),
            nameof(IScimUserStore.GetUserAsync) => Task.FromResult(Find(p => p.UserKey == (string)args![2]!)),
            nameof(IScimUserStore.GetUserByObjectSidAsync) => Task.FromResult(Find(p => p.ObjectSid == (string)args![2]!)),
            nameof(IScimUserStore.GetUserByEntraObjectIdAsync) => Task.FromResult<ScimUser?>(null),
            nameof(IScimUserStore.GetUserByUpnAsync) => Task.FromResult<ScimUser?>(null),
            _ => NullFake.UseDefault
        });
    }

    public static IGovernanceDomainRepository DomainRepository(Sid? ownerGroup = null) =>
        NullFake.Create<IGovernanceDomainRepository>((m, args) =>
            m.Name == nameof(IGovernanceDomainRepository.GetAsync) && (TenantId)args![0]! == TenantA && (Guid)args[1]! == DomainId
                ? Task.FromResult<GovernanceDomain?>(new GovernanceDomain(
                    DomainId, TenantA, "Sales", null, null, ownerGroup ?? OwnerGroupSid, ProtectionClass.Confidential, CatalogVisibilityMode.Open, false, 1,
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                : m.Name == nameof(IGovernanceDomainRepository.GetAsync)
                    ? Task.FromResult<GovernanceDomain?>(null)
                    : NullFake.UseDefault);
}

/// <summary>In-memory <see cref="IDeputyRelationRepository"/> with the store's filter semantics and all-or-nothing change sets.</summary>
internal sealed class InMemoryDeputyRelationRepository : IDeputyRelationRepository
{
    public List<DeputyRelation> Rows { get; } = [];

    public List<AuditLogEntry> Audit { get; } = [];

    public Exception? FailApplyWith { get; set; }

    public int ApplyCalls { get; private set; }

    public Task<IReadOnlyList<DeputyRelation>> ListAsync(TenantId tenant, DeputyRelationFilter filter, int max, CancellationToken ct = default)
    {
        static bool In(IReadOnlyCollection<string>? keys, params string?[] values) =>
            keys is null || values.Any(v => v is not null && keys.Contains(v));

        var rows = Rows
            .Where(r => r.TenantId == tenant)
            .Where(r => filter.Source is null || r.Source == filter.Source)
            .Where(r => In(filter.DeputyKeys, r.DeputyKey, r.DeputyAltKey))
            .Where(r => In(filter.PrincipalKeys, r.PrincipalKey, r.PrincipalAltKey))
            .Where(r => In(filter.SubjectKeys, r.DeputyKey, r.DeputyAltKey, r.PrincipalKey, r.PrincipalAltKey))
            .Where(r => filter.ActiveAt is not { } at || r.IsActiveAt(at))
            .OrderBy(r => r.Source, StringComparer.Ordinal).ThenBy(r => r.ExternalId, StringComparer.Ordinal)
            .Take(max)
            .ToList();
        return Task.FromResult<IReadOnlyList<DeputyRelation>>(rows);
    }

    public Task<bool> ApplyAsync(TenantId tenant, DeputyRelationChanges changes, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct = default)
    {
        ApplyCalls++;
        if (FailApplyWith is { } failure)
        {
            throw failure;
        }

        var next = Rows.ToList();
        foreach (var before in changes.Deletes)
        {
            if (!next.Remove(before))
            {
                return Task.FromResult(false);
            }
        }

        foreach (var (before, after) in changes.Updates)
        {
            var index = next.IndexOf(before);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            next[index] = after;
        }

        foreach (var insert in changes.Inserts)
        {
            if (next.Any(r => r.TenantId == insert.TenantId && r.Source == insert.Source && r.ExternalId == insert.ExternalId))
            {
                return Task.FromResult(false);
            }

            next.Add(insert);
        }

        Rows.Clear();
        Rows.AddRange(next);
        Audit.AddRange(audit);
        return Task.FromResult(true);
    }
}
