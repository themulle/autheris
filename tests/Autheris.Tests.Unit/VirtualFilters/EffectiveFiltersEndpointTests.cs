namespace Autheris.Tests.Unit.VirtualFilters;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 6: effective-filters explains for a user and an object which bindings apply, which columns
/// are missing, what supersedes what, and the resulting SQL (the same text the access decision uses).
/// </summary>
public sealed class EffectiveFiltersEndpointTests
{
    private static readonly TableMetadata Air1 = MandatoryRowFilterResolverTests.Table("fms", "air1", "id", "client_id");
    private static readonly TableMetadata Dm1 = MandatoryRowFilterResolverTests.Table("dm", "dm1", "id");

    private static readonly VirtualFilterSnapshot Snapshot = new(7,
        [MandatoryRowFilterResolverTests.Filter("filter_a", "client_id"), MandatoryRowFilterResolverTests.Filter("filter_b", "client_id", "ts")],
        [MandatoryRowFilterResolverTests.Profile(UncoveredPolicy.Deny, new FilterBinding { FilterName = "filter_a" }, new FilterBinding { FilterName = "filter_b" })]);

    private static MandatoryRowFilterResolver Resolver() =>
        new(new MandatoryRowFilterResolverTests.FixedSnapshot(Snapshot), new MandatoryRowFilterResolverTests.PlaceholderPredicates());

    private static ITableMetadataRepository Catalog()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Air1.Identifier, Arg.Any<CancellationToken>()).Returns(Air1);
        repo.GetTableMetadataAsync(Dm1.Identifier, Arg.Any<CancellationToken>()).Returns(Dm1);
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new List<TableMetadata> { Air1, Dm1 });
        return repo;
    }

    private static HttpContext Context(string query, params string[] roles)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-AUDITOR"),
            TenantId = MandatoryRowFilterResolverTests.Tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(roles.Where(r => r is not ("ClusterAdmin" or "GovernanceAdmin"))),
            ClusterRoles = new HashSet<string>(roles.Where(r => r is "ClusterAdmin" or "GovernanceAdmin")),
            AuthenticationScheme = "Test"
        };
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int Status, JsonDocument? Body)> GetAsync(string query, params string[] roles)
    {
        var context = Context(query, roles);
        var result = await VirtualFilterEndpoints.EffectiveFiltersAsync(context, Resolver(), Catalog());
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, text.Length > 0 && context.Response.StatusCode == 200 ? JsonDocument.Parse(text) : null);
    }

    [Theory]
    [InlineData("GovernanceAdmin", 200)]
    [InlineData("SecurityAuditor", 200)]
    [InlineData("FilterAdmin", 200)]
    [InlineData("Consumer", 403)]
    public async Task Access_IsLimitedToGovernanceRoles(string role, int expected)
    {
        (await GetAsync($"?user={MandatoryRowFilterResolverTests.David.Value}&table=lwetem_prod.fms.air1", role)).Status.ShouldBe(expected);
    }

    [Fact]
    public async Task ForOneObject_ExplainsBindingsAndShowsTheSql()
    {
        var (_, body) = await GetAsync($"?user={MandatoryRowFilterResolverTests.David.Value}&table=lwetem_prod.fms.air1", "SecurityAuditor");
        var root = body!.RootElement;

        root.GetProperty("generation").GetInt64().ShouldBe(7);
        root.GetProperty("decision").GetString().ShouldBe("allow");
        root.GetProperty("sql").GetString().ShouldBe("P_filter_a(client_id)");
        root.GetProperty("applied_filters").EnumerateArray().Select(e => e.GetString()).ShouldBe(["filter_a"]);
        var bindings = root.GetProperty("profiles")[0].GetProperty("bindings").EnumerateArray().ToList();
        bindings[0].GetProperty("applies").GetBoolean().ShouldBeTrue();
        bindings[1].GetProperty("applies").GetBoolean().ShouldBeFalse();
        bindings[1].GetProperty("missing_columns").EnumerateArray().Select(e => e.GetString()).ShouldBe(["ts"]);
    }

    [Fact]
    public async Task UncoveredObject_IsReportedAsUnmatchedDeny()
    {
        var (_, body) = await GetAsync($"?user={MandatoryRowFilterResolverTests.David.Value}&table=lwetem_prod.dm.dm1", "SecurityAuditor");

        body!.RootElement.GetProperty("decision").GetString().ShouldBe("unmatched-deny");
    }

    [Fact]
    public async Task WithoutTable_ListsEveryCatalogObject()
    {
        var (_, body) = await GetAsync($"?user={MandatoryRowFilterResolverTests.David.Value}", "SecurityAuditor");

        var items = body!.RootElement.GetProperty("objects").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("table").GetString()).ShouldBe(["lwetem_prod.dm.dm1", "lwetem_prod.fms.air1"], ignoreOrder: true);
        items.Single(i => i.GetProperty("table").GetString() == "lwetem_prod.dm.dm1").GetProperty("decision").GetString().ShouldBe("unmatched-deny");
    }

    [Fact]
    public async Task MissingUser_Returns400()
    {
        (await GetAsync("?table=lwetem_prod.fms.air1", "SecurityAuditor")).Status.ShouldBe(400);
    }

    [Fact]
    public async Task ExplanationAndDecision_UseTheSameSql()
    {
        var resolver = Resolver();
        var query = new MandatoryFilterQuery(MandatoryRowFilterResolverTests.David, new HashSet<Sid>(), new HashSet<string>(), MandatoryRowFilterResolverTests.Tenant, Air1);

        (await resolver.ExplainAsync(query)).Outcome.PredicateSql.ShouldBe((await resolver.ResolveAsync(query)).PredicateSql);
    }
}
