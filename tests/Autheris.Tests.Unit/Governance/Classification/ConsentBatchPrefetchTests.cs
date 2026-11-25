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
    public async Task InternalPatternAllow_PrefetchesApproversInOneBatch_AndKeepsApproversIsolated()
    {
        var ownerA = "S-1-5-21-999-1";
        var ownerB = "S-1-5-21-999-2";
        var grantA = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = Guid.Empty,
            TenantId = Tenant,
            ObjectPattern = "sales.*.*",
            DataSource = "sales",
            WorkflowMode = ConsentWorkflowMode.Internal,
            ApprovingOwners = [ownerA],
            GranteeType = GranteeType.User,
            GranteeSid = User,
            Effect = ConsentEffect.Allow,
            ValidFrom = ReadAt.AddDays(-1),
            ValidTo = ReadAt.AddDays(1)
        };
        var grantB = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = Guid.Empty,
            TenantId = Tenant,
            ObjectPattern = "sales.*.*",
            DataSource = "sales",
            WorkflowMode = ConsentWorkflowMode.Internal,
            ApprovingOwners = [ownerB],
            GranteeType = GranteeType.User,
            GranteeSid = User,
            Effect = ConsentEffect.Allow,
            ValidFrom = ReadAt.AddDays(-1),
            ValidTo = ReadAt.AddDays(1)
        };
        var ownership = new CountingOwnership(new Dictionary<TableIdentifier, HashSet<string>>
        {
            [Orders] = [ownerA],
            [Invoices] = [ownerB]
        });

        var prefetch = await ConsentBatchPrefetch.LoadAsync(
            new TestInMemoryConsentRepository([grantA, grantB]),
            [User],
            [Orders, Invoices],
            Tenant,
            ReadAt,
            default,
            ownership);

        ownership.BatchCalls.ShouldBe(1);
        ownership.SingleCalls.ShouldBe(0);
        prefetch.TryIsAnyApproverAuthorized(Orders, [ownerA], ReadAt, out var ordA).ShouldBeTrue();
        ordA.ShouldBeTrue();
        prefetch.TryIsAnyApproverAuthorized(Orders, [ownerB], ReadAt, out var ordB).ShouldBeTrue();
        ordB.ShouldBeFalse();
        prefetch.TryIsAnyApproverAuthorized(Invoices, [ownerB], ReadAt, out var invB).ShouldBeTrue();
        invB.ShouldBeTrue();
        prefetch.TryIsAnyApproverAuthorized(Orders, ["S-1-5-21-999-UNPREFETCHED"], ReadAt, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task DirectGrantsOnly_SkipsContainerOwnershipBatchLookup()
    {
        var ownership = new CountingOwnership(new Dictionary<TableIdentifier, HashSet<string>>());
        await ConsentBatchPrefetch.LoadAsync(
            new TestInMemoryConsentRepository([Grant(Orders, User, ReadAt.AddDays(-1), ReadAt.AddDays(1))]),
            [User],
            [Orders, Invoices],
            Tenant,
            ReadAt,
            default,
            ownership);

        ownership.BatchCalls.ShouldBe(0);
        ownership.SingleCalls.ShouldBe(0);
    }

    private sealed class CountingOwnership(Dictionary<TableIdentifier, HashSet<string>> byTable)
        : Autheris.Application.Governance.Ownership.IContainerOwnership
    {
        public int BatchCalls { get; private set; }
        public int SingleCalls { get; private set; }

        public Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableAsync(TableIdentifier table, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContainerOwnerEntry>>([]);

        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, System.Threading.CancellationToken ct = default)
        {
            SingleCalls++;
            return Task.FromResult(byTable.TryGetValue(table, out var set) && set.Contains(approverSid.Value));
        }

        public Task<IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>>> FilterAuthorizedApproversForTablesAsync(
            IReadOnlyCollection<TableMetadata> requested,
            IReadOnlyCollection<string> candidateApproverSids,
            DateTimeOffset atTime,
            System.Threading.CancellationToken ct = default)
        {
            BatchCalls++;
            IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>> res = requested
                .Select(static t => t.Identifier)
                .Distinct()
                .ToDictionary(
                    t => t,
                    t => (IReadOnlySet<string>)(byTable.TryGetValue(t, out var set)
                        ? candidateApproverSids.Where(set.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : System.Collections.Frozen.FrozenSet<string>.Empty));
            return Task.FromResult(res);
        }
    }
}
