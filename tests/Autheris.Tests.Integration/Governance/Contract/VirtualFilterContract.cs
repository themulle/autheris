using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.VirtualFilters;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>IVirtualFilterRepository</c> across PostgreSQL and SQL Server engines: filters and access profiles are
/// written as one change set (one transaction, one generation increment) and replaced by tenant and name.
/// </summary>
public abstract class VirtualFilterContract : GovernanceContractBase
{
    private const string DefaultName = "nicht_ausgelieferte_krane";

    private IVirtualFilterRepository Repo => Store.VirtualFilters;

    private VirtualFilter Filter(string name = DefaultName, TenantId? tenant = null) =>
        VirtualFilterTestData.DavidFilter(name) with { TenantId = tenant ?? Store.TenantA };

    private VirtualFilterAccessProfile Profile(TenantId? tenant = null) =>
        VirtualFilterTestData.DavidProfile() with { TenantId = tenant ?? Store.TenantA };

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Repo.ApplyAsync(new VirtualFilterChangeSet
        {
            SaveFilters = [Filter("only_a"), Filter("only_b", Store.TenantB)],
            SaveProfiles = [Profile(), Profile(Store.TenantB)]
        });

        var snapshot = await Repo.LoadSnapshotAsync();

        snapshot.Filters.Single(f => f.Name == "only_a").TenantId.ShouldBe(Store.TenantA);
        snapshot.Filters.Single(f => f.Name == "only_b").TenantId.ShouldBe(Store.TenantB);
        snapshot.Profiles.Count(p => p.TenantId == Store.TenantA).ShouldBe(1);
        snapshot.Profiles.Count(p => p.TenantId == Store.TenantB).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter()], SaveProfiles = [Profile()] });

        await Repo.ApplyAsync(new VirtualFilterChangeSet
        {
            SaveFilters = [Filter(tenant: Store.TenantB) with { Id = Guid.NewGuid(), ValidToColumn = "crane.date_of_delivery" }]
        });
        await Repo.ApplyAsync(new VirtualFilterChangeSet
        {
            DeleteFilters = [(Store.TenantB, DefaultName)],
            DeleteProfiles = [(Store.TenantB, "david")]
        });

        var snapshot = await Repo.LoadSnapshotAsync();
        snapshot.Filters.Single(f => f.TenantId == Store.TenantA).ValidToColumn.ShouldBeNull();
        snapshot.Filters.Any(f => f.TenantId == Store.TenantB).ShouldBeFalse();
        snapshot.Profiles.Single().TenantId.ShouldBe(Store.TenantA);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var start = await Repo.GetGenerationAsync();

        Should.Throw<ArgumentException>(() => { _ = new TenantId("invalid tenant!"); });
        await Should.ThrowAsync<Exception>(() => Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter() with { TenantId = default }] }));

        (await Repo.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
        (await Repo.GetGenerationAsync()).ShouldBe(start);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var filter = Filter();
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [filter], SaveProfiles = [Profile()] });

        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [filter], SaveProfiles = [Profile()] });

        var snapshot = await Repo.LoadSnapshotAsync();
        snapshot.Filters.ShouldHaveSingleItem().ComputeDefinitionHash().ShouldBe(filter.ComputeDefinitionHash());
        snapshot.Profiles.ShouldHaveSingleItem();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter()] });
        var start = await Repo.GetGenerationAsync();
        var duplicateId = Filter("zweiter_filter") with { Id = Guid.NewGuid() };

        // the second filter reuses the primary key of the first one with another name -> constraint violation
        await Should.ThrowAsync<Exception>(() => Repo.ApplyAsync(new VirtualFilterChangeSet
        {
            SaveFilters = [duplicateId, duplicateId with { Name = "dritter_filter" }]
        }));

        var snapshot = await Repo.LoadSnapshotAsync();
        snapshot.Filters.Select(f => f.Name).ShouldBe([DefaultName]);
        snapshot.Generation.ShouldBe(start);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter("c_filter"), Filter("a_filter"), Filter("b_filter")] });

        var first = (await Repo.LoadSnapshotAsync()).Filters.Select(f => f.Name).ToList();
        var second = (await Repo.LoadSnapshotAsync()).Filters.Select(f => f.Name).ToList();

        first.Order().ShouldBe(["a_filter", "b_filter", "c_filter"]);
        second.ShouldBe(first);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var filters = Enumerable.Range(0, 60).Select(i => Filter($"filter_{i:D2}")).ToList();
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = filters });

        (await Repo.LoadSnapshotAsync()).Filters.Count.ShouldBe(60);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter()], SaveProfiles = [Profile()] });

        await Repo.ApplyAsync(new VirtualFilterChangeSet { DeleteProfiles = [(Store.TenantA, "david")], DeleteFilters = [(Store.TenantA, DefaultName)] });

        var snapshot = await Repo.LoadSnapshotAsync();
        snapshot.Filters.ShouldBeEmpty();
        snapshot.Profiles.ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task Filter_RoundTrips_WithItsDefinitionAndHash()
    {
        if (!Store.IsAvailable) return;
        var filter = Filter() with
        {
            ValidToColumn = "crane.date_of_delivery",
            Supersedes = ["alter_filter"],
            ManagedBy = new ManagedBy("governance/access", "abc123")
        };

        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [filter] });
        var loaded = (await Repo.LoadSnapshotAsync()).Filters.ShouldHaveSingleItem();

        loaded.Name.ShouldBe(filter.Name);
        loaded.TenantId.ShouldBe(Store.TenantA);
        loaded.Structured!.Joins.ShouldHaveSingleItem().Table.ShouldBe(new TableIdentifier("lwetem_prod", "md", "crane"));
        loaded.Structured.Where.ShouldHaveSingleItem().Operator.ShouldBe(FilterConditionOperator.IsNull);
        loaded.ValidToColumn.ShouldBe("crane.date_of_delivery");
        loaded.Supersedes.ShouldBe(["alter_filter"]);
        loaded.ManagedBy.ShouldBe(new ManagedBy("governance/access", "abc123"));
        loaded.StoredDefinitionHash.ShouldBe(filter.ComputeDefinitionHash());
        loaded.ComputeDefinitionHash().ShouldBe(filter.ComputeDefinitionHash());
    }

    [RequiresContainerRuntimeFact]
    public async Task Profile_RoundTrips_WithItsBindings()
    {
        if (!Store.IsAvailable) return;
        var profile = Profile() with
        {
            Bindings =
            [
                new FilterBinding { FilterName = DefaultName, TargetPattern = "lwetem_prod.*.*.client_id", TimeColumn = "ts", ObjectKinds = FilterObjectKinds.Relation },
                new FilterBinding { FilterName = "per_seriennummer", ColumnMap = new Dictionary<string, string> { ["serial_number"] = "crane_serial_number" } }
            ]
        };

        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [profile] });
        var loaded = (await Repo.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem();

        loaded.GranteeSid.ShouldBe(new Sid("S-1-5-21-LWE-DAVID"));
        loaded.Scope.ShouldBe("lwetem_prod.*.*");
        loaded.Uncovered.ShouldBe(UncoveredPolicy.Deny);
        loaded.Bindings.Count.ShouldBe(2);
        loaded.Bindings[0].ObjectKinds.ShouldBe(FilterObjectKinds.Relation);
        loaded.Bindings[1].TargetPattern.ShouldBeNull();
        loaded.Bindings[1].ColumnMap!["serial_number"].ShouldBe("crane_serial_number");
        loaded.ComputeDefinitionHash().ShouldBe(profile.ComputeDefinitionHash());
    }

    [RequiresContainerRuntimeFact]
    public async Task SavingTheSameName_ReplacesIt_PerTenant()
    {
        if (!Store.IsAvailable) return;
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter()] });
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter() with { Id = Guid.NewGuid(), ValidToColumn = "crane.date_of_delivery" }] });
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter(tenant: Store.TenantB) with { Id = Guid.NewGuid() }] });

        var filters = (await Repo.LoadSnapshotAsync()).Filters;
        filters.Count.ShouldBe(2);
        filters.Single(f => f.TenantId == Store.TenantA).ValidToColumn.ShouldBe("crane.date_of_delivery");
    }

    [RequiresContainerRuntimeFact]
    public async Task AChangeSet_IsOneTransaction_AndIncrementsTheGenerationOnce()
    {
        if (!Store.IsAvailable) return;
        var start = await Repo.GetGenerationAsync();

        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter(), Filter("zweiter_filter")], SaveProfiles = [Profile()] });
        (await Repo.GetGenerationAsync()).ShouldBe(start + 1);

        await Repo.ApplyAsync(new VirtualFilterChangeSet { DeleteProfiles = [(Store.TenantA, "david")], DeleteFilters = [(Store.TenantA, "zweiter_filter")] });
        var snapshot = await Repo.LoadSnapshotAsync();
        snapshot.Generation.ShouldBe(start + 2);
        snapshot.Profiles.ShouldBeEmpty();
        snapshot.Filters.ShouldHaveSingleItem().Name.ShouldBe(DefaultName);
    }

    [RequiresContainerRuntimeFact]
    public async Task AnEmptyChangeSet_KeepsTheGeneration()
    {
        if (!Store.IsAvailable) return;
        var start = await Repo.GetGenerationAsync();

        await Repo.ApplyAsync(new VirtualFilterChangeSet());

        (await Repo.GetGenerationAsync()).ShouldBe(start);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Apply_StaleGeneration_ThrowsConflict()
    {
        if (!Store.IsAvailable) return;
        // a change computed from an older snapshot is refused and nothing is written
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter()] });
        var stale = (await Repo.LoadSnapshotAsync()).Generation;
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter() with { Id = Guid.NewGuid(), ValidToColumn = "crane.date_of_delivery" }] });
        var before = await Repo.LoadSnapshotAsync();

        await Should.ThrowAsync<VirtualFilterConflictException>(() => Repo.ApplyAsync(new VirtualFilterChangeSet
        {
            ExpectedGeneration = stale,
            DeleteFilters = [(Store.TenantA, DefaultName)]
        }));

        var after = await Repo.LoadSnapshotAsync();
        after.Generation.ShouldBe(before.Generation);
        after.Filters.Count.ShouldBe(before.Filters.Count);
    }

    [RequiresContainerRuntimeFact]
    public async Task Apply_CurrentGeneration_SucceedsAndIncrementsOnce()
    {
        if (!Store.IsAvailable) return;
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter()] });
        var current = await Repo.LoadSnapshotAsync();

        await Repo.ApplyAsync(new VirtualFilterChangeSet { ExpectedGeneration = current.Generation, DeleteFilters = [(Store.TenantA, DefaultName)] });

        var after = await Repo.LoadSnapshotAsync();
        after.Generation.ShouldBe(current.Generation + 1);
        after.Filters.ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task ConcurrentApproveAndSave_SecondFails_ApprovedDefinitionKept()
    {
        if (!Store.IsAvailable) return;
        await Repo.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter()] });
        var read = await Repo.LoadSnapshotAsync();
        var original = read.Filters.Single();

        // Reviewer A saves a changed definition first; reviewer B still holds the older snapshot.
        await Repo.ApplyAsync(new VirtualFilterChangeSet { ExpectedGeneration = read.Generation, SaveFilters = [original with { ValidToColumn = "crane.date_of_delivery" }] });

        await Should.ThrowAsync<VirtualFilterConflictException>(() => Repo.ApplyAsync(new VirtualFilterChangeSet
        {
            ExpectedGeneration = read.Generation,
            SaveFilters = [original with { ValidToColumn = null }]
        }));

        (await Repo.LoadSnapshotAsync()).Filters.Single().ValidToColumn.ShouldBe("crane.date_of_delivery");
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlVirtualFilterContractTests(GovernancePostgresFixture fixture) : VirtualFilterContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "virtual_filter_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerVirtualFilterContractTests(GovernanceSqlServerFixture fixture) : VirtualFilterContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "virtual_filter_contract");
}
