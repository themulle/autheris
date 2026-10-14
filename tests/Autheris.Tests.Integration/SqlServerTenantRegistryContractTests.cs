namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

using Xunit;

/// <summary>CR-ADG-31 / WP-X0p: the tenant registry contract on real SqlServer. Needs Docker; without it each test returns early.</summary>
public sealed class SqlServerTenantRegistryContractTests : IAsyncLifetime
{
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
    public async Task ConcurrentRegistration_OneWins_AcrossConnections()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        ITenantRegistry registry = repository;

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(async () => (await registry.RegisterOrGetAsync(new TenantId("race"), "iss", default)).ToString())));

        results.Count(r => r == nameof(TenantRegistration.Registered)).ShouldBe(1);
        results.Count(r => r == nameof(TenantRegistration.Existing)).ShouldBe(23);
    }

    [Fact]
    public async Task ConcurrentCaseVariants_NeverBothAllowed_AndTheWholeGroupIsDenied()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        ITenantRegistry registry = repository;
        var spellings = new[] { "Dup", "DUP", "dup" };

        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(async () => await registry.RegisterOrGetAsync(new TenantId(spellings[i % 3]), "iss", default))));

        results.Count(r => r == TenantRegistration.Registered).ShouldBe(1);
        foreach (var s in spellings)
        {
            (await registry.RegisterOrGetAsync(new TenantId(s), "iss", default)).ShouldBe(TenantRegistration.Collision);
        }
    }

    [Fact]
    public async Task Preseed_MarksConfiguredCollisionGroups_AndPersistsAcrossRepositories()
    {
        if (!_available) return;
        await using (var first = NewRepository())
        {
            await ((ITenantRegistry)first).PreseedAsync(["acme", "ACME", "beta"], default);
        }

        await using var second = NewRepository();
        ITenantRegistry registry = second;
        (await registry.RegisterOrGetAsync(new TenantId("acme"), "iss", default)).ShouldBe(TenantRegistration.Collision);
        (await registry.RegisterOrGetAsync(new TenantId("beta"), "iss", default)).ShouldBe(TenantRegistration.Existing);
    }
}
