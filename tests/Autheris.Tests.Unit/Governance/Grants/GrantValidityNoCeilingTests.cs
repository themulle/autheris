using Autheris.Application.Governance.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants;

/// <summary>
/// Owner decision 2026-11-16: grants may be valid for multiple years and there is no compiled-in maximum. The maximum validity
/// comes only from the required <c>Gateway:Governance:Grants:MaxDuration</c> configuration; it still fails closed.
/// </summary>
public sealed class GrantValidityNoCeilingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    internal static GrantValidityPolicy Validity(string max = "P5Y", string def = "P1Y", string? declarative = "P5Y") =>
        new(Options.Create(new GrantValidityOptions { MaxDuration = max, DefaultDuration = def, DeclarativeMaxDuration = declarative }));

    [Fact]
    public void TestDefault_IsMultiYear_AndDocumentedAsNotAProductionFallback()
    {
        var options = GrantValidityOptions.CreateDefault();

        options.MaxDuration.ShouldBe("P5Y");
        options.DefaultDuration.ShouldBe("P1Y");
        options.DeclarativeMaxDuration.ShouldBe("P5Y");
        GrantValidityPolicy.CreateDefault().MaxDuration.ShouldBe(TimeSpan.FromDays(5 * 365));
        new GrantValidityOptions().MaxDuration.ShouldBeEmpty("production has no compiled-in value");
    }

    [Fact]
    public void ThreeYearRequest_IsResolved_ForAnyChannel()
    {
        var policy = Validity();

        policy.ResolveRequestedValidTo(Now, null, null, durationDays: 3 * 365).ShouldBe(Now.AddDays(3 * 365));
        policy.ResolveRequestedValidTo(Now, null, null, requestedDuration: "P3Y").ShouldBe(Now.AddDays(3 * 365));
        policy.ResolveRequestedValidTo(Now, null, Now.AddDays(3 * 365)).ShouldBe(Now.AddDays(3 * 365));
        policy.ValidateDurationDays(366); // the former 1..365 day cap is gone
        policy.ValidateDurationDays(5 * 365);
    }

    [Fact]
    public void ApprovalOfAThreeYearRequest_IsAllowed_AndGroupsAreBoundedToo()
    {
        var policy = Validity();
        var req = new ConsentRequest { RequestedValidTo = Now.AddDays(3 * 365), RequestedGranteeType = GranteeType.Group };

        policy.ResolveApprovedValidTo(req, null, Now).ShouldBe(Now.AddDays(3 * 365));

        var tooLong = new ConsentRequest { RequestedValidTo = Now.AddDays(5 * 365 + 10), RequestedGranteeType = GranteeType.Group };
        Should.Throw<GrantValidityException>(() => policy.ResolveApprovedValidTo(tooLong, null, Now))
            .ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveDurationDays_StayInvalid(int days)
    {
        var policy = Validity();

        Should.Throw<GrantValidityException>(() => policy.ValidateDurationDays(days)).ErrorCode.ShouldBe("VALIDATION_ERROR");
        Should.Throw<GrantValidityException>(() => policy.ResolveRequestedValidTo(Now, null, null, durationDays: days))
            .ErrorCode.ShouldBe("VALIDATION_ERROR");
    }

    [Fact]
    public void AbsurdDurations_AreRejectedCleanly_WithoutOverflow()
    {
        var policy = Validity();

        Should.Throw<GrantValidityException>(() => policy.ResolveRequestedValidTo(Now, null, null, durationDays: int.MaxValue))
            .ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
        Should.Throw<GrantValidityException>(() => policy.ValidateDurationDays(int.MaxValue))
            .ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
        Should.Throw<GrantValidityException>(() => policy.ResolveRequestedValidTo(Now, null, null, requestedDuration: "P100Y"))
            .ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
        Should.Throw<GrantValidityException>(() => policy.ResolveRequestedValidTo(Now, null, null, requestedDuration: "P99999999999Y"))
            .ErrorCode.ShouldBe("VALIDATION_ERROR");
        Should.Throw<GrantValidityException>(() => policy.ResolveRequestedValidTo(Now, null, DateTimeOffset.MaxValue))
            .ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
    }

    [Fact]
    public void HugeConfiguredMaximum_DoesNotOverflowTheArithmetic()
    {
        var policy = Validity(max: "P20000Y", def: "P1Y", declarative: "P20000Y");

        policy.ResolveRequestedValidTo(Now, null, null, requestedDuration: "P100Y").ShouldBe(Now.AddDays(100 * 365));
        policy.ResolveApprovedValidTo(new ConsentRequest { RequestedValidTo = Now.AddDays(10) }, null, Now).ShouldBe(Now.AddDays(10));
        Should.NotThrow(() => policy.ValidateDeclarativeValidTo(Now.AddDays(10), Now));
    }

    [Fact]
    public void ConfiguredMaximum_ComposesWithTheOptionalClassLimits_MinOverConfiguredOnly()
    {
        var policy = new GrantPolicy(
            Options.Create(new IndividualGrantOptions { SensitiveMaxDays = 90 }),
            Validity(max: "P5Y"));

        var regular = policy.Evaluate(Request(GrantSensitivity.Regular, 3 * 365));
        regular.Allowed.ShouldBeTrue();

        var sensitiveWithinClass = policy.Evaluate(Request(GrantSensitivity.Sensitive, 90));
        sensitiveWithinClass.Allowed.ShouldBeTrue();

        var sensitiveOverClass = policy.Evaluate(Request(GrantSensitivity.Sensitive, 91));
        sensitiveOverClass.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
        sensitiveOverClass.MaxValidTo.ShouldBe(Now.AddDays(90));
    }

    [Fact]
    public void OptionsWithClassLimitBelowOne_AreRejectedByDataAnnotations_ButNullAndMultiYearAreValid()
    {
        static bool IsValid(IndividualGrantOptions o) =>
            System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                o, new System.ComponentModel.DataAnnotations.ValidationContext(o), [], validateAllProperties: true);

        IsValid(new IndividualGrantOptions { RegularMaxDays = 0 }).ShouldBeFalse();
        IsValid(new IndividualGrantOptions { PrivilegedMaxDays = -3 }).ShouldBeFalse();
        IsValid(new IndividualGrantOptions()).ShouldBeTrue();
        IsValid(new IndividualGrantOptions { RegularMaxDays = 3650, UnmaskedMaxDays = 1095, PrivilegedMaxDays = 400 }).ShouldBeTrue();
    }

    private static GrantRequest Request(GrantSensitivity sensitivity, int days) =>
        new(GranteeType.User, "S-1-5-21-ALICE", GrantKind.Consent, Now.AddDays(days), Now, sensitivity,
            Requester: "S-1-5-21-REQ", Justification: "needed for the multi-year project");
}

/// <summary>Fail-closed proof of the configured maximum: no compiled-in ceiling, but also no way around the configured one.</summary>
[Trait("Category", "Security")]
public sealed class GrantValidityConfiguredMaximumSecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DurationAboveTheConfiguredMaximum_IsRefused_OnEveryChannel()
    {
        var policy = GrantValidityNoCeilingTests.Validity(max: "P3Y", def: "P1Y", declarative: "P3Y");
        var over = Now.AddDays(3 * 365 + 1);

        Should.Throw<GrantValidityException>(() => policy.ResolveRequestedValidTo(Now, null, over))
            .ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
        Should.Throw<GrantValidityException>(() => policy.ResolveRequestedValidTo(Now, null, null, durationDays: 3 * 365 + 1))
            .ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
        Should.Throw<GrantValidityException>(() => policy.ValidateDurationDays(3 * 365 + 1))
            .ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
        Should.Throw<GrantValidityException>(() => policy.ResolveApprovedValidTo(
                new ConsentRequest { RequestedValidTo = over }, null, Now))
            .ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
        Should.Throw<GrantPolicyViolationException>(() => policy.ValidateDeclarativeValidTo(over, Now))
            .Decision.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);

        // exactly the maximum is still allowed
        policy.ResolveRequestedValidTo(Now, null, Now.AddDays(3 * 365)).ShouldBe(Now.AddDays(3 * 365));
    }

    [Theory]
    [InlineData(GranteeType.User, GrantSensitivity.Regular, MaskingPolicyMode.Default, false)]
    [InlineData(GranteeType.User, GrantSensitivity.Sensitive, MaskingPolicyMode.Default, false)]
    [InlineData(GranteeType.User, GrantSensitivity.Regular, MaskingPolicyMode.Unmasked, false)]
    [InlineData(GranteeType.User, GrantSensitivity.Regular, MaskingPolicyMode.Default, true)]
    [InlineData(GranteeType.ServicePrincipal, GrantSensitivity.Regular, MaskingPolicyMode.Default, false)]
    [InlineData(GranteeType.Group, GrantSensitivity.Regular, MaskingPolicyMode.Default, false)]
    public void GrantPolicy_RefusesAnExpiryAboveTheConfiguredMaximum_ForEveryGrantClass(GranteeType type, GrantSensitivity sensitivity, MaskingPolicyMode mask, bool privileged)
    {
        var policy = new GrantPolicy(Options.Create(new IndividualGrantOptions()), GrantValidityNoCeilingTests.Validity(max: "P3Y", def: "P1Y", declarative: "P3Y"));
        var request = new GrantRequest(type, "S-1-5-21-ALICE", GrantKind.Consent, Now.AddDays(3 * 365 + 1), Now, sensitivity, mask, privileged,
            Requester: "S-1-5-21-REQ", Justification: "needed for the multi-year project");

        var decision = policy.Evaluate(request);

        decision.Allowed.ShouldBeFalse();
        decision.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
        policy.Evaluate(request with { ValidTo = Now.AddDays(3 * 365) }).Allowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", "P1Y")]
    [InlineData(null, "P1Y")]
    [InlineData("   ", "P1Y")]
    [InlineData("not-a-duration", "P1Y")]
    [InlineData("P0D", "P1D")]
    [InlineData("P1Y", "P2Y")]
    public void Startup_FailsWithoutAValidMaxDuration_OrWhenDefaultExceedsIt(string? max, string def)
    {
        var options = new GrantValidityOptions { MaxDuration = max!, DefaultDuration = def };

        new GrantValidityOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
        Should.Throw<OptionsValidationException>(() => new GrantValidityPolicy(Options.Create(options)));
    }
}
