namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using FsCheck;
using FsCheck.Xunit;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 3: which filters apply to a caller and an object. Bindings of the caller's profiles apply
/// when the target pattern matches and the object has every column the filter needs; several apply with AND; an
/// object in scope that no binding covers follows the profile's uncovered policy; supersedes replaces.
/// </summary>
public sealed class MandatoryRowFilterResolverTests
{
    internal static readonly TenantId Tenant = new("tenant_lwe");
    internal static readonly Sid David = new("S-1-5-21-LWE-DAVID");

    internal sealed class FixedSnapshot(VirtualFilterSnapshot snapshot) : IVirtualFilterSnapshotProvider
    {
        public VirtualFilterSnapshot Snapshot { get; set; } = snapshot;
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(Snapshot);
        public void Invalidate() { }
    }

    /// <summary>Placeholder SQL "P_filter(column,...)" so the tests see which filter applied to which columns.</summary>
    internal sealed class PlaceholderPredicates : IVirtualFilterPredicateBuilder
    {
        public string Build(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect) =>
            $"P_{filter.Name}({string.Join(",", VirtualFilterColumns.RequiredTargetColumns(filter, binding))})";
    }

    internal static VirtualFilter Filter(string name, params string[] keys) => VirtualFilterModelTests.DavidFilter(name) with
    {
        KeyColumns = keys.Select(k => "client." + k).ToList()
    };

    internal static AccessProfile Profile(UncoveredPolicy uncovered, params FilterBinding[] bindings) => new()
    {
        TenantId = Tenant,
        Name = "david",
        GranteeSid = David,
        Scope = "lwetem_prod.*.*",
        Uncovered = uncovered,
        Bindings = bindings
    };

    internal static TableMetadata Table(string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", schema, table),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = schema, TableName = table, SourceType = "SqlServer" },
        Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static MandatoryRowFilterResolver Resolver(VirtualFilterSnapshot snapshot) =>
        new(new FixedSnapshot(snapshot), new PlaceholderPredicates());

    private static VirtualFilterSnapshot Snapshot(IEnumerable<VirtualFilter> filters, params AccessProfile[] profiles) => new(1, filters.ToList(), profiles);

    private static MandatoryFilterQuery Query(TableMetadata table, Sid? user = null, IReadOnlySet<Sid>? groups = null, IReadOnlySet<string>? roles = null,
        FilterObjectKinds kind = FilterObjectKinds.Relation, TenantId? tenant = null) =>
        new(user ?? David, groups ?? new HashSet<Sid>(), roles ?? new HashSet<string>(), tenant ?? Tenant, table, kind);

    private static readonly VirtualFilter ByClient = Filter("filter_a", "client_id");
    private static readonly VirtualFilter ByClientAndTs = Filter("filter_b", "client_id", "ts");

    [Fact]
    public async Task MatchingBinding_YieldsItsPredicate()
    {
        var resolver = Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a", TargetPattern = "lwetem_prod.*.*.client_id" })));

        var outcome = await resolver.ResolveAsync(Query(Table("fms", "air1", "id", "client_id")));

        outcome.IsDenied.ShouldBeFalse();
        outcome.PredicateSql.ShouldBe("P_filter_a(client_id)");
        outcome.AppliedFilters.ShouldBe(["filter_a"]);
    }

    [Fact]
    public async Task NoProfileForTheCaller_ChangesNothing()
    {
        var resolver = Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a" })));

        var outcome = await resolver.ResolveAsync(Query(Table("fms", "air1", "client_id"), user: new Sid("S-1-5-21-OTHER")));

        outcome.ShouldBe(MandatoryFilterOutcome.None);
    }

    [Theory]
    [InlineData(GranteeType.Group, true)]
    [InlineData(GranteeType.Role, true)]
    [InlineData(GranteeType.User, false)]
    public async Task ProfilesForGroupsAndRoles_ApplyLikeConsents(GranteeType type, bool expected)
    {
        var profile = Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a" }) with
        {
            GranteeType = type,
            GranteeSid = type == GranteeType.Role ? (Sid?)null : new Sid("S-1-5-21-GROUP-CRANES"),
            RoleName = type == GranteeType.Role ? "CraneAnalyst" : null
        };
        var resolver = Resolver(Snapshot([ByClient], profile));

        var outcome = await resolver.ResolveAsync(Query(Table("fms", "air1", "client_id"),
            groups: new HashSet<Sid> { new("S-1-5-21-GROUP-CRANES") }, roles: new HashSet<string> { "CraneAnalyst" }));

        (outcome.PredicateSql != null).ShouldBe(expected);
    }

    [Fact]
    public async Task ProfileOfAnotherTenant_DoesNotApply()
    {
        var resolver = Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a" })));

        (await resolver.ResolveAsync(Query(Table("fms", "air1", "client_id"), tenant: new TenantId("other")))).ShouldBe(MandatoryFilterOutcome.None);
    }

    [Fact]
    public async Task ObjectOutsideTheScope_IsUnchanged()
    {
        var resolver = Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a" }) with { Scope = "lwetem_prod.fms.*" }));

        (await resolver.ResolveAsync(Query(Table("md", "crane", "serial_number")))).ShouldBe(MandatoryFilterOutcome.None);
    }

    [Fact]
    public async Task UncoveredObjectInScope_IsDeniedWithReason_OrSkipped()
    {
        var binding = new FilterBinding { FilterName = "filter_a", TargetPattern = "lwetem_prod.*.*.client_id" };
        var crane = Table("md", "crane", "serial_number", "is_delivered");

        var denied = await Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Deny, binding))).ResolveAsync(Query(crane));
        denied.IsDenied.ShouldBeTrue();
        denied.DenyReason.ShouldNotBeNull().ShouldContain("david");

        (await Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Skip, binding))).ResolveAsync(Query(crane))).ShouldBe(MandatoryFilterOutcome.None);
    }

    [Fact]
    public async Task AFilterWithoutTheNeededColumns_DoesNotApply_AnotherFilterOfTheProfileCovers()
    {
        // filter_b needs client_id and ts; dm.dm1 only has client_id -> only filter_a covers it (not denied as uncovered)
        var profile = Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a" }, new FilterBinding { FilterName = "filter_b" });
        var resolver = Resolver(Snapshot([ByClient, ByClientAndTs], profile));

        var both = await resolver.ResolveAsync(Query(Table("fms", "air1", "client_id", "ts")));
        both.PredicateSql.ShouldBe("(P_filter_a(client_id)) AND (P_filter_b(client_id,ts))");
        both.AppliedFilters.ShouldBe(["filter_a", "filter_b"]);

        var onlyA = await resolver.ResolveAsync(Query(Table("dm", "dm1", "client_id")));
        onlyA.PredicateSql.ShouldBe("P_filter_a(client_id)");
    }

    [Fact]
    public async Task ColumnSegment_MustMatchAColumnOfTheObject()
    {
        var resolver = Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Skip, new FilterBinding { FilterName = "filter_a", TargetPattern = "lwetem_prod.*.*.device_id" })));

        (await resolver.ResolveAsync(Query(Table("fms", "air1", "client_id")))).ShouldBe(MandatoryFilterOutcome.None);
    }

    [Fact]
    public async Task ColumnMap_RenamesTheRequiredColumn()
    {
        var resolver = Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Deny,
            new FilterBinding { FilterName = "filter_a", ColumnMap = new Dictionary<string, string> { ["client_id"] = "cid" } })));

        (await resolver.ResolveAsync(Query(Table("fms", "air9", "cid")))).PredicateSql.ShouldBe("P_filter_a(cid)");
        (await resolver.ResolveAsync(Query(Table("fms", "air1", "client_id")))).IsDenied.ShouldBeTrue();
    }

    [Fact]
    public async Task ObjectKinds_LimitWhereABindingApplies()
    {
        var resolver = Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Skip, new FilterBinding { FilterName = "filter_a", ObjectKinds = FilterObjectKinds.Relation })));

        (await resolver.ResolveAsync(Query(Table("fms", "air1", "client_id"), kind: FilterObjectKinds.ProcedureResult))).ShouldBe(MandatoryFilterOutcome.None);
    }

    [Fact]
    public async Task ObjectOfAnotherDataSourceThanTheFilter_IsNotCovered()
    {
        var foreign = Table("fms", "air1", "client_id") with { Identifier = new TableIdentifier("other_source", "fms", "air1") };
        var resolver = Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a", TargetPattern = "*.*.*" }) with { Scope = "*.*.*" }));

        (await resolver.ResolveAsync(Query(foreign))).IsDenied.ShouldBeTrue();
    }

    [Fact]
    public async Task Supersedes_ReplacesTheOtherFilter_WhereBothApply()
    {
        var replacing = ByClientAndTs with { Supersedes = ["filter_a"] };
        var resolver = Resolver(Snapshot([ByClient, replacing], Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a" }, new FilterBinding { FilterName = "filter_b" })));

        (await resolver.ResolveAsync(Query(Table("fms", "air1", "client_id", "ts")))).AppliedFilters.ShouldBe(["filter_b"]);
        (await resolver.ResolveAsync(Query(Table("dm", "dm1", "client_id")))).AppliedFilters.ShouldBe(["filter_a"]);
    }

    [Fact]
    public async Task TwoProfilesOfTheCaller_BothApply_AndAnyUncoveredDenyDenies()
    {
        var userProfile = Profile(UncoveredPolicy.Skip, new FilterBinding { FilterName = "filter_a" });
        var roleProfile = Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_b" }) with
        {
            Name = "analysts", GranteeType = GranteeType.Role, GranteeSid = null, RoleName = "CraneAnalyst"
        };
        var resolver = Resolver(Snapshot([ByClient, ByClientAndTs], userProfile, roleProfile));
        var roles = new HashSet<string> { "CraneAnalyst" };

        (await resolver.ResolveAsync(Query(Table("fms", "air1", "client_id", "ts"), roles: roles))).AppliedFilters.ShouldBe(["filter_a", "filter_b"]);
        (await resolver.ResolveAsync(Query(Table("dm", "dm1", "client_id"), roles: roles))).IsDenied.ShouldBeTrue();   // role profile: uncovered deny
    }

    [Fact]
    public async Task UnknownFilterOrBuilderFailure_FailsClosed()
    {
        var resolver = new MandatoryRowFilterResolver(
            new FixedSnapshot(Snapshot([ByClient], Profile(UncoveredPolicy.Skip, new FilterBinding { FilterName = "filter_a" }))),
            new ThrowingPredicates());

        var outcome = await resolver.ResolveAsync(Query(Table("fms", "air1", "client_id")));

        outcome.IsDenied.ShouldBeTrue();
        outcome.DenyReason.ShouldNotBeNull().ShouldContain("filter_a");
    }

    private sealed class ThrowingPredicates : IVirtualFilterPredicateBuilder
    {
        public string Build(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect) =>
            throw new InvalidOperationException("cannot build");
    }

    [Fact]
    public async Task ANewGeneration_IsUsedImmediately()
    {
        var provider = new FixedSnapshot(Snapshot([ByClient], Profile(UncoveredPolicy.Skip, new FilterBinding { FilterName = "filter_a" })));
        var resolver = new MandatoryRowFilterResolver(provider, new PlaceholderPredicates());
        var air1 = Table("fms", "air1", "client_id", "ts");
        (await resolver.ResolveAsync(Query(air1))).AppliedFilters.ShouldBe(["filter_a"]);

        provider.Snapshot = new VirtualFilterSnapshot(2, [ByClient, ByClientAndTs],
            [Profile(UncoveredPolicy.Skip, new FilterBinding { FilterName = "filter_a" }, new FilterBinding { FilterName = "filter_b" })]);

        (await resolver.ResolveAsync(Query(air1))).AppliedFilters.ShouldBe(["filter_a", "filter_b"]);
    }

    [Fact]
    public async Task SameSchemaAndTableInAnotherDataSource_IsResolvedSeparately()
    {
        // The memo must key on the full identifier: same schema and table name in another data source is another object.
        var resolver = Resolver(Snapshot([ByClient], Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a" }) with { Scope = "*.*.*" }));
        var local = Table("fms", "air1", "client_id");
        var foreign = local with { Identifier = new TableIdentifier("other_source", "fms", "air1") };

        (await resolver.ResolveAsync(Query(local))).PredicateSql.ShouldBe("P_filter_a(client_id)");
        (await resolver.ResolveAsync(Query(foreign))).IsDenied.ShouldBeTrue();   // filter_a is defined for lwetem_prod only
    }

    [Property(MaxTest = 50)]
    public bool OrderOfBindings_DoesNotChangeTheResult(bool reverse)
    {
        var bindings = new[] { new FilterBinding { FilterName = "filter_b" }, new FilterBinding { FilterName = "filter_a" } };
        var resolver = Resolver(Snapshot([ByClient, ByClientAndTs], Profile(UncoveredPolicy.Deny, reverse ? bindings.Reverse().ToArray() : bindings)));
        var outcome = resolver.ResolveAsync(Query(Table("fms", "air1", "client_id", "ts"))).AsTask().GetAwaiter().GetResult();
        return outcome.PredicateSql == "(P_filter_a(client_id)) AND (P_filter_b(client_id,ts))";
    }
}
