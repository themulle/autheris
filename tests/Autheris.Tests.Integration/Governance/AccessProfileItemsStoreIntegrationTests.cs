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

/// <summary>Profile items store (PostgreSQL): the table entries and the masking level live in item rows, tenant isolated, atomic and removed with the profile.</summary>
[Trait("Category", "Security")]
public sealed class AccessProfileItemsStoreIntegrationTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private static AccessProfile Profile(TenantId tenant, string id, MaskingPolicyMode mode, params string[] tables) => new()
    {
        ProfileId = id,
        Name = id,
        TenantId = tenant,
        MaskingMode = mode,
        TargetTables = [.. tables],
        ValidTo = DateTimeOffset.UtcNow.AddDays(10),
        CreatedBy = "admin@autheris.internal"
    };

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var raw = new NpgsqlConnection(connectionString);
        await raw.OpenAsync();
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Items_TenantA_NeverVisibleToTenantB()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(Substitute.For<IEpochValidationService>());
        await repo.UpsertProfileAsync(Profile(TenantA, "shared-id", MaskingPolicyMode.Unmasked, "sales.*", "hr.people"));
        await repo.UpsertProfileAsync(Profile(TenantB, "shared-id", MaskingPolicyMode.Strict, "ops.*"));

        var a = (await repo.GetProfileAsync(TenantA, "shared-id"))!;
        var b = (await repo.GetProfileAsync(TenantB, "shared-id"))!;

        a.TargetTables.ShouldBe(["sales.*", "hr.people"]);
        a.MaskingMode.ShouldBe(MaskingPolicyMode.Unmasked);
        b.TargetTables.ShouldBe(["ops.*"]);
        b.MaskingMode.ShouldBe(MaskingPolicyMode.Strict);
    }

    [Fact]
    public async Task DeleteProfile_RemovesItemsAndClassificationRows()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(Substitute.For<IEpochValidationService>(), out var connectionString);
        await repo.UpsertProfileAsync(Profile(TenantA, "to-delete", MaskingPolicyMode.Default, "sales.*", "hr.*"));
        (await ScalarAsync(connectionString, "SELECT COUNT(*) FROM access_profile_items WHERE profile_id = 'to-delete'")).ShouldBe(2);

        (await repo.DeleteProfileAsync(TenantA, "to-delete")).ShouldBeTrue();

        (await ScalarAsync(connectionString, "SELECT COUNT(*) FROM access_profile_items WHERE profile_id = 'to-delete'")).ShouldBe(0);
        (await ScalarAsync(connectionString, "SELECT COUNT(*) FROM access_profile_classification_access WHERE profile_id = 'to-delete'")).ShouldBe(0);
    }

    [Fact]
    public async Task ConcurrentUpsertSameProfile_ResultIsOneWritersCompleteState()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(Substitute.For<IEpochValidationService>());
        await repo.UpsertProfileAsync(Profile(TenantA, "race", MaskingPolicyMode.Default, "seed.*"));
        var writerOne = Profile(TenantA, "race", MaskingPolicyMode.Strict, "one.a", "one.b", "one.c");
        var writerTwo = Profile(TenantA, "race", MaskingPolicyMode.Unmasked, "two.a", "two.b");

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => repo.UpsertProfileAsync(i % 2 == 0 ? writerOne : writerTwo)));

        var result = (await repo.GetProfileAsync(TenantA, "race"))!;
        var one = result.MaskingMode == MaskingPolicyMode.Strict && result.TargetTables.SequenceEqual(["one.a", "one.b", "one.c"]);
        var two = result.MaskingMode == MaskingPolicyMode.Unmasked && result.TargetTables.SequenceEqual(["two.a", "two.b"]);
        (one || two).ShouldBeTrue("the stored profile is always one writer's complete state");
    }

    [Fact]
    public async Task UpsertFailureMidway_RollsBackItemsAndLevel()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(Substitute.For<IEpochValidationService>());
        await repo.UpsertProfileAsync(Profile(TenantA, "atomic", MaskingPolicyMode.Default, "keep.*"));
        var failing = Profile(TenantA, "atomic", MaskingPolicyMode.Strict, "replaced.*");
        failing.Assignments.Add(new AccessProfileAssignment { Subject = "analysts", SubjectType = AccessProfileSubjectType.Role });

        await Should.ThrowAsync<Exception>(() => repo.UpsertProfileAsync(failing));

        var stored = (await repo.GetProfileAsync(TenantA, "atomic"))!;
        stored.TargetTables.ShouldBe(["keep.*"]);
        stored.MaskingMode.ShouldBe(MaskingPolicyMode.Default);
    }
}
