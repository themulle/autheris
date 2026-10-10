namespace Autheris.Tests.Unit.VirtualFilters;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 WI-3 (M1): the resolver is the single policy decision point for the (filter, binding) pairs a caller is
/// subject to on an HTTP table. The outcome carries exactly the effective pairs (after grantee matching and
/// <c>supersedes</c>), tagged with the snapshot generation.
/// </summary>
public sealed class MandatoryRowFilterResolverAppliedBindingsTests
{
    private const long Generation = 42;
    private static readonly TenantId Tenant = MandatoryRowFilterResolverTests.Tenant;
    private static readonly Sid Alice = new("S-1-5-21-VF-ALICE");
    private static readonly Sid Bob = new("S-1-5-21-VF-BOB");

    private static readonly TableMetadata HttpCustomers = new()
    {
        Identifier = new TableIdentifier("crm_api", "public", "customers"),
        Table = new Table
        {
            SourceName = "crm_api",
            SchemaName = "public",
            TableName = "customers",
            SourceType = "Http",
            DataSourceType = DataSourceType.HttpDeclarative,
            IsActive = true
        },
        Columns = new[] { "id", "account_id", "name" }.Select(c => new TableColumn { ColumnName = c, DataType = "integer" }).ToList()
    };

    private static readonly VirtualFilter FilterA = MandatoryRowFilterResolverTests.Filter("filter_a", "client_id");
    private static readonly VirtualFilter FilterB = MandatoryRowFilterResolverTests.Filter("filter_b", "client_id");

    private static FilterBinding Bind(string filter, string targetColumn) => new()
    {
        FilterName = filter,
        TargetPattern = "crm_api.public.customers",
        ColumnMap = new Dictionary<string, string> { ["client_id"] = targetColumn }
    };

    private static VirtualFilterAccessProfile Profile(string name, Sid grantee, params FilterBinding[] bindings) => new()
    {
        TenantId = Tenant,
        Name = name,
        GranteeSid = grantee,
        Scope = "*.*.*",
        Uncovered = UncoveredPolicy.Skip,
        Bindings = bindings
    };

    private static ValueTask<MandatoryFilterOutcome> ResolveAsync(
        Sid caller, IVirtualFilterPredicateBuilder? predicates, IReadOnlyList<VirtualFilter> filters, params VirtualFilterAccessProfile[] profiles)
    {
        var resolver = new MandatoryRowFilterResolver(
            new MandatoryRowFilterResolverTests.FixedSnapshot(new VirtualFilterSnapshot(Generation, filters, profiles)),
            predicates ?? new MandatoryRowFilterResolverTests.PlaceholderPredicates());
        return resolver.ResolveAsync(new MandatoryFilterQuery(caller, new HashSet<Sid>(), new HashSet<string>(), Tenant, HttpCustomers));
    }

    [Fact]
    public async Task Resolve_TwoProfilesSameFilterDifferentColumnMaps_ReturnsBothBindings()
    {
        var outcome = await ResolveAsync(Alice, null, [FilterA],
            Profile("p1", Alice, Bind("filter_a", "id")),
            Profile("p2", Alice, Bind("filter_a", "account_id") with { TargetPattern = "crm_api.*.customers" }));

        outcome.IsDenied.ShouldBeFalse();
        outcome.AppliedBindings.Count.ShouldBe(2);
        outcome.AppliedBindings.Select(b => b.Binding.ColumnMap!["client_id"]).OrderBy(c => c).ShouldBe(["account_id", "id"]);
        outcome.AppliedBindings.ShouldAllBe(b => b.SnapshotGeneration == Generation && b.Filter.Id == FilterA.Id);
    }

    [Fact]
    public async Task Resolve_OtherGranteesProfile_NotInAppliedBindings()
    {
        // ST-10: Alice's decision contains only Alice's binding.
        var outcome = await ResolveAsync(Alice, null, [FilterA, FilterB],
            Profile("alice", Alice, Bind("filter_a", "id")),
            Profile("bob", Bob, Bind("filter_b", "id")));

        outcome.AppliedBindings.ShouldHaveSingleItem().Filter.Name.ShouldBe("filter_a");
        outcome.AppliedFilters.ShouldBe(["filter_a"]);
    }

    [Fact]
    public async Task Resolve_SupersededFilter_NotInAppliedBindings()
    {
        var replacing = FilterB with { Supersedes = ["filter_a"] };

        var outcome = await ResolveAsync(Alice, null, [FilterA, replacing],
            Profile("alice", Alice, Bind("filter_a", "id"), Bind("filter_b", "id") with { TargetPattern = "crm_api.public.*" }));

        outcome.AppliedBindings.ShouldHaveSingleItem().Filter.Name.ShouldBe("filter_b");
    }

    [Fact]
    public async Task Resolve_IdenticalPairInTwoProfiles_AppearsOnce()
    {
        var outcome = await ResolveAsync(Alice, null, [FilterA],
            Profile("p1", Alice, Bind("filter_a", "id")),
            Profile("p2", Alice, Bind("filter_a", "id")));

        outcome.AppliedBindings.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Resolve_HttpTarget_ProducesPredicateAndBindings()
    {
        // The real predicate builder does not throw for HTTP metadata; the predicate stays the deny path for every
        // channel that cannot enforce it, the bindings feed the gateway enforcement.
        var outcome = await ResolveAsync(Alice, new StructuredFilterSqlBuilder(), [FilterA], Profile("alice", Alice, Bind("filter_a", "id")));

        outcome.IsDenied.ShouldBeFalse();
        outcome.PredicateSql.ShouldNotBeNull().ShouldContain("EXISTS");
        var pair = outcome.AppliedBindings.ShouldHaveSingleItem();
        pair.Filter.ShouldBeSameAs(FilterA);
        pair.Key.TenantId.ShouldBe(Tenant);
        pair.Key.FilterDefinitionHash.ShouldBe(FilterA.ComputeDefinitionHash());
    }

    [Fact]
    public async Task Resolve_NoApplyingFilter_HasNoBindings()
    {
        var outcome = await ResolveAsync(Bob, null, [FilterA], Profile("alice", Alice, Bind("filter_a", "id")));

        outcome.ShouldBe(MandatoryFilterOutcome.None);
        outcome.AppliedBindings.ShouldBeEmpty();
    }
}
