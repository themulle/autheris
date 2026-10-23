namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #281 WP1: the trusted document store contract on real SqlServer (same cases as the SQLite repository tests). Needs Docker; without it each test returns early.</summary>
public sealed class SqlServerTrustedDocumentRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly string H1 = new('a', 64);
    private static readonly string H2 = new('b', 64);
    private static readonly string[] Origins = ["https://app.example.com"];
    private SqlServerTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        _available = _db.IsAvailable;
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private SqlServerGovernanceRepository NewRepository()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return GovernanceStoreFactory.CreateSqlServer(Substitute.For<IEpochValidationService>(), Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "SqlServer", ConnectionString = _db!.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
        }), env);
    }

    [Fact]
    public async Task Replace_Diff_Retire_AndGeneration_HoldOnTheEngine()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        ITrustedDocumentRepository store = repository;
        var start = await store.GetTrustedDocumentGenerationAsync();

        var created = await store.ReplaceTrustedDocumentVersionAsync("shop", "1.0", Origins, [H1], 20, Now);
        created.Created.ShouldBeTrue();
        created.Added.ShouldBe([H1]);

        var diff = await store.ReplaceTrustedDocumentVersionAsync("shop", "1.0", Origins, [H2], 20, Now);
        diff.Added.ShouldBe([H2]);
        diff.Removed.ShouldBe([H1]);

        var snapshot = await store.LoadTrustedDocumentSnapshotAsync();
        snapshot.Generation.ShouldBe(start + 2);
        var version = snapshot.ActiveVersions.Single(v => v.AppId == "shop");
        version.Hashes.ShouldBe([H2]);
        version.Origins.ShouldBe(Origins);

        (await store.RetireTrustedDocumentVersionAsync("shop", "1.0", Now)).ShouldBe(TrustedDocumentRetireOutcome.Retired);
        (await store.RetireTrustedDocumentVersionAsync("shop", "1.0", Now)).ShouldBe(TrustedDocumentRetireOutcome.AlreadyRetired);
        (await store.GetTrustedDocumentGenerationAsync()).ShouldBe(start + 3);
        (await store.LoadTrustedDocumentSnapshotAsync()).ActiveVersions.Where(v => v.AppId == "shop").ShouldBeEmpty();
        (await store.GetTrustedDocumentAppAsync("shop"))!.Versions.Single().Status.ShouldBe(TrustedDocumentVersionStatus.Retired);
    }

    [Fact]
    public async Task RetiredVersion_AndVersionCap_AreRefused_WithoutChanges()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        ITrustedDocumentRepository store = repository;

        await store.ReplaceTrustedDocumentVersionAsync("capped", "1", Origins, [H1], 1, Now);
        await Should.ThrowAsync<TrustedDocumentVersionLimitException>(() => store.ReplaceTrustedDocumentVersionAsync("capped", "2", Origins, [H1], 1, Now));
        await store.RetireTrustedDocumentVersionAsync("capped", "1", Now);
        var generation = await store.GetTrustedDocumentGenerationAsync();
        await Should.ThrowAsync<TrustedDocumentVersionRetiredException>(() => store.ReplaceTrustedDocumentVersionAsync("capped", "1", Origins, [H2], 1, Now));

        (await store.GetTrustedDocumentGenerationAsync()).ShouldBe(generation);
    }
}
