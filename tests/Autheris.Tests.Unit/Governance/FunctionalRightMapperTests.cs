namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Security;
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
}
