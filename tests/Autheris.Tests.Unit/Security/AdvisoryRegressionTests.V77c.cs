namespace Autheris.Tests.Unit.Security;

using System;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    // GHSA-v77c-jh96-7rgr (details: private advisory).
    [Theory]
    [InlineData("case_01")]
    [InlineData("case_02")]
    [InlineData("case_03")]
    [InlineData("case_04")]
    [InlineData("case_05")]
    [InlineData("case_06")]
    public void Ghsa_v77c_7rgr_regression(string bullet)
    {
        var store = new AccessProfileStore(AccessProfileDialect.PostgreSql, (_, _) => throw new NotSupportedException());
        var now = DateTimeOffset.UtcNow;
        var profile = new AccessProfile
        {
            ProfileId = "p-1", TenantId = new TenantId("tenant-a"), Name = "P", MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["a.b.c"], CreatedBy = "admin", CreatedAt = now
        };

        AccessProfileAssignment WithKind(AccessProfileAssignmentKind kind) => new()
        {
            Subject = "alice", SubjectType = AccessProfileSubjectType.User, AssignedAt = now, ExpiresAt = now.AddHours(12), CreatedBy = "req",
            ApprovedBy = null, Justification = "j", Kind = kind
        };

        switch (bullet)
        {
            case "case_01":
                Enum.IsDefined(store.ReadKind("BreakGlass")).ShouldBeFalse();
                break;
            case "case_02":
                Enum.IsDefined(store.ReadKind("99")).ShouldBeFalse();
                Enum.IsDefined(store.ReadKind("-1")).ShouldBeFalse();
                break;
            case "case_03":
                Enum.IsDefined(store.ReadKind("")).ShouldBeFalse();
                break;
            case "case_04":
                AccessProfileAssignmentRules.IsEffective(profile, WithKind(store.ReadKind("BreakGlass")), now).ShouldBeFalse();
                break;
            case "case_05":
                Should.Throw<AccessProfileAssignmentRejectedException>(() =>
                    AccessProfileAssignmentRules.ValidateForAssignment(profile, WithKind(store.ReadKind("BreakGlass")), now));
                break;
            case "case_06":
                store.ReadKind("standard").ShouldBe(AccessProfileAssignmentKind.Standard);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bullet), bullet, null);
        }
    }
}
