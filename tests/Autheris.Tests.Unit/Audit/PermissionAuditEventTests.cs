using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

[Trait("Category", "Security")]
public sealed class PermissionAuditEventTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    [Fact]
    public void Each_event_has_required_fields_and_no_values_or_secrets()
    {
        var details = new PermissionAuditDetails(
            Actor: "user:approver",
            Subject: "user:alice",
            TargetObject: OrdersTable.ToString(),
            ValidFrom: Now,
            ValidTo: Now.AddDays(30),
            Justification: "Need access for invoice audit.",
            Capacity: ApproverCapacity.DataOwner,
            SelfGrant: false,
            RequestId: Guid.NewGuid(),
            ActorKind: PermissionAuditActorKind.User,
            GrantKind: "DATA");

        var json = details.ToDetailsJson();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("Actor").GetString().ShouldBe("user:approver");
        doc.RootElement.GetProperty("Subject").GetString().ShouldBe("user:alice");
        doc.RootElement.GetProperty("Object").GetString().ShouldBe(OrdersTable.ToString());
        doc.RootElement.GetProperty("ValidFrom").GetDateTimeOffset().ShouldBe(Now);
        doc.RootElement.GetProperty("ValidTo").GetDateTimeOffset().ShouldBe(Now.AddDays(30));
        doc.RootElement.GetProperty("Justification").GetString().ShouldBe("Need access for invoice audit.");
        doc.RootElement.GetProperty("Capacity").GetInt32().ShouldBe((int)ApproverCapacity.DataOwner);
        doc.RootElement.GetProperty("ActorKind").GetString().ShouldBe("User");

        json.ShouldNotContain("Bearer");
        json.ShouldNotContain("https://hooks.");
    }

    [Fact]
    public async Task Actor_kind_service_principal_recorded()
    {
        var audit = new InMemoryAuditWriter();
        var pending = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = OrdersTable,
            RequesterSid = new Sid("user:alice"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "user:alice",
            BusinessJustification = "Pipeline integration access.",
            Status = "PENDING",
            RequestedAt = Now.AddMinutes(-5),
            RequestedValidFrom = Now,
            RequestedValidTo = Now.AddDays(30),
            TenantId = TenantA,
            Version = 1,
            RequestKind = ConsentRequestKind.Data,
            GrantKind = PermissionGrantKind.Data
        };

        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.TryDecideAsync))
            {
                var decideFn = (Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>>)args![2]!;
                var applyFn = (Func<IGovernanceTransaction, PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<GrantRef>>>)args[3]!;
                return Task.Run(async () =>
                {
                    var tx = NullFake.Create<IGovernanceTransaction>();
                    var rec = await decideFn(tx, pending);
                    await applyFn(tx, rec, pending);
                    return new DecisionCasResult(DecisionCasOutcome.Applied, pending);
                });
            }

            return NullFake.UseDefault;
        });

        var engine = new PermissionRequestEngine(
            approvals,
            audit,
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            [new FakeNoOpApplier()],
            timeProvider: new FakeTimeProvider(Now));

        var spPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new(ClaimTypes.PrimarySid, "sp:worker-approver"),
            new("appid", "worker-approver"),
            new("idtyp", "app"),
            new("tenant_id", TenantA.Value),
            new(ClaimTypes.Role, "GovernanceAdmin")
        ], "Bearer"));

        var spContext = new SecurityPrincipalContext
        {
            UserSid = new Sid("sp:worker-approver"),
            TenantId = TenantA,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(["GovernanceAdmin"], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = spPrincipal
        };

        await engine.DecideAsync(new PermissionDecisionCommand(pending.Id, 1, spContext, Approve: true));

        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.PermissionApproved);
        entry.DetailsJson.ShouldContain("\"ActorKind\":\"ServicePrincipal\"");
    }

    private sealed class FakeNoOpApplier : IGrantEffectApplier
    {
        public ConsentRequestKind RequestKind => ConsentRequestKind.Data;
        public Task<IReadOnlyList<GrantRef>> ApplyAsync(IGovernanceTransaction transaction, ApprovedPermission approved, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GrantRef>>([new GrantRef(Guid.NewGuid(), PermissionGrantKind.Data)]);
    }
}
