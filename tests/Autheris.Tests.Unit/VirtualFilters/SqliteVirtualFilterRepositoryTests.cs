namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
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
    private static readonly TenantId Tenant = new("tenant_lwe");

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
            ManagedBy = new ManagedBy("governance/access", "abc123")
        };

        await Repository.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [filter] });
        var loaded = (await Repository.LoadSnapshotAsync()).Filters.ShouldHaveSingleItem();

        loaded.Name.ShouldBe(filter.Name);
        loaded.TenantId.ShouldBe(Tenant);
        loaded.Structured!.Joins.ShouldHaveSingleItem().Table.ShouldBe(new TableIdentifier("lwetem_prod", "md", "crane"));
        loaded.Structured.Where.ShouldHaveSingleItem().Operator.ShouldBe(FilterConditionOperator.IsNull);
        loaded.ValidToColumn.ShouldBe("crane.date_of_delivery");
        loaded.Supersedes.ShouldBe(["alter_filter"]);
        loaded.ManagedBy.ShouldBe(new ManagedBy("governance/access", "abc123"));
        loaded.StoredDefinitionHash.ShouldBe(filter.ComputeDefinitionHash());
        loaded.ComputeDefinitionHash().ShouldBe(filter.ComputeDefinitionHash());
    }

    [Fact]
    public async Task Profile_RoundTrips_WithItsBindings()
    {
        var profile = VirtualFilterModelTests.DavidProfile() with
        {
            Bindings =
            [
                new FilterBinding { FilterName = "nicht_ausgelieferte_krane", TargetPattern = "lwetem_prod.*.*.client_id", TimeColumn = "ts", ObjectKinds = FilterObjectKinds.Relation },
                new FilterBinding { FilterName = "per_seriennummer", ColumnMap = new Dictionary<string, string> { ["serial_number"] = "crane_serial_number" } }
            ]
        };

        await Repository.ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [profile] });
        var loaded = (await Repository.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem();

        loaded.GranteeSid.ShouldBe(new Sid("S-1-5-21-LWE-DAVID"));
        loaded.Scope.ShouldBe("lwetem_prod.*.*");
        loaded.Uncovered.ShouldBe(UncoveredPolicy.Deny);
        loaded.Bindings.Count.ShouldBe(2);
        loaded.Bindings[0].ObjectKinds.ShouldBe(FilterObjectKinds.Relation);
        loaded.Bindings[1].TargetPattern.ShouldBeNull();
        loaded.Bindings[1].ColumnMap!["serial_number"].ShouldBe("crane_serial_number");
        loaded.ComputeDefinitionHash().ShouldBe(profile.ComputeDefinitionHash());
    }

    [Fact]
    public async Task SavingTheSameName_ReplacesIt_PerTenant()
    {
        await Repository.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [VirtualFilterModelTests.DavidFilter()] });
        await Repository.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [VirtualFilterModelTests.DavidFilter() with { Id = Guid.NewGuid(), ValidToColumn = "crane.date_of_delivery" }] });
        await Repository.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [VirtualFilterModelTests.DavidFilter() with { Id = Guid.NewGuid(), TenantId = new TenantId("other") }] });

        var filters = (await Repository.LoadSnapshotAsync()).Filters;
        filters.Count.ShouldBe(2);
        filters.Single(f => f.TenantId == Tenant).ValidToColumn.ShouldBe("crane.date_of_delivery");
    }

    [Fact]
    public async Task AChangeSet_IsOneTransaction_AndIncrementsTheGenerationOnce()
    {
        var start = await Repository.GetGenerationAsync();

        await Repository.ApplyAsync(new VirtualFilterChangeSet
        {
            SaveFilters = [VirtualFilterModelTests.DavidFilter(), VirtualFilterModelTests.DavidFilter("zweiter_filter")],
            SaveProfiles = [VirtualFilterModelTests.DavidProfile()]
        });
        (await Repository.GetGenerationAsync()).ShouldBe(start + 1);

        await Repository.ApplyAsync(new VirtualFilterChangeSet { DeleteProfiles = [(Tenant, "david")], DeleteFilters = [(Tenant, "zweiter_filter")] });
        var snapshot = await Repository.LoadSnapshotAsync();
        snapshot.Generation.ShouldBe(start + 2);
        snapshot.Profiles.ShouldBeEmpty();
        snapshot.Filters.ShouldHaveSingleItem().Name.ShouldBe("nicht_ausgelieferte_krane");
    }

    [Fact]
    public async Task AFailingChangeSet_WritesNothing()
    {
        await Repository.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [VirtualFilterModelTests.DavidFilter()] });
        var start = await Repository.GetGenerationAsync();
        var duplicateId = VirtualFilterModelTests.DavidFilter("zweiter_filter") with { Id = Guid.NewGuid() };

        // the second filter reuses the primary key of the first one with another name -> constraint violation
        await Should.ThrowAsync<Exception>(() => Repository.ApplyAsync(new VirtualFilterChangeSet
        {
            SaveFilters = [duplicateId, duplicateId with { Name = "dritter_filter" }]
        }));

        var snapshot = await Repository.LoadSnapshotAsync();
        snapshot.Filters.Select(f => f.Name).ShouldBe(["nicht_ausgelieferte_krane"]);
        snapshot.Generation.ShouldBe(start);
    }

    [Fact]
    public async Task AnEmptyChangeSet_KeepsTheGeneration()
    {
        var start = await Repository.GetGenerationAsync();
        await Repository.ApplyAsync(new VirtualFilterChangeSet());
        (await Repository.GetGenerationAsync()).ShouldBe(start);
    }
}
