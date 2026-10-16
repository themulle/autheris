using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.AccessProfiles;

/// <summary>WP-SC-03 / SEC-SC-04 / SEC-SC-20: the matrix of the assignment rules (write validation, read-time effectiveness, precedence).</summary>
[Trait("Category", "Security")]
public sealed class AccessProfileAssignmentRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("tenant-a");

    private static AccessProfile Profile(MaskingPolicyMode mode, string id = "p1") => new()
    {
        ProfileId = id, Name = id, TenantId = Tenant, MaskingMode = mode, TargetTables = ["*.*"], CreatedAt = Now.AddDays(-400)
    };

    private static AccessProfileAssignment User(string subject = "alice", int? days = 10, string? created = "req", string? approved = "app", string? why = "need") => new()
    {
        Subject = subject, SubjectType = AccessProfileSubjectType.User, AssignedAt = Now,
        ExpiresAt = days is { } d ? Now.AddDays(d) : null, CreatedBy = created, ApprovedBy = approved, Justification = why
    };

    private static void Rejected(Action act, string code) =>
        Should.Throw<AccessProfileAssignmentRejectedException>(act).Code.ShouldBe(code);

    [Fact]
    public void IndividualGrant_WithoutExpiry_IsRejected_ForEveryMode()
    {
        foreach (var mode in Enum.GetValues<MaskingPolicyMode>())
        {
            Rejected(() => AccessProfileAssignmentRules.ValidateForAssignment(Profile(mode), User(days: null), Now), AccessProfileAssignmentRules.RequiresExpiry);
        }
    }

    [Fact]
    public void Assignment_WithExpiryInThePast_IsRejected() =>
        Rejected(() => AccessProfileAssignmentRules.ValidateForAssignment(Profile(MaskingPolicyMode.Default), User(days: -1), Now), AccessProfileAssignmentRules.ExpiryInThePast);

    [Fact]
    public void RegularIndividualGrant_IsLimitedTo180Days()
    {
        AccessProfileAssignmentRules.ValidateForAssignment(Profile(MaskingPolicyMode.Strict), User(days: 180), Now);
        Rejected(() => AccessProfileAssignmentRules.ValidateForAssignment(Profile(MaskingPolicyMode.Strict), User(days: 181), Now), AccessProfileAssignmentRules.TooLong);
    }

    [Theory]
    [InlineData(null, "app", "need")]
    [InlineData("req", null, "need")]
    [InlineData("req", "req", "need")]
    [InlineData("REQ", "req", "need")]
    [InlineData("req", "alice", "need")]
    [InlineData("alice", "app", "need")]
    [InlineData("req", "app", null)]
    [InlineData("req", "app", " ")]
    public void IndividualUnmasked_WithoutFourEyes_IsRejected(string? created, string? approved, string? why) =>
        Rejected(() => AccessProfileAssignmentRules.ValidateForAssignment(Profile(MaskingPolicyMode.Unmasked), User(created: created, approved: approved, why: why), Now),
            AccessProfileAssignmentRules.FourEyesRequired);

    [Fact]
    public void IndividualUnmasked_IsLimitedTo30Days()
    {
        AccessProfileAssignmentRules.ValidateForAssignment(Profile(MaskingPolicyMode.Unmasked), User(days: 30), Now);
        Rejected(() => AccessProfileAssignmentRules.ValidateForAssignment(Profile(MaskingPolicyMode.Unmasked), User(days: 31), Now), AccessProfileAssignmentRules.TooLong);
    }

    [Fact]
    public void UnmaskedGroupMapping_CanBeStoredPending_ButIsNotEffectiveUntilApprovedByASecondAdmin()
    {
        var profile = Profile(MaskingPolicyMode.Unmasked);
        var pending = new AccessProfileAssignment { Subject = "AUT-Unmask", SubjectType = AccessProfileSubjectType.Group, AssignedAt = Now, CreatedBy = "adm1" };
        AccessProfileAssignmentRules.ValidateForAssignment(profile, pending, Now);

        AccessProfileAssignmentRules.IsEffective(profile, pending, Now).ShouldBeFalse();
        AccessProfileAssignmentRules.IsEffective(profile, pending with { ApprovedBy = "adm1" }, Now).ShouldBeFalse();
        AccessProfileAssignmentRules.IsEffective(profile, pending with { ApprovedBy = null, CreatedBy = null }, Now).ShouldBeFalse();
        AccessProfileAssignmentRules.IsEffective(profile, pending with { ApprovedBy = "adm2", ApprovedAt = Now }, Now).ShouldBeTrue();
        AccessProfileAssignmentRules.IsEffective(profile, pending with { SubjectType = AccessProfileSubjectType.Role, ApprovedBy = "adm2", ApprovedAt = Now }, Now).ShouldBeTrue();
    }

    [Fact]
    public void DefaultAndStrictGroupMappings_AreEffective_WithoutApproval()
    {
        var mapping = new AccessProfileAssignment { Subject = "G", SubjectType = AccessProfileSubjectType.Group, AssignedAt = Now };

        AccessProfileAssignmentRules.IsEffective(Profile(MaskingPolicyMode.Strict), mapping, Now).ShouldBeTrue();
        AccessProfileAssignmentRules.IsEffective(Profile(MaskingPolicyMode.Default), mapping, Now).ShouldBeTrue();
    }

    [Fact]
    public void IndividualUnmasked_IsEffectiveOnlyWithFourEyesAndExplicitExpiry()
    {
        var profile = Profile(MaskingPolicyMode.Unmasked);

        AccessProfileAssignmentRules.IsEffective(profile, User(), Now.AddDays(1)).ShouldBeTrue();
        AccessProfileAssignmentRules.IsEffective(profile, User(), Now.AddDays(10)).ShouldBeFalse();
        AccessProfileAssignmentRules.IsEffective(profile, User(approved: null), Now.AddDays(1)).ShouldBeFalse();
        AccessProfileAssignmentRules.IsEffective(profile, User(days: null), Now.AddDays(1)).ShouldBeFalse();
    }

    [Fact]
    public void LegacyIndividualAssignmentWithoutExpiry_IsClampedAndInactiveAfterTheClamp()
    {
        var regular = Profile(MaskingPolicyMode.Default);
        var legacy = User(days: null, created: null, approved: null, why: null);

        AccessProfileAssignmentRules.EffectiveExpiry(regular, legacy).ShouldBe(Now.AddDays(180));
        AccessProfileAssignmentRules.IsEffective(regular, legacy, Now.AddDays(179)).ShouldBeTrue();
        AccessProfileAssignmentRules.IsEffective(regular, legacy, Now.AddDays(181)).ShouldBeFalse();
    }

    [Fact]
    public void LegacyIndividualAssignmentOverTheLimit_IsClampedToTheMaximum()
    {
        var unmasked = Profile(MaskingPolicyMode.Unmasked);
        var longGrant = User(days: 300);

        AccessProfileAssignmentRules.EffectiveExpiry(unmasked, longGrant).ShouldBe(Now.AddDays(30));
        AccessProfileAssignmentRules.IsEffective(unmasked, longGrant, Now.AddDays(31)).ShouldBeFalse();
    }

    [Fact]
    public void ProfileValidTo_StillEndsEveryAssignment()
    {
        var profile = new AccessProfile { ProfileId = "p", Name = "p", TenantId = Tenant, ValidTo = Now.AddDays(1), MaskingMode = MaskingPolicyMode.Default };

        AccessProfileAssignmentRules.IsEffective(profile, User(), Now.AddDays(2)).ShouldBeFalse();
    }

    private static AccessProfileAssignment BreakGlass(int hours = 24, string? approved = null, string? why = "outage") => new()
    {
        Subject = "alice", SubjectType = AccessProfileSubjectType.User, Kind = AccessProfileAssignmentKind.BreakGlass,
        AssignedAt = Now, ExpiresAt = Now.AddHours(hours), CreatedBy = "req", ApprovedBy = approved, Justification = why
    };

    [Fact]
    public void BreakGlass_IsLimitedTo72Hours_AndNeedsAJustification()
    {
        var profile = Profile(MaskingPolicyMode.Unmasked);
        AccessProfileAssignmentRules.ValidateForAssignment(profile, BreakGlass(72), Now);
        Rejected(() => AccessProfileAssignmentRules.ValidateForAssignment(profile, BreakGlass(73), Now), AccessProfileAssignmentRules.TooLong);
        Rejected(() => AccessProfileAssignmentRules.ValidateForAssignment(profile, BreakGlass(why: null), Now), AccessProfileAssignmentRules.BreakGlassRequiresJustification);
        Rejected(() => AccessProfileAssignmentRules.ValidateForAssignment(profile, BreakGlass(approved: "req"), Now), AccessProfileAssignmentRules.SameApprover);
    }

    [Fact]
    public void BreakGlass_WithoutApproval_ExpiresAfter24Hours_AndAtItsOwnExpiry()
    {
        var profile = Profile(MaskingPolicyMode.Unmasked);
        var open = BreakGlass(72);

        AccessProfileAssignmentRules.IsEffective(profile, open, Now.AddHours(23)).ShouldBeTrue();
        AccessProfileAssignmentRules.IsEffective(profile, open, Now.AddHours(25)).ShouldBeFalse();
        AccessProfileAssignmentRules.IsEffective(profile, open with { ApprovedBy = "app" }, Now.AddHours(25)).ShouldBeTrue();
        AccessProfileAssignmentRules.IsEffective(profile, open with { ApprovedBy = "app" }, Now.AddHours(73)).ShouldBeFalse();
        AccessProfileAssignmentRules.IsEffective(profile, open with { ApprovedBy = "req" }, Now.AddHours(1)).ShouldBeFalse();
    }

    [Fact]
    public void BreakGlass_StoredWithALongerExpiry_IsClampedTo72Hours()
    {
        var profile = Profile(MaskingPolicyMode.Unmasked);
        var tooLong = BreakGlass(500, approved: "app");

        AccessProfileAssignmentRules.EffectiveExpiry(profile, tooLong).ShouldBe(Now.AddHours(72));
        AccessProfileAssignmentRules.IsEffective(profile, tooLong, Now.AddHours(80)).ShouldBeFalse();
    }

    [Fact]
    public void Approver_CannotBeTheGrantee_NorTheRequesterOnFourEyesCases()
    {
        var unmasked = Profile(MaskingPolicyMode.Unmasked);
        var assignment = User(created: "req");

        Rejected(() => AccessProfileAssignmentRules.ValidateApprover(unmasked, assignment, "ALICE"), AccessProfileAssignmentRules.SelfRenewal);
        Rejected(() => AccessProfileAssignmentRules.ValidateApprover(unmasked, assignment, "Req"), AccessProfileAssignmentRules.SameApprover);
        AccessProfileAssignmentRules.ValidateApprover(unmasked, assignment, "owner");
        AccessProfileAssignmentRules.ValidateApprover(Profile(MaskingPolicyMode.Default), assignment, "req");
    }

    [Fact]
    public void Precedence_IsStrictThenDefaultThenUnmasked_WithOrdinalTieBreak()
    {
        var ordered = AccessProfileAssignmentRules.Order([
            Profile(MaskingPolicyMode.Unmasked, "a"), Profile(MaskingPolicyMode.Default, "z"), Profile(MaskingPolicyMode.Strict, "m"),
            Profile(MaskingPolicyMode.Default, "b"), Profile(MaskingPolicyMode.Strict, "B")]);

        ordered.Select(p => p.ProfileId).ShouldBe(["B", "m", "b", "z", "a"]);
    }

    [Fact]
    public void Precedence_IsIndependentOfInputOrder()
    {
        var profiles = new[] { Profile(MaskingPolicyMode.Unmasked, "u"), Profile(MaskingPolicyMode.Strict, "s"), Profile(MaskingPolicyMode.Default, "d") };

        var forward = AccessProfileAssignmentRules.Order(profiles).Select(p => p.ProfileId).ToArray();
        var backward = AccessProfileAssignmentRules.Order(profiles.Reverse()).Select(p => p.ProfileId).ToArray();

        forward.ShouldBe(backward);
        forward.ShouldBe(["s", "d", "u"]);
    }

    [Fact]
    public void ProfileClamp_AppliesToLegacyProfilesOnly_AssignmentBackedProfilesAreBoundedPerAssignment()
    {
        var legacy = Profile(MaskingPolicyMode.Unmasked);
        legacy.IsActive(Now).ShouldBeFalse(); // created 400 days ago, no assignments: clamped to 30 days

        var backed = Profile(MaskingPolicyMode.Unmasked);
        backed.Assignments.Add(new AccessProfileAssignment
        {
            Subject = "analysts", SubjectType = AccessProfileSubjectType.Group, AssignedAt = Now.AddDays(-1), CreatedBy = "req", ApprovedBy = "app", ApprovedAt = Now.AddDays(-1)
        });
        backed.IsActive(Now).ShouldBeTrue();
        AccessProfileAssignmentRules.IsEffective(backed, backed.Assignments[0], Now).ShouldBeTrue();
        AccessProfileAssignmentRules.IsEffective(backed, backed.Assignments[0] with { ApprovedBy = null }, Now).ShouldBeFalse();
    }

    [Fact]
    public void SEC_SC_04_UnmaskedMapping_LapsesAfter90DaysWithoutRecertification_AndNeedsAnApprovalTime()
    {
        var profile = Profile(MaskingPolicyMode.Unmasked);
        var mapping = new AccessProfileAssignment
        {
            Subject = "AUT-Unmask", SubjectType = AccessProfileSubjectType.Group, AssignedAt = Now.AddDays(-100), CreatedBy = "adm1", ApprovedBy = "adm2", ApprovedAt = Now.AddDays(-89)
        };

        AccessProfileAssignmentRules.IsEffective(profile, mapping, Now).ShouldBeTrue();
        AccessProfileAssignmentRules.IsEffective(profile, mapping with { ApprovedAt = Now.AddDays(-91) }, Now).ShouldBeFalse();
        AccessProfileAssignmentRules.IsEffective(profile, mapping with { ApprovedAt = null }, Now).ShouldBeFalse();
        AccessProfileAssignmentRules.EffectiveUntil(profile, mapping).ShouldBe(Now.AddDays(1));
    }

    [Fact]
    public void SEC_SC_04_CachedProfile_IsReCheckedForEveryAssignmentExpiry_IncludingUnapprovedBreakGlass()
    {
        var profile = Profile(MaskingPolicyMode.Unmasked);
        var breakGlass = new AccessProfileAssignment
        {
            Subject = "alice", AssignedAt = Now, ExpiresAt = Now.AddHours(60), CreatedBy = "req", Justification = "incident", Kind = AccessProfileAssignmentKind.BreakGlass
        };
        profile.Assignments.Add(breakGlass);

        profile.IsActive(Now.AddHours(23)).ShouldBeTrue();
        profile.IsActive(Now.AddHours(25)).ShouldBeFalse("an unapproved break-glass lapses after 24 h even when the profile is served from the cache");
    }

    [Fact]
    public void SEC_SC_04_CachedProfile_StaysActiveWhileAnyAssignmentIsEffective()
    {
        var profile = Profile(MaskingPolicyMode.Strict);
        profile.Assignments.Add(new AccessProfileAssignment { Subject = "u1", AssignedAt = Now, ExpiresAt = Now.AddDays(1) });
        profile.Assignments.Add(new AccessProfileAssignment { Subject = "u2", AssignedAt = Now, ExpiresAt = Now.AddDays(10) });

        profile.IsActive(Now.AddDays(5)).ShouldBeTrue();
        profile.IsActive(Now.AddDays(11)).ShouldBeFalse();
    }
}
