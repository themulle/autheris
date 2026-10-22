namespace Autheris.Tests.Unit.AccessProfiles;

using System;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>GOV-07: a numeric masking mode outside the enum is never stored (the bulk four-eyes check no longer backs this up).</summary>
[Trait("Category", "Security")]
public sealed class AccessProfileUndefinedModeTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Store_UndefinedMode_Rejected()
    {
        var profile = new AccessProfile
        {
            ProfileId = "prof-undefined",
            Name = "Undefined mode",
            TenantId = new TenantId("tenant-a"),
            MaskingMode = (MaskingPolicyMode)99,
            TargetTables = ["sales.*"],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "admin@autheris.internal"
        };

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _repo.UpsertProfileAsync(profile));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public void UndefinedMode_IsNotADefinedEnumValue(int value)
    {
        Enum.IsDefined((MaskingPolicyMode)value).ShouldBeFalse();
    }

    [Fact]
    public void UndefinedMode_IsEnforcedAsStrict_AndRanksLikeStrict()
    {
        AccessProfileAssignmentRules.EnforcedMode((MaskingPolicyMode)99).ShouldBe(MaskingPolicyMode.Strict);
        AccessProfileAssignmentRules.Rank((MaskingPolicyMode)99).ShouldBe(AccessProfileAssignmentRules.Rank(MaskingPolicyMode.Strict));
    }

    [Fact]
    public void UndefinedModeProfile_DoesNotOutrankOrNeutraliseStrict()
    {
        AccessProfile Profile(string id, MaskingPolicyMode mode) => new()
        {
            ProfileId = id, Name = id, TenantId = new TenantId("tenant-a"), MaskingMode = mode, TargetTables = ["*.*"], CreatedAt = DateTimeOffset.UtcNow
        };

        var ordered = AccessProfileAssignmentRules.Order([Profile("unmasked", MaskingPolicyMode.Unmasked), Profile("undefined", (MaskingPolicyMode)99), Profile("strict", MaskingPolicyMode.Strict)]);

        // The undefined mode is treated as Strict, so it is never ranked behind Unmasked and the most restrictive profile leads.
        ordered[^1].ProfileId.ShouldBe("unmasked");
        AccessProfileAssignmentRules.EnforcedMode(ordered[0].MaskingMode).ShouldBe(MaskingPolicyMode.Strict);
    }
}
