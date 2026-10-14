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
/// F-GOV-14 WI-2 (SEC-7, SEC-8): target patterns of cross-source bindings on HTTP tables. A regex pattern matches the
/// full, source-qualified name; a pattern that cannot be evaluated denies with <c>VF_PATTERN_EVALUATION_FAILED</c>
/// instead of silently not applying the filter.
/// </summary>
public sealed class MandatoryRowFilterResolverCrossSourceTests
{
    private static readonly TenantId Tenant = MandatoryRowFilterResolverTests.Tenant;
    private static readonly Sid David = MandatoryRowFilterResolverTests.David;

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
        Columns = new[] { "id", "name" }.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static MandatoryRowFilterResolver Resolver(string targetPattern, UncoveredPolicy uncovered = UncoveredPolicy.Skip)
    {
        var filter = MandatoryRowFilterResolverTests.Filter("filter_a", "client_id");
        var profile = MandatoryRowFilterResolverTests.Profile(uncovered, new FilterBinding
        {
            FilterName = "filter_a",
            TargetPattern = targetPattern,
            ColumnMap = new Dictionary<string, string> { ["client_id"] = "id" }
        }) with { Scope = "*.*.*" };

        return new MandatoryRowFilterResolver(
            new MandatoryRowFilterResolverTests.FixedSnapshot(new VirtualFilterSnapshot(1, [filter], [profile])),
            new MandatoryRowFilterResolverTests.PlaceholderPredicates());
    }

    private static ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryRowFilterResolver resolver) =>
        resolver.ResolveAsync(new MandatoryFilterQuery(David, new HashSet<Sid>(), new HashSet<string>(), Tenant, HttpCustomers));

    [Theory]
    [InlineData("regex:^crm_api\\.(public")]   // invalid regex written directly to the repository
    [InlineData("regex:^(a)\\1$")]             // backreference: not supported by the linear engine
    [InlineData("^(?=crm).*")]                 // lookaround: not supported by the linear engine
    public async Task Resolve_InvalidStoredRegex_DeniesWithPatternEvaluationFailed(string pattern)
    {
        // ST-7 / ST-8: never "binding silently not applied" (the profile's uncovered policy is Skip here).
        var outcome = await ResolveAsync(Resolver(pattern));

        outcome.IsDenied.ShouldBeTrue();
        outcome.DenyReason.ShouldNotBeNull().ShouldStartWith("VF_PATTERN_EVALUATION_FAILED");
    }

    [Fact]
    public async Task Resolve_OverlongStoredRegex_DeniesWithPatternEvaluationFailed()
    {
        var outcome = await ResolveAsync(Resolver("regex:^" + new string('a', 300) + "$"));

        outcome.IsDenied.ShouldBeTrue();
        outcome.DenyReason.ShouldNotBeNull().ShouldStartWith("VF_PATTERN_EVALUATION_FAILED");
    }

    [Theory]
    [InlineData("regex:^crm_api\\.public\\.customers$")]
    [InlineData("^CRM_API\\.public\\.cust.*$")]
    [InlineData("crm_api.public.customers")]
    public async Task Resolve_SourceQualifiedPatternOnHttpTable_Applies(string pattern)
    {
        // ST-6 and the glob non-regression: the binding applies to crm_api.public.customers.
        var outcome = await ResolveAsync(Resolver(pattern));

        outcome.IsDenied.ShouldBeFalse();
        outcome.PredicateSql.ShouldBe("P_filter_a(id)");
    }

    [Theory]
    [InlineData("regex:^public\\.customers$")]       // schema.table only: the source is part of the name
    [InlineData("regex:^crm_api\\.public\\.cust")]   // full match: a prefix does not match
    public async Task Resolve_PatternThatIsNotAFullSourceQualifiedMatch_DoesNotApply(string pattern)
    {
        var outcome = await ResolveAsync(Resolver(pattern));

        outcome.ShouldBe(MandatoryFilterOutcome.None);
    }
}
