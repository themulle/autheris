using Autheris.Application.Governance.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants;

/// <summary>WP-SC-16 / SEC-1009-27: capacity-driven individual and group grant policy.</summary>
public sealed class GrantPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static GrantPolicy Policy(IndividualGrantOptions? o = null, AccessPolicyOptions? access = null) =>
        new(Options.Create(o ?? new IndividualGrantOptions()), accessPolicyOptions: Options.Create(access ?? new AccessPolicyOptions()));

    private static GrantRequest Req(
        TimeSpan? validFor,
        GrantSensitivity sens = GrantSensitivity.Regular,
        MaskingPolicyMode mask = MaskingPolicyMode.Default,
        bool privileged = false,
        GranteeType type = GranteeType.User,
        GrantKind kind = GrantKind.Consent,
        bool customer = false,
        IReadOnlyList<string>? approvers = null,
        string? justification = "needed for the quarterly audit",
        ApproverCapacity? capacity = null) =>
        new(type, "S-1-5-21-ALICE", kind, validFor is null ? null : Now + validFor.Value, Now, sens, mask, privileged, customer,
            "S-1-5-21-REQ", approvers, Justification: justification, Capacity: capacity);

    private static GrantPolicy PolicyWithMax(string max, IndividualGrantOptions? o = null) =>
        new(Options.Create(o ?? new IndividualGrantOptions()),
            new GrantValidityPolicy(Options.Create(new GrantValidityOptions { MaxDuration = max, DefaultDuration = "P1D" })));

    private static readonly TimeSpan ThreeYears = TimeSpan.FromDays(3 * 365);

    [Theory]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, false, GranteeType.User, false)]
    [InlineData(GrantSensitivity.Sensitive, MaskingPolicyMode.Default, false, GranteeType.User, false)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Unmasked, false, GranteeType.User, false)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, true, GranteeType.User, false)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, false, GranteeType.ServicePrincipal, false)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, false, GranteeType.User, true)]
    public void SEC_SC_20_MultiYearGrant_IsAllowedForEverySubject_UnderOwnerCapacity(GrantSensitivity sens, MaskingPolicyMode mask, bool priv, GranteeType type, bool customer)
    {
        var d = Policy().Evaluate(Req(ThreeYears, sens, mask, priv, type, customer: customer, capacity: ApproverCapacity.DataOwner));

        d.Allowed.ShouldBeTrue();
        d.RequiresFourEyes.ShouldBeFalse();
        d.MaxValidTo.ShouldBe(Now + TimeSpan.FromDays(3650));
    }

    [Fact]
    public void SEC_SC_20_ConfiguredMaximum_IsHonored_AndOneSecondOverIsRejected()
    {
        var p = PolicyWithMax("P5Y");
        var max = TimeSpan.FromDays(5 * 365);

        p.Evaluate(Req(max)).Allowed.ShouldBeTrue();

        var over = p.Evaluate(Req(max + TimeSpan.FromSeconds(1)));
        over.Allowed.ShouldBeFalse();
        over.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
        over.MaxValidTo.ShouldBe(Now + max);
    }

    [Fact]
    public void SEC_SC_20_OperatorConfiguredClassLimit_ShortensTheWindow()
    {
        var p = Policy(new IndividualGrantOptions
        {
            SensitiveMaxDays = 20, UnmaskedMaxDays = 20, PrivilegedMaxDays = 20, RegularMaxDays = 20,
        });

        p.Evaluate(Req(TimeSpan.FromDays(20))).Allowed.ShouldBeTrue();
        var over = p.Evaluate(Req(TimeSpan.FromDays(21)));
        over.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
        over.MaxValidTo.ShouldBe(Now + TimeSpan.FromDays(20));
    }

    [Fact]
    public void SEC_SC_20_ClassLimit_NeverLengthensTheTenantMaximum()
    {
        var p = PolicyWithMax("P1Y", new IndividualGrantOptions { RegularMaxDays = 4000 });

        p.Evaluate(Req(TimeSpan.FromDays(365))).Allowed.ShouldBeTrue();
        p.Evaluate(Req(TimeSpan.FromDays(366))).ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
    }

    [Fact]
    public void SEC_SC_20_AbsurdlyLargeMaximum_DoesNotOverflow()
    {
        var p = new GrantPolicy(
            Options.Create(new IndividualGrantOptions { RegularMaxDays = int.MaxValue }),
            new GrantValidityPolicy(Options.Create(new GrantValidityOptions { MaxDuration = "P20000Y", DefaultDuration = "P1D", DeclarativeMaxDuration = "P20000Y" })),
            Options.Create(new AccessPolicyOptions { MaxDataOwnerDays = 36500 }));

        var d = p.Evaluate(Req(TimeSpan.FromDays(36500)));

        d.Allowed.ShouldBeTrue();
        d.MaxValidTo.ShouldBe(Now + TimeSpan.FromDays(36500));
    }

    [Theory]
    [InlineData(GrantKind.Consent)]
    [InlineData(GrantKind.AccessProfileAssignment)]
    [InlineData(GrantKind.VirtualFilterProfile)]
    [InlineData(GrantKind.ManifestPolicy)]
    [InlineData(GrantKind.McpAdminGrant)]
    [InlineData(GrantKind.BulkGrant)]
    public void SEC_SC_20_MissingExpiry_IsRejected_OnEveryGrantKind(GrantKind kind)
    {
        var d = Policy().Evaluate(Req(null, kind: kind));

        d.Allowed.ShouldBeFalse();
        d.ErrorCode.ShouldBe(GrantErrorCodes.RequiresExpiry);
    }

    [Fact]
    public void SEC_SC_20_ExpiryInThePast_OrNow_IsRejected()
    {
        Policy().Evaluate(Req(TimeSpan.FromDays(-1))).ErrorCode.ShouldBe(GrantErrorCodes.ExpiryInPast);
        Policy().Evaluate(Req(TimeSpan.Zero)).ErrorCode.ShouldBe(GrantErrorCodes.ExpiryInPast);
    }

    [Theory]
    [InlineData(GranteeType.Group)]
    public void SEC_SC_20_StandingGroupGrantWithoutCapacity_IsStanding(GranteeType type)
    {
        var d = Policy().Evaluate(Req(null, type: type));

        d.Allowed.ShouldBeTrue();
        d.Class.ShouldBe(GrantClass.Standing);
    }

    [Fact]
    public void SEC_SC_20_NonPositiveConfiguration_FailsClosedToShortestSpan()
    {
        var p = Policy(new IndividualGrantOptions { RegularMaxDays = 0 });
        p.Evaluate(Req(TimeSpan.FromDays(2))).ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
    }

    [Fact]
    public void SEC_SC_20_CustomerRealm_RegularAllowed_UnmaskedAndPrivilegedDisabled()
    {
        var p = Policy();
        p.Evaluate(Req(ThreeYears, customer: true)).Allowed.ShouldBeTrue();
        p.Evaluate(Req(TimeSpan.FromDays(7), mask: MaskingPolicyMode.Unmasked, customer: true)).ErrorCode.ShouldBe(GrantErrorCodes.DisabledForRealm);
        p.Evaluate(Req(TimeSpan.FromDays(1), privileged: true, customer: true)).ErrorCode.ShouldBe(GrantErrorCodes.DisabledForRealm);

        var limited = Policy(new IndividualGrantOptions { CustomerRegularMaxDays = 90 });
        limited.Evaluate(Req(TimeSpan.FromDays(90), customer: true)).Allowed.ShouldBeTrue();
        limited.Evaluate(Req(TimeSpan.FromDays(91), customer: true)).ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);

        var enabled = Policy(new IndividualGrantOptions { AllowCustomerUnmasked = true });
        enabled.Evaluate(Req(ThreeYears, mask: MaskingPolicyMode.Unmasked, customer: true, capacity: ApproverCapacity.DataOwner)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void SEC_SC_20_SingleApproverSufficient_NeverRequiresFourEyes()
    {
        var d = Policy().Evaluate(Req(TimeSpan.FromDays(30), GrantSensitivity.Sensitive, capacity: ApproverCapacity.DataOwner));
        d.Allowed.ShouldBeTrue();
        d.RequiresFourEyes.ShouldBeFalse();
    }

    [Fact]
    public void SEC_SC_20_Renewal_ByTheGrantee_IsRejected()
    {
        var r = Req(TimeSpan.FromDays(30), kind: GrantKind.Renewal) with { Renewer = "s-1-5-21-ALICE" };
        Policy().Evaluate(r).ErrorCode.ShouldBe(GrantErrorCodes.RenewalByGrantee);

        Policy().Evaluate(r with { Renewer = null }).ErrorCode.ShouldBe(GrantErrorCodes.RenewalByGrantee);
        Policy().Evaluate(r with { Renewer = "OWNER" }).Allowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    [InlineData(2, true, true)]
    [InlineData(3, false, false)]
    public void SEC_SC_20_Renewal_HintsRoleConversion_ThenRequiresCampaign(int previousRenewals, bool hint, bool allowed)
    {
        var d = Policy().Evaluate(Req(TimeSpan.FromDays(30), kind: GrantKind.Renewal) with { Renewer = "OWNER", RenewalCount = previousRenewals });

        d.Allowed.ShouldBe(allowed);
        d.SuggestRoleConversion.ShouldBe(hint);
        if (!allowed)
        {
            d.ErrorCode.ShouldBe(GrantErrorCodes.RenewalRequiresCampaign);
            d.RequiresCampaignDecision.ShouldBeTrue();
        }
    }

    [Fact]
    public void SEC_SC_20_GroupGrantee_IsBoundedByTheTenantMaximum()
    {
        var p = PolicyWithMax("P1Y");

        p.Evaluate(Req(TimeSpan.FromDays(365), type: GranteeType.Group)).Allowed.ShouldBeTrue();
        var over = p.Evaluate(Req(TimeSpan.FromDays(366), type: GranteeType.Group));
        over.Allowed.ShouldBeFalse();
        over.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
    }

    [Fact]
    public void SEC_SC_20_UnknownGranteeType_FailsClosed()
    {
        var r = Req(TimeSpan.FromDays(1)) with { GranteeType = (GranteeType)99 };
        Policy().Evaluate(r).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void SEC_SC_20_NullRequest_FailsClosed()
    {
        Policy().Evaluate(null!).Allowed.ShouldBeFalse();
    }
}
