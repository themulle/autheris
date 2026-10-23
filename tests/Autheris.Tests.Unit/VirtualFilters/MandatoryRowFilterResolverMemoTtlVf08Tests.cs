namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>VF-08: memoized resolutions expire, so catalog changes that do not bump the VF generation are picked up.</summary>
[Trait("Category", "Security")]
public sealed class MandatoryRowFilterResolverMemoTtlVf08Tests
{
    private sealed class CountingPredicates : IVirtualFilterPredicateBuilder
    {
        public int Builds { get; private set; }

        public string Build(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect)
        {
            Builds++;
            return "P_" + filter.Name;
        }
    }

    private static (MandatoryRowFilterResolver Resolver, CountingPredicates Predicates, MandatoryFilterQuery Query) Create()
    {
        var filter = MandatoryRowFilterResolverTests.Filter("filter_a", "client_id");
        var profile = MandatoryRowFilterResolverTests.Profile(UncoveredPolicy.Skip, new FilterBinding
        {
            FilterName = "filter_a",
            TargetPattern = "lwetem_prod.*.*.client_id"
        });
        var predicates = new CountingPredicates();
        var resolver = new MandatoryRowFilterResolver(
            new MandatoryRowFilterResolverTests.FixedSnapshot(new VirtualFilterSnapshot(1, [filter], [profile])),
            predicates);
        var query = new MandatoryFilterQuery(
            MandatoryRowFilterResolverTests.David,
            new HashSet<Sid>(),
            new HashSet<string>(),
            MandatoryRowFilterResolverTests.Tenant,
            MandatoryRowFilterResolverTests.Table("dbo", "orders", "client_id"));
        return (resolver, predicates, query);
    }

    [Fact]
    public async Task WithinTheTtl_TheOutcomeIsMemoized()
    {
        var (resolver, predicates, query) = Create();

        await resolver.ResolveAsync(query);
        var afterFirst = predicates.Builds;
        await resolver.ResolveAsync(query);

        afterFirst.ShouldBeGreaterThan(0);
        predicates.Builds.ShouldBe(afterFirst);
    }

    [Fact]
    public async Task AfterTheTtl_TheOutcomeIsResolvedAgain()
    {
        var (resolver, predicates, query) = Create();
        resolver.MemoTtl = TimeSpan.Zero;

        await resolver.ResolveAsync(query);
        var afterFirst = predicates.Builds;
        await resolver.ResolveAsync(query);

        predicates.Builds.ShouldBe(afterFirst * 2);
    }
}
