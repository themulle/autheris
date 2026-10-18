namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PLAN-GOV-129 WP-1 test 12: four-eyes, last-known-good, generation, audit with hashes only (BC-129-10, BC-129-03, BC-129-15).</summary>
public sealed class CallerAttributeAdministrationServiceTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant_a");
    private static readonly CallerAttributeActor Alice = new("oid-alice", HoldsApproverRole: false);
    private static readonly CallerAttributeActor Bob = new("oid-bob", HoldsApproverRole: true);

    private readonly SqliteGovernanceRepository _store = GovernanceStoreFactory.CreateSqlite(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=caa_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));

    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly List<AuditLogEntry> _entries = [];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));

    public CallerAttributeAdministrationServiceTests()
    {
        _audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(_entries.Add), Arg.Any<System.Threading.CancellationToken>()).Returns(Task.CompletedTask);
    }

    public void Dispose() => _store.Dispose();

    private (CallerAttributeAdministrationService Service, CallerAttributeRegistry Registry) Create(bool enabled = true, bool separateRole = true)
    {
        var options = Options.Create(new GatewayOptions { CallerAttributes = new CallerAttributeOptions { Enabled = enabled, RequireSeparateApproverRole = separateRole } });
        var registry = new CallerAttributeRegistry(_store, options, _time);
        return (new CallerAttributeAdministrationService(_store, registry, _audit, options, _time, []), registry);
    }

    private static CallerAttributeDefinition Claims(string name = "cost_center", string description = "v1") =>
        SqliteCallerAttributeRepositoryTests.Definition(name, description);

    private static CallerAttributeDefinition Table(string name = "dept") => SqliteCallerAttributeRepositoryTests.Definition(name) with
    {
        Provider = new CallerAttributeProviderBinding("attribute-table", new Dictionary<string, string>())
    };

    [Fact]
    public async Task Disabled_RefusesSubmit_AndApproval()
    {
        var (service, _) = Create(enabled: false);
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SubmitDefinitionAsync(Claims(), Alice));
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.ApproveDefinitionAsync(Tenant, "cost_center", "h", Bob));
    }

    [Theory]
    [InlineData("Cost")]
    [InlineData("cost__center")]
    [InlineData("1cost")]
    [InlineData("cost-center")]
    [InlineData("cost_")]
    [InlineData("")]
    public async Task InvalidName_IsRefused(string name)
    {
        var (service, _) = Create();
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SubmitDefinitionAsync(Claims(name), Alice));
    }

    [Fact]
    public async Task OutOfRangeLimits_AreRefused()
    {
        var (service, _) = Create();
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SubmitDefinitionAsync(Claims() with { TtlSeconds = 301 }, Alice));
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SubmitDefinitionAsync(Claims() with { TtlSeconds = 0 }, Alice));
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SubmitDefinitionAsync(Claims() with { MaxValues = 201 }, Alice));
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SubmitDefinitionAsync(Claims() with { MaxValues = 0 }, Alice));
    }

    [Fact]
    public async Task Submit_StoresPending_Audits_WithHashesOnly_AndInvalidatesTheRegistry()
    {
        var (service, registry) = Create();
        var def = Claims();
        (await registry.GetAsync(default)).Definitions.ShouldBeEmpty();

        await service.SubmitDefinitionAsync(def, Alice);

        var entry = _entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.CallerAttributeDefinitionPending);
        entry.DetailsJson.ShouldContain(def.ComputeDefinitionHash());
        entry.DetailsJson.ShouldNotContain("department");   // provider configuration is not audited in clear
        (await registry.GetAsync(default)).Definitions.ShouldBeEmpty();   // pending is not active
    }

    [Fact]
    public async Task Approve_ByTheCreator_IsRefused_ByAnotherPerson_Activates()
    {
        var (service, registry) = Create();
        var def = Claims();
        await service.SubmitDefinitionAsync(def, Alice);

        var selfApprover = Alice with { HoldsApproverRole = true };
        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() => service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), selfApprover));
        ex.Code.ShouldBe(CallerAttributeChangeResult.SameSubject.ToString());

        await service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), Bob);
        (await registry.GetAsync(default)).TryGetDefinition(Tenant, def.Name, out var active).ShouldBeTrue();
        active.Status.ShouldBe(FilterApprovalStatus.Active);
        _entries.Last().EventType.ShouldBe(AuditEventTypes.CallerAttributeDefinitionApplied);
    }

    [Fact]
    public async Task Approve_WithAStaleHash_IsRefused()
    {
        var (service, _) = Create();
        var def = Claims();
        await service.SubmitDefinitionAsync(def, Alice);
        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() => service.ApproveDefinitionAsync(Tenant, def.Name, "not-the-hash", Bob));
        ex.Code.ShouldBe(CallerAttributeChangeResult.HashMismatch.ToString());
    }

    [Fact]
    public async Task AttributeTableProvider_NeedsAnApproverWithTheSeparateRole_WhenRequired()
    {
        var (service, _) = Create(separateRole: true);
        var def = Table();
        await service.SubmitDefinitionAsync(def, Alice);

        var noRole = Bob with { HoldsApproverRole = false };
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), noRole));
        await service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), Bob);
    }

    [Fact]
    public async Task ClaimsProvider_DoesNotNeedTheSeparateRole()
    {
        var (service, registry) = Create(separateRole: true);
        var def = Claims();
        await service.SubmitDefinitionAsync(def, Alice);
        await service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), Bob with { HoldsApproverRole = false });
        (await registry.GetAsync(default)).TryGetDefinition(Tenant, def.Name, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task ActiveDefinition_StaysLastKnownGood_WhileAChangeWaits_AndTheGenerationAdvances()
    {
        var (service, registry) = Create();
        var def = Claims();
        await service.SubmitDefinitionAsync(def, Alice);
        await service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), Bob);
        var generation = (await registry.GetAsync(default)).Generation;

        var changed = def with { Description = "v2" };
        await service.SubmitDefinitionAsync(changed, Alice);

        var snapshot = await registry.GetAsync(default);
        snapshot.Generation.ShouldBeGreaterThan(generation);
        snapshot.TryGetDefinition(Tenant, def.Name, out var active).ShouldBeTrue();
        active.Description.ShouldBe("v1");
    }

    [Fact]
    public async Task Deletion_IsFourEyes_AndRemovesTheAttributeFromTheRegistry()
    {
        var (service, registry) = Create();
        var def = Claims();
        await service.SubmitDefinitionAsync(def, Alice);
        await service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), Bob);

        await service.RequestDeletionAsync(Tenant, def.Name, Alice);
        (await registry.GetAsync(default)).TryGetDefinition(Tenant, def.Name, out _).ShouldBeTrue();   // still active until approved
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), Alice with { HoldsApproverRole = true }));
        await service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), Bob);

        (await registry.GetAsync(default)).TryGetDefinition(Tenant, def.Name, out _).ShouldBeFalse();
        _entries.Last().EventType.ShouldBe(AuditEventTypes.CallerAttributeDefinitionDeleted);
    }

    [Fact]
    public async Task ServiceSet_RequiresARegisteredActiveAttribute_AndFourEyes()
    {
        var (service, registry) = Create();
        var app = "11111111-1111-1111-1111-111111111111";
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SubmitServiceSetAsync(Tenant, app, "cost_center", ["secret-4711"], Alice));

        var def = Claims();
        await service.SubmitDefinitionAsync(def, Alice);
        await service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), Bob);

        await service.SubmitServiceSetAsync(Tenant, app, "cost_center", ["secret-4711"], Alice);
        (await registry.GetAsync(default)).TryGetServiceSet(Tenant, app, "cost_center", out _).ShouldBeFalse();   // pending: not applied

        var set = new ServiceAttributeSet(Guid.Empty, Tenant, app, "cost_center", ["secret-4711"], FilterApprovalStatus.PendingApproval);
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.ApproveServiceSetAsync(Tenant, app, "cost_center", set.ComputeValuesHash(), Alice with { HoldsApproverRole = true }));
        await service.ApproveServiceSetAsync(Tenant, app, "cost_center", set.ComputeValuesHash(), Bob);
        (await registry.GetAsync(default)).TryGetServiceSet(Tenant, app.ToUpperInvariant(), "cost_center", out var values).ShouldBeTrue();
        values.ShouldBe(["secret-4711"]);
        _entries.Last().EventType.ShouldBe(AuditEventTypes.CallerAttributeServiceSetChanged);
        _entries.Last().DetailsJson.ShouldNotContain("secret-4711");   // values never audited
    }

    [Fact]
    public async Task ServiceSet_ValueCountAboveTheDefinitionMaximum_IsRefused()
    {
        var (service, _) = Create();
        var def = Claims() with { MaxValues = 2 };
        await service.SubmitDefinitionAsync(def, Alice);
        await service.ApproveDefinitionAsync(Tenant, def.Name, def.ComputeDefinitionHash(), Bob);
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SubmitServiceSetAsync(Tenant, "app", def.Name, ["1", "2", "3"], Alice));
    }
}
