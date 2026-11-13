namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Application.Policy;
using Autheris.Domain.Governance;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>Roles never grant data access: a role grantee matches no grant, but a role-bound restriction keeps applying.</summary>
public sealed class RoleIndependenceTests
{
    private static readonly Sid User = new("S-1-5-21-ALICE-1001");
    private static readonly IReadOnlySet<Sid> NoGroups = new HashSet<Sid>();
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(["analysts"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    [Trait("Category", "Security")]
    public void RoleGranteeConsent_NeverMatches()
    {
        GranteeMatcher.MatchesGrant(GranteeType.Role, null, User, NoGroups).ShouldBeFalse();
        GranteeMatcher.MatchesGrant(GranteeType.Role, User, User, NoGroups).ShouldBeFalse("a role grantee never matches, whatever SID it carries");
        GranteeMatcher.MatchesGrant(GranteeType.User, User, User, NoGroups).ShouldBeTrue();
        GranteeMatcher.MatchesGrant(GranteeType.Group, new Sid("G1"), User, new HashSet<Sid> { new("G1") }).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RoleBoundRestriction_StillMatches()
    {
        GranteeMatcher.MatchesRestriction(GranteeType.Role, null, "analysts", null, User, NoGroups, Roles).ShouldBeTrue();
        GranteeMatcher.MatchesRestriction(GranteeType.Role, null, "other", null, User, NoGroups, Roles).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RoleGranteeConsent_FailsValidation()
    {
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            GranteeType = GranteeType.Role,
            RoleName = "analysts",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };

        Should.Throw<InvalidOperationException>(consent.Validate).Message.ShouldContain("Role grantees are not supported");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RoleAssignee_IsRejectedForProfileAssignments()
    {
        var profile = new AccessProfile { TenantId = new TenantId("t"), ProfileId = "p", Name = "p", MaskingMode = MaskingPolicyMode.Strict };
        var assignment = new AccessProfileAssignment { Subject = "analysts", SubjectType = AccessProfileSubjectType.Role };

        Should.Throw<AccessProfileAssignmentRejectedException>(() => AccessProfileAssignmentRules.ValidateForAssignment(profile, assignment, DateTimeOffset.UtcNow))
            .Code.ShouldBe(AccessProfileAssignmentRules.RoleSubjectUnsupported);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void No_data_plane_functional_rights_exist()
    {
        var declared = typeof(FunctionalRight).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(FunctionalRight))
            .Select(f => ((FunctionalRight)f.GetValue(null)!).Value)
            .ToList();

        declared.ShouldNotContain("Data.UnmaskSensitive");
        declared.ShouldNotContain("Data.ExportClearText");
        FunctionalRight.PrivilegedValues.ShouldNotContain("Data.UnmaskSensitive");
        FunctionalRight.PrivilegedValues.ShouldNotContain("Data.ExportClearText");
        ((FunctionalRight)"Data.UnmaskSensitive").IsPrivileged.ShouldBeFalse("an unknown persisted value grants and privileges nothing");
    }
}
