namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// #138 INC-1 P3 (T-CAT138-09, F-C7, SEC-CAT138-04): REST list, MCP <c>list_datasets</c> and GraphQL <c>catalog</c> answer the same set for
/// the same caller, and a table the ReBAC data-path rule denies (<c>can_query</c>) never shows up on any channel, for administrator and
/// non-administrator callers and for every tier (Hidden, Summary, Full).
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogChannelParityMatrixTests
{
    private static TableMetadata Row(string name, DatasetVisibility tier)
    {
        var t = Wp0.Table("sales", name, "Short " + name, ["id", "secret_col"]);
        return t with { Table = t.Table with { Visibility = tier } };
    }

    private static GatewayOptions Opts(bool rebac) => new()
    {
        Tenancy = new TenancyOptions { Mode = nameof(TenancyMode.Multi) },
        Rebac = new RebacOptions { Enabled = rebac }
    };

    private static IRebacEvaluator Evaluator(params TableMetadata[] denied)
    {
        var deniedObjects = denied.Select(t => RebacTableGate.ObjectId(t.Identifier)).ToHashSet();
        var evaluator = Substitute.For<IRebacEvaluator>();
        evaluator.IsEnabled.Returns(true);
        evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => deniedObjects.Contains(ci.Arg<RebacCheckRequest>().Object) ? RebacCheckResult.Denied : RebacCheckResult.Permitted);
        return evaluator.WithBatchFromCheck();
    }

    private static ClaimsPrincipal Caller(bool admin) =>
        admin ? Wp0.User("S-ADMIN", "tenant-a", ["ClusterAdmin"]) : Wp0.User("S-USER", "tenant-a");

    private static async Task<(string[] RestNames, string[] McpNames, string[] GraphQlNames)> ListAsync(
        ClaimsPrincipal user, TableMetadata[] rows, GatewayOptions options, IRebacEvaluator? rebac)
    {
        var tables = Wp0.Tables(rows);
        var consents = Wp0.Consents(rows.Select(r => Wp0.Allow(r.Identifier, "S-USER", Wp0.TenantA, "id")).ToArray());
        var service = CatalogVisibilityTestFactory.Create(tables, consents, options, rebac);
        var mcp = new McpDatasetCatalog(
            tables, consents, Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(options), visibilityService: service);
        var rest = new CatalogDiscoveryService(tables, Substitute.For<IAuditLogRepository>(), Options.Create(options), service);
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = user });

        var restNames = (await rest.ListDatasetsAsync(Wp0.Context(user))).Select(d => d.Table).Order().ToArray();
        var mcpNames = (await mcp.ListDatasetsAsync(user)).Datasets.Select(d => d.Id.Split('.')[^1]).Order().ToArray();
        var gqlNames = (await new Query().GetCatalogAsync(
            null, null, null, null, null, tables, consents, null, null, accessor, Options.Create(options), service))
            .Select(t => t.TableName).Order().ToArray();
        return (restNames, mcpNames, gqlNames);
    }

    [Theory]
    [InlineData(false, DatasetVisibility.Hidden)]
    [InlineData(false, DatasetVisibility.Summary)]
    [InlineData(false, DatasetVisibility.Full)]
    [InlineData(true, DatasetVisibility.Hidden)]
    [InlineData(true, DatasetVisibility.Summary)]
    [InlineData(true, DatasetVisibility.Full)]
    public async Task CatalogNeverShowsWhatDataPathDeniesByRebac_Theory(bool admin, DatasetVisibility tier)
    {
        var allowed = Row("allowed_t", tier);
        var denied = Row("denied_t", tier);
        var rows = new[] { allowed, denied };
        var user = Caller(admin);

        var (rest, mcp, graphQl) = await ListAsync(user, rows, Opts(rebac: true), Evaluator(denied));

        // One rule: all channels agree, and the ReBAC-denied table is on none of them.
        mcp.ShouldBe(rest);
        graphQl.ShouldBe(rest);
        rest.ShouldNotContain("denied_t");

        // ReBAC only shrinks: without it the same channels still agree, and the allowed table keeps its answer.
        var (restOff, mcpOff, graphQlOff) = await ListAsync(user, rows, Opts(rebac: false), null);
        mcpOff.ShouldBe(restOff);
        graphQlOff.ShouldBe(restOff);
        rest.ShouldBe(restOff.Where(n => n != "denied_t").ToArray());
    }

    [Fact]
    public async Task ReBacEnabledWithoutTuples_ConsentedCaller_SeesNothing_OnAllThreeChannels()
    {
        var a = Row("a_t", DatasetVisibility.Full);
        var b = Row("b_t", DatasetVisibility.Full);

        var (rest, mcp, graphQl) = await ListAsync(Caller(admin: false), [a, b], Opts(rebac: true), Evaluator(a, b));

        rest.ShouldBeEmpty();
        mcp.ShouldBeEmpty();
        graphQl.ShouldBeEmpty();
    }
}
