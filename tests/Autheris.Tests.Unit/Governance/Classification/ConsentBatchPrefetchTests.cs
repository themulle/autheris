namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1043: a batch of decisions reads the caller's consents once (<see cref="ConsentBatchPrefetch"/>). Each decision must see the
/// per-table read's result at its own time, restricted to what existed when the batch was read (fail closed), and the prefetch must
/// refuse requests it was not read for (other tenant, other subjects, other tables, an earlier time).
/// </summary>
[Trait("Category", "Security")]
public sealed class ConsentBatchPrefetchTests
{
    private static readonly TenantId Tenant = new("tenant-consent-prefetch");
    private static readonly DateTimeOffset ReadAt = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly Sid User = new("S-1-5-21-7-7-7-1001");
    private static readonly Sid Group = new("S-1-5-21-7-7-7-3001");
    private static readonly Sid Stranger = new("S-1-5-21-7-7-7-9999");
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private static readonly TableIdentifier Invoices = new("sales", "dbo", "invoices");

    private static Consent Grant(TableIdentifier table, Sid grantee, DateTimeOffset from, DateTimeOffset to, ConsentEffect effect = ConsentEffect.Allow) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Tenant,
        TableIdentifier = table,
        GranteeType = grantee == Group ? GranteeType.Group : GranteeType.User,
        GranteeSid = grantee,
        Effect = effect,
        ValidFrom = from,
        ValidTo = to,
        ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
    };

    private static Task<ConsentBatchPrefetch> LoadAsync(IReadOnlyList<Consent> consents, params Sid[] subjects) =>
        ConsentBatchPrefetch.LoadAsync(new TestInMemoryConsentRepository(consents), subjects, [Orders, Invoices], Tenant, ReadAt, default);

    [Fact]
    public async Task Decision_SeesTheConsentsOfItsTableAndSubjects()
    {
        var userGrant = Grant(Orders, User, ReadAt.AddDays(-1), ReadAt.AddDays(1));
        var groupGrant = Grant(Orders, Group, ReadAt.AddDays(-1), ReadAt.AddDays(1));
        var otherTable = Grant(Invoices, User, ReadAt.AddDays(-1), ReadAt.AddDays(1));
        var prefetch = await LoadAsync([userGrant, groupGrant, otherTable], User, Group);

        prefetch.TryGetActiveConsents([User, Group], Orders, ReadAt, Tenant, out var both).ShouldBeTrue();
        both.Select(static c => c.Id).ShouldBe([userGrant.Id, groupGrant.Id], ignoreOrder: true);

        // A decision that reads with fewer subjects (e.g. the classification step with user SIDs only) gets only their consents.
        prefetch.TryGetActiveConsents([User], Orders, ReadAt, Tenant, out var userOnly).ShouldBeTrue();
        userOnly.Select(static c => c.Id).ShouldBe([userGrant.Id]);
    }

    [Fact]
    public async Task ConsentThatExpiresDuringTheBatch_IsDropped()
    {
        var expiring = Grant(Orders, User, ReadAt.AddDays(-1), ReadAt.AddSeconds(2));
        var prefetch = await LoadAsync([expiring], User);

        prefetch.TryGetActiveConsents([User], Orders, ReadAt.AddSeconds(1), Tenant, out var before).ShouldBeTrue();
        before.Count.ShouldBe(1);
        prefetch.TryGetActiveConsents([User], Orders, ReadAt.AddSeconds(3), Tenant, out var after).ShouldBeTrue();
        after.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConsentThatStartsDuringTheBatch_IsMissing_FailClosed()
    {
        var starting = Grant(Orders, User, ReadAt.AddSeconds(1), ReadAt.AddDays(1));
        var prefetch = await LoadAsync([starting], User);

        prefetch.TryGetActiveConsents([User], Orders, ReadAt.AddSeconds(2), Tenant, out var consents).ShouldBeTrue();
        consents.ShouldBeEmpty();
    }

    [Fact]
    public async Task OtherSubject_OtherTenant_OtherTable_OrEarlierTime_IsNotAnswered()
    {
        var prefetch = await ConsentBatchPrefetch.LoadAsync(
            new TestInMemoryConsentRepository([Grant(Orders, User, ReadAt.AddDays(-1), ReadAt.AddDays(1))]),
            [User], [Orders], Tenant, ReadAt, default);

        prefetch.TryGetActiveConsents([User, Stranger], Orders, ReadAt, Tenant, out _).ShouldBeFalse();
        prefetch.TryGetActiveConsents([User], Orders, ReadAt, new TenantId("tenant-other"), out _).ShouldBeFalse();
        prefetch.TryGetActiveConsents([User], Invoices, ReadAt, Tenant, out _).ShouldBeFalse();
        prefetch.TryGetActiveConsents([User], Orders, ReadAt.AddSeconds(-1), Tenant, out _).ShouldBeFalse();
        prefetch.TryGetActivePatternConsents(new TenantId("tenant-other"), ReadAt, out _).ShouldBeFalse();
        prefetch.TryGetActivePatternConsents(Tenant, ReadAt.AddSeconds(-1), out _).ShouldBeFalse();
    }

    [Fact]
    public async Task NotYetValidDeny_IsKept_LikeThePerTableRead()
    {
        // The per-table read keeps a deny whose validity has not started (row filter and final rule both exempt it).
        var futureDeny = Grant(Orders, User, ReadAt.AddDays(1), ReadAt.AddDays(2), ConsentEffect.Deny);
        // filterWindow: false returns the consents as stored (the repository's own row filter is covered by the integration tests).
        var repository = new TestInMemoryConsentRepository([futureDeny], filterWindow: false);
        var prefetch = await ConsentBatchPrefetch.LoadAsync(repository, [User], [Orders], Tenant, ReadAt, default);

        prefetch.TryGetActiveConsents([User], Orders, ReadAt, Tenant, out var consents).ShouldBeTrue();
        consents.Select(static c => c.Id).ShouldBe([futureDeny.Id]);
    }

    [Fact]
    public async Task StalePrefetch_IsNotAnswered_SoARevokeIsSeenByTheRepositoryRead()
    {
        var prefetch = await LoadAsync([Grant(Orders, User, ReadAt.AddDays(-1), ReadAt.AddDays(1))], User);

        prefetch.TryGetActiveConsents([User], Orders, ReadAt + ConsentBatchPrefetch.MaxAge, Tenant, out _).ShouldBeTrue();
        prefetch.TryGetActiveConsents([User], Orders, ReadAt + ConsentBatchPrefetch.MaxAge + TimeSpan.FromMilliseconds(1), Tenant, out _).ShouldBeFalse();
        prefetch.TryGetActivePatternConsents(Tenant, ReadAt + ConsentBatchPrefetch.MaxAge + TimeSpan.FromMilliseconds(1), out _).ShouldBeFalse();
    }

    [Fact]
    public async Task PatternPrefetch_FiltersBySubjectsAndExcludesNonDataGrants()
    {
        static Consent PatternGrant(Sid grantee, PermissionGrantKind kind = PermissionGrantKind.Data, ConsentEffect effect = ConsentEffect.Allow, DateTimeOffset? from = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            DataSource = "sales",
            ObjectPattern = "sales.dbo.*",
            TableIdentifier = default,
            GranteeType = grantee == Group ? GranteeType.Group : GranteeType.User,
            GranteeSid = grantee,
            Effect = effect,
            GrantKind = kind,
            ValidFrom = from ?? ReadAt.AddDays(-1),
            ValidTo = ReadAt.AddDays(5)
        };

        var userData = PatternGrant(User);
        var groupData = PatternGrant(Group);
        var strangerData = PatternGrant(Stranger);
        var userUnmask = PatternGrant(User, kind: PermissionGrantKind.Unmask);
        var allPatterns = new[] { userData, groupData, strangerData, userUnmask };
        var inner = new TestInMemoryConsentRepository(allPatterns);
        var spy = new PatternMethodRecordingRepo(inner, allPatterns);

        var prefetch = await ConsentBatchPrefetch.LoadAsync(spy, [User, Group], [Orders], Tenant, ReadAt, default);

        spy.SubjectFilteredPatternCalls.ShouldBe(1);
        spy.UnfilteredPatternCalls.ShouldBe(0);

        prefetch.TryGetActivePatternConsents([User], Tenant, ReadAt, out var userOnly).ShouldBeTrue();
        userOnly.Select(static c => c.Id).ShouldBe([userData.Id]);

        prefetch.TryGetActivePatternConsents([User, Group], Tenant, ReadAt, out var both).ShouldBeTrue();
        both.Select(static c => c.Id).ShouldBe([userData.Id, groupData.Id], ignoreOrder: true);

        prefetch.TryGetActivePatternConsents([User, Stranger], Tenant, ReadAt, out _).ShouldBeFalse();
    }

    private sealed class PatternMethodRecordingRepo(IConsentRepository inner, IReadOnlyList<Consent> allConsents) : IConsentRepository
    {
        public int SubjectFilteredPatternCalls { get; private set; }
        public int UnfilteredPatternCalls { get; private set; }

        public Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<Consent>>> GetActiveConsentsForSubjectsBatchAsync(
            IEnumerable<Sid> subjects, IReadOnlyCollection<TableIdentifier> tables, DateTimeOffset atTime, TenantId tenantId, CancellationToken ct = default) =>
            inner.GetActiveConsentsForSubjectsBatchAsync(subjects, tables, atTime, tenantId, ct);

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(
            IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
            inner.GetActiveConsentsForSubjectsAsync(subjects, table, atTime, tenantId, ct);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(
            IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
            inner.GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, tenantId, ct);

        public Task<IReadOnlyList<Consent>> GetActivePatternConsentsAsync(TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default)
        {
            UnfilteredPatternCalls++;
            return Task.FromResult<IReadOnlyList<Consent>>(allConsents.Where(c => c.IsPatternGrant && c.TenantId == tenantId && c.IsActive(atTime)).ToList());
        }

        public Task<IReadOnlyList<Consent>> GetPatternConsentsWithPendingDeniesAsync(TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default)
        {
            UnfilteredPatternCalls++;
            return Task.FromResult<IReadOnlyList<Consent>>(allConsents.Where(c => c.IsPatternGrant && c.TenantId == tenantId && (c.IsActive(atTime) || (c.Effect == ConsentEffect.Deny && c.ValidFrom > atTime))).ToList());
        }

        public Task<IReadOnlyList<Consent>> GetPatternConsentsForSubjectsWithPendingDeniesAsync(
            IEnumerable<Sid> subjects, TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default)
        {
            SubjectFilteredPatternCalls++;
            var set = subjects.ToHashSet();
            return Task.FromResult<IReadOnlyList<Consent>>(allConsents.Where(c =>
                c.IsPatternGrant && c.TenantId == tenantId &&
                c.GrantKind == PermissionGrantKind.Data &&
                c.GranteeSid is { } sid && set.Contains(sid) &&
                (c.IsActive(atTime) || (c.Effect == ConsentEffect.Deny && c.ValidFrom > atTime))).ToList());
        }

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => inner.CreateConsentAsync(consent, ct);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => inner.GetConsentByIdAsync(consentId, ct);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, Autheris.Domain.Security.ConsentRevocationAuthority authority, CancellationToken ct = default) => inner.RevokeConsentAsync(consentId, revokedBySid, reason, authority, ct);
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => inner.GetExpiringConsentsAsync(threshold, maxResults, ct);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => inner.GetActiveConsentsByConsentRequestIdAsync(consentRequestId, atTime, ct);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => inner.RevokeSystemConsentAsync(consentId, consentRequestId, revokedBySid, reason, ct);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => inner.GetConsentPageAsync(query, ct);
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, Autheris.Domain.Security.ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => inner.ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, ct);
    }
}
