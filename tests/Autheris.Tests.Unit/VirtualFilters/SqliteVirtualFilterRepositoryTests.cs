namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Virtual filters, phase 2: storage contract (SQLite; PostgreSQL in the integration tests).</summary>
public sealed class SqliteVirtualFilterRepositoryTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repository = new(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=vf_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));

    private IVirtualFilterRepository Repository => _repository;

    public void Dispose() => _repository.Dispose();

    [Fact]
    public async Task Filter_RoundTrips_WithItsDefinitionAndHash()
    {
        var filter = VirtualFilterModelTests.DavidFilter() with
        {
            ValidToColumn = "crane.date_of_delivery",
            Supersedes = ["alter_filter"],
            ManagedBy = new ManagedBy("governance/access/david.yaml", "abc123")
        };

        await Repository.SaveFilterAsync(filter);
        var snapshot = await Repository.LoadSnapshotAsync();

        var loaded = snapshot.Filters.ShouldHaveSingleItem();
        loaded.Name.ShouldBe(filter.Name);
        loaded.TenantId.ShouldBe(filter.TenantId);
        loaded.Structured!.Joins.ShouldHaveSingleItem().Table.ShouldBe(new TableIdentifier("lwetem_prod", "md", "crane"));
        loaded.Structured.Where.ShouldHaveSingleItem().Operator.ShouldBe(FilterConditionOperator.IsNull);
        loaded.ValidToColumn.ShouldBe("crane.date_of_delivery");
        loaded.Supersedes.ShouldBe(["alter_filter"]);
        loaded.ManagedBy.ShouldBe(new ManagedBy("governance/access/david.yaml", "abc123"));
        loaded.StoredDefinitionHash.ShouldBe(filter.ComputeDefinitionHash());
        loaded.ComputeDefinitionHash().ShouldBe(filter.ComputeDefinitionHash());
    }

    [Fact]
    public async Task Binding_RoundTrips()
    {
        var binding = VirtualFilterModelTests.DavidBinding() with
        {
            TimeColumn = "ts",
            ColumnMap = new System.Collections.Generic.Dictionary<string, string> { ["client_id"] = "cid" },
            ObjectKinds = FilterObjectKinds.Relation
        };

        await Repository.SaveBindingAsync(binding);
        var loaded = (await Repository.LoadSnapshotAsync()).Bindings.ShouldHaveSingleItem();

        loaded.Id.ShouldBe(binding.Id);
        loaded.GranteeSid.ShouldBe(binding.GranteeSid);
        loaded.OnUnmatched.ShouldBe(OnUnmatched.Deny);
        loaded.ObjectKinds.ShouldBe(FilterObjectKinds.Relation);
        loaded.ColumnMap!["client_id"].ShouldBe("cid");
        loaded.ComputeDefinitionHash().ShouldBe(binding.ComputeDefinitionHash());
    }

    [Fact]
    public async Task SavingAFilterWithTheSameName_ReplacesIt_PerTenant()
    {
        await Repository.SaveFilterAsync(VirtualFilterModelTests.DavidFilter());
        await Repository.SaveFilterAsync(VirtualFilterModelTests.DavidFilter() with { ValidToColumn = "crane.date_of_delivery" });
        await Repository.SaveFilterAsync(VirtualFilterModelTests.DavidFilter() with { TenantId = new TenantId("other") });

        var filters = (await Repository.LoadSnapshotAsync()).Filters;
        filters.Count.ShouldBe(2);
        filters.Single(f => f.TenantId == new TenantId("tenant_lwe")).ValidToColumn.ShouldBe("crane.date_of_delivery");
    }

    [Fact]
    public async Task EveryWrite_IncrementsTheGeneration()
    {
        var start = await Repository.GetGenerationAsync();

        await Repository.SaveFilterAsync(VirtualFilterModelTests.DavidFilter());
        var binding = VirtualFilterModelTests.DavidBinding();
        await Repository.SaveBindingAsync(binding);
        (await Repository.DeleteBindingAsync(binding.TenantId, binding.Id)).ShouldBeTrue();
        (await Repository.DeleteFilterAsync(new TenantId("tenant_lwe"), "nicht_ausgelieferte_krane")).ShouldBeTrue();

        (await Repository.GetGenerationAsync()).ShouldBe(start + 4);
        (await Repository.LoadSnapshotAsync()).Generation.ShouldBe(start + 4);
    }

    [Fact]
    public async Task DeletingSomethingMissing_ReturnsFalse_WithoutNewGeneration()
    {
        var start = await Repository.GetGenerationAsync();

        (await Repository.DeleteFilterAsync(new TenantId("tenant_lwe"), "gibt_es_nicht")).ShouldBeFalse();
        (await Repository.DeleteBindingAsync(new TenantId("tenant_lwe"), Guid.NewGuid())).ShouldBeFalse();

        (await Repository.GetGenerationAsync()).ShouldBe(start);
    }

    [Fact]
    public async Task DeleteBinding_OnlyWithinItsTenant()
    {
        var binding = VirtualFilterModelTests.DavidBinding();
        await Repository.SaveBindingAsync(binding);

        (await Repository.DeleteBindingAsync(new TenantId("other"), binding.Id)).ShouldBeFalse();
        (await Repository.LoadSnapshotAsync()).Bindings.ShouldHaveSingleItem();
    }
}
