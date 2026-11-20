namespace Autheris.Tests.Integration.Governance;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.Governance.Contract;
using Shouldly;
using Xunit;

/// <summary>Profile items store on PostgreSQL and SQL Server: the table entries and the masking level live in item rows, tenant isolated, atomic and removed with the profile.</summary>
public abstract class AccessProfileItemsStoreIntegrationTests : IAsyncLifetime
{
    protected GovernanceTestStore Store { get; private set; } = null!;

    private IAccessProfileRepository Repo => Store.AccessProfiles;

    protected abstract Task<GovernanceTestStore> CreateStoreAsync();

    public async Task InitializeAsync() => Store = await CreateStoreAsync();

    public async Task DisposeAsync()
    {
        if (Store is not null)
        {
            await Store.DisposeAsync();
        }
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Items_TenantA_NeverVisibleToTenantB()
    {
        if (!Store.IsAvailable) return;
        var id = Id("shared");
        await Repo.UpsertProfileAsync(Profile(Store.TenantA, id, MaskingPolicyMode.Unmasked, "sales.*", "hr.people"));
        await Repo.UpsertProfileAsync(Profile(Store.TenantB, id, MaskingPolicyMode.Strict, "ops.*"));

        var a = (await Repo.GetProfileAsync(Store.TenantA, id))!;
        var b = (await Repo.GetProfileAsync(Store.TenantB, id))!;

        a.TargetTables.ShouldBe(["sales.*", "hr.people"]);
        a.MaskingMode.ShouldBe(MaskingPolicyMode.Unmasked);
        b.TargetTables.ShouldBe(["ops.*"]);
        b.MaskingMode.ShouldBe(MaskingPolicyMode.Strict);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task DeleteProfile_RemovesItemsAndClassificationRows()
    {
        if (!Store.IsAvailable) return;
        var id = Id("del");
        await Repo.UpsertProfileAsync(Profile(Store.TenantA, id, MaskingPolicyMode.Default, "sales.*", "hr.*"));
        (await CountAsync($"SELECT COUNT(*) FROM ACCESS_PROFILE_ITEMS WHERE profile_id = '{id}'")).ShouldBe(2);

        (await Repo.DeleteProfileAsync(Store.TenantA, id)).ShouldBeTrue();

        (await CountAsync($"SELECT COUNT(*) FROM ACCESS_PROFILE_ITEMS WHERE profile_id = '{id}'")).ShouldBe(0);
        (await CountAsync($"SELECT COUNT(*) FROM ACCESS_PROFILE_CLASSIFICATION_ACCESS WHERE profile_id = '{id}'")).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ConcurrentUpsertSameProfile_ResultIsOneWritersCompleteState()
    {
        if (!Store.IsAvailable) return;
        var id = Id("race");
        await Repo.UpsertProfileAsync(Profile(Store.TenantA, id, MaskingPolicyMode.Default, "seed.*"));
        var writerOne = Profile(Store.TenantA, id, MaskingPolicyMode.Strict, "one.a", "one.b", "one.c");
        var writerTwo = Profile(Store.TenantA, id, MaskingPolicyMode.Unmasked, "two.a", "two.b");

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Repo.UpsertProfileAsync(i % 2 == 0 ? writerOne : writerTwo)));

        IsCompleteState((await Repo.GetProfileAsync(Store.TenantA, id))!).ShouldBeTrue("the stored profile is always one writer's complete state");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ConcurrentReadsDuringUpserts_NeverSeeAMixedState()
    {
        if (!Store.IsAvailable) return;
        var id = Id("mix");
        await Repo.UpsertProfileAsync(Profile(Store.TenantA, id, MaskingPolicyMode.Strict, "one.a", "one.b", "one.c"));
        var writerOne = Profile(Store.TenantA, id, MaskingPolicyMode.Strict, "one.a", "one.b", "one.c");
        var writerTwo = Profile(Store.TenantA, id, MaskingPolicyMode.Unmasked, "two.a", "two.b");
        var mixed = 0;

        var writers = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await Repo.UpsertProfileAsync(i % 2 == 0 ? writerTwo : writerOne);
            }
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                var read = await Repo.GetProfileAsync(Store.TenantA, id);
                if (read is null || !IsCompleteState(read))
                {
                    Interlocked.Increment(ref mixed);
                }
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writers));

        mixed.ShouldBe(0, "a read never combines the mode of one write with the tables of another");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task UpsertFailureMidway_RollsBackItemsAndLevel()
    {
        if (!Store.IsAvailable) return;
        var id = Id("atomic");
        await Repo.UpsertProfileAsync(Profile(Store.TenantA, id, MaskingPolicyMode.Default, "keep.*"));
        var failing = Profile(Store.TenantA, id, MaskingPolicyMode.Strict, "replaced.*");
        failing.Assignments.Add(new AccessProfileAssignment { Subject = "analysts", SubjectType = AccessProfileSubjectType.Role });

        await Should.ThrowAsync<Exception>(() => Repo.UpsertProfileAsync(failing));

        var stored = (await Repo.GetProfileAsync(Store.TenantA, id))!;
        stored.TargetTables.ShouldBe(["keep.*"]);
        stored.MaskingMode.ShouldBe(MaskingPolicyMode.Default);
    }

    private static string Id(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

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

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = await Store.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static bool IsCompleteState(AccessProfile p) =>
        (p.MaskingMode == MaskingPolicyMode.Strict && p.TargetTables.SequenceEqual(["one.a", "one.b", "one.c"])) ||
        (p.MaskingMode == MaskingPolicyMode.Unmasked && p.TargetTables.SequenceEqual(["two.a", "two.b"]));
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlAccessProfileItemsStoreIntegrationTests(GovernancePostgresFixture fixture) : AccessProfileItemsStoreIntegrationTests
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() => GovernanceTestStore.CreatePostgreSqlAsync(fixture, "access_profile_items");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerAccessProfileItemsStoreIntegrationTests(GovernanceSqlServerFixture fixture) : AccessProfileItemsStoreIntegrationTests
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() => GovernanceTestStore.CreateSqlServerAsync(fixture, "access_profile_items");
}
