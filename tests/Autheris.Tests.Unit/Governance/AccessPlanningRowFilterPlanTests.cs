using System.Security;
using System.Text;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #107 (SEC-RF107-14b, c): row filter evaluation plans in the two-phase flow (kind separation, optimistic concurrency, TOTP).</summary>
[Trait("Category", "Security")]
public sealed class AccessPlanningRowFilterPlanTests
{
    private const string Admin = "S-1-5-21-1-500";
    private const string OtherAdmin = "S-1-5-21-1-501";
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private static readonly Guid RuleId = Guid.NewGuid();
    private static readonly RowFilterEvaluationSettings Stored = new(RowFilterEvaluationStrategy.Exists, 30, null);

    private readonly IRowFilterEvaluationAdminService _admin = Substitute.For<IRowFilterEvaluationAdminService>();
    private readonly ITableMetadataRepository _tables = Substitute.For<ITableMetadataRepository>();
    private readonly ITotpSecretStore _secrets = new InMemoryTotpSecretStore();
    private readonly TotpVerificationService _totp = new();
    private readonly InMemoryAccessPlanStore _store = new();
    private readonly AccessPlanningService _service;

    public AccessPlanningRowFilterPlanTests()
    {
        var keyVault = Substitute.For<IKeyVaultSecretProvider>();
        keyVault.GetSecretBytes("accessplan-signing").Returns(Encoding.UTF8.GetBytes("signing-secret-for-the-row-filter-plan-tests"));
        _tables.GetActiveTableIdentifierAsync(Orders, Arg.Any<CancellationToken>()).Returns(Orders);
        _admin.ValidateRuleAsync(RuleId, new TenantId(Tenant), Arg.Any<RowFilterEvaluationSettings>(), Arg.Any<CancellationToken>())
            .Returns(new RowFilterEvaluationPreview(Orders, Stored));
        _admin.ValidateObjectAsync(Orders, Arg.Any<RowFilterEvaluationSettings?>(), Arg.Any<CancellationToken>())
            .Returns(new RowFilterEvaluationPreview(Orders, Stored));
        _service = new AccessPlanningService(
            Substitute.For<IAuditLogRepository>(), _tables, Substitute.For<IRebacStore>(), _totp, _secrets,
            secretProvider: keyVault,
            planOptions: Options.Create(new AccessPlanOptions { SigningKeyRef = "accessplan-signing" }),
            accessPlanStore: _store,
            rowFilterEvaluationAdmin: _admin);
    }

    private static AdminPlanRowFilterEvaluationRequest RuleRequest(string strategy = "KeySetCache", int? duration = 120, int? keys = 500)
        => new(RuleId.ToString("D"), null, strategy, duration, keys, "tuning");

    private static AdminPlanRowFilterEvaluationRequest ObjectRequest(string strategy = "KeySetCache", int? duration = 120, int? keys = 500)
        => new(null, "sales.dbo.orders", strategy, duration, keys);

    private async Task<string> ConfirmAsync(string planId, string admin = Admin)
    {
        var enrollment = _totp.GenerateEnrollment(admin, "admin@autheris.local");
        await _secrets.SetSecretAsync(admin, enrollment.SecretBase32);
        return (await _service.ConfirmPlanAsync(planId, _totp.GenerateTotpCode(enrollment.SecretBase32), admin)).ConfirmationToken;
    }

    private void ChangeReturns(long epoch = 9)
    {
        _admin.SetRuleAsync(RuleId, new TenantId(Tenant), Arg.Any<RowFilterEvaluationSettings>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new RowFilterEvaluationChange(Orders, new TenantId(Tenant), RuleId, Stored, ci.Arg<RowFilterEvaluationSettings>(), epoch));
        _admin.SetObjectAsync(Orders, Arg.Any<RowFilterEvaluationSettings?>(), Arg.Any<Sid>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(ci => new RowFilterEvaluationChange(Orders, null, null, Stored, ci.ArgAt<RowFilterEvaluationSettings?>(1) ?? RowFilterEvaluationSettings.Inherit, epoch));
    }

    // ---- planning ----

    [Fact]
    public async Task Plan_Rule_StoresAKindedPlanWithBeforeAndAfter_AndDoesNotWrite()
    {
        var plan = await _service.PlanRowFilterEvaluationAsync(RuleRequest(), Admin, Tenant);

        plan.Level.ShouldBe("rule");
        plan.RowFilterId.ShouldBe(RuleId.ToString("D"));
        plan.DatasetId.ShouldBe("sales.dbo.orders");
        plan.Before.ShouldBe(new RowFilterEvaluationSettingsDto("Exists", 30, null));
        plan.After.ShouldBe(new RowFilterEvaluationSettingsDto("KeySetCache", 120, 500));
        plan.Warnings.ShouldNotBeEmpty();
        var stored = (await _store.GetPlanAsync(plan.PlanId))!;
        stored.PlanKind.ShouldBe(AccessPlanKind.RowFilterEvaluation);
        stored.RowFilterEvaluation!.Before.ShouldBe(Stored);
        stored.TenantId.ShouldBe(Tenant);
        stored.Request.Grants.ShouldBeEmpty();
        await _admin.DidNotReceiveWithAnyArgs().SetRuleAsync(default, default, default!, default, default);
    }

    [Fact]
    public async Task Plan_Object_ResolvesTheDataset_AndAllNullMeansClear()
    {
        var plan = await _service.PlanRowFilterEvaluationAsync(new AdminPlanRowFilterEvaluationRequest(null, "sales.dbo.orders", null, null, null), Admin, Tenant);

        plan.Level.ShouldBe("object");
        plan.After.ShouldBe(new RowFilterEvaluationSettingsDto(null, null, null));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("keysetcache")]
    [InlineData(" Exists")]
    [InlineData("")]
    public async Task Plan_StrategyThatIsNotAKnownName_IsRejected_SEC_RF107_14b(string strategy)
        => await Should.ThrowAsync<ArgumentException>(() => _service.PlanRowFilterEvaluationAsync(RuleRequest(strategy), Admin, Tenant));

    [Fact]
    public async Task Plan_NeitherOrBothTargets_AreRejected()
    {
        await Should.ThrowAsync<ArgumentException>(() => _service.PlanRowFilterEvaluationAsync(new AdminPlanRowFilterEvaluationRequest(null, null, "Exists", null, null), Admin, Tenant));
        await Should.ThrowAsync<ArgumentException>(() => _service.PlanRowFilterEvaluationAsync(
            new AdminPlanRowFilterEvaluationRequest(RuleId.ToString("D"), "sales.dbo.orders", "Exists", null, null), Admin, Tenant));
    }

    [Fact]
    public async Task Plan_RuleWithoutTenantContext_IsRejected()
        => await Should.ThrowAsync<SecurityException>(() => _service.PlanRowFilterEvaluationAsync(RuleRequest(), Admin, null));

    [Fact]
    public async Task Plan_UnknownRuleOrForeignTenant_GivesTheSameFixedMessage_WithoutEchoingTheId()
    {
        var unknown = Guid.NewGuid();
        _admin.ValidateRuleAsync(unknown, Arg.Any<TenantId>(), Arg.Any<RowFilterEvaluationSettings>(), Arg.Any<CancellationToken>())
            .Returns((RowFilterEvaluationPreview?)null);

        var ex = await Should.ThrowAsync<SecurityException>(() => _service.PlanRowFilterEvaluationAsync(
            new AdminPlanRowFilterEvaluationRequest(unknown.ToString("D"), null, "Exists", null, null), Admin, Tenant));

        ex.Message.ShouldNotContain(unknown.ToString("D"));
    }

    [Fact]
    public async Task Plan_ValidationFailureOfTheAdminService_Propagates()
    {
        _admin.ValidateRuleAsync(RuleId, Arg.Any<TenantId>(), Arg.Any<RowFilterEvaluationSettings>(), Arg.Any<CancellationToken>())
            .Returns<RowFilterEvaluationPreview?>(_ => throw new RowFilterEvaluationValidationException("maxKeys exceeds the configured maximum of 2000."));

        await Should.ThrowAsync<RowFilterEvaluationValidationException>(() => _service.PlanRowFilterEvaluationAsync(RuleRequest(keys: 99999), Admin, Tenant));
    }

    // ---- apply ----

    [Fact]
    public async Task ConfirmedRulePlan_IsApplied_WithActorTenantAndSettings()
    {
        ChangeReturns(epoch: 11);
        var plan = await _service.PlanRowFilterEvaluationAsync(RuleRequest(), Admin, Tenant);
        var token = await ConfirmAsync(plan.PlanId);

        var result = await _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, Tenant);

        result.Success.ShouldBeTrue();
        result.Level.ShouldBe("rule");
        result.NewEpoch.ShouldBe(11);
        await _admin.Received(1).SetRuleAsync(RuleId, new TenantId(Tenant),
            new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 120, 500), new Sid(Admin), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmedObjectPlan_IsApplied_AndClearPassesNull()
    {
        ChangeReturns();
        var plan = await _service.PlanRowFilterEvaluationAsync(new AdminPlanRowFilterEvaluationRequest(null, "sales.dbo.orders", null, null, null), Admin, Tenant);
        var token = await ConfirmAsync(plan.PlanId);

        var result = await _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, Tenant);

        result.Level.ShouldBe("object");
        await _admin.Received(1).SetObjectAsync(Orders, null, new Sid(Admin), new TenantId(Tenant), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanKindMismatch_Rejected_InBothDirections_SEC_RF107_14c()
    {
        ChangeReturns();
        // 1. a row filter plan must not be applied as an access plan.
        var rf = await _service.PlanRowFilterEvaluationAsync(RuleRequest(), Admin, Tenant);
        var rfToken = await ConfirmAsync(rf.PlanId);
        await Should.ThrowAsync<SecurityException>(() => _service.ApplyAccessAsync(new AdminApplyAccessRequest(rf.PlanId, rfToken), Admin, Tenant));

        // 2. an access plan must not be applied as a row filter plan.
        var access = await _service.PlanAccessAsync(new AdminPlanAccessRequest(
            "sales.dbo.orders", [new PrincipalAccessGrant("bob", new Dictionary<string, string> { ["id"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(10))], "x"), OtherAdmin, Tenant);
        // A second administrator: a TOTP code is single use per window.
        var accessToken = await ConfirmAsync(access.PlanId, OtherAdmin);
        await Should.ThrowAsync<SecurityException>(() => _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(access.PlanId, accessToken), OtherAdmin, Tenant));

        await _admin.DidNotReceiveWithAnyArgs().SetRuleAsync(default, default, default!, default, default);
    }

    [Fact]
    public async Task ChangedSincePlan_Rejected_AndTokenNotConsumed_SEC_RF107_14c()
    {
        ChangeReturns();
        var plan = await _service.PlanRowFilterEvaluationAsync(RuleRequest(), Admin, Tenant);
        var token = await ConfirmAsync(plan.PlanId);
        // Somebody changed the stored settings after planning.
        _admin.ValidateRuleAsync(RuleId, new TenantId(Tenant), Arg.Any<RowFilterEvaluationSettings>(), Arg.Any<CancellationToken>())
            .Returns(new RowFilterEvaluationPreview(Orders, new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 999, 1)));

        await Should.ThrowAsync<SecurityException>(() => _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, Tenant));

        await _admin.DidNotReceiveWithAnyArgs().SetRuleAsync(default, default, default!, default, default);
        (await _store.TryConsumeTokenAsync(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token))), TimeSpan.FromMinutes(1))).ShouldBeTrue();
    }

    [Fact]
    public async Task Apply_ValidatesAgainstTheCurrentCaps_SEC_RF107_14b()
    {
        ChangeReturns();
        var plan = await _service.PlanRowFilterEvaluationAsync(RuleRequest(), Admin, Tenant);
        var token = await ConfirmAsync(plan.PlanId);
        // Caps were lowered after planning: the admin service now rejects the planned values.
        _admin.ValidateRuleAsync(RuleId, new TenantId(Tenant), Arg.Any<RowFilterEvaluationSettings>(), Arg.Any<CancellationToken>())
            .Returns<RowFilterEvaluationPreview?>(_ => throw new RowFilterEvaluationValidationException("durationSeconds exceeds the configured maximum of 60 seconds."));

        await Should.ThrowAsync<RowFilterEvaluationValidationException>(() => _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, Tenant));

        await _admin.DidNotReceiveWithAnyArgs().SetRuleAsync(default, default, default!, default, default);
    }

    [Fact]
    public async Task RequiresTotp_SEC_RF107_14c()
    {
        var plan = await _service.PlanRowFilterEvaluationAsync(RuleRequest(), Admin, Tenant);

        // No enrolled secret: the plan cannot be confirmed.
        await Should.ThrowAsync<SecurityException>(() => _service.ConfirmPlanAsync(plan.PlanId, "123456", Admin));
        // An unconfirmed plan cannot be applied, with a missing or a made-up token.
        await Should.ThrowAsync<SecurityException>(() => _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(plan.PlanId, ""), Admin, Tenant));
        await Should.ThrowAsync<SecurityException>(() => _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(plan.PlanId, "forged-token"), Admin, Tenant));
        await _admin.DidNotReceiveWithAnyArgs().SetRuleAsync(default, default, default!, default, default);
    }

    [Fact]
    public async Task Apply_IsBoundToTheInitiatingAdmin_AndTheTenant()
    {
        ChangeReturns();
        var plan = await _service.PlanRowFilterEvaluationAsync(RuleRequest(), Admin, Tenant);
        var token = await ConfirmAsync(plan.PlanId);

        await Should.ThrowAsync<SecurityException>(() => _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(plan.PlanId, token), "S-1-5-21-1-999", Tenant));
        await Should.ThrowAsync<SecurityException>(() => _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, "tenant-b"));
        await _admin.DidNotReceiveWithAnyArgs().SetRuleAsync(default, default, default!, default, default);
    }

    [Fact]
    public async Task Apply_Replay_IsRejected()
    {
        ChangeReturns();
        var plan = await _service.PlanRowFilterEvaluationAsync(RuleRequest(), Admin, Tenant);
        var token = await ConfirmAsync(plan.PlanId);
        await _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, Tenant);

        await Should.ThrowAsync<SecurityException>(() => _service.ApplyRowFilterEvaluationAsync(new AdminApplyAccessRequest(plan.PlanId, token), Admin, Tenant));
    }

    [Fact]
    public async Task Service_WithoutAdminService_FailsClosed()
    {
        var bare = new AccessPlanningService(Substitute.For<IAuditLogRepository>());

        await Should.ThrowAsync<SecurityException>(() => bare.PlanRowFilterEvaluationAsync(RuleRequest(), Admin, Tenant));
    }

    [Fact]
    public async Task PlanKind_SurvivesTheClusterStoreJsonRoundTrip()
    {
        var cluster = new ClusterAccessPlanStore(new InMemoryClusterStateProvider());
        var plan = new StoredAccessPlan(
            new AdminPlanAccessResult("plan_x", "sales.dbo.orders", [], [], DateTimeOffset.UtcNow.AddMinutes(5)),
            new AdminPlanAccessRequest("sales.dbo.orders", [], ""), Admin, TenantId: Tenant,
            PlanKind: AccessPlanKind.RowFilterEvaluation,
            RowFilterEvaluation: new RowFilterEvaluationPlan(RuleId, "sales.dbo.orders", Stored, new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 5, 6)));
        await cluster.TrySavePlanAsync(plan, TimeSpan.FromMinutes(5), 10);

        var read = (await cluster.GetPlanAsync("plan_x"))!;

        read.PlanKind.ShouldBe(AccessPlanKind.RowFilterEvaluation);
        read.RowFilterEvaluation.ShouldBe(plan.RowFilterEvaluation);
    }

    [Fact]
    public async Task OldStoredPlansWithoutAKind_DefaultToAccess()
    {
        var state = new InMemoryClusterStateProvider();
        // A plan serialized before issue #107 has no PlanKind / RowFilterEvaluation properties.
        await state.SetAsync("accessplan:plan:plan_old", new
        {
            Result = new { PlanId = "plan_old", DatasetId = "a.b.c", Diffs = Array.Empty<object>(), Warnings = Array.Empty<string>(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) },
            Request = new { DatasetId = "a.b.c", Grants = Array.Empty<object>(), Reason = "" },
            AdminSid = Admin
        }, TimeSpan.FromMinutes(5));

        var read = await new ClusterAccessPlanStore(state).GetPlanAsync("plan_old");

        read!.PlanKind.ShouldBe(AccessPlanKind.Access);
        read.RowFilterEvaluation.ShouldBeNull();
    }
}
