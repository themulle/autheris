namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Common.Interfaces;
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

/// <summary>Caller attribute administration on a real store: a save applies at once (single approval), generation, audit with hashes only, approver role for data-granting providers.</summary>
public sealed class CallerAttributeAdministrationServiceTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant_a");
    private static readonly CallerAttributeActor Alice = new("oid-alice", HoldsApproverRole: false);
    private static readonly CallerAttributeActor Bob = new("oid-bob", HoldsApproverRole: true);

    private readonly PostgreSqlGovernanceRepository _store = GovernanceTestDatabase.CreateRepository(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions()));

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
        Autheris.Tests.Unit.Identity.Attributes.CallerAttributeTestSupport.Definition(name, description);

    private static CallerAttributeDefinition Table(string name = "dept") => Autheris.Tests.Unit.Identity.Attributes.CallerAttributeTestSupport.Definition(name) with
    {
        Provider = new CallerAttributeProviderBinding("attribute-table", new Dictionary<string, string>())
    };

    [Fact]
    public async Task Disabled_RefusesSave_AndDelete()
    {
        var (service, _) = Create(enabled: false);
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveDefinitionAsync(Claims(), Bob));
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.DeleteDefinitionAsync(Tenant, "cost_center", Bob));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("double__underscore")]
    [InlineData("1leading")]
    public async Task InvalidName_IsRefused(string name)
    {
        var (service, _) = Create();
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveDefinitionAsync(Claims(name), Alice));
    }

    [Fact]
    public async Task OutOfRangeTtlOrMaxValues_AreRefused()
    {
        var (service, _) = Create();
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveDefinitionAsync(Claims() with { TtlSeconds = 301 }, Alice));
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveDefinitionAsync(Claims() with { TtlSeconds = 0 }, Alice));
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveDefinitionAsync(Claims() with { MaxValues = 201 }, Alice));
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveDefinitionAsync(Claims() with { MaxValues = 0 }, Alice));
    }

    [Fact]
    public async Task Save_AppliesAtOnce_Audits_WithHashesOnly_AndInvalidatesTheRegistry()
    {
        var (service, registry) = Create();
        var def = Claims();
        (await registry.GetAsync(default)).Definitions.ShouldBeEmpty();

        await service.SaveDefinitionAsync(def, Alice);

        var entry = _entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.CallerAttributeDefinitionApplied);
        entry.DetailsJson.ShouldContain(def.ComputeDefinitionHash());
        entry.DetailsJson.ShouldNotContain("department");   // provider configuration is not audited in clear
        (await registry.GetAsync(default)).Definitions.ShouldHaveSingleItem();   // active at once
    }

    [Fact]
    public async Task Save_OfAnExistingDefinition_ReplacesIt_AndAuditsBeforeAndAfter()
    {
        var (service, registry) = Create();
        var def = Claims();
        await service.SaveDefinitionAsync(def, Alice);
        var changed = Claims(description: "v2");

        await service.SaveDefinitionAsync(changed, Alice);

        _entries.Last().DetailsJson.ShouldContain(def.ComputeDefinitionHash());
        _entries.Last().DetailsJson.ShouldContain(changed.ComputeDefinitionHash());
        (await registry.GetAsync(default)).Definitions.Single().Description.ShouldBe("v2");
    }

    [Fact]
    public async Task AttributeTableProvider_NeedsTheApproverRole_WhenRequired()
    {
        var (service, _) = Create();
        var def = Table();

        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveDefinitionAsync(def, Bob with { HoldsApproverRole = false }));
        ex.Code.ShouldBe("ApproverRoleRequired");
        await service.SaveDefinitionAsync(def, Bob);
    }

    [Fact]
    public async Task AttributeTableProvider_NeedsNoApproverRole_WhenNotRequired()
    {
        var (service, _) = Create(separateRole: false);
        await service.SaveDefinitionAsync(Table(), Bob with { HoldsApproverRole = false });
    }

    [Fact]
    public async Task Delete_RemovesTheDefinition_AtOnce_AndAudits()
    {
        var (service, registry) = Create();
        var def = Claims();
        await service.SaveDefinitionAsync(def, Alice);

        await service.DeleteDefinitionAsync(Tenant, def.Name, Alice);

        (await registry.GetAsync(default)).Definitions.ShouldBeEmpty();
        _entries.Last().EventType.ShouldBe(AuditEventTypes.CallerAttributeDefinitionDeleted);
    }

    [Fact]
    public async Task ServiceSet_RequiresARegisteredActiveAttribute_AndTheApproverRole()
    {
        var (service, registry) = Create();
        var app = "11111111-1111-1111-1111-111111111111";
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveServiceSetAsync(Tenant, app, "cost_center", ["secret-4711"], Bob));

        await service.SaveDefinitionAsync(Claims(), Alice);

        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveServiceSetAsync(Tenant, app, "cost_center", ["secret-4711"], Alice));
        await service.SaveServiceSetAsync(Tenant, app, "cost_center", ["secret-4711"], Bob);
        (await registry.GetAsync(default)).TryGetServiceSet(Tenant, app.ToUpperInvariant(), "cost_center", out var values).ShouldBeTrue();
        values.ShouldBe(["secret-4711"]);
        _entries.Last().EventType.ShouldBe(AuditEventTypes.CallerAttributeServiceSetChanged);
        _entries.Last().DetailsJson.ShouldNotContain("secret-4711");   // values never audited
    }

    [Fact]
    public async Task ServiceSet_ForTheWritersOwnIdentifier_IsRefused()
    {
        var (service, _) = Create();
        await service.SaveDefinitionAsync(Claims(), Alice);
        var self = Bob with { OwnIdentifiers = ["app-self"] };

        var ex = await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveServiceSetAsync(Tenant, "app-self", "cost_center", ["1"], self));
        ex.Code.ShouldBe("SelfWidening");
    }

    [Fact]
    public async Task ServiceSet_ValueCountAboveTheDefinitionMaximum_IsRefused()
    {
        var (service, _) = Create();
        var def = Claims() with { MaxValues = 2 };
        await service.SaveDefinitionAsync(def, Alice);
        await Should.ThrowAsync<CallerAttributeAdminException>(() => service.SaveServiceSetAsync(Tenant, "app", def.Name, ["1", "2", "3"], Bob));
    }
}
