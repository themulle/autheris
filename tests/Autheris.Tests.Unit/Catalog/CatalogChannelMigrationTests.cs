namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// #138 INC-1 P2: REST and MCP discovery run through one <see cref="ICatalogVisibilityService"/> (SEC-CAT138-03, -06, -07, -09, -13, -14,
/// -15, -16; T-CAT138-06..08, -10..12).
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogChannelMigrationTests
{
    private static readonly TableMetadata Ok = Wp0.Table("sales", "ok_t", "Visible", ["id", "amount"]);
    private static readonly TableMetadata Inactive = Wp0.Table("sales", "inactive_t", "Inactive", ["id"], active: false);
    private static readonly TableMetadata Foreign = Wp0.Table("sales", "foreign_t", "Other tenant", ["id"]);
    private static readonly TableMetadata Denied = Wp0.Table("sales", "denied_t", "ReBAC denied", ["id"]);

    private static GatewayOptions Opts(bool rebac, TenancyMode mode = TenancyMode.Multi) => new()
    {
        Tenancy = new TenancyOptions { Mode = mode.ToString() },
        Rebac = new RebacOptions { Enabled = rebac }
    };

    /// <summary>ReBAC double: enabled, answers <c>can_query</c> per object; every subject it was asked for is recorded.</summary>
    private sealed class RebacDouble
    {
        public RebacDouble(params TableMetadata[] deniedTables)
        {
            var deniedObjects = deniedTables.Select(t => RebacTableGate.ObjectId(t.Identifier)).ToHashSet();
            Evaluator = Substitute.For<IRebacEvaluator>();
            Evaluator.IsEnabled.Returns(true);
            Evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
            {
                var request = ci.Arg<RebacCheckRequest>();
                Subjects.Add(request.User);
                return deniedObjects.Contains(request.Object) ? RebacCheckResult.Denied : RebacCheckResult.Permitted;
            });
            Evaluator.WithBatchFromCheck();
        }

        public IRebacEvaluator Evaluator { get; }

        public List<string> Subjects { get; } = [];
    }

    private static IConsentRepository ConsentsFor(string sid, params TableMetadata[] tables) =>
        Wp0.Consents(tables.Select(t => Wp0.Allow(t.Identifier, sid, Wp0.TenantA, "id")).ToArray());

    private static (McpDatasetCatalog Mcp, CatalogDiscoveryService RestService, ICatalogVisibilityService Service) Channels(
        ITableMetadataRepository tables, IConsentRepository consents, GatewayOptions options, IRebacEvaluator? rebac = null,
        IGraphQlCatalogMap? graphQl = null, ICatalogVisibilityService? serviceOverride = null)
    {
        var service = serviceOverride ?? CatalogVisibilityTestFactory.Create(tables, consents, options, rebac);
        var mcp = new McpDatasetCatalog(
            tables, consents, Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(options), graphQlMap: graphQl, visibilityService: service);
        var rest = new CatalogDiscoveryService(
            tables, Substitute.For<IAuditLogRepository>(), consents, Options.Create(options), service);
        return (mcp, rest, service);
    }

    private static string Normalise(string message, string name) => message.Replace(name, "X", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------------------------------------------
    // T-CAT138-06 / SEC-CAT138-09
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FindVisible_InactiveWrongTenantUnknownRebacDenied_IdenticalErrorAndMessage_T_CAT138_06()
    {
        var tables = Wp0.Tables(Ok, Inactive, Foreign, Denied);
        var consents = Wp0.Consents(
            Wp0.Allow(Ok.Identifier, "S-USER", Wp0.TenantA, "id"),
            Wp0.Allow(Inactive.Identifier, "S-USER", Wp0.TenantA, "id"),
            Wp0.Allow(Denied.Identifier, "S-USER", Wp0.TenantA, "id"),
            Wp0.Allow(Foreign.Identifier, "S-USER", Wp0.TenantB, "id"));
        var rebac = new RebacDouble(Denied);
        var (mcp, rest, _) = Channels(tables, consents, Opts(rebac: true), rebac.Evaluator);
        var user = Wp0.User("S-USER", "tenant-a");
        var context = Wp0.Context(user);

        var hidden = new[] { "inactive_t", "foreign_t", "denied_t", "unknown_t" };
        var describe = new List<string>();
        var sample = new List<string>();
        foreach (var name in hidden)
        {
            var describeEx = await Should.ThrowAsync<TableNotFoundException>(() => mcp.DescribeDatasetAsync(user, $"sales.dbo.{name}"));
            var sampleEx = await Should.ThrowAsync<TableNotFoundException>(() => mcp.SampleRowsAsync(user, $"sales.dbo.{name}", 3));
            describe.Add(Normalise(describeEx.Message, name));
            sample.Add(Normalise(sampleEx.Message, name));
            (await rest.GetDatasetDetailAsync(new TableIdentifier("sales", "dbo", name), context)).ShouldBeNull();
        }

        describe.Distinct().Count().ShouldBe(1);
        sample.Distinct().Count().ShouldBe(1);
        describe[0].ShouldBe(sample[0]);
        describe[0].ShouldNotContain("rebac", Case.Insensitive);
        (await rest.GetDatasetDetailAsync(Ok.Identifier, context)).ShouldNotBeNull();
        (await mcp.DescribeDatasetAsync(user, "sales.dbo.ok_t")).Id.ShouldBe("sales.dbo.ok_t");
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // T-CAT138-07 / SEC-CAT138-08
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Describe_RelationTargetDenied_NoNameInRelationsAccessPathsLineageOrError_T_CAT138_07()
    {
        var tables = Wp0.Tables(Ok, Denied);
        var consents = ConsentsFor("S-USER", Ok, Denied);
        var rebac = new RebacDouble(Denied);
        var graphQl = Substitute.For<IGraphQlCatalogMap>();
        graphQl.GetTableAsync(Ok.Identifier, Arg.Any<CancellationToken>()).Returns(new GraphQlTableMapping(
            Ok.Identifier, "salesOk", "OkT", "OkTFilter", "OkTOrder", [new GraphQlColumnMapping("id", "id", "Int")],
            [new GraphQlRelationMapping("deniedRel", Denied.Identifier, IsList: true), new GraphQlRelationMapping("self", Ok.Identifier, IsList: false)]));
        var (mcp, _, _) = Channels(tables, consents, Opts(rebac: true), rebac.Evaluator, graphQl);

        var description = await mcp.DescribeDatasetAsync(Wp0.User("S-USER", "tenant-a"), "sales.dbo.ok_t");

        description.GraphQl.ShouldNotBeNull().Relations.Select(r => r.Dataset).ShouldBe(["sales.dbo.ok_t"]);
        JsonSerializer.Serialize(description).ShouldNotContain("denied_t", Case.Insensitive);
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // T-CAT138-08 / SEC-CAT138-03
    // ------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("S-USER", "Bearer")]
    [InlineData("cert:ABCDEF", "Certificate")]
    public async Task RestRebacSubject_EqualsDataPathSubject_ForUserAndServiceToken_T_CAT138_08(string sid, string authType)
    {
        var tables = Wp0.Tables(Ok);
        var consents = ConsentsFor(sid, Ok);
        var rebac = new RebacDouble();
        var (_, rest, _) = Channels(tables, consents, Opts(rebac: true), rebac.Evaluator);
        var principal = Wp0.User(sid, "tenant-a", authType: authType);

        (await rest.ListDatasetsAsync(Wp0.Context(principal))).ShouldHaveSingleItem();

        // The data path keys ReBAC on the principal SID (query.UserSid); the catalog must ask for the same subject.
        rebac.Subjects.ShouldNotBeEmpty();
        rebac.Subjects.ShouldAllBe(s => s == principal.GetUserSid()!.Value.Value);
    }

    [Fact]
    public async Task RestSubjectDifferentFromPrincipalSid_StricterOfBoth_Empty_T_CAT138_08()
    {
        var tables = Wp0.Tables(Ok);
        var consents = ConsentsFor("S-USER", Ok);
        var rebac = new RebacDouble();
        var (_, rest, _) = Channels(tables, consents, Opts(rebac: true), rebac.Evaluator);
        var principal = Wp0.User("S-USER", "tenant-a");
        var forged = new RequestContext(Wp0.TenantA, new Sid("S-OTHER"), User: principal);

        (await rest.ListDatasetsAsync(forged)).ShouldBeEmpty();
        (await rest.GetDatasetDetailAsync(Ok.Identifier, forged)).ShouldBeNull();
        rebac.Subjects.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // T-CAT138-10 / SEC-CAT138-15 (Summary: no columns, no samples)
    // ------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryRow_NoColumnsNoSamples_ThroughServiceRestAndMcp_RebacOnAndOff_T_CAT138_10(bool rebacOn)
    {
        var plain = Wp0.Table("sales", "sum_t", "Summary", ["id", "secret"]);
        var summary = plain with { Table = plain.Table with { Visibility = DatasetVisibility.Summary } };
        var tables = Wp0.Tables(summary);
        var rebac = new RebacDouble();
        var options = Opts(rebacOn);
        var (mcp, rest, service) = Channels(tables, Wp0.Consents(), options, rebacOn ? rebac.Evaluator : null);
        var user = Wp0.User("S-USER", "tenant-a");

        // The engine only produces Summary rows for shared tables on the discovery surface.
        var shared = new GatewayOptions { Tenancy = options.Tenancy, Rebac = options.Rebac, Catalog = new DataCatalogOptions { SharedTables = ["sales.dbo.sum_t"] } };
        (mcp, rest, service) = Channels(tables, Wp0.Consents(), shared, rebacOn ? rebac.Evaluator : null);

        var detail = await rest.GetDatasetDetailAsync(summary.Identifier, Wp0.Context(user));
        detail.ShouldNotBeNull().Columns.ShouldBeEmpty();
        var description = await mcp.DescribeDatasetAsync(user, "sales.dbo.sum_t");
        description.Columns.ShouldBeEmpty();
        description.View.ShouldBe(McpDatasetCatalog.SummaryView);
        (await service.FindVisibleAsync(CatalogVisibilityTestFactory.Caller(user), summary.Identifier))!.Value.Shape.ShouldBe(CatalogOutputShape.Summary);

        await Should.ThrowAsync<GatewayForbiddenException>(() => mcp.SampleRowsAsync(user, "sales.dbo.sum_t", 3));
        await Should.ThrowAsync<GatewayForbiddenException>(() => mcp.SampleRowsAsync(user, "sales.dbo.sum_t", 3, synthetic: true));
    }

    [Fact]
    public async Task SampleRows_SummaryShape_Denied_FullRequired()
    {
        // The service says Summary for a row whose own tier is Full: the channel obeys the service shape, never recomputes it.
        var full = Wp0.Table("sales", "full_t", "Full", ["id"]) with { CatalogView = CatalogView.Full };
        var service = Substitute.For<ICatalogVisibilityService>();
        service.FindVisibleAsync(Arg.Any<CatalogCaller>(), full.Identifier, Arg.Any<CancellationToken>())
            .Returns(new CatalogVisibleRow(full, CatalogOutputShape.Summary));
        var gateway = Substitute.For<IGatewayExecutionService>();
        var mcp = new McpDatasetCatalog(
            Wp0.Tables(full), Wp0.Consents(), gateway, Substitute.For<IAuditLogRepository>(),
            options: Options.Create(Opts(false)), visibilityService: service);

        await Should.ThrowAsync<GatewayForbiddenException>(() => mcp.SampleRowsAsync(Wp0.User("S-USER", "tenant-a"), "sales.dbo.full_t", 3));

        gateway.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Shape_ComesFromServiceResolver_NotRecomputedByChannel()
    {
        var full = Wp0.Table("sales", "full_t", "Full", ["id", "secret"]) with { CatalogView = CatalogView.Full };
        var service = Substitute.For<ICatalogVisibilityService>();
        service.FindVisibleAsync(Arg.Any<CatalogCaller>(), full.Identifier, Arg.Any<CancellationToken>())
            .Returns(new CatalogVisibleRow(full, CatalogOutputShape.Summary));
        var options = Opts(false);
        var mcp = new McpDatasetCatalog(
            Wp0.Tables(full), Wp0.Consents(), Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(options), visibilityService: service);
        var rest = new CatalogDiscoveryService(Wp0.Tables(full), Substitute.For<IAuditLogRepository>(), Wp0.Consents(), Options.Create(options), service);
        var user = Wp0.User("S-USER", "tenant-a");

        (await mcp.DescribeDatasetAsync(user, "sales.dbo.full_t")).Columns.ShouldBeEmpty();
        (await rest.GetDatasetDetailAsync(full.Identifier, Wp0.Context(user)))!.Columns.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // T-CAT138-11
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AdminClass_RebacOn_NoTuples_SeesNothing_RestAndMcp_T_CAT138_11()
    {
        var tables = Wp0.Tables(Ok, Denied);
        var rebac = new RebacDouble(Ok, Denied);
        var (mcp, rest, _) = Channels(tables, Wp0.Consents(), Opts(rebac: true), rebac.Evaluator);
        var admin = Wp0.User("S-ADMIN", null, ["ClusterAdmin"]);

        (await rest.ListDatasetsAsync(Wp0.Context(admin))).ShouldBeEmpty();
        (await rest.GetDatasetDetailAsync(Ok.Identifier, Wp0.Context(admin))).ShouldBeNull();
        (await mcp.ListDatasetsAsync(admin)).Total.ShouldBe(0);
        await Should.ThrowAsync<TableNotFoundException>(() => mcp.DescribeDatasetAsync(admin, "sales.dbo.ok_t"));
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // T-CAT138-12
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Datasources_AdminGateBeforeVisibility_RebacShrinksOnly_T_CAT138_12()
    {
        var a = Wp0.Table("alpha", "t1", "A", ["id"], baseUrl: "https://a.internal/api");
        var b = Wp0.Table("beta", "t2", "B", ["id"], baseUrl: "https://b.internal/api");
        var tables = Wp0.Tables(a, b);
        var spy = Substitute.For<ICatalogVisibilityService>();
        var rebac = new RebacDouble(b);
        var options = Opts(rebac: true);
        var (_, restSpied, _) = Channels(tables, Wp0.Consents(), options, serviceOverride: spy);
        var (_, restReal, _) = Channels(tables, Wp0.Consents(), options, rebac.Evaluator);

        // Gate first: a non-administrator never reaches the visibility service.
        (await restSpied.ListDatasourcesAsync(Wp0.Context(Wp0.User("S-USER", "tenant-a")))).ShouldBeEmpty();
        await spy.DidNotReceiveWithAnyArgs().GetVisibleAsync(default!, default, default);

        // ReBAC only shrinks the administrator answer.
        var sources = await restReal.ListDatasourcesAsync(Wp0.Context(Wp0.User("S-ADMIN", null, ["ClusterAdmin"])));
        sources.Select(s => s.Domain).ShouldBe(["alpha"]);
        sources.ShouldNotContain(s => s.BaseUrl == "https://b.internal/api");
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // SEC-CAT138-14 (REST/MCP part): no SID, nothing visible, nothing read
    // ------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ClusterAdmin")]
    [InlineData("GovernanceAdmin")]
    [InlineData("Subject")]
    public async Task NameOnlyPrincipal_NoSid_Empty_AllChannels(string role)
    {
        var tables = Wp0.Tables(Ok);
        var consents = Wp0.Consents();
        var rebac = new RebacDouble();
        var (mcp, rest, _) = Channels(tables, consents, Opts(rebac: true), rebac.Evaluator);
        var nameOnly = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice"), new Claim(ClaimTypes.Role, role), new Claim("tenant_id", "tenant-a")], "Bearer"));
        tables.ClearReceivedCalls();
        consents.ClearReceivedCalls();

        (await rest.ListDatasetsAsync(new RequestContext(nameOnly, Wp0.TenantA))).ShouldBeEmpty();
        (await rest.GetDatasetDetailAsync(Ok.Identifier, new RequestContext(nameOnly, Wp0.TenantA))).ShouldBeNull();
        (await mcp.ListDatasetsAsync(nameOnly)).Total.ShouldBe(0);
        await Should.ThrowAsync<TableNotFoundException>(() => mcp.DescribeDatasetAsync(nameOnly, "sales.dbo.ok_t"));

        await tables.DidNotReceiveWithAnyArgs().GetAllTablesAsync(default);
        await tables.DidNotReceiveWithAnyArgs().GetTableMetadataAsync(default!, default);
        await consents.DidNotReceiveWithAnyArgs().GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default);
        rebac.Subjects.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Section 5 MCP tests
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MissingVisibilityService_Empty()
    {
        var tables = Wp0.Tables(Ok);
        var consents = ConsentsFor("S-USER", Ok);
        var mcp = new McpDatasetCatalog(
            tables, consents, Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(), options: Options.Create(Opts(false)));
        var rest = new CatalogDiscoveryService(tables, Substitute.For<IAuditLogRepository>(), consents, Options.Create(Opts(false)));
        var user = Wp0.User("S-USER", "tenant-a");

        (await mcp.ListDatasetsAsync(user)).Total.ShouldBe(0);
        await Should.ThrowAsync<TableNotFoundException>(() => mcp.DescribeDatasetAsync(user, "sales.dbo.ok_t"));
        (await rest.ListDatasetsAsync(Wp0.Context(user))).ShouldBeEmpty();
        (await rest.GetDatasetDetailAsync(Ok.Identifier, Wp0.Context(user))).ShouldBeNull();
    }

    [Fact]
    public async Task AuthBypass_Unchanged_WholeCatalog_ServiceNeverCalled_NoWidenedShaping()
    {
        var unmarked = Wp0.Table("sales", "unmarked_t", "U", ["id"]) with { CatalogView = CatalogView.Unmarked };
        var spy = Substitute.For<ICatalogVisibilityService>();
        var options = new GatewayOptions { Tenancy = Opts(false).Tenancy, Rebac = Opts(false).Rebac, Insecure = new InsecureGettingStartedOptions { danger_bypass_mcp_auth = true } };
        var mcp = new McpDatasetCatalog(
            Wp0.Tables(Ok, unmarked), Wp0.Consents(), Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(options), visibilityService: spy);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        (await mcp.ListDatasetsAsync(anonymous)).Total.ShouldBe(2);
        (await mcp.DescribeDatasetAsync(anonymous, "sales.dbo.ok_t")).Id.ShouldBe("sales.dbo.ok_t");
        await spy.DidNotReceiveWithAnyArgs().GetVisibleAsync(default!, default, default);
        await spy.DidNotReceiveWithAnyArgs().FindVisibleAsync(default!, default, default);
    }

    [Fact]
    public async Task List_RebacDeniedTable_AbsentFromPageAndPaging()
    {
        var tables = Wp0.Tables(Ok, Denied);
        var consents = ConsentsFor("S-USER", Ok, Denied);
        var rebac = new RebacDouble(Denied);
        var (mcp, rest, _) = Channels(tables, consents, Opts(rebac: true), rebac.Evaluator);
        var user = Wp0.User("S-USER", "tenant-a");

        var list = await mcp.ListDatasetsAsync(user, limit: 1);

        list.Total.ShouldBe(1);
        list.Datasets.Select(d => d.Id).ShouldBe(["sales.dbo.ok_t"]);
        list.Truncated.ShouldBeFalse();
        (await rest.ListDatasetsAsync(Wp0.Context(user))).Select(d => d.Table).ShouldBe(["ok_t"]);
    }
}
