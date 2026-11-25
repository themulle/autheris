using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

using Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Issue #1121 against the real persistence of both engines: the first approval of a grant on secret data records a step (with the approver's
/// identifier forms) under the version CAS and creates no grant; a second, different approver completes it exactly once; the same person
/// is refused under another identifier form; one rejection ends it; a failed audit records nothing.
/// </summary>
public abstract class SecretFourEyesStoreTestsBase : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    protected GovernanceTestStore Store { get; private set; } = null!;

    protected abstract Task<GovernanceTestStore> CreateStoreAsync();

    public async Task InitializeAsync() => Store = await CreateStoreAsync();

    public async Task DisposeAsync()
    {
        if (Store is not null)
        {
            await Store.DisposeAsync();
        }
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Secret_grant_needs_two_distinct_approvals_on_the_real_store()
    {
        if (!Store.IsAvailable) return;
        var (engine, applier, request) = await ArrangeAsync("four_eyes");

        var first = await engine.DecideAsync(Approve(request.Id, 1, Dg("S-1-5-21-100-200-300-1001", new Claim(ClaimTypes.Upn, "dg.one@corp.example"))));
        first.Request.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        first.Grants.ShouldBeEmpty();
        await applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);

        var stored = (await Store.ConsentApprovals.GetConsentRequestAsync(request.Id)).ShouldNotBeNull();
        stored.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        stored.Version.ShouldBe(2);
        stored.DecidedBy.ShouldBeNull();
        var step = stored.ApprovalSteps.ShouldHaveSingleItem();
        step.StepNumber.ShouldBe(1);
        step.ApproverIdentifiers.ShouldContain("dg.one@corp.example");

        // The same person under another identifier form is refused; the request stays awaiting the second approval.
        var same = await Should.ThrowAsync<ConsentDecisionException>(() => engine.DecideAsync(Approve(request.Id, 2, Dg("dg.one@corp.example"))));
        same.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);

        var second = await engine.DecideAsync(Approve(request.Id, 2, Dg("S-1-5-21-100-200-300-1002")));
        second.Request.Status.ShouldBe("APPROVED");
        await applier.ReceivedWithAnyArgs(1).ApplyAsync(default!, default!, default);

        var done = (await Store.ConsentApprovals.GetConsentRequestAsync(request.Id)).ShouldNotBeNull();
        done.Status.ShouldBe("APPROVED");
        done.DecidedBy.ShouldBe(new Sid("S-1-5-21-100-200-300-1002"));
        done.ApprovalSteps.Select(s => s.StepNumber).ShouldBe([1, 2]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Concurrent_second_approvals_complete_exactly_once()
    {
        if (!Store.IsAvailable) return;
        var (engine, applier, request) = await ArrangeAsync("four_eyes_race");
        await engine.DecideAsync(Approve(request.Id, 1, Dg("S-1-5-21-100-200-300-2001")));

        var outcomes = await Task.WhenAll(
            Outcome(engine.DecideAsync(Approve(request.Id, 2, Dg("S-1-5-21-100-200-300-2002")))),
            Outcome(engine.DecideAsync(Approve(request.Id, 2, Dg("S-1-5-21-100-200-300-2003")))));

        outcomes.Count(o => o == "APPROVED").ShouldBe(1);
        outcomes.Count(o => o == "REQUEST_ALREADY_DECIDED").ShouldBe(1);
        await applier.ReceivedWithAnyArgs(1).ApplyAsync(default!, default!, default);
        (await Store.ConsentApprovals.GetConsentRequestAsync(request.Id))!.ApprovalSteps.Count.ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Concurrent_approvals_by_the_same_person_never_complete()
    {
        if (!Store.IsAvailable) return;
        var (engine, applier, request) = await ArrangeAsync("four_eyes_same");

        var outcomes = await Task.WhenAll(
            Outcome(engine.DecideAsync(Approve(request.Id, 1, Dg("S-1-5-21-100-200-300-3001")))),
            Outcome(engine.DecideAsync(Approve(request.Id, 1, Dg("S-1-5-21-100-200-300-3001")))));

        outcomes.ShouldNotContain("APPROVED");
        (await Store.ConsentApprovals.GetConsentRequestAsync(request.Id))!.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        await applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task One_rejection_after_the_first_approval_ends_the_request()
    {
        if (!Store.IsAvailable) return;
        var (engine, applier, request) = await ArrangeAsync("four_eyes_reject");
        await engine.DecideAsync(Approve(request.Id, 1, Dg("S-1-5-21-100-200-300-4001")));

        var rejected = await engine.DecideAsync(new PermissionDecisionCommand(
            request.Id, 2, Dg("S-1-5-21-100-200-300-4002"), Approve: false, Reason: "The access is broader than the task needs."));
        rejected.Request.Status.ShouldBe("REJECTED");

        var late = await Should.ThrowAsync<ConsentDecisionException>(() => engine.DecideAsync(Approve(request.Id, 3, Dg("S-1-5-21-100-200-300-4003"))));
        late.StatusCode.ShouldBe(409);
        await applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Failed_audit_of_the_first_approval_records_no_step()
    {
        if (!Store.IsAvailable) return;
        var audit = Substitute.For<IAuditWriter>();
        audit.RecordRequiredAsync(default!, default).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("audit down")));
        var (engine, _, request) = await ArrangeAsync("four_eyes_audit", audit);

        await Should.ThrowAsync<InvalidOperationException>(() => engine.DecideAsync(Approve(request.Id, 1, Dg("S-1-5-21-100-200-300-5001"))));

        var reloaded = (await Store.ConsentApprovals.GetConsentRequestAsync(request.Id)).ShouldNotBeNull();
        reloaded.Status.ShouldBe("PENDING");
        reloaded.Version.ShouldBe(1);
        reloaded.ApprovalSteps.ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task Inbox_page_query_lists_pending_and_awaiting_second_approval()
    {
        if (!Store.IsAvailable) return;
        var (engine, _, awaiting) = await ArrangeAsync("four_eyes_inbox");
        await engine.DecideAsync(Approve(awaiting.Id, 1, Dg("S-1-5-21-100-200-300-6001")));
        var pending = await CreateRequestAsync((await Store.TableMetadata.GetTableMetadataAsync(awaiting.TableIdentifier))!);

        var page = await Store.ConsentApprovals.QueryTenantConsentRequestsAsync(
            new ConsentRequestPageQuery(Store.TenantA, "PENDING", Limit: 200, AdditionalStatus: SecretFourEyesRule.PendingSecondApproval));
        var onlyPending = await Store.ConsentApprovals.QueryTenantConsentRequestsAsync(new ConsentRequestPageQuery(Store.TenantA, "PENDING", Limit: 200));

        page.Items.Select(r => r.Id).ShouldContain(awaiting.Id);
        page.Items.Select(r => r.Id).ShouldContain(pending.Id);
        onlyPending.Items.Select(r => r.Id).ShouldNotContain(awaiting.Id);
    }

    private static PermissionDecisionCommand Approve(Guid id, int version, SecurityPrincipalContext decider) =>
        new(id, version, decider, Approve: true);

    private static async Task<string> Outcome(Task<PermissionDecisionResult> decision)
    {
        try
        {
            return (await decision).Request.Status;
        }
        catch (ConsentDecisionException ex)
        {
            return ex.ErrorCode;
        }
    }

    private SecurityPrincipalContext Dg(string sid, params Claim[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new("tenant_id", Store.TenantA.Value), new(ClaimTypes.Role, "GovernanceAdmin") };
        claims.AddRange(extra);
        return new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = Store.TenantA,
            ResolvedTenant = Store.TenantA,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(["GovernanceAdmin"], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };
    }

    private async Task<(PermissionRequestEngine Engine, IGrantEffectApplier Applier, ConsentRequest Request)> ArrangeAsync(string prefix, IAuditWriter? audit = null)
    {
        var table = new TableIdentifier("sales", "dbo", prefix + "_" + Guid.NewGuid().ToString("N")[..8]);
        var meta = await Store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = table,
            OwnerTenantId = Store.TenantA.Value,
            Table = new Table { Id = Guid.NewGuid(), SourceName = table.Domain, SchemaName = table.Schema, TableName = table.TableName, IsActive = true },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" }]
        });

        var protection = new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), null, ProtectionClass.Secret, ProtectionClass.Secret, null,
            new Dictionary<string, ColumnProtection>(), CatalogVisibilityMode.Open);
        var provider = new FixedProtectionProvider(protection);
        var applier = Substitute.For<IGrantEffectApplier>();
        applier.RequestKind.Returns(ConsentRequestKind.Data);
        applier.ApplyAsync(default!, default!, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<GrantRef>>([new GrantRef(Guid.NewGuid(), PermissionGrantKind.Data, table)]));

        var engine = new PermissionRequestEngine(
            Store.ConsentApprovals,
            audit ?? Substitute.For<IAuditWriter>(),
            new ApproverResolver(protectionSnapshotProvider: provider),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            [applier],
            protectionProvider: provider,
            timeProvider: new FakeTimeProvider(Now));

        return (engine, applier, await CreateRequestAsync(meta));
    }

    private Task<ConsentRequest> CreateRequestAsync(TableMetadata meta) =>
        Store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = meta.Table.Id,
            TableIdentifier = meta.Identifier,
            RequesterSid = new Sid("S-1-5-21-100-200-300-9001"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-100-200-300-9001",
            BusinessJustification = "Quarter close needs the secret payroll figures.",
            Status = "PENDING",
            RequestedAt = Now.AddMinutes(-5),
            RequestedValidFrom = Now,
            RequestedValidTo = Now.AddDays(30),
            TenantId = Store.TenantA
        });
}

internal sealed class FixedProtectionProvider(EffectiveProtection protection) : IProtectionSnapshotProvider
{
    public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) => ValueTask.FromResult(protection);
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlSecretFourEyesStoreTests(GovernancePostgresFixture fixture) : SecretFourEyesStoreTestsBase
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() => GovernanceTestStore.CreatePostgreSqlAsync(fixture, "secret_four_eyes");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerSecretFourEyesStoreTests(GovernanceSqlServerFixture fixture) : SecretFourEyesStoreTestsBase
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() => GovernanceTestStore.CreateSqlServerAsync(fixture, "secret_four_eyes");
}
