namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// Virtual filters, phase 2: the storage contract on real PostgreSQL (same cases as the SQLite repository tests).
/// Needs Docker; without it each test returns early.
/// </summary>
public sealed class PostgreSqlVirtualFilterContractTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("tenant_lwe");
    private PostgreSqlContainer? _container;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _container.StartAsync();
            _available = true;
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    private PostgreSqlGovernanceRepository NewRepository()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return new PostgreSqlGovernanceRepository(Substitute.For<IEpochValidationService>(), Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = _container!.GetConnectionString(),
                EnableOutboxProcessor = false,
                SeedDemoData = false
            }
        }), env);
    }

    private static VirtualFilter Filter(string name = "nicht_ausgelieferte_krane") => new()
    {
        TenantId = Tenant,
        Name = name,
        Source = "lwetem_prod",
        KeyColumns = ["client.client_id"],
        ValidToColumn = "crane.date_of_delivery",
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("lwetem_prod", "conf", "client"),
            FromAlias = "client",
            Joins = [new FilterJoin(new TableIdentifier("lwetem_prod", "md", "crane"), "crane", "crane.serial_number", "client.crane_serial_number")],
            Where = [new FilterCondition("crane.is_delivered", FilterConditionOperator.IsNull)]
        },
        ManagedBy = new ManagedBy("governance/access", "c1")
    };

    private static FilterBinding Binding() => new()
    {
        TenantId = Tenant,
        FilterName = "nicht_ausgelieferte_krane",
        TargetPattern = "lwetem_prod.*.*.client_id",
        GranteeSid = new Sid("S-1-5-21-LWE-DAVID"),
        OnUnmatched = OnUnmatched.Deny,
        TimeColumn = "ts"
    };

    [Fact]
    public async Task FiltersAndBindings_RoundTrip_AndEveryWriteIncrementsTheGeneration()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        IVirtualFilterRepository store = repository;
        var start = await store.GetGenerationAsync();

        await store.SaveFilterAsync(Filter());
        await store.SaveFilterAsync(Filter() with { ValidToColumn = null });   // same tenant and name: replaced
        var binding = Binding();
        await store.SaveBindingAsync(binding);

        var snapshot = await store.LoadSnapshotAsync();
        snapshot.Generation.ShouldBe(start + 3);
        var filter = snapshot.Filters.ShouldHaveSingleItem();
        filter.ValidToColumn.ShouldBeNull();
        filter.ManagedBy.ShouldBe(new ManagedBy("governance/access", "c1"));
        filter.StoredDefinitionHash.ShouldBe(filter.ComputeDefinitionHash());
        var loaded = snapshot.Bindings.ShouldHaveSingleItem();
        loaded.ComputeDefinitionHash().ShouldBe(binding.ComputeDefinitionHash());

        (await store.DeleteBindingAsync(new TenantId("other"), binding.Id)).ShouldBeFalse();
        (await store.DeleteBindingAsync(Tenant, binding.Id)).ShouldBeTrue();
        (await store.DeleteFilterAsync(Tenant, "nicht_ausgelieferte_krane")).ShouldBeTrue();
        (await store.DeleteFilterAsync(Tenant, "nicht_ausgelieferte_krane")).ShouldBeFalse();
        (await store.GetGenerationAsync()).ShouldBe(start + 5);
    }

    [Fact]
    public async Task SameFilterName_InTwoTenants_IsAllowed()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        IVirtualFilterRepository store = repository;

        await store.SaveFilterAsync(Filter());
        await store.SaveFilterAsync(Filter() with { Id = Guid.NewGuid(), TenantId = new TenantId("other") });

        (await store.LoadSnapshotAsync()).Filters.Count.ShouldBe(2);
    }
}
