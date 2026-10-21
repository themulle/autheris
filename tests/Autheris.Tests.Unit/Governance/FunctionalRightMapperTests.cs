namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
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

    [Fact]
    public async Task FunctionalRightMapper_FourEyesEnforced_ThrowsWhenSelfApproved()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";
        var groupOrRole = "engineers";
        var right = FunctionalRight.DataUnmaskSensitive;

        // Existing mapping created by user-alice
        var existingMapping = new FunctionalRightMapping(
            tenantId.Value,
            realmId,
            groupOrRole,
            right,
            ApprovedBy: string.Empty,
            ApprovedAt: null,
            RecertClass: RecertClass.Standard.ToString(),
            CreatedBy: "user-alice",
            CreatedAt: DateTimeOffset.UtcNow
        );

        _repository.GetMappingAsync(tenantId, realmId, groupOrRole, right, Arg.Any<CancellationToken>())
            .Returns(existingMapping);

        // Attempt self-approval by user-alice
        var act = () => _sut.ApproveMappingAsync(tenantId, realmId, groupOrRole, right, "user-alice");

        await act.ShouldThrowAsync<SecurityException>();
    }

    [Fact]
    public async Task FunctionalRightMapper_Approve_SucceedsWithDifferentApprover()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";
        var groupOrRole = "engineers";
        var right = new FunctionalRight("Data.Read");

        var existingMapping = new FunctionalRightMapping(
            tenantId.Value,
            realmId,
            groupOrRole,
            right,
            ApprovedBy: string.Empty,
            ApprovedAt: null,
            RecertClass: RecertClass.Standard.ToString(),
            CreatedBy: "user-alice",
            CreatedAt: DateTimeOffset.UtcNow
        );

        _repository.GetMappingAsync(tenantId, realmId, groupOrRole, right, Arg.Any<CancellationToken>())
            .Returns(existingMapping);

        await _sut.ApproveMappingAsync(tenantId, realmId, groupOrRole, right, "user-bob");

        await _repository.Received(1).ApproveMappingAsync(
            tenantId, realmId, groupOrRole, right, "user-bob", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FunctionalRightMapper_PrivilegedRight_CustomerRealm_Blocked()
    {
        var tenantId = new TenantId("tenant-1");
        var customerRealmId = "customer-b2b";
        var groupOrRole = "analysts";
        var privilegedRight = new FunctionalRight("Security.Admin"); // IsPrivileged = true

        // Configure realm as Customer realm
        var customerRealm = new IdentityRealm(
            customerRealmId,
            IdentityRealmKind.Customer,
            new TenantBindingSpecification(TenantBindingKind.Fixed, tenantId.Value),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            issuers: ["https://customer.issuer.com"]);

        _realmRegistry.GetRealm(customerRealmId).Returns(customerRealm);

        var act = () => _sut.ProposeMappingAsync(
            tenantId, customerRealmId, groupOrRole, privilegedRight, RecertClass.Privileged, "user-alice");

        await act.ShouldThrowAsync<SecurityException>();
    }

    [Fact]
    public async Task FunctionalRightMapper_AuditRecorded_OnCreateAndApprove()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";
        var groupOrRole = "engineers";
        var right = new FunctionalRight("Data.Read");

        var workforceRealm = new IdentityRealm(
            realmId,
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, tenantId.Value),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            issuers: ["https://sts.windows.net/test"]);

        _realmRegistry.GetRealm(realmId).Returns(workforceRealm);

        var proposed = await _sut.ProposeMappingAsync(
            tenantId, realmId, groupOrRole, right, RecertClass.Standard, "user-alice");

        proposed.ShouldNotBeNull();
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "SECURITY_FUNCTIONAL_MAPPING_CREATED"),
            Arg.Any<CancellationToken>());

        var existingMapping = new FunctionalRightMapping(
            tenantId.Value,
            realmId,
            groupOrRole,
            right,
            ApprovedBy: string.Empty,
            ApprovedAt: null,
            RecertClass: RecertClass.Standard.ToString(),
            CreatedBy: "user-alice",
            CreatedAt: DateTimeOffset.UtcNow
        );

        _repository.GetMappingAsync(tenantId, realmId, groupOrRole, right, Arg.Any<CancellationToken>())
            .Returns(existingMapping);

        await _sut.ApproveMappingAsync(tenantId, realmId, groupOrRole, right, "user-bob");

        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "SECURITY_FUNCTIONAL_MAPPING_APPROVED"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FunctionalRightMapper_NonPrivilegedRight_PreservesSelfApproval()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";
        var groupOrRole = "engineers";
        var nonPrivilegedRight = FunctionalRight.DataExport; // IsPrivileged = false

        var workforceRealm = new IdentityRealm(
            realmId,
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, tenantId.Value),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            issuers: ["https://sts.windows.net/test"]);

        _realmRegistry.GetRealm(realmId).Returns(workforceRealm);

        FunctionalRightMapping? createdMapping = null;
        await _repository.CreateMappingAsync(
            Arg.Do<FunctionalRightMapping>(m => createdMapping = m),
            Arg.Any<CancellationToken>());

        var mapping = new FunctionalRightMapping(
            tenantId.Value,
            realmId,
            groupOrRole,
            nonPrivilegedRight,
            ApprovedBy: "user-alice",
            ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: RecertClass.Standard.ToString(),
            CreatedBy: "user-alice",
            CreatedAt: DateTimeOffset.UtcNow
        );

        await _sut.CreateMappingAsync(mapping);

        createdMapping.ShouldNotBeNull();
        createdMapping.ApprovedBy.ShouldBe("user-alice");
        createdMapping.ApprovedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task FunctionalRightMapper_PrivilegedRight_CreatorIdenticalToApprover_ClearsApproval()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";
        var groupOrRole = "engineers";
        var privilegedRight = FunctionalRight.DataUnmaskSensitive; // IsPrivileged = true

        var workforceRealm = new IdentityRealm(
            realmId,
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, tenantId.Value),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            issuers: ["https://sts.windows.net/test"]);

        _realmRegistry.GetRealm(realmId).Returns(workforceRealm);

        FunctionalRightMapping? createdMapping = null;
        await _repository.CreateMappingAsync(
            Arg.Do<FunctionalRightMapping>(m => createdMapping = m),
            Arg.Any<CancellationToken>());

        var mapping = new FunctionalRightMapping(
            tenantId.Value,
            realmId,
            groupOrRole,
            privilegedRight,
            ApprovedBy: "user-alice",
            ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: RecertClass.Privileged.ToString(),
            CreatedBy: "user-alice",
            CreatedAt: DateTimeOffset.UtcNow
        );

        await _sut.CreateMappingAsync(mapping);

        createdMapping.ShouldNotBeNull();
        createdMapping.ApprovedBy.ShouldBeNull();
        createdMapping.ApprovedAt.ShouldBeNull();
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
            "t1", "ext-realm", "external-users", FunctionalRight.DataUnmaskSensitive,
            ApprovedBy: null, ApprovedAt: null, RecertClass: RecertClass.Privileged.ToString(),
            CreatedBy: "admin", CreatedAt: DateTimeOffset.UtcNow);

        Should.Throw<SecurityException>(() =>
            Autheris.Application.Governance.Rights.Validators.FunctionalRightMappingValidator.ValidateCreation(privilegedMapping, realm));

        var tenantAdminMapping = new FunctionalRightMapping(
            "t1", "ext-realm", "external-users", FunctionalRight.TenantAdmin,
            ApprovedBy: null, ApprovedAt: null, RecertClass: RecertClass.Privileged.ToString(),
            CreatedBy: "admin", CreatedAt: DateTimeOffset.UtcNow);

        Should.Throw<SecurityException>(() =>
            Autheris.Application.Governance.Rights.Validators.FunctionalRightMappingValidator.ValidateCreation(tenantAdminMapping, realm));

        var govAdminMapping = new FunctionalRightMapping(
            "t1", "ext-realm", "external-users", FunctionalRight.GovernanceAdmin,
            ApprovedBy: null, ApprovedAt: null, RecertClass: RecertClass.Privileged.ToString(),
            CreatedBy: "admin", CreatedAt: DateTimeOffset.UtcNow);

        Should.Throw<SecurityException>(() =>
            Autheris.Application.Governance.Rights.Validators.FunctionalRightMappingValidator.ValidateCreation(govAdminMapping, realm));
    }

    [Fact]
    public async Task FunctionalRightMapper_GetRightsForCallerAsync_ResolvesApprovedRightsAndLegacyRoles()
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
            RecertClass: RecertClass.Standard.ToString(), CreatedBy: "creator-alice",
            CreatedAt: DateTimeOffset.UtcNow);

        _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([mapping]);

        var rights = await _sut.GetRightsForCallerAsync(principal, tenantId, realmId);

        rights.ShouldContain(FunctionalRight.DataExport);
        rights.ShouldContain(new FunctionalRight("Consumer")); // From legacy GatewayRole

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
            RecertClass: RecertClass.Standard.ToString(), CreatedBy: "alice",
            CreatedAt: DateTimeOffset.UtcNow);

        var escalatedAdminMapping = new FunctionalRightMapping(
            tenantId.Value, customerRealmId, "b2c-users", FunctionalRight.TenantAdmin,
            ApprovedBy: "bob", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: RecertClass.Privileged.ToString(), CreatedBy: "alice",
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
    public async Task FunctionalRightMapper_GetRightsForGroupsAndRoles_UnapprovedOrSelfApprovedPrivileged_FilteredFailClosed()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var selfApprovedPrivileged = new FunctionalRightMapping(
            tenantId.Value, realmId, "devs", FunctionalRight.DataUnmaskSensitive,
            ApprovedBy: "alice", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: RecertClass.Privileged.ToString(), CreatedBy: "alice", // Self-approved!
            CreatedAt: DateTimeOffset.UtcNow);

        var unapprovedStandard = new FunctionalRightMapping(
            tenantId.Value, realmId, "devs", FunctionalRight.DataExport,
            ApprovedBy: null, ApprovedAt: null, // Unapproved!
            RecertClass: RecertClass.Standard.ToString(), CreatedBy: "alice",
            CreatedAt: DateTimeOffset.UtcNow);

        _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([selfApprovedPrivileged, unapprovedStandard]);

        var rights = await _sut.GetRightsForGroupsAndRolesAsync(tenantId, realmId, ["devs"]);

        rights.ShouldBeEmpty();

        var emptyCall = await _sut.GetRightsForGroupsAndRolesAsync(tenantId, realmId, []);
        emptyCall.ShouldBeEmpty();
    }

    [Fact]
    public async Task FunctionalRightMapper_GetRightsForCallerAsync_SupportsAllGroupClaimTypes()
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
            new Claim(ClaimTypes.GroupSid, "grp-5")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var mapping1 = new FunctionalRightMapping(
            tenantId.Value, realmId, "grp-1", FunctionalRight.Consumer,
            ApprovedBy: "admin", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: RecertClass.Standard.ToString(), CreatedBy: "admin",
            CreatedAt: DateTimeOffset.UtcNow);

        _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputGrps = callInfo.ArgAt<IReadOnlyCollection<string>>(2);
                inputGrps.ShouldContain("grp-1");
                inputGrps.ShouldContain("grp-2");
                inputGrps.ShouldContain("role-3");
                inputGrps.ShouldContain("role-4");
                inputGrps.ShouldContain("grp-5");
                return [mapping1];
            });

        var rights = await _sut.GetRightsForCallerAsync(principal, tenantId, realmId);
        rights.ShouldContain(FunctionalRight.Consumer);
    }

    [Fact]
    public async Task FunctionalRightMapper_ApproveMappingAsync_NotFound_ThrowsKeyNotFoundException()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        _repository.GetMappingAsync(tenantId, realmId, "non-existent-group", FunctionalRight.Consumer, Arg.Any<CancellationToken>())
            .Returns((FunctionalRightMapping?)null);

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            _sut.ApproveMappingAsync(tenantId, realmId, "non-existent-group", FunctionalRight.Consumer, "approver-1"));
    }

    [Fact]
    public async Task FunctionalRightMapper_DeleteMappingAsync_AuditsDeletion()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        await _sut.DeleteMappingAsync(tenantId, realmId, "dev-group", FunctionalRight.DataExport, "deleter-1");

        await _repository.Received(1).DeleteMappingAsync(tenantId, realmId, "dev-group", FunctionalRight.DataExport, Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "SECURITY_FUNCTIONAL_MAPPING_DELETED" &&
                e.ActorSid == new Sid("deleter-1") &&
                e.Decision == "ALLOW"),
            Arg.Any<CancellationToken>());
    }
}
