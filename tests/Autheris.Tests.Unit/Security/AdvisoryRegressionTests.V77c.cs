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
    [InlineData("unknown_kind_text_is_not_defined")]
    [InlineData("numeric_kind_outside_the_enum_is_not_defined")]
    [InlineData("empty_kind_is_not_defined")]
    [InlineData("unknown_kind_is_never_effective")]
    [InlineData("unknown_kind_is_refused_on_write")]
    [InlineData("known_kind_still_reads_back")]
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
            case "unknown_kind_text_is_not_defined":
                Enum.IsDefined(store.ReadKind("BreakGlass")).ShouldBeFalse();
                break;
            case "numeric_kind_outside_the_enum_is_not_defined":
                Enum.IsDefined(store.ReadKind("99")).ShouldBeFalse();
                Enum.IsDefined(store.ReadKind("-1")).ShouldBeFalse();
                break;
            case "empty_kind_is_not_defined":
                Enum.IsDefined(store.ReadKind("")).ShouldBeFalse();
                break;
            case "unknown_kind_is_never_effective":
                AccessProfileAssignmentRules.IsEffective(profile, WithKind(store.ReadKind("BreakGlass")), now).ShouldBeFalse();
                break;
            case "unknown_kind_is_refused_on_write":
                Should.Throw<AccessProfileAssignmentRejectedException>(() =>
                    AccessProfileAssignmentRules.ValidateForAssignment(profile, WithKind(store.ReadKind("BreakGlass")), now));
                break;
            case "known_kind_still_reads_back":
                store.ReadKind("standard").ShouldBe(AccessProfileAssignmentKind.Standard);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bullet), bullet, null);
        }
    }
}
