using Autheris.Application.Governance.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants;

/// <summary>WP-SC-16 / SEC-SC-20: time-bound individual grant policy (class x duration x four-eyes matrix); no compiled-in ceilings.</summary>
public sealed class GrantPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static GrantPolicy Policy(IndividualGrantOptions? o = null) => new(Options.Create(o ?? new IndividualGrantOptions()));

    private static GrantRequest Req(
        TimeSpan? validFor,
        GrantSensitivity sens = GrantSensitivity.Regular,
        MaskingPolicyMode mask = MaskingPolicyMode.Default,
        bool privileged = false,
        GranteeType type = GranteeType.User,
        GrantKind kind = GrantKind.Consent,
        bool customer = false,
        IReadOnlyList<string>? approvers = null,
        string? justification = "needed for the quarterly audit") =>
        new(type, "S-1-5-21-ALICE", kind, validFor is null ? null : Now + validFor.Value, Now, sens, mask, privileged, customer,
            "S-1-5-21-REQ", approvers, Justification: justification);

    // Owner decision 2026-11-16: no compiled-in ceilings. The maximum comes from configuration only (test default P5Y here).
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
    public void SEC_SC_20_MultiYearGrant_IsAllowedForEveryClass_UnderTheConfiguredMaximum(GrantSensitivity sens, MaskingPolicyMode mask, bool priv, GranteeType type, bool customer)
    {
        // Unmasked/privileged in a customer realm stay disabled by default; the customer case here is regular data.
        var d = Policy().Evaluate(Req(ThreeYears, sens, mask, priv, type, customer: customer));

        d.Allowed.ShouldBeTrue();
        d.MaxValidTo.ShouldBe(Now + TimeSpan.FromDays(5 * 365));
    }

    [Theory]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, false, GranteeType.User)]
    [InlineData(GrantSensitivity.Sensitive, MaskingPolicyMode.Default, false, GranteeType.User)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Unmasked, false, GranteeType.User)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, true, GranteeType.User)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, false, GranteeType.ServicePrincipal)]
    public void SEC_SC_20_ConfiguredMaximum_IsTheOnlyCeiling_AndOneSecondOverIsRejected(GrantSensitivity sens, MaskingPolicyMode mask, bool priv, GranteeType type)
    {
        var p = PolicyWithMax("P5Y");
        var max = TimeSpan.FromDays(5 * 365);

        p.Evaluate(Req(max, sens, mask, priv, type)).Allowed.ShouldBeTrue();

        var over = p.Evaluate(Req(max + TimeSpan.FromSeconds(1), sens, mask, priv, type));
        over.Allowed.ShouldBeFalse();
        over.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
        over.MaxValidTo.ShouldBe(Now + max);
    }

    [Theory]
    [InlineData(GrantSensitivity.Sensitive, MaskingPolicyMode.Default, false, GranteeType.User)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Unmasked, false, GranteeType.User)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, true, GranteeType.User)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, false, GranteeType.ServicePrincipal)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, false, GranteeType.User)]
    public void SEC_SC_20_OperatorConfiguredClassLimit_StillShortensTheClass(GrantSensitivity sens, MaskingPolicyMode mask, bool priv, GranteeType type)
    {
        var p = Policy(new IndividualGrantOptions
        {
            SensitiveMaxDays = 20, UnmaskedMaxDays = 20, PrivilegedMaxDays = 20, ServicePrincipalMaxDays = 20, RegularMaxDays = 20,
        });

        p.Evaluate(Req(TimeSpan.FromDays(20), sens, mask, priv, type)).Allowed.ShouldBeTrue();
        var over = p.Evaluate(Req(TimeSpan.FromDays(21), sens, mask, priv, type));
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
        var p = PolicyWithMax("P20000Y", new IndividualGrantOptions { RegularMaxDays = int.MaxValue });

        var d = p.Evaluate(Req(TimeSpan.FromDays(365 * 100)));

        d.Allowed.ShouldBeTrue();
        d.MaxValidTo.ShouldBe(DateTimeOffset.MaxValue);
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
    [InlineData(GranteeType.Role)]
    public void SEC_SC_20_GroupAndRoleGrants_AreStanding_NotLimited(GranteeType type)
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
    public void SEC_SC_20_CustomerRealm_MultiYearRegular_Allowed_UnmaskedAndPrivilegedDisabled()
    {
        var p = Policy();
        p.Evaluate(Req(ThreeYears, customer: true)).Allowed.ShouldBeTrue();
        p.Evaluate(Req(TimeSpan.FromDays(7), mask: MaskingPolicyMode.Unmasked, customer: true)).ErrorCode.ShouldBe(GrantErrorCodes.DisabledForRealm);
        p.Evaluate(Req(TimeSpan.FromDays(1), privileged: true, customer: true)).ErrorCode.ShouldBe(GrantErrorCodes.DisabledForRealm);

        var limited = Policy(new IndividualGrantOptions { CustomerRegularMaxDays = 90 });
        limited.Evaluate(Req(TimeSpan.FromDays(90), customer: true)).Allowed.ShouldBeTrue();
        limited.Evaluate(Req(TimeSpan.FromDays(91), customer: true)).ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);

        var enabled = Policy(new IndividualGrantOptions { AllowCustomerUnmasked = true });
        enabled.Evaluate(Req(ThreeYears, mask: MaskingPolicyMode.Unmasked, customer: true, approvers: ["A", "B"])).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void SEC_SC_20_Unmasked_RequiresJustification()
    {
        Policy().Evaluate(Req(TimeSpan.FromDays(7), mask: MaskingPolicyMode.Unmasked, justification: " ")).ErrorCode
            .ShouldBe(GrantErrorCodes.JustificationRequired);
    }

    [Fact]
    public void SEC_SC_20_SensitiveGrant_NeedsTwoIndependentApprovers_WhenApprovalsAreEnforced()
    {
        var p = Policy();
        var days = TimeSpan.FromDays(30);

        // Request time: approvals not presented yet, only duration/expiry are validated.
        p.Evaluate(Req(days, GrantSensitivity.Sensitive)).Allowed.ShouldBeTrue();
        p.Evaluate(Req(days, GrantSensitivity.Sensitive)).RequiresFourEyes.ShouldBeTrue();

        // Activation time: requester and grantee never count as approvers; duplicates count once.
        p.Evaluate(Req(days, GrantSensitivity.Sensitive, approvers: ["OWNER"])).ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        p.Evaluate(Req(days, GrantSensitivity.Sensitive, approvers: ["OWNER", "owner"])).ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        p.Evaluate(Req(days, GrantSensitivity.Sensitive, approvers: ["OWNER", "S-1-5-21-REQ"])).ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        p.Evaluate(Req(days, GrantSensitivity.Sensitive, approvers: ["OWNER", "s-1-5-21-alice"])).ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        p.Evaluate(Req(days, GrantSensitivity.Sensitive, approvers: [])).ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        p.Evaluate(Req(days, GrantSensitivity.Sensitive, approvers: ["OWNER", "SECOND"])).Allowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, false, false, GrantRecertClass.Campaign365)]
    [InlineData(GrantSensitivity.Sensitive, MaskingPolicyMode.Default, false, true, GrantRecertClass.Campaign90)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Unmasked, false, true, GrantRecertClass.Campaign90)]
    [InlineData(GrantSensitivity.Regular, MaskingPolicyMode.Default, true, true, GrantRecertClass.Campaign90)]
    public void SEC_SC_20_RecertClass_AndFourEyes_FollowTheClass(GrantSensitivity s, MaskingPolicyMode m, bool priv, bool fourEyes, GrantRecertClass recert)
    {
        var d = Policy().Evaluate(Req(TimeSpan.FromDays(1), s, m, priv));

        d.Allowed.ShouldBeTrue();
        d.RequiresFourEyes.ShouldBe(fourEyes);
        d.RecertClass.ShouldBe(recert);
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
    public void SEC_SC_20_RenewalNewValidTo_IsCappedByTheConfiguredMaximum()
    {
        var r = Req(TimeSpan.FromDays(5 * 365 + 1), kind: GrantKind.Renewal) with { Renewer = "OWNER" };
        Policy().Evaluate(r).ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
        Policy().Evaluate(r with { ValidTo = Now + ThreeYears }).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void SEC_SC_20_GroupGrantee_IsBoundedByTheTenantMaximumLikeEveryone()
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
