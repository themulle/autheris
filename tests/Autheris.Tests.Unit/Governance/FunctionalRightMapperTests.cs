namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Governance.SingleApproval;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class FunctionalRightMapperTests
{
    private readonly IFunctionalRightRepository _repository = Substitute.For<IFunctionalRightRepository>();
    private readonly IIdentityRealmRegistry _realmRegistry = Substitute.For<IIdentityRealmRegistry>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly FunctionalRightMapper _sut;

    public FunctionalRightMapperTests()
    {
        _sut = new FunctionalRightMapper(_repository, _realmRegistry, _auditWriter);
    }

    private static IdentityRealm WorkforceRealm(string realmId, params GatewayRole[] allowed) => new(
        realmId,
        IdentityRealmKind.EntraWorkforce,
        new TenantBindingSpecification(TenantBindingKind.Fixed, "tenant-1"),
        RealmGroupSource.Resolver,
        RealmProvisioningMode.Scim,
        issuers: ["https://sts.windows.net/test"],
        allowedGatewayRoles: new HashSet<GatewayRole>(allowed));

    private static SingleApprovalWriter Writer(string subject = "user-alice", string[]? groups = null) =>
        new(subject, [subject, subject + "@corp.example"], groups ?? ["grp-alice"]);

    [Fact]
    public async Task Save_is_the_approval_and_the_mapping_is_effective_immediately()
    {
        var tenantId = new TenantId("tenant-1");
        _realmRegistry.GetRealm("workforce-entra").Returns(WorkforceRealm("workforce-entra"));
        FunctionalRightMapping? captured = null;
        await _repository.CreateMappingAsync(Arg.Do<FunctionalRightMapping>(m => captured = m), Arg.Any<CancellationToken>());

        var saved = await _sut.SaveMappingAsync(tenantId, "workforce-entra", "engineers", FunctionalRight.DataExport, "", Writer());

        captured.ShouldNotBeNull();
        captured.ApprovedBy.ShouldBe("user-alice");
        captured.ApprovedAt.ShouldNotBeNull();
        captured.CreatedBy.ShouldBe("user-alice");
        captured.RecertClass.ShouldBe("Standard");
        captured.IsApproved.ShouldBeTrue();
        saved.ShouldBe(captured);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("ClusterAdmin")]
    [InlineData("TenantAdmin")]
    [InlineData("GovernanceAdmin")]
    [InlineData("Governance.Approve")]
    [InlineData("Governance.Admin")]
    [InlineData("Security.Admin")]
    [InlineData("DataProduct.Admin")]
    public async Task Mapping_never_confers_it_or_governance(string right)
    {
        var tenantId = new TenantId("tenant-1");
        var everyRole = Enum.GetValues<GatewayRole>();
        _realmRegistry.GetRealm("workforce-entra").Returns(WorkforceRealm("workforce-entra", everyRole));

        // Never saved, even in a realm that allows every gateway role.
        await Should.ThrowAsync<SecurityException>(() =>
            _sut.SaveMappingAsync(tenantId, "workforce-entra", "engineers", new FunctionalRight(right), "Privileged", Writer()));
        await _repository.DidNotReceiveWithAnyArgs().CreateMappingAsync(default!, default);

        // A legacy row that confers a capacity is ignored at read time.
        var legacy = new FunctionalRightMapping(tenantId, "workforce-entra", "engineers", new FunctionalRight(right),
            ApprovedBy: "approver-bob", ApprovedAt: DateTimeOffset.UtcNow, RecertClass: "Privileged", CreatedBy: "creator-alice", CreatedAt: DateTimeOffset.UtcNow);
        _repository.GetApprovedMappingsForGroupsAndRolesAsync(tenantId, "workforce-entra", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([legacy]);

        (await _sut.GetRightsForGroupsAndRolesAsync(tenantId, "workforce-entra", ["engineers"])).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Mapping_cannot_confer_role_outside_realm()
    {
        var tenantId = new TenantId("tenant-1");
        _realmRegistry.GetRealm("workforce-entra").Returns(WorkforceRealm("workforce-entra", GatewayRole.Consumer));

        // Inside the realm's allowed roles: saved and effective.
        var allowed = await _sut.SaveMappingAsync(tenantId, "workforce-entra", "engineers", FunctionalRight.Consumer, "Standard", Writer());
        allowed.FunctionalRight.ShouldBe(FunctionalRight.Consumer);

        // Outside the realm's allowed roles, or in an unknown realm: refused (fail closed).
        await Should.ThrowAsync<SecurityException>(() =>
            _sut.SaveMappingAsync(tenantId, "workforce-entra", "engineers", FunctionalRight.SchemaPublisher, "Standard", Writer()));
        await Should.ThrowAsync<SecurityException>(() =>
            _sut.SaveMappingAsync(tenantId, "unknown-realm", "engineers", FunctionalRight.Consumer, "Standard", Writer()));
        await _repository.Received(1).CreateMappingAsync(Arg.Any<FunctionalRightMapping>(), Arg.Any<CancellationToken>());

        // A legacy row outside the realm's roles confers nothing; a right that names no role is not bounded by the realm.
        FunctionalRightMapping Row(FunctionalRight right) => new(tenantId, "workforce-entra", "engineers", right,
            ApprovedBy: "approver-bob", ApprovedAt: DateTimeOffset.UtcNow, RecertClass: "Standard", CreatedBy: "creator-alice", CreatedAt: DateTimeOffset.UtcNow);
        _repository.GetApprovedMappingsForGroupsAndRolesAsync(tenantId, "workforce-entra", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([Row(FunctionalRight.Consumer), Row(FunctionalRight.SchemaPublisher), Row(FunctionalRight.DataExport)]);

        var rights = await _sut.GetRightsForGroupsAndRolesAsync(tenantId, "workforce-entra", ["engineers"]);

        rights.ShouldBe([FunctionalRight.Consumer, FunctionalRight.DataExport], ignoreOrder: true);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("user-alice")]
    [InlineData("USER-ALICE@corp.example")]
    [InlineData("grp-alice")]
    [InlineData("DataSteward")]
    public async Task Writer_cannot_map_a_right_to_own_identifier_group_or_role(string groupOrRole)
    {
        _realmRegistry.GetRealm("workforce-entra").Returns(WorkforceRealm("workforce-entra"));
        var writer = Writer(groups: ["grp-alice", "DataSteward"]);

        var ex = await Should.ThrowAsync<SingleApprovalRefusedException>(() =>
            _sut.SaveMappingAsync(new TenantId("tenant-1"), "workforce-entra", groupOrRole, FunctionalRight.DataExport, "Standard", writer));

        ex.Code.ShouldBe("SelfWidening");
        await _repository.DidNotReceiveWithAnyArgs().CreateMappingAsync(default!, default);
        await _auditWriter.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Save_audits_before_and_after_hash_without_values_and_fails_closed()
    {
        var tenantId = new TenantId("tenant-1");
        _realmRegistry.GetRealm("workforce-entra").Returns(WorkforceRealm("workforce-entra"));
        AuditLogEntry? entry = null;
        await _auditWriter.RecordRequiredAsync(Arg.Do<AuditLogEntry>(e => entry = e), Arg.Any<CancellationToken>());

        await _sut.SaveMappingAsync(tenantId, "workforce-entra", "engineers", FunctionalRight.DataExport, "Standard", Writer());

        entry.ShouldNotBeNull();
        entry.EventType.ShouldBe("SECURITY_FUNCTIONAL_MAPPING_SAVED");
        entry.ActorSid.ShouldBe(new Sid("user-alice"));
        entry.TargetTable.ShouldBe("FUNCTIONAL_RIGHT_MAPPINGS");
        entry.DetailsJson.ShouldContain("\"after\":\"");
        entry.DetailsJson.ShouldNotContain("engineers");
        entry.DetailsJson.ShouldNotContain("Data.Export");

        // A failing audit refuses the change before anything is written.
        _repository.ClearReceivedCalls();
        _auditWriter.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("audit down")));
        var ex = await Should.ThrowAsync<SingleApprovalRefusedException>(() =>
            _sut.SaveMappingAsync(tenantId, "workforce-entra", "engineers", FunctionalRight.DataExport, "Standard", Writer()));
        ex.Code.ShouldBe("AuditFailed");
        await _repository.DidNotReceiveWithAnyArgs().CreateMappingAsync(default!, default);

        // Without an audit writer nothing is written either.
        var unaudited = new FunctionalRightMapper(_repository, _realmRegistry);
        (await Should.ThrowAsync<SingleApprovalRefusedException>(() =>
            unaudited.SaveMappingAsync(tenantId, "workforce-entra", "engineers", FunctionalRight.DataExport, "Standard", Writer()))).Code.ShouldBe("AuditFailed");
        await _repository.DidNotReceiveWithAnyArgs().CreateMappingAsync(default!, default);
    }

    [Theory]
    [InlineData(IdentityRealmKind.EntraExternalId)]
    [InlineData(IdentityRealmKind.AzureAdB2C)]
    [InlineData(IdentityRealmKind.Customer)]
    public void FunctionalRightMappingValidator_CustomerRealms_BlockPrivilegedAndAdminRights(IdentityRealmKind kind)
    {
        var realm = new IdentityRealm(
            "ext-realm",
            kind,
            new TenantBindingSpecification(TenantBindingKind.Fixed, "t1"),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            issuers: ["https://ext.example.com"]);

        var privilegedMapping = new FunctionalRightMapping(
            "t1", "ext-realm", "external-users", FunctionalRight.SecurityAdmin,
            ApprovedBy: null, ApprovedAt: null, RecertClass: "Privileged",
            CreatedBy: "admin", CreatedAt: DateTimeOffset.UtcNow);

        Should.Throw<SecurityException>(() =>
            Autheris.Application.Governance.Rights.Validators.FunctionalRightMappingValidator.ValidateCreation(privilegedMapping, realm));

        var tenantAdminMapping = new FunctionalRightMapping(
            "t1", "ext-realm", "external-users", FunctionalRight.TenantAdmin,
            ApprovedBy: null, ApprovedAt: null, RecertClass: "Privileged",
            CreatedBy: "admin", CreatedAt: DateTimeOffset.UtcNow);

        Should.Throw<SecurityException>(() =>
            Autheris.Application.Governance.Rights.Validators.FunctionalRightMappingValidator.ValidateCreation(tenantAdminMapping, realm));

        var govAdminMapping = new FunctionalRightMapping(
            "t1", "ext-realm", "external-users", FunctionalRight.GovernanceAdmin,
            ApprovedBy: null, ApprovedAt: null, RecertClass: "Privileged",
            CreatedBy: "admin", CreatedAt: DateTimeOffset.UtcNow);

        Should.Throw<SecurityException>(() =>
            Autheris.Application.Governance.Rights.Validators.FunctionalRightMappingValidator.ValidateCreation(govAdminMapping, realm));
    }

    [Fact]
    public async Task FunctionalRightMapper_GetRightsForCallerAsync_ResolvesApprovedRights()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var claims = new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.GroupSid, "eng-group"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Consumer"),
            new System.Security.Claims.Claim("group", "ops-team")
        };
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "TestAuth"));

        var mapping = new FunctionalRightMapping(
            tenantId.Value, realmId, "eng-group", FunctionalRight.DataExport,
            ApprovedBy: "approver-bob", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: "Standard", CreatedBy: "creator-alice",
            CreatedAt: DateTimeOffset.UtcNow);

        _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([mapping]);

        var rights = await _sut.GetRightsForCallerAsync(principal, tenantId, realmId);

        rights.ShouldContain(FunctionalRight.DataExport);
        rights.ShouldNotContain(new FunctionalRight("Consumer")); // NL-SC-01: the role claim alone grants nothing
        rights.Count.ShouldBe(1);

        var hasExport = await _sut.HasRightAsync(principal, tenantId, realmId, FunctionalRight.DataExport);
        hasExport.ShouldBeTrue();

        var hasTenantAdmin = await _sut.HasRightAsync(principal, tenantId, realmId, FunctionalRight.TenantAdmin);
        hasTenantAdmin.ShouldBeFalse();
    }

    [Fact]
    public async Task FunctionalRightMapper_GetRightsForGroupsAndRoles_CustomerRealm_CeilingApplied()
    {
        var tenantId = new TenantId("tenant-1");
        var customerRealmId = "customer-b2c";

        var customerRealm = new IdentityRealm(
            customerRealmId,
            IdentityRealmKind.AzureAdB2C,
            new TenantBindingSpecification(TenantBindingKind.Fixed, tenantId.Value),
            RealmGroupSource.Token,
            RealmProvisioningMode.None);

        _realmRegistry.GetRealm(customerRealmId).Returns(customerRealm);

        var validExportMapping = new FunctionalRightMapping(
            tenantId.Value, customerRealmId, "b2c-users", FunctionalRight.DataExport,
            ApprovedBy: "bob", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: "Standard", CreatedBy: "alice",
            CreatedAt: DateTimeOffset.UtcNow);

        var escalatedAdminMapping = new FunctionalRightMapping(
            tenantId.Value, customerRealmId, "b2c-users", FunctionalRight.TenantAdmin,
            ApprovedBy: "bob", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: "Privileged", CreatedBy: "alice",
            CreatedAt: DateTimeOffset.UtcNow);

        _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, customerRealmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([validExportMapping, escalatedAdminMapping]);

        var rights = await _sut.GetRightsForGroupsAndRolesAsync(
            tenantId, customerRealmId, ["b2c-users"]);

        // DataExport is allowed; TenantAdmin is filtered by customer realm ceiling
        rights.ShouldContain(FunctionalRight.DataExport);
        rights.ShouldNotContain(FunctionalRight.TenantAdmin);
    }

    [Fact]
    public async Task FunctionalRightMapper_GetRightsForGroupsAndRoles_UnapprovedOrPrivileged_FilteredFailClosed()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var privileged = new FunctionalRightMapping(
            tenantId.Value, realmId, "devs", FunctionalRight.SecurityAdmin,
            ApprovedBy: "bob", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: "Privileged", CreatedBy: "alice",
            CreatedAt: DateTimeOffset.UtcNow);

        var unapprovedStandard = new FunctionalRightMapping(
            tenantId.Value, realmId, "devs", FunctionalRight.DataExport,
            ApprovedBy: null, ApprovedAt: null, // Unapproved!
            RecertClass: "Standard", CreatedBy: "alice",
            CreatedAt: DateTimeOffset.UtcNow);

        _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([privileged, unapprovedStandard]);

        var rights = await _sut.GetRightsForGroupsAndRolesAsync(tenantId, realmId, ["devs"]);

        rights.ShouldBeEmpty();

        var emptyCall = await _sut.GetRightsForGroupsAndRolesAsync(tenantId, realmId, []);
        emptyCall.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("otherTenant:ClusterAdmin")]
    [InlineData(":GovernanceAdmin")]
    public async Task FunctionalRightMapper_GetRightsForCallerAsync_TenantPrefixedLegacyRole_IsNotMapped_GOV13(string role)
    {
        var tenantId = new TenantId("tenant-1");
        var claims = new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role) };
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "TestAuth"));
        _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, "workforce-entra", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var rights = await _sut.GetRightsForCallerAsync(principal, tenantId, "workforce-entra");

        rights.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Gateway_Role_Claim_Alone_Grants_No_Functional_Right()
    {
        var tenantId = new TenantId("tenant-1");
        _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, "workforce-entra", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var roleNames = Enum.GetValues<Autheris.Domain.Security.GatewayRole>().Select(r => r.ToString()).Append("NoSuchRole");
        foreach (var role in roleNames)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "TestAuth"));
            var rights = await _sut.GetRightsForCallerAsync(principal, tenantId, "workforce-entra");
            rights.ShouldBeEmpty(role);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Configured_Role_Right_Assignment_Still_Grants_Right()
    {
        var tenantId = new TenantId("tenant-1");
        var mapping = new FunctionalRightMapping(
            tenantId.Value, "workforce-entra", "DataSteward", FunctionalRight.DataExport,
            ApprovedBy: "approver-bob", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: "Standard", CreatedBy: "creator-alice", CreatedAt: DateTimeOffset.UtcNow);
        _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, "workforce-entra", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([mapping]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "DataSteward"), new Claim(ClaimTypes.Role, "ClusterAdmin")], "TestAuth"));

        var rights = await _sut.GetRightsForCallerAsync(principal, tenantId, "workforce-entra");

        rights.ShouldBe([FunctionalRight.DataExport], ignoreOrder: true);
    }

    [Fact]
    public async Task FunctionalRightMapper_GetRightsForCallerAsync_ReadsRolesAndPipelineGroupSidsOnly()
    {
        var tenantId = new TenantId("tenant-claims");
        var realmId = "workforce-entra";

        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "user-claims"),
            new Claim("group", "grp-1"),
            new Claim("groups", "grp-2"),
            new Claim("roles", "role-3"),
            new Claim(ClaimTypes.Role, "role-4"),
            Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-1105"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-1-2-3-1106")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var mapping1 = new FunctionalRightMapping(
            tenantId.Value, realmId, "role-4", FunctionalRight.McpUse,
            ApprovedBy: "admin", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: "Standard", CreatedBy: "admin",
            CreatedAt: DateTimeOffset.UtcNow);

        _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputGrps = callInfo.ArgAt<IReadOnlyCollection<string>>(2);
                inputGrps.ShouldNotContain("grp-1");
                inputGrps.ShouldNotContain("grp-2");
                inputGrps.ShouldContain("role-3");
                inputGrps.ShouldContain("role-4");
                inputGrps.ShouldContain("S-1-5-21-1-2-3-1105");
                inputGrps.ShouldNotContain("S-1-5-21-1-2-3-1106");
                return [mapping1];
            });

        var rights = await _sut.GetRightsForCallerAsync(principal, tenantId, realmId);
        rights.ShouldContain(FunctionalRight.McpUse);
    }

    [Fact]
    public async Task FunctionalRightMapper_DeleteMappingAsync_AuditsDeletion()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        await _sut.DeleteMappingAsync(tenantId, realmId, "dev-group", FunctionalRight.DataExport, SingleApprovalWriter.Of("deleter-1"));

        await _repository.Received(1).DeleteMappingAsync(tenantId, realmId, "dev-group", FunctionalRight.DataExport, Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "SECURITY_FUNCTIONAL_MAPPING_DELETED" &&
                e.ActorSid == new Sid("deleter-1") &&
                e.Decision == "ALLOW"),
            Arg.Any<CancellationToken>());
    }
}
