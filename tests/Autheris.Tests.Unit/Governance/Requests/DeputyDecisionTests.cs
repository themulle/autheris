using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Governance.Requests.DeputyTestSupport;

namespace Autheris.Tests.Unit.Governance.Requests;

/// <summary>Issue #1126: a deputy decides through the engine (audited on behalf of B), never self-approves, and sees B's requests in the inbox.</summary>
[Trait("Category", "Security")]
public sealed class DeputyDecisionTests
{
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private readonly InMemoryDeputyRelationRepository _relations = new();
    private readonly FakeTimeProvider _time = new(Now);

    private ConsentRequest Pending(string requester) => new()
    {
        Id = Guid.NewGuid(),
        TableId = Guid.NewGuid(),
        TableIdentifier = OrdersTable,
        RequesterSid = new Sid(requester),
        RequestedGranteeType = GranteeType.User,
        RequestedGranteeRef = requester,
        BusinessJustification = "Quarterly financial order audit.",
        Status = "PENDING",
        RequestedAt = Now.AddHours(-1),
        RequestedValidFrom = Now,
        RequestedValidTo = Now.AddDays(30),
        UpdatedAt = Now.AddHours(-1),
        TenantId = TenantA,
        Version = 1,
        RequestKind = ConsentRequestKind.Data,
        GrantKind = PermissionGrantKind.Data
    };

    private ApproverEligibility Eligibility() =>
        new(new GovernanceCapabilities(domainRepository: DomainRepository()), new DeputyAuthority(_relations, Directory(), _time));

    private ApproverResolver Resolver() => new(DomainRepository(), protectionSnapshotProvider: new FixedProtection());

    [Fact]
    public async Task Deputy_rejects_for_the_owner_member_and_the_audit_names_the_principal()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(5)));
        var request = Pending("user:requester");
        var audit = new InMemoryAuditWriter();
        var engine = Engine(request, audit);

        var result = await engine.DecideAsync(new PermissionDecisionCommand(request.Id, 1, ContextFor(Alice), Approve: false, Reason: "Not needed for the close."));

        result.Request.Status.ShouldBe("REJECTED");
        var entry = audit.Entries.Single(e => e.EventType == AuditEventTypes.PermissionRejected);
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        details.RootElement.GetProperty("OnBehalfOf").GetString().ShouldBe(Bob.Reference);
        details.RootElement.GetProperty("Capacity").GetInt32().ShouldBe((int)ApproverCapacity.DataOwner);
    }

    [Fact]
    public async Task Deputy_cannot_decide_its_own_request()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(5)));
        var request = Pending(Alice.ObjectSid);
        var engine = Engine(request, new InMemoryAuditWriter());

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() =>
            engine.DecideAsync(new PermissionDecisionCommand(request.Id, 1, ContextFor(Alice), Approve: false, Reason: "Self decision attempt.")));
        ex.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task Deputy_outside_the_window_is_refused_by_the_engine()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(1), Now.AddDays(5)));
        var request = Pending("user:requester");
        var engine = Engine(request, new InMemoryAuditWriter());

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() =>
            engine.DecideAsync(new PermissionDecisionCommand(request.Id, 1, ContextFor(Alice), Approve: false, Reason: "Too early to decide.")));
        ex.ErrorCode.ShouldBe("FORBIDDEN");
    }

    [Fact]
    public async Task Inbox_lists_the_principals_requests_for_a_deputy_without_groups()
    {
        _relations.Rows.Add(Relation(Alice, Bob, Now.AddDays(-1), Now.AddDays(5)));
        var mine = Pending("user:requester");
        var bobs = Pending(Bob.ObjectSid);
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, _) =>
            m.Name == nameof(IConsentApprovalRepository.QueryTenantConsentRequestsAsync)
                ? Task.FromResult(new ConsentRequestPage([mine, bobs], null))
                : NullFake.UseDefault);
        var inbox = new PermissionInboxService(
            approvals,
            NullFake.Create<IConsentRepository>((_, _) => NullFake.UseDefault),
            Resolver(),
            Eligibility(),
            new SeparationOfDutiesService(),
            new GovernanceCapabilities(domainRepository: DomainRepository()),
            DomainRepository(),
            _time,
            deputies: new DeputyAuthority(_relations, Directory(), _time),
            deputyRelations: _relations);

        var page = await inbox.GetInboxAsync(ContextFor(Alice), TenantA, null, 50);
        page.Items.ShouldHaveSingleItem().Request.Id.ShouldBe(mine.Id, "never B's own request");

        _relations.Rows.Clear();
        (await inbox.GetInboxAsync(ContextFor(Alice), TenantA, null, 50)).Items.ShouldBeEmpty();
    }

    private PermissionRequestEngine Engine(ConsentRequest request, InMemoryAuditWriter audit)
    {
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
        {
            if (m.Name != nameof(IConsentApprovalRepository.TryDecideAsync))
            {
                return NullFake.UseDefault;
            }

            var decide = (Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>>)args![2]!;
            var apply = (Func<IGovernanceTransaction, PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<GrantRef>>>)args[3]!;
            return Task.Run(async () =>
            {
                var tx = NullFake.Create<IGovernanceTransaction>((_, _) => NullFake.UseDefault);
                var record = await decide(tx, request);
                var updated = new ConsentRequest
                {
                    Id = request.Id,
                    TableId = request.TableId,
                    TableIdentifier = request.TableIdentifier,
                    RequesterSid = request.RequesterSid,
                    RequestedGranteeType = request.RequestedGranteeType,
                    RequestedGranteeRef = request.RequestedGranteeRef,
                    BusinessJustification = request.BusinessJustification,
                    Status = record.NewStatus,
                    RequestedAt = request.RequestedAt,
                    RequestedValidTo = record.ApprovedValidTo,
                    TenantId = request.TenantId,
                    Version = 2,
                    RequestKind = request.RequestKind,
                    GrantKind = request.GrantKind,
                    DecidedBy = record.DecidedBy,
                    DecidedAt = record.DecidedAt
                };
                await apply(tx, record, updated);
                return new DecisionCasResult(DecisionCasOutcome.Applied, updated, []);
            });
        });

        return new PermissionRequestEngine(
            approvals,
            audit,
            Resolver(),
            Eligibility(),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            [],
            timeProvider: _time);
    }

    private sealed class FixedProtection : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(new EffectiveProtection(
                new ContainerRef(ContainerKind.Domain, DomainId),
                OwnerGroupSid,
                ProtectionClass.Confidential,
                ProtectionClass.Confidential,
                null,
                new Dictionary<string, ColumnProtection>(),
                CatalogVisibilityMode.Open));
    }
}
