namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SR-L-06: confirmer binding at apply, bounded plan state, demo directory gating.</summary>
[Trait("Category", "Security")]
public sealed class AccessPlanBindingAndBoundsTests
{
    private const string AdminA = "S-1-5-21-1-500";
    private const string AdminB = "S-1-5-21-1-501";
    private const string DatasetId = "sales.public.orders";

    private readonly IRebacStore _rebac = Substitute.For<IRebacStore>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly ITotpSecretStore _secrets = new InMemoryTotpSecretStore();
    private readonly TotpVerificationService _totp = new();
    private readonly MutableTimeProvider _time = new();

    private AccessPlanningService CreateService(AccessPlanOptions? options = null, IHostEnvironment? env = null)
        => new(
            _audit,
            Substitute.For<ITableMetadataRepository>(),
            _rebac,
            _totp,
            _secrets,
            planOptions: Options.Create(options ?? new AccessPlanOptions()),
            hostEnvironment: env,
            timeProvider: _time);

    private AdminPlanAccessRequest Request() => new(
        DatasetId,
        [new PrincipalAccessGrant("analyst_bob", new Dictionary<string, string> { ["id"] = "clear" }, null, _time.GetUtcNow().AddDays(30))],
        "test");

    private async Task<string> ConfirmAsync(AccessPlanningService service, string planId, string adminSid)
    {
        var enrollment = _totp.GenerateEnrollment(adminSid, "admin@autheris.local");
        await _secrets.SetSecretAsync(adminSid, enrollment.SecretBase32);
        var code = _totp.GenerateTotpCode(enrollment.SecretBase32);
        return (await service.ConfirmPlanAsync(planId, code, adminSid)).ConfirmationToken;
    }

    [Fact]
    public async Task ApplyAccess_TokenFromDifferentAdmin_Throws()
    {
        var service = CreateService();
        var plan = await service.PlanAccessAsync(Request(), AdminA, "tenant-a");
        var token = await ConfirmAsync(service, plan.PlanId, AdminA);

        await Should.ThrowAsync<SecurityException>(() =>
            service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), AdminB, "tenant-a"));

        await _rebac.DidNotReceiveWithAnyArgs().AddTuplesAsync(default!);

        // The legitimate admin can still apply: the failed attempt did not consume the token.
        var ok = await service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), AdminA, "tenant-a");
        ok.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task ApplyAccess_TenantMismatch_Throws()
    {
        var service = CreateService();
        var plan = await service.PlanAccessAsync(Request(), AdminA, "tenant-a");
        var token = await ConfirmAsync(service, plan.PlanId, AdminA);

        await Should.ThrowAsync<SecurityException>(() =>
            service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), AdminA, "tenant-b"));

        await _rebac.DidNotReceiveWithAnyArgs().AddTuplesAsync(default!);
    }

    [Fact]
    public async Task ApplyAccess_SidContainingColons_RoundTrips()
    {
        const string colonSid = "corp:S-1-5-21:500";
        var service = CreateService();
        var plan = await service.PlanAccessAsync(Request(), colonSid, "tenant-a");
        var token = await ConfirmAsync(service, plan.PlanId, colonSid);

        var ok = await service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), colonSid, "tenant-a");
        ok.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task PlanAccess_ExceedsPerAdminCap_Throws()
    {
        var service = CreateService(new AccessPlanOptions { MaxPendingPlansPerAdmin = 2 });

        await service.PlanAccessAsync(Request(), AdminA);
        await service.PlanAccessAsync(Request(), AdminA);

        await Should.ThrowAsync<SecurityException>(() => service.PlanAccessAsync(Request(), AdminA));

        // Another administrator is unaffected by the cap of the first.
        (await service.PlanAccessAsync(Request(), AdminB)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ExpiredPlans_AreSweptFromStore()
    {
        var service = CreateService(new AccessPlanOptions { PlanTtlMinutes = 5, SweepIntervalSeconds = 1 });
        var plan = await service.PlanAccessAsync(Request(), AdminA, "tenant-a");
        var token = await ConfirmAsync(service, plan.PlanId, AdminA);
        await service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), AdminA, "tenant-a");

        service.PlanCount.ShouldBe(1);
        service.ConsumedTokenCount.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(6));
        service.SweepExpired(force: true);

        service.PlanCount.ShouldBe(0);
        service.ConsumedTokenCount.ShouldBe(0);
    }

    [Fact]
    public async Task PlanAccess_OpportunisticallyPrunesExpiredPlans_SoCapFrees()
    {
        var service = CreateService(new AccessPlanOptions { MaxPendingPlansPerAdmin = 1, PlanTtlMinutes = 1, SweepIntervalSeconds = 1 });
        await service.PlanAccessAsync(Request(), AdminA);

        _time.Advance(TimeSpan.FromMinutes(2));

        await service.PlanAccessAsync(Request(), AdminA);
        service.PlanCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResolvePrincipal_NonDevelopment_DoesNotReturnDemoDirectory()
    {
        var ownership = Substitute.For<IDataOwnershipRepository>();
        var prod = Substitute.For<IHostEnvironment>();
        prod.EnvironmentName.Returns("Production");
        var service = new AccessPlanningService(
            _audit, dataOwnershipRepository: ownership, hostEnvironment: prod, timeProvider: _time);

        var result = await service.ResolvePrincipalAsync(new AdminResolvePrincipalRequest("a"), AdminA);

        result.Matches.ShouldBeEmpty();
        await ownership.DidNotReceiveWithAnyArgs().GetDataOwnersForTableAsync(default!, default);
    }

    [Fact]
    public async Task ResolvePrincipal_NoHostEnvironment_DoesNotReturnDemoDirectory()
    {
        var service = CreateService();
        var result = await service.ResolvePrincipalAsync(new AdminResolvePrincipalRequest("David"), AdminA);
        result.Matches.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolvePrincipal_Development_ReturnsDemoDirectory()
    {
        var dev = Substitute.For<IHostEnvironment>();
        dev.EnvironmentName.Returns("Development");
        var service = CreateService(env: dev);

        var result = await service.ResolvePrincipalAsync(new AdminResolvePrincipalRequest("David"), AdminA);
        result.Matches.ShouldNotBeEmpty();
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
