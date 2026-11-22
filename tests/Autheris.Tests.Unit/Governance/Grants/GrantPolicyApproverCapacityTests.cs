using System;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants;

[Trait("Category", "Security")]
public sealed class GrantPolicyApproverCapacityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    [Fact]
    public void Missing_end_date_rejected()
    {
        var policy = new GrantPolicy();
        var dataDecision = policy.Evaluate(new GrantRequest(
            GranteeType.User, "user:alice", GrantKind.Consent, ValidTo: null, Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.DataOwner));
        dataDecision.Allowed.ShouldBeFalse();
        dataDecision.ErrorCode.ShouldBe(GrantErrorCodes.EndDateRequired);

        var unmaskDecision = policy.Evaluate(new GrantRequest(
            GranteeType.User, "user:alice", GrantKind.Consent, ValidTo: null, Now,
            RequestKind: ConsentRequestKind.Unmask, Capacity: ApproverCapacity.Governance));
        unmaskDecision.Allowed.ShouldBeFalse();
        unmaskDecision.ErrorCode.ShouldBe(GrantErrorCodes.EndDateRequired);
    }

    [Fact]
    public void Over_limit_rejected_not_truncated()
    {
        var policy = new GrantPolicy();
        var itOver = policy.Evaluate(new GrantRequest(
            GranteeType.User, "user:alice", GrantKind.Consent, ValidTo: Now.AddDays(366), Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.ItAdmin));
        itOver.Allowed.ShouldBeFalse();
        itOver.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsApproverLimit);
        itOver.MaxValidTo.ShouldBe(Now.AddDays(365));

        var ownerExact = policy.Evaluate(new GrantRequest(
            GranteeType.User, "user:alice", GrantKind.Consent, ValidTo: Now.AddDays(3650), Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.DataOwner));
        ownerExact.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Secret_has_no_lower_cap()
    {
        var policy = new GrantPolicy();
        var confidential = policy.Evaluate(new GrantRequest(
            GranteeType.User, "user:alice", GrantKind.Consent, ValidTo: Now.AddDays(3650), Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.DataOwner, ProtectionClass: ProtectionClass.Confidential));
        var secret = policy.Evaluate(new GrantRequest(
            GranteeType.User, "user:alice", GrantKind.Consent, ValidTo: Now.AddDays(3650), Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.DataOwner, ProtectionClass: ProtectionClass.Secret));

        confidential.Allowed.ShouldBeTrue();
        secret.Allowed.ShouldBeTrue();
        secret.MaxValidTo.ShouldBe(confidential.MaxValidTo);
    }

    [Fact]
    public void ServicePrincipal_grantee_uses_capacity_maxima()
    {
        var policy = new GrantPolicy();
        var allowed = policy.Evaluate(new GrantRequest(
            GranteeType.ServicePrincipal, "sp:worker-1", GrantKind.Consent, ValidTo: Now.AddDays(3650), Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.Governance));
        allowed.Allowed.ShouldBeTrue();

        var itOver = policy.Evaluate(new GrantRequest(
            GranteeType.ServicePrincipal, "sp:worker-1", GrantKind.Consent, ValidTo: Now.AddDays(400), Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.ItAdmin));
        itOver.Allowed.ShouldBeFalse();
        itOver.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsApproverLimit);
    }

    [Fact]
    public void Past_valid_to_rejected()
    {
        var policy = new GrantPolicy();
        var decision = policy.Evaluate(new GrantRequest(
            GranteeType.User, "user:alice", GrantKind.Consent, ValidTo: Now.AddSeconds(-5), Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.DataOwner));
        decision.Allowed.ShouldBeFalse();
        decision.ErrorCode.ShouldBe(GrantErrorCodes.ExpiryInPast);
    }

    [Fact]
    public void Offset_normalised_to_utc()
    {
        var policy = new GrantPolicy();
        var nonUtc = new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.FromHours(5));
        var decision = policy.Evaluate(new GrantRequest(
            GranteeType.User, "user:alice", GrantKind.Consent, ValidTo: nonUtc, Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.ItAdmin));
        decision.Allowed.ShouldBeTrue();
        decision.MaxValidTo.ShouldBe(Now.AddDays(365));
        decision.MaxValidTo!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Far_future_rejected()
    {
        var policy = new GrantPolicy();
        var decision = policy.Evaluate(new GrantRequest(
            GranteeType.User, "user:alice", GrantKind.Consent, ValidTo: Now.AddYears(20), Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.DataOwner));
        decision.Allowed.ShouldBeFalse();
        decision.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsApproverLimit);
    }

    [Fact]
    public void Tenant_cannot_loosen_deployment_ceiling()
    {
        var policy = new GrantPolicy(
            accessPolicyOptions: Options.Create(new AccessPolicyOptions { MaxItAdminDays = 90 }));
        var tenantPolicy = new TenantAccessPolicy(
            new TenantId("tenant-a"),
            MaxDataOwnerDays: 3650,
            MaxGovernanceDays: 3650,
            MaxItAdminDays: 500,
            MaxSelfGrantDays: 365,
            SelfGrantCeiling: ProtectionClass.Confidential,
            ReminderDays: [7],
            DefaultContainerVisibility: CatalogVisibilityMode.OnRequest,
            Internal: new InternalAudiencePolicy([], []),
            ServeImportSamples: true,
            QuarantineSuspectedPiiSamples: false,
            MaxBundleItems: 50,
            MaxPendingRequestsPerRequester: 100,
            MaxTablesPerAssignment: 1000,
            MaxPageSize: 500);

        var decision = policy.Evaluate(new GrantRequest(
            GranteeType.User, "user:alice", GrantKind.Consent, ValidTo: Now.AddDays(120), Now,
            RequestKind: ConsentRequestKind.Data, Capacity: ApproverCapacity.ItAdmin, TenantPolicy: tenantPolicy));

        decision.Allowed.ShouldBeFalse();
        decision.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsApproverLimit);
        decision.MaxValidTo.ShouldBe(Now.AddDays(90));
    }
}
