namespace Autheris.Tests.Unit.Security;

using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Infrastructure.Persistence;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>CR-ADG-31 / WP-X0p: the persistent registry on SQLite (the same portable store backs PostgreSQL and SQL Server).</summary>
public sealed class TenantRegistryStoreTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repo = new(Substitute.For<IEpochValidationService>());
    private ITenantRegistry Registry => _repo;

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task FirstSeenTenant_Registered()
    {
        (await Registry.RegisterOrGetAsync(new TenantId("acme"), "iss", default)).ShouldBe(TenantRegistration.Registered);
        (await Registry.RegisterOrGetAsync(new TenantId("acme"), "iss", default)).ShouldBe(TenantRegistration.Existing);
    }

    [Fact]
    public async Task CaseVariantOfIdpOnlyTenant_Collision_AndEverySpellingOfTheGroupIsDenied()
    {
        (await Registry.RegisterOrGetAsync(new TenantId("acme"), "iss", default)).ShouldBe(TenantRegistration.Registered);

        (await Registry.RegisterOrGetAsync(new TenantId("ACME"), "iss", default)).ShouldBe(TenantRegistration.Collision);
        (await Registry.RegisterOrGetAsync(new TenantId("acme"), "iss", default)).ShouldBe(TenantRegistration.Collision); // the first spelling too
        (await Registry.RegisterOrGetAsync(new TenantId("AcMe"), "iss", default)).ShouldBe(TenantRegistration.Collision);
        (await Registry.RegisterOrGetAsync(new TenantId("beta"), "iss", default)).ShouldBe(TenantRegistration.Registered);
    }

    [Fact]
    public async Task ConcurrentRegistration_OneWins()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => await Registry.RegisterOrGetAsync(new TenantId("race"), "iss", default))));

        results.Count(r => r == TenantRegistration.Registered).ShouldBe(1);
        results.Count(r => r == TenantRegistration.Existing).ShouldBe(31);
    }

    [Fact]
    public async Task ConcurrentCaseVariants_NeverBothAllowed()
    {
        var spellings = new[] { "Race", "RACE", "race" };
        var results = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(i => Task.Run(async () => (Spelling: spellings[i % 3], Result: await Registry.RegisterOrGetAsync(new TenantId(spellings[i % 3]), "iss", default)))));

        results.Count(r => r.Result == TenantRegistration.Registered).ShouldBe(1);
        // After the dust settles every spelling is denied.
        foreach (var s in spellings)
        {
            (await Registry.RegisterOrGetAsync(new TenantId(s), "iss", default)).ShouldBe(TenantRegistration.Collision);
        }
    }

    [Fact]
    public async Task ConfiguredTenants_Preseeded()
    {
        await Registry.PreseedAsync(["beta", "gamma"], default);

        (await Registry.RegisterOrGetAsync(new TenantId("beta"), "iss", default)).ShouldBe(TenantRegistration.Existing);
        (await Registry.RegisterOrGetAsync(new TenantId("BETA"), "iss", default)).ShouldBe(TenantRegistration.Collision);
    }

    [Fact]
    public async Task ExistingCollisionGroup_InConfiguration_AllSpellingsDenied_AfterPreseed()
    {
        await Registry.PreseedAsync(["acme", "ACME", "beta", "", "not valid!"], default);

        (await Registry.RegisterOrGetAsync(new TenantId("acme"), "iss", default)).ShouldBe(TenantRegistration.Collision);
        (await Registry.RegisterOrGetAsync(new TenantId("ACME"), "iss", default)).ShouldBe(TenantRegistration.Collision);
        (await Registry.RegisterOrGetAsync(new TenantId("Acme"), "iss", default)).ShouldBe(TenantRegistration.Collision);
        (await Registry.RegisterOrGetAsync(new TenantId("beta"), "iss", default)).ShouldBe(TenantRegistration.Existing);
    }

    [Fact]
    public async Task PreseedIsIdempotent()
    {
        await Registry.PreseedAsync(["beta"], default);
        await Registry.PreseedAsync(["beta"], default);

        (await Registry.RegisterOrGetAsync(new TenantId("beta"), "iss", default)).ShouldBe(TenantRegistration.Existing);
    }
}
