using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>ITrustedDocumentRepository</c> across PostgreSQL and SQL Server engines. The runtime-managed trusted
/// GraphQL documents are scoped by app, not by tenant: every mutation changes the data and the generation in one transaction.
/// </summary>
public abstract class TrustedDocumentContract : GovernanceContractBase
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly string H1 = new('a', 64);
    private static readonly string H2 = new('b', 64);
    private static readonly string H3 = new('c', 64);
    private static readonly string[] Origins = ["https://app.example.com"];

    private ITrustedDocumentRepository Repo => Store.TrustedDocuments;

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Repo.ReplaceTrustedDocumentVersionAsync("a", "1", ["https://a.example.com"], [H1], 20, Now);
        await Repo.ReplaceTrustedDocumentVersionAsync("b", "1", ["https://b.example.com"], [H2], 20, Now);

        var snapshot = await Repo.LoadTrustedDocumentSnapshotAsync();
        snapshot.ActiveVersions.Single(v => v.AppId == "a").Hashes.ShouldBe([H1]);
        snapshot.ActiveVersions.Single(v => v.AppId == "b").Origins.ShouldBe(["https://b.example.com"]);
        var app = (await Repo.GetTrustedDocumentAppAsync("a")).ShouldNotBeNull();
        app.Origins.ShouldBe(["https://a.example.com"]);
        app.Versions.ShouldHaveSingleItem();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Repo.ReplaceTrustedDocumentVersionAsync("a", "1", ["https://a.example.com"], [H1], 20, Now);

        await Repo.ReplaceTrustedDocumentVersionAsync("b", "1", ["https://b.example.com"], [H2], 20, Now);
        (await Repo.RetireTrustedDocumentVersionAsync("b", "1", Now)).ShouldBe(TrustedDocumentRetireOutcome.Retired);

        var snapshot = await Repo.LoadTrustedDocumentSnapshotAsync();
        var version = snapshot.ActiveVersions.ShouldHaveSingleItem();
        version.AppId.ShouldBe("a");
        version.Hashes.ShouldBe([H1]);
        version.Origins.ShouldBe(["https://a.example.com"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var start = await Repo.GetTrustedDocumentGenerationAsync();

        // The repository has no tenant parameter (apps are the scope); an unknown or blank app never resolves to another app's data.
        (await Repo.GetTrustedDocumentAppAsync("nope")).ShouldBeNull();
        (await Repo.GetTrustedDocumentAppAsync(string.Empty)).ShouldBeNull();
        (await Repo.RetireTrustedDocumentVersionAsync(string.Empty, "1", Now)).ShouldBe(TrustedDocumentRetireOutcome.NotFound);

        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(start);
        (await Repo.LoadTrustedDocumentSnapshotAsync()).ActiveVersions.ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1.0.0", Origins, [H1, H2], 20, Now);

        var diff = await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1.0.0", Origins, [H2, H3], 20, Now.AddMinutes(1));
        diff.Created.ShouldBeFalse();
        diff.Added.ShouldBe([H3]);
        diff.Removed.ShouldBe([H1]);
        diff.Unchanged.ShouldBe(1);
        diff.OriginsChanged.ShouldBeFalse();

        var again = await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1.0.0", Origins, [H3, H2], 20, Now.AddMinutes(2));
        again.Added.ShouldBeEmpty();
        again.Removed.ShouldBeEmpty();
        again.Unchanged.ShouldBe(2);

        (await Repo.LoadTrustedDocumentSnapshotAsync()).ActiveVersions.Single().Hashes.ShouldBe([H2, H3]);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                await Repo.ReplaceTrustedDocumentVersionAsync("shop", "v" + i, Origins, [H1], 3, Now);
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        })).ToArray();
        var outcomes = await Task.WhenAll(tasks);

        outcomes.Where(e => e is not null).ShouldAllBe(e => e is TrustedDocumentVersionLimitException || e is TrustedDocumentConcurrentChangeException);
        (await Repo.LoadTrustedDocumentSnapshotAsync()).ActiveVersions.Count(v => v.AppId == "shop").ShouldBeLessThanOrEqualTo(3);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var delta = await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1.0.0", Origins, [H2, H1], 20, Now);
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "2.0.0", ["https://b.example.com", "https://c.example.com"], [H1], 20, Now);

        delta.Added.ShouldBe([H1, H2]);
        var first = await Repo.LoadTrustedDocumentSnapshotAsync();
        var second = await Repo.LoadTrustedDocumentSnapshotAsync();
        first.ActiveVersions.Single(v => v.Version == "1.0.0").Hashes.ShouldBe([H1, H2]);
        second.ActiveVersions.Select(v => v.Version).ShouldBe(first.ActiveVersions.Select(v => v.Version));
        var app = (await Repo.GetTrustedDocumentAppAsync("shop")).ShouldNotBeNull();
        app.Origins.ShouldBe(["https://b.example.com", "https://c.example.com"]);
        app.Versions.Select(v => v.Version).ShouldBe(["1.0.0", "2.0.0"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 2, Now);
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "2", Origins, [H1], 2, Now);
        var generation = await Repo.GetTrustedDocumentGenerationAsync();

        await Should.ThrowAsync<TrustedDocumentVersionLimitException>(
            () => Repo.ReplaceTrustedDocumentVersionAsync("shop", "3", Origins, [H1], 2, Now));
        (await Repo.ReplaceTrustedDocumentVersionAsync("shop", "2", Origins, [H1, H2], 2, Now)).Added.ShouldBe([H2]);

        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(generation + 1);
        await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now);
        (await Repo.ReplaceTrustedDocumentVersionAsync("shop", "3", Origins, [H1], 2, Now)).Created.ShouldBeTrue();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1, H2], 20, Now);

        (await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now.AddHours(1))).ShouldBe(TrustedDocumentRetireOutcome.Retired);

        (await Repo.LoadTrustedDocumentSnapshotAsync()).ActiveVersions.ShouldBeEmpty();
        var version = (await Repo.GetTrustedDocumentAppAsync("shop"))!.Versions.ShouldHaveSingleItem();
        version.Status.ShouldBe(TrustedDocumentVersionStatus.Retired);
        version.HashCount.ShouldBe(2);
        version.RetiredAt.ShouldBe(Now.AddHours(1));
    }

    [RequiresContainerRuntimeFact]
    public async Task FreshStore_HasNoActiveVersionsAndNoApps()
    {
        if (!Store.IsAvailable) return;

        var snapshot = await Repo.LoadTrustedDocumentSnapshotAsync();

        snapshot.Generation.ShouldBe(await Repo.GetTrustedDocumentGenerationAsync());
        snapshot.ActiveVersions.ShouldBeEmpty();
        (await Repo.GetTrustedDocumentAppAsync("shop")).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task Replace_NewVersion_StoresHashesAndOrigins_AndRoundTrips()
    {
        if (!Store.IsAvailable) return;

        var delta = await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1.0.0", Origins, [H2, H1], 20, Now);

        delta.Created.ShouldBeTrue();
        delta.Added.ShouldBe([H1, H2]);
        delta.Removed.ShouldBeEmpty();
        delta.Unchanged.ShouldBe(0);
        delta.OriginsChanged.ShouldBeTrue();
        var version = (await Repo.LoadTrustedDocumentSnapshotAsync()).ActiveVersions.ShouldHaveSingleItem();
        version.AppId.ShouldBe("shop");
        version.Version.ShouldBe("1.0.0");
        version.Hashes.ShouldBe([H1, H2]);
        version.Origins.ShouldBe(Origins);
    }

    [RequiresContainerRuntimeFact]
    public async Task Retire_IsIdempotent_AndReportsUnknown()
    {
        if (!Store.IsAvailable) return;
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 20, Now);
        await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now);
        var generation = await Repo.GetTrustedDocumentGenerationAsync();

        (await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now)).ShouldBe(TrustedDocumentRetireOutcome.AlreadyRetired);
        (await Repo.RetireTrustedDocumentVersionAsync("shop", "9", Now)).ShouldBe(TrustedDocumentRetireOutcome.NotFound);
        (await Repo.RetireTrustedDocumentVersionAsync("nope", "1", Now)).ShouldBe(TrustedDocumentRetireOutcome.NotFound);

        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(generation);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Replace_OnRetiredVersion_Throws_AndChangesNothing()
    {
        if (!Store.IsAvailable) return;
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 20, Now);
        await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now);
        var generation = await Repo.GetTrustedDocumentGenerationAsync();

        await Should.ThrowAsync<TrustedDocumentVersionRetiredException>(
            () => Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", ["https://x.example.com"], [H2], 20, Now));

        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(generation);
        (await Repo.GetTrustedDocumentAppAsync("shop"))!.Origins.ShouldBe(Origins);
    }

    [RequiresContainerRuntimeFact]
    public async Task EveryMutation_BumpsGenerationOnce_InTheSameTransaction()
    {
        if (!Store.IsAvailable) return;
        var start = await Repo.GetTrustedDocumentGenerationAsync();

        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 20, Now);
        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(start + 1);
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 20, Now); // a no-op PUT still bumps: the actor trace is audited
        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(start + 2);
        await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now);
        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(start + 3);

        // A snapshot always carries the generation of the data it holds.
        (await Repo.LoadTrustedDocumentSnapshotAsync()).Generation.ShouldBe(start + 3);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlTrustedDocumentContractTests(GovernancePostgresFixture fixture) : TrustedDocumentContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "trusted_document_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerTrustedDocumentContractTests(GovernanceSqlServerFixture fixture) : TrustedDocumentContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "trusted_document_contract");
}
