using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

public sealed class PermissionInboxServiceTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Guid SalesDomain = Guid.NewGuid();
    private static readonly Guid HrDomain = Guid.NewGuid();
    private static readonly Sid SalesOwners = new("group:S-1-5-21-SALES-OWNERS");
    private static readonly Sid HrOwners = new("group:S-1-5-21-HR-OWNERS");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly TableIdentifier PayrollTable = new("hr", "dbo", "payroll");

    private static SecurityPrincipalContext ContextFor(
        string sid,
        string? role = null,
        Sid[]? groups = null,
        GroupResolutionStatus groupStatus = GroupResolutionStatus.Complete)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new("tenant_id", TenantA.Value) };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = TenantA,
            GroupSids = groups?.ToHashSet() ?? new HashSet<Sid>(),
            GroupResolutionStatus = groupStatus,
            TenantRoles = role is null ? new HashSet<string>() : new HashSet<string>([role], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };
    }

    private static ConsentRequest Pending(TableIdentifier table, string requester = "user:bob") => new()
    {
        Id = Guid.NewGuid(),
        TableId = Guid.NewGuid(),
        TableIdentifier = table,
        RequesterSid = new Sid(requester),
        RequestedGranteeType = GranteeType.User,
        RequestedGranteeRef = requester,
        BusinessJustification = "Need data access for quarterly reporting.",
        Status = "PENDING",
        RequestedAt = DateTimeOffset.UnixEpoch,
        RequestedValidFrom = DateTimeOffset.UnixEpoch,
        RequestedValidTo = DateTimeOffset.UnixEpoch.AddDays(30),
        TenantId = TenantA,
        Version = 1,
        RequestKind = ConsentRequestKind.Data,
        GrantKind = PermissionGrantKind.Data
    };

    private static (PermissionInboxService Service, List<ConsentPageQuery> GrantQueries) Create(
        IReadOnlyList<ConsentRequest> pending,
        IReadOnlyList<Guid>? domainTables = null,
        bool protectionThrows = false)
    {
        var domainRepo = NullFake.Create<IGovernanceDomainRepository>((m, args) =>
        {
            if (m.Name == nameof(IGovernanceDomainRepository.GetAsync))
            {
                var id = (Guid)args![1]!;
                var owner = id == HrDomain ? HrOwners : SalesOwners;
                return Task.FromResult<GovernanceDomain?>(new GovernanceDomain(id, TenantA, "Domain", null, null, owner, ProtectionClass.Confidential, CatalogVisibilityMode.Open, false, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
            }

            if (m.Name == nameof(IGovernanceDomainRepository.ListDomainTableIdsAsync))
            {
                return Task.FromResult<IReadOnlyList<Guid>>(domainTables ?? []);
            }

            return NullFake.UseDefault;
        });

        var protection = new StubProtectionProvider(table =>
        {
            if (protectionThrows)
            {
                throw new InvalidOperationException("snapshot unavailable");
            }

            var hr = table.Equals(PayrollTable);
            return new EffectiveProtection(
                new ContainerRef(ContainerKind.Domain, hr ? HrDomain : SalesDomain),
                hr ? HrOwners : SalesOwners,
                ProtectionClass.Confidential,
                ProtectionClass.Confidential,
                null,
                new Dictionary<string, ColumnProtection>(),
                CatalogVisibilityMode.Open);
        });

        var approvals = NullFake.Create<IConsentApprovalRepository>((m, _) =>
            m.Name == nameof(IConsentApprovalRepository.QueryTenantConsentRequestsAsync)
                ? Task.FromResult(new ConsentRequestPage(pending, null))
                : NullFake.UseDefault);

        var grantQueries = new List<ConsentPageQuery>();
        var consents = NullFake.Create<IConsentRepository>((m, args) =>
        {
            if (m.Name == nameof(IConsentRepository.GetConsentPageAsync))
            {
                grantQueries.Add((ConsentPageQuery)args![0]!);
                return Task.FromResult(new ConsentPage([], null));
            }

            return NullFake.UseDefault;
        });

        var caps = new GovernanceCapabilities(domainRepository: domainRepo);
        var service = new PermissionInboxService(
            approvals,
            consents,
            new ApproverResolver(domainRepo, protectionSnapshotProvider: protection),
            new ApproverEligibility(caps),
            new SeparationOfDutiesService(),
            caps,
            domainRepo);
        return (service, grantQueries);
    }

    [Fact]
    public async Task Inbox_lists_only_requests_the_caller_may_decide()
    {
        var orders = Pending(OrdersTable);
        var payroll = Pending(PayrollTable);
        var (service, _) = Create([orders, payroll]);

        var page = await service.GetInboxAsync(ContextFor("user:owner", groups: [SalesOwners]), TenantA, null, 50);

        var item = page.Items.ShouldHaveSingleItem();
        item.Request.Id.ShouldBe(orders.Id);
        item.Capacity.ShouldBe(ApproverCapacity.DataOwner);
    }

    [Fact]
    public async Task Inbox_hides_requests_of_users_without_capacity()
    {
        var (service, _) = Create([Pending(OrdersTable)]);

        (await service.GetInboxAsync(ContextFor("user:mallory"), TenantA, null, 50)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Requesters_own_request_is_not_in_their_inbox()
    {
        var (service, _) = Create([Pending(OrdersTable, requester: "user:owner")]);

        (await service.GetInboxAsync(ContextFor("user:owner", groups: [SalesOwners]), TenantA, null, 50)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Degraded_group_resolution_yields_an_empty_inbox()
    {
        var (service, _) = Create([Pending(OrdersTable)]);

        var page = await service.GetInboxAsync(
            ContextFor("user:owner", groups: [SalesOwners], groupStatus: GroupResolutionStatus.Degraded), TenantA, null, 50);

        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Request_whose_approvers_cannot_be_resolved_is_never_listed()
    {
        var (service, _) = Create([Pending(OrdersTable)], protectionThrows: true);

        (await service.GetInboxAsync(ContextFor("user:dg", "GovernanceAdmin"), TenantA, null, 50)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Inbox_page_is_capped()
    {
        var many = Enumerable.Range(0, PermissionInboxService.MaxPageSize + 200).Select(_ => Pending(OrdersTable)).ToList();
        var (service, _) = Create(many);

        var page = await service.GetInboxAsync(ContextFor("user:owner", groups: [SalesOwners]), TenantA, null, int.MaxValue);

        page.Items.Count.ShouldBe(PermissionInboxService.MaxPageSize);
    }

    [Fact]
    public async Task Owner_member_and_governance_see_the_container_grants_others_get_nothing()
    {
        var tableIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var (service, queries) = Create([], tableIds);
        var container = new ContainerRef(ContainerKind.Domain, SalesDomain);

        (await service.GetContainerGrantsAsync(ContextFor("user:owner", groups: [SalesOwners]), TenantA, container, null, 50, false)).ShouldNotBeNull();
        (await service.GetContainerGrantsAsync(ContextFor("user:dg", "GovernanceAdmin"), TenantA, container, null, 50, false)).ShouldNotBeNull();
        (await service.GetContainerGrantsAsync(ContextFor("user:mallory"), TenantA, container, null, 50, false)).ShouldBeNull();
        (await service.GetContainerGrantsAsync(ContextFor("user:it", "TenantAdmin"), TenantA, container, null, 50, false)).ShouldBeNull();
        (await service.GetContainerGrantsAsync(ContextFor("user:owner", groups: [SalesOwners]), TenantA, new ContainerRef(ContainerKind.DataProduct, SalesDomain), null, 50, false)).ShouldBeNull();

        queries.Count.ShouldBe(2);
        queries[0].TableIds.ShouldNotBeNull().ShouldBe(tableIds);
        queries[0].GrantKind.ShouldBeNull();
    }

    [Fact]
    public async Task Container_grant_page_size_is_capped()
    {
        var (service, queries) = Create([], [Guid.NewGuid()]);

        await service.GetContainerGrantsAsync(
            ContextFor("user:dg", "GovernanceAdmin"), TenantA, new ContainerRef(ContainerKind.Domain, SalesDomain), null, int.MaxValue, false);

        queries.ShouldHaveSingleItem().PageSize.ShouldBe(PermissionInboxService.MaxPageSize);
    }

    private sealed class StubProtectionProvider(Func<TableIdentifier, EffectiveProtection> get) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(get(table));
    }
}
