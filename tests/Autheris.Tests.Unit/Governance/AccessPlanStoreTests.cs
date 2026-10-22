namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.State;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>ARC-C-05 / SR-L-06 Part C: access plans, confirmation tokens and secrets in shared state with TTL.</summary>
[Trait("Category", "Security")]
public sealed class AccessPlanStoreTests
{
    private const string Admin = "S-1-5-21-1-500";
    private const string DatasetId = "sales.public.orders";

    private readonly IRebacStore _rebac = Substitute.For<IRebacStore>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly ITotpSecretStore _secrets = new InMemoryTotpSecretStore();
    private readonly TotpVerificationService _totp = new();

    private static AdminPlanAccessRequest Request() => new(
        DatasetId,
        [new PrincipalAccessGrant("analyst_bob", new Dictionary<string, string> { ["id"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(30))],
        "test");

    private static IKeyVaultSecretProvider SigningProvider()
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes("accessplan-signing").Returns(Encoding.UTF8.GetBytes("shared-signing-secret-of-the-cluster"));
        return provider;
    }

    private AccessPlanningService Node(IAccessPlanStore store, IKeyVaultSecretProvider? provider = null, AccessPlanOptions? options = null)
        => new(
            _audit,
            Substitute.For<ITableMetadataRepository>(),
            _rebac,
            _totp,
            _secrets,
            secretProvider: provider ?? SigningProvider(),
            planOptions: Options.Create(options ?? new AccessPlanOptions { SigningKeyRef = "accessplan-signing" }),
            accessPlanStore: store);

    private async Task<string> ConfirmAsync(AccessPlanningService service, string planId)
    {
        var enrollment = _totp.GenerateEnrollment(Admin, "admin@autheris.local");
        await _secrets.SetSecretAsync(Admin, enrollment.SecretBase32);
        var code = _totp.GenerateTotpCode(enrollment.SecretBase32);
        return (await service.ConfirmPlanAsync(planId, code, Admin)).ConfirmationToken;
    }

    [Fact]
    public async Task TwoNodes_PlanCreatedOnA_ConfirmedAndAppliedOnB()
    {
        var state = new InMemoryClusterStateProvider();
        var nodeA = Node(new ClusterAccessPlanStore(state));
        var nodeB = Node(new ClusterAccessPlanStore(state));

        var plan = await nodeA.PlanAccessAsync(Request(), Admin, "tenant-a");
        var token = await ConfirmAsync(nodeB, plan.PlanId);
        var result = await nodeB.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, "tenant-a");

        result.Success.ShouldBeTrue();
        (await nodeA.GetPlanAsync(plan.PlanId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ConsumedTokenOnA_IsRejectedOnB()
    {
        var state = new InMemoryClusterStateProvider();
        var nodeA = Node(new ClusterAccessPlanStore(state));
        var nodeB = Node(new ClusterAccessPlanStore(state));

        var plan = await nodeA.PlanAccessAsync(Request(), Admin, "tenant-a");
        var token = await ConfirmAsync(nodeA, plan.PlanId);
        await nodeA.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, "tenant-a");

        await Should.ThrowAsync<SecurityException>(() =>
            nodeB.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, "tenant-a"));
    }

    [Fact]
    public async Task ClusterStore_DoesNotPersistTokenInPlainText()
    {
        var state = new InMemoryClusterStateProvider();
        var node = Node(new ClusterAccessPlanStore(state));
        var plan = await node.PlanAccessAsync(Request(), Admin);
        var token = await ConfirmAsync(node, plan.PlanId);

        var stored = await state.GetAsync<StoredAccessPlan>("accessplan:plan:" + plan.PlanId);

        stored.ShouldNotBeNull();
        stored!.ConfirmedTokenHash.ShouldNotBeNullOrEmpty();
        stored.ConfirmedTokenHash.ShouldNotBe(token);
    }

    [Fact]
    public async Task NodesWithDifferentSigningKeys_RejectEachOthersTokens()
    {
        var state = new InMemoryClusterStateProvider();
        var otherProvider = Substitute.For<IKeyVaultSecretProvider>();
        otherProvider.GetSecretBytes("accessplan-signing").Returns(Encoding.UTF8.GetBytes("a-different-secret"));
        var nodeA = Node(new ClusterAccessPlanStore(state));
        var nodeB = Node(new ClusterAccessPlanStore(state), otherProvider);

        var plan = await nodeA.PlanAccessAsync(Request(), Admin, "tenant-a");
        var token = await ConfirmAsync(nodeA, plan.PlanId);

        await Should.ThrowAsync<SecurityException>(() =>
            nodeB.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, "tenant-a"));
    }

    [Fact]
    public async Task ExpiredPlans_AreEvicted()
    {
        var time = new MutableTimeProvider();
        var store = new InMemoryAccessPlanStore(time, TimeSpan.FromSeconds(1));
        var plan = new StoredAccessPlan(
            new AdminPlanAccessResult("plan_x", DatasetId, [], [], time.GetUtcNow().AddMinutes(5)), Request(), Admin);

        (await store.TrySavePlanAsync(plan, TimeSpan.FromMinutes(5), 10)).ShouldBeTrue();
        (await store.TryConsumeTokenAsync("hash", TimeSpan.FromMinutes(5))).ShouldBeTrue();
        store.PlanCount.ShouldBe(1);

        time.Advance(TimeSpan.FromMinutes(6));
        store.Sweep(force: true);

        store.PlanCount.ShouldBe(0);
        store.ConsumedTokenCount.ShouldBe(0);
        (await store.GetPlanAsync("plan_x")).ShouldBeNull();
    }

    [Fact]
    public async Task InMemoryStore_TokenConsumedOnlyOnce()
    {
        var store = new InMemoryAccessPlanStore();
        (await store.TryConsumeTokenAsync("h", TimeSpan.FromMinutes(1))).ShouldBeTrue();
        (await store.TryConsumeTokenAsync("h", TimeSpan.FromMinutes(1))).ShouldBeFalse();
    }

    [Fact]
    public async Task ClusterStore_PerAdminCap_IsEnforcedAcrossNodes()
    {
        var state = new InMemoryClusterStateProvider();
        var options = new AccessPlanOptions { MaxPendingPlansPerAdmin = 2, SigningKeyRef = "accessplan-signing" };
        var nodeA = Node(new ClusterAccessPlanStore(state), options: options);
        var nodeB = Node(new ClusterAccessPlanStore(state), options: options);

        await nodeA.PlanAccessAsync(Request(), Admin);
        await nodeB.PlanAccessAsync(Request(), Admin);

        await Should.ThrowAsync<SecurityException>(() => nodeA.PlanAccessAsync(Request(), Admin));
    }

    [Fact]
    public async Task Apply_WhenStateUnavailable_Throws()
    {
        var state = new InMemoryClusterStateProvider();
        var plan = await Node(new ClusterAccessPlanStore(state)).PlanAccessAsync(Request(), Admin, "tenant-a");

        var broken = Substitute.For<IDistributedClusterStateProvider>();
        broken.GetAsync<StoredAccessPlan>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<StoredAccessPlan?>(new ClusterStateUnavailableException()));
        var node = Node(new ClusterAccessPlanStore(broken));

        await Should.ThrowAsync<ClusterStateUnavailableException>(() =>
            node.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, "token"), Admin, "tenant-a"));
        await _rebac.DidNotReceiveWithAnyArgs().AddTuplesAsync(default!);
    }

    [Fact]
    public async Task ClusterStore_TokenConsumption_WhenIncrementUnavailable_Throws()
    {
        var broken = Substitute.For<IDistributedClusterStateProvider>();
        broken.IncrementAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<long?>(null));
        var store = new ClusterAccessPlanStore(broken);

        await Should.ThrowAsync<ClusterStateUnavailableException>(async () =>
            await store.TryConsumeTokenAsync("h", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ClusterWide_WithoutSigningKeyRef_FailsFast()
    {
        var topology = new ClusterStateTopology(ClusterStateMode.External);

        Should.Throw<InvalidOperationException>(() => new AccessPlanningService(
            _audit,
            options: null,
            planOptions: Options.Create(new AccessPlanOptions()),
            clusterTopology: topology));
    }

    [Fact]
    public async Task RegisterDatasource_WhenVaultReadOnly_Rejects()
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.When(p => p.SetSecret(Arg.Any<string>(), Arg.Any<byte[]>())).Do(_ => throw new NotSupportedException());
        var service = Node(new InMemoryAccessPlanStore(), provider, new AccessPlanOptions());
        var request = new AdminRegisterDatasourceRequest("crane", "telemetry",
            Auth: new DatasourceAuthDto("apiKey", Secret: "plain-secret"));

        await Should.ThrowAsync<InvalidOperationException>(() => service.RegisterDatasourceAsync(request, Admin));
    }

    [Fact]
    public async Task RegisterDatasource_InlineSecret_IsWrittenToProviderOnly()
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        var service = Node(new InMemoryAccessPlanStore(), provider, new AccessPlanOptions());
        var request = new AdminRegisterDatasourceRequest("crane", "telemetry",
            Auth: new DatasourceAuthDto("apiKey", Secret: "plain-secret"));

        var result = await service.RegisterDatasourceAsync(request, Admin);

        result.SecretRef.ShouldBe("datasource:telemetry:crane:auth_secret");
        provider.Received(1).SetSecret("datasource:telemetry:crane:auth_secret", Arg.Any<byte[]>());
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
