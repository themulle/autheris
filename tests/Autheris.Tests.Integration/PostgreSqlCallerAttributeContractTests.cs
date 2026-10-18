namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PLAN-GOV-129 WP-1: the caller attribute store contract on real PostgreSql (same cases as the SQLite repository tests). Needs Docker; without it each test returns early.</summary>
public sealed class PostgreSqlCallerAttributeContractTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("tenant_a");
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
    private PostgreSqlTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            _available = _db.IsAvailable;
        }
        catch (Exception ex)
        {
            _available = false;
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
        }
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private PostgreSqlGovernanceRepository NewRepository()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db!.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
        }), env);
    }

    private static CallerAttributeDefinition Definition(string description = "v1") => new(
        Guid.NewGuid(), Tenant, "cost_center", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60,
        new CallerAttributeProviderBinding("claims", new Dictionary<string, string> { ["claim"] = "department" }), description, FilterApprovalStatus.PendingApproval);

    [Fact]
    public async Task FourEyes_Flow_HoldsOnTheEngine()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        ICallerAttributeRepository store = repository;
        var start = await store.GetCallerAttributeGenerationAsync();
        var def = Definition();

        (await store.SubmitDefinitionAsync(def, "alice", Now)).ShouldBe(CallerAttributeChangeResult.Applied);
        (await store.ApproveDefinitionAsync(Tenant, def.Name, "ALICE", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.SameSubject);
        (await store.ApproveDefinitionAsync(Tenant, def.Name, "bob", "stale", Now)).ShouldBe(CallerAttributeChangeResult.HashMismatch);
        (await store.ApproveDefinitionAsync(Tenant, def.Name, "bob", def.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.Applied);

        var changed = def with { Description = "v2" };
        await store.SubmitDefinitionAsync(changed, "carol", Now);
        var record = (await store.LoadCallerAttributeSnapshotAsync()).Attributes.Single();
        record.Definition.Description.ShouldBe("v1");
        record.Draft!.Description.ShouldBe("v2");
        (await store.ApproveDefinitionAsync(Tenant, def.Name, "bob", changed.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.Applied);

        await store.RequestDeletionAsync(Tenant, def.Name, "carol", Now);
        (await store.ApproveDefinitionAsync(Tenant, def.Name, "bob", changed.ComputeDefinitionHash(), Now)).ShouldBe(CallerAttributeChangeResult.Applied);
        var after = await store.LoadCallerAttributeSnapshotAsync();
        after.Attributes.ShouldBeEmpty();
        after.Generation.ShouldBe(start + 6);
    }

    [Fact]
    public async Task ServiceSet_FourEyes_HoldsOnTheEngine()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        ICallerAttributeRepository store = repository;
        var app = "AAAAAAAA-0000-0000-0000-000000000001";
        await store.SubmitServiceSetAsync(Tenant, app, "cost_center", ["100"], "alice", Now);
        var set = (await store.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single().Set;
        set.ApplicationId.ShouldBe(app.ToLowerInvariant());

        (await store.ApproveServiceSetAsync(Tenant, app, "cost_center", "alice", set.ComputeValuesHash(), Now)).ShouldBe(CallerAttributeChangeResult.SameSubject);
        (await store.ApproveServiceSetAsync(Tenant, app, "cost_center", "bob", set.ComputeValuesHash(), Now)).ShouldBe(CallerAttributeChangeResult.Applied);
        (await store.LoadCallerAttributeSnapshotAsync()).ServiceSets.Single().Set.Status.ShouldBe(FilterApprovalStatus.Active);
    }
}
