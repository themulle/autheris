namespace Autheris.Tests.Unit.ConsentRequests;

using System;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class GrantValidityPolicyTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static GrantValidityPolicy CreatePolicy(
        string maxDuration = "P2M",
        string defaultDuration = "P2W",
        string? declarativeMaxDuration = "P2M") =>
        new(Options.Create(new GrantValidityOptions
        {
            MaxDuration = maxDuration,
            DefaultDuration = defaultDuration,
            DeclarativeMaxDuration = declarativeMaxDuration
        }));

    [Theory]
    [InlineData("P14D", 14)]
    [InlineData("P2W", 14)]
    [InlineData("P2M", 60)]
    [InlineData("P1Y", 365)]
    public void Iso8601Duration_ValidDateDurations_ParseExpectedDays(string raw, int expectedDays)
    {
        Iso8601Duration.TryParse(raw, out var duration).ShouldBeTrue();
        duration.ShouldBe(TimeSpan.FromDays(expectedDays));
    }

    [Fact]
    public void Iso8601Duration_TimeAndCombinedComponents_Parse()
    {
        Iso8601Duration.TryParse("PT12H", out var halfDay).ShouldBeTrue();
        halfDay.ShouldBe(TimeSpan.FromHours(12));

        Iso8601Duration.TryParse("P1DT6H30M", out var combined).ShouldBeTrue();
        combined.ShouldBe(TimeSpan.FromHours(30.5));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("P0D")]
    [InlineData("-P1D")]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("14D")]
    [InlineData("P1X")]
    public void Iso8601Duration_InvalidOrZeroDurations_ReturnFalse(string? raw)
    {
        Iso8601Duration.TryParse(raw, out _).ShouldBeFalse();
    }

    [Fact]
    public void OptionsValidator_RejectsInvalidOrInconsistentDurations()
    {
        var validator = new GrantValidityOptionsValidator();

        validator.Validate(null, GrantValidityOptions.CreateDefault()).Succeeded.ShouldBeTrue();
        validator.Validate(null, new GrantValidityOptions { MaxDuration = "bad", DefaultDuration = "P1W" }).Failed.ShouldBeTrue();
        validator.Validate(null, new GrantValidityOptions { MaxDuration = "P1W", DefaultDuration = "P2W" }).Failed.ShouldBeTrue();
        validator.Validate(null, new GrantValidityOptions { MaxDuration = "P2M", DefaultDuration = "P2W", DeclarativeMaxDuration = "not-iso" }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void ResolveRequestedValidTo_DefaultsAndExplicitDuration_WorkWithinBounds()
    {
        var policy = CreatePolicy();

        var defaultEnd = policy.ResolveRequestedValidTo(FixedNow, null, null);
        defaultEnd.ShouldBe(FixedNow.AddDays(14));

        var isoEnd = policy.ResolveRequestedValidTo(FixedNow, null, null, requestedDuration: "P3W");
        isoEnd.ShouldBe(FixedNow.AddDays(21));

        var daysEnd = policy.ResolveRequestedValidTo(FixedNow, null, null, durationDays: 30);
        daysEnd.ShouldBe(FixedNow.AddDays(30));

        var explicitEnd = policy.ResolveRequestedValidTo(FixedNow, null, FixedNow.AddDays(45));
        explicitEnd.ShouldBe(FixedNow.AddDays(45));
    }

    [Fact]
    public void ResolveRequestedValidTo_PastOrExceedingMax_Throws()
    {
        var policy = CreatePolicy();

        var past = Should.Throw<GrantValidityException>(() =>
            policy.ResolveRequestedValidTo(FixedNow, null, FixedNow.AddMinutes(-1)));
        past.ErrorCode.ShouldBe(GrantErrorCodes.ExpiryInPast);

        var tooLong = Should.Throw<GrantValidityException>(() =>
            policy.ResolveRequestedValidTo(FixedNow, null, FixedNow.AddDays(61)));
        tooLong.ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
    }

    [Fact]
    public void ResolveApprovedValidTo_CanShorten_NeverExtendsOrExceedsMax()
    {
        var policy = CreatePolicy();
        var req = new ConsentRequest
        {
            RequestedValidTo = FixedNow.AddDays(30)
        };

        policy.ResolveApprovedValidTo(req, null, FixedNow).ShouldBe(FixedNow.AddDays(30));
        policy.ResolveApprovedValidTo(req, FixedNow.AddDays(10), FixedNow).ShouldBe(FixedNow.AddDays(10));

        var extendEx = Should.Throw<GrantValidityException>(() =>
            policy.ResolveApprovedValidTo(req, FixedNow.AddDays(31), FixedNow));
        extendEx.ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsRequestedCode);

        var elapsedEx = Should.Throw<GrantValidityException>(() =>
            policy.ResolveApprovedValidTo(req, null, FixedNow.AddDays(30)));
        elapsedEx.ErrorCode.ShouldBe(GrantValidityPolicy.RequestValidityElapsedCode);
        elapsedEx.StatusCode.ShouldBe(409);

        var overMaxReq = new ConsentRequest { RequestedValidTo = FixedNow.AddDays(90) };
        var maxEx = Should.Throw<GrantValidityException>(() =>
            policy.ResolveApprovedValidTo(overMaxReq, FixedNow.AddDays(70), FixedNow));
        maxEx.ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
    }

    [Fact]
    public void ValidateDeclarativeValidTo_RequiresFutureAndBoundedExpiry()
    {
        var policy = CreatePolicy(declarativeMaxDuration: "P2M");

        var missing = Should.Throw<GrantPolicyViolationException>(() => policy.ValidateDeclarativeValidTo(null, FixedNow));
        missing.Decision.ErrorCode.ShouldBe(GrantErrorCodes.RequiresExpiry);

        var past = Should.Throw<GrantPolicyViolationException>(() => policy.ValidateDeclarativeValidTo(FixedNow, FixedNow));
        past.Decision.ErrorCode.ShouldBe(GrantErrorCodes.ExpiryInPast);

        var overMax = Should.Throw<GrantPolicyViolationException>(() => policy.ValidateDeclarativeValidTo(FixedNow.AddDays(61), FixedNow));
        overMax.Decision.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);

        Should.NotThrow(() => policy.ValidateDeclarativeValidTo(FixedNow.AddDays(60), FixedNow));
    }

    [Fact]
    public void ValidateDurationDays_EnforcesLowerAndMaxBounds()
    {
        var policy = CreatePolicy();

        Should.Throw<GrantValidityException>(() => policy.ValidateDurationDays(0)).ErrorCode.ShouldBe("VALIDATION_ERROR");
        Should.Throw<GrantValidityException>(() => policy.ValidateDurationDays(61)).ErrorCode.ShouldBe(GrantValidityPolicy.ValidityExceedsMaximumCode);
        Should.NotThrow(() => policy.ValidateDurationDays(60));
    }
}
