namespace Autheris.Tests.Integration.Governance;

using System;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>GOV-07: an undefined numeric masking mode is refused on write and read back as the most restrictive mode (PostgreSQL).</summary>
[Trait("Category", "Security")]
public sealed class AccessProfileMaskingModeIntegrityTests
{
    private static readonly TenantId TenantLiebherr = new("liebherr");

    [Fact]
    public async Task PostgreSqlStore_UndefinedMaskingMode_IsRefusedOnWrite_AndReadAsStrict()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(Substitute.For<IEpochValidationService>(), out var connectionString);

        AccessProfile Profile(MaskingPolicyMode mode) => new()
        {
            ProfileId = "prof-mode",
            Name = "Mode Profile",
            TenantId = TenantLiebherr,
            MaskingMode = mode,
            TargetTables = ["tem.*"],
            ValidTo = DateTimeOffset.UtcNow.AddDays(10),
            CreatedBy = "admin@autheris.internal"
        };

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repo.UpsertProfileAsync(Profile((MaskingPolicyMode)7)));

        // A row written before the guard (or by another writer) with a numeric mode is read as the most restrictive mode.
        await repo.UpsertProfileAsync(Profile(MaskingPolicyMode.Default));
        (await repo.GetProfileAsync(TenantLiebherr, "prof-mode"))!.MaskingMode.ShouldBe(MaskingPolicyMode.Default);
        await using (var raw = new NpgsqlConnection(connectionString))
        {
            await raw.OpenAsync();
            await using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE access_profiles SET masking_mode = '7' WHERE profile_id = 'prof-mode'";
            (await cmd.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        var fetched = await repo.GetProfileAsync(TenantLiebherr, "prof-mode");
        fetched.ShouldNotBeNull();
        fetched.MaskingMode.ShouldBe(MaskingPolicyMode.Strict);
    }
}
