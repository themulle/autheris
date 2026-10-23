namespace Autheris.Tests.Unit.Governance.TrustedDocuments;

using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #281 WP1: storage contract of the app-scoped trusted documents (SQLite; PostgreSql and SqlServer in the integration tests).</summary>
public sealed class SqliteTrustedDocumentRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly string H1 = new('a', 64);
    private static readonly string H2 = new('b', 64);
    private static readonly string H3 = new('c', 64);
    private static readonly string[] Origins = ["https://app.example.com"];

    private readonly SqliteGovernanceRepository _store = GovernanceStoreFactory.CreateSqlite(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=td_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));

    private ITrustedDocumentRepository Repo => _store;

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task FreshStore_HasGenerationZero_AndNoActiveVersions()
    {
        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(0);
        var snapshot = await Repo.LoadTrustedDocumentSnapshotAsync();
        snapshot.Generation.ShouldBe(0);
        snapshot.ActiveVersions.ShouldBeEmpty();
        (await Repo.GetTrustedDocumentAppAsync("shop")).ShouldBeNull();
    }

    [Fact]
    public async Task Replace_NewVersion_StoresHashesAndOrigins_AndRoundTrips()
    {
        var delta = await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1.0.0", Origins, [H2, H1], 20, Now);

        delta.Created.ShouldBeTrue();
        delta.Added.ShouldBe([H1, H2]);
        delta.Removed.ShouldBeEmpty();
        delta.Unchanged.ShouldBe(0);
        delta.OriginsChanged.ShouldBeTrue();

        var snapshot = await Repo.LoadTrustedDocumentSnapshotAsync();
        var version = snapshot.ActiveVersions.ShouldHaveSingleItem();
        version.AppId.ShouldBe("shop");
        version.Version.ShouldBe("1.0.0");
        version.Hashes.ShouldBe([H1, H2]);
        version.Origins.ShouldBe(Origins);
    }

    [Fact]
    public async Task Replace_Existing_ReturnsDiff_AndIdempotentRePutIsEmpty()
    {
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

    [Fact]
    public async Task Replace_ChangesOriginSetOfTheApp()
    {
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 20, Now);
        var delta = await Repo.ReplaceTrustedDocumentVersionAsync("shop", "2", ["https://b.example.com", "https://c.example.com"], [H1], 20, Now);

        delta.OriginsChanged.ShouldBeTrue();
        var app = (await Repo.GetTrustedDocumentAppAsync("shop")).ShouldNotBeNull();
        app.Origins.ShouldBe(["https://b.example.com", "https://c.example.com"]);
        app.Versions.Select(v => v.Version).ShouldBe(["1", "2"]);
    }

    [Fact]
    public async Task Retire_SoftRetires_KeepsRows_AndRemovesFromActiveSnapshot()
    {
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1, H2], 20, Now);

        (await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now.AddHours(1))).ShouldBe(TrustedDocumentRetireOutcome.Retired);

        (await Repo.LoadTrustedDocumentSnapshotAsync()).ActiveVersions.ShouldBeEmpty();
        var version = (await Repo.GetTrustedDocumentAppAsync("shop"))!.Versions.ShouldHaveSingleItem();
        version.Status.ShouldBe(TrustedDocumentVersionStatus.Retired);
        version.HashCount.ShouldBe(2);
        version.RetiredAt.ShouldBe(Now.AddHours(1));
    }

    [Fact]
    public async Task Retire_IsIdempotent_AndReportsUnknown()
    {
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 20, Now);
        await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now);
        var generation = await Repo.GetTrustedDocumentGenerationAsync();

        (await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now)).ShouldBe(TrustedDocumentRetireOutcome.AlreadyRetired);
        (await Repo.RetireTrustedDocumentVersionAsync("shop", "9", Now)).ShouldBe(TrustedDocumentRetireOutcome.NotFound);
        (await Repo.RetireTrustedDocumentVersionAsync("nope", "1", Now)).ShouldBe(TrustedDocumentRetireOutcome.NotFound);

        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(generation);
    }

    [Fact]
    public async Task Replace_OnRetiredVersion_Throws_AndChangesNothing()
    {
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 20, Now);
        await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now);
        var generation = await Repo.GetTrustedDocumentGenerationAsync();

        await Should.ThrowAsync<TrustedDocumentVersionRetiredException>(
            () => Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", ["https://x.example.com"], [H2], 20, Now));

        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(generation);
        (await Repo.GetTrustedDocumentAppAsync("shop"))!.Origins.ShouldBe(Origins);
    }

    [Fact]
    public async Task Replace_BeyondActiveVersionCap_Throws_ButExistingVersionsStillUpdate()
    {
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 2, Now);
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "2", Origins, [H1], 2, Now);

        await Should.ThrowAsync<TrustedDocumentVersionLimitException>(
            () => Repo.ReplaceTrustedDocumentVersionAsync("shop", "3", Origins, [H1], 2, Now));
        (await Repo.ReplaceTrustedDocumentVersionAsync("shop", "2", Origins, [H1, H2], 2, Now)).Added.ShouldBe([H2]);

        await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now);
        (await Repo.ReplaceTrustedDocumentVersionAsync("shop", "3", Origins, [H1], 2, Now)).Created.ShouldBeTrue();
    }

    [Fact]
    public async Task EveryMutation_BumpsGenerationOnce_InTheSameTransaction()
    {
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 20, Now);
        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(1);
        await Repo.ReplaceTrustedDocumentVersionAsync("shop", "1", Origins, [H1], 20, Now);   // no-op PUT still bumps: the actor trace is audited
        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(2);
        await Repo.RetireTrustedDocumentVersionAsync("shop", "1", Now);
        (await Repo.GetTrustedDocumentGenerationAsync()).ShouldBe(3);

        // A snapshot always carries the generation of the data it holds.
        (await Repo.LoadTrustedDocumentSnapshotAsync()).Generation.ShouldBe(3);
    }

    [Fact]
    public async Task Apps_AreIsolated_FromEachOther()
    {
        await Repo.ReplaceTrustedDocumentVersionAsync("a", "1", ["https://a.example.com"], [H1], 20, Now);
        await Repo.ReplaceTrustedDocumentVersionAsync("b", "1", ["https://b.example.com"], [H2], 20, Now);

        var snapshot = await Repo.LoadTrustedDocumentSnapshotAsync();
        snapshot.ActiveVersions.Single(v => v.AppId == "a").Hashes.ShouldBe([H1]);
        snapshot.ActiveVersions.Single(v => v.AppId == "b").Origins.ShouldBe(["https://b.example.com"]);
    }
}
