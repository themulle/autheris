using Autheris.Application.Governance.Policy;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Policy;

[Trait("Category", "Security")]
public sealed class AccessPolicyOptionsValidatorTests
{
    private readonly AccessPolicyOptionsValidator _validator = new();

    [Fact]
    public void Defaults_pass_validation()
    {
        var result = _validator.Validate(null, new AccessPolicyOptions());
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Ceiling_secret_rejected()
    {
        var result = _validator.Validate(null, new AccessPolicyOptions
        {
            SelfGrantCeiling = ProtectionClass.Secret
        });

        result.Succeeded.ShouldBeFalse();
        result.FailureMessage.ShouldNotBeNull();
        result.FailureMessage.ShouldContain("Secret", Case.Insensitive);
    }

    [Theory]
    [InlineData(0, 3650, 365, 365, 50, 100, 1000, 500)]
    [InlineData(3650, 0, 365, 365, 50, 100, 1000, 500)]
    [InlineData(3650, 3650, -1, 365, 50, 100, 1000, 500)]
    [InlineData(3650, 3650, 365, 0, 50, 100, 1000, 500)]
    [InlineData(3650, 3650, 365, 365, 0, 100, 1000, 500)]
    [InlineData(3650, 3650, 365, 365, 50, 0, 1000, 500)]
    [InlineData(3650, 3650, 365, 365, 50, 100, 0, 500)]
    [InlineData(3650, 3650, 365, 365, 50, 100, 1000, -5)]
    public void Non_positive_maxima_rejected(
        int maxDataOwner,
        int maxGov,
        int maxIt,
        int maxSelfGrant,
        int maxBundle,
        int maxPending,
        int maxTables,
        int maxPage)
    {
        var result = _validator.Validate(null, new AccessPolicyOptions
        {
            MaxDataOwnerDays = maxDataOwner,
            MaxGovernanceDays = maxGov,
            MaxItAdminDays = maxIt,
            MaxSelfGrantDays = maxSelfGrant,
            MaxBundleItems = maxBundle,
            MaxPendingRequestsPerRequester = maxPending,
            MaxTablesPerAssignment = maxTables,
            MaxPageSize = maxPage
        });

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void Reminder_list_bounds_enforced()
    {
        _validator.Validate(null, new AccessPolicyOptions { ReminderDays = [0] }).Succeeded.ShouldBeFalse();
        _validator.Validate(null, new AccessPolicyOptions { ReminderDays = [366] }).Succeeded.ShouldBeFalse();
        _validator.Validate(null, new AccessPolicyOptions { ReminderDays = Enumerable.Range(1, 11).ToList() }).Succeeded.ShouldBeFalse();
        _validator.Validate(null, new AccessPolicyOptions { ReminderDays = [365, 30, 7, 1] }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Internal_sids_and_issuer_guids_validated()
    {
        _validator.Validate(null, new AccessPolicyOptions
        {
            Internal = new InternalAccessPolicySettings { TrustedIssuerTenantIds = ["not-a-guid"] }
        }).Succeeded.ShouldBeFalse();

        _validator.Validate(null, new AccessPolicyOptions
        {
            Internal = new InternalAccessPolicySettings { EmployeeGroupSids = ["not-a-sid"] }
        }).Succeeded.ShouldBeFalse();

        _validator.Validate(null, new AccessPolicyOptions
        {
            Internal = new InternalAccessPolicySettings
            {
                TrustedIssuerTenantIds = [Guid.NewGuid().ToString("D")],
                EmployeeGroupSids = ["S-1-5-21-1001-2002-3003-512"],
                TrustedServicePrincipalClientIds = ["app-client-1"]
            }
        }).Succeeded.ShouldBeTrue();
    }
}
