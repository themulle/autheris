namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Mcp;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class McpQueryGuideTests
{
    private static readonly McpSessionContext Session = new(
        "s-1", "sp-1", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        UserSid: "S-1-5-21-1", Roles: ["Analyst"], IsReadOnly: true);

    private static IReadPathSwitches SwitchesFrom(GatewayOptions? options = null) =>
        ReadPathSwitches.From(options ?? new GatewayOptions());

    [Fact]
    public void Build_DefaultEnabledFeatures_CoversFullGraphQlDialectAndEnabledTools()
    {
        var switches = SwitchesFrom();
        var guide = McpQueryGuideBuilder.Build(switches, new GraphQLOptions());

        // Execution paths & discovery tools
        guide.ShouldContain("POST /graphql");
        guide.ShouldContain("query_graphql");
        guide.ShouldContain("find_relevant_tables");
        guide.ShouldContain("describe_dataset");
        guide.ShouldContain("query_sql");
        guide.ShouldContain("query_dataset");

        // Paging & connections
        guide.ShouldContain("Connection(");
        guide.ShouldContain("first: 100");
        guide.ShouldContain("after: $after");
        guide.ShouldContain("pageInfo { hasNextPage hasPreviousPage startCursor endCursor }");
        guide.ShouldContain("totalCount");

        // Key order
        guide.ShouldContain("orderBy");
        guide.ShouldContain("AutherisSortDirection");
        guide.ShouldContain("ASC");
        guide.ShouldContain("DESC");

        // Filter operators
        foreach (var op in new[] { "eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "isNull", "contains", "startsWith", "endsWith" })
        {
            guide.ShouldContain($"`{op}`");
        }
        guide.ShouldContain("`and:");
        guide.ShouldContain("`or:");
        guide.ShouldContain("`not:");

        // Aggregation & time buckets (enabled by default)
        guide.ShouldContain("_aggregate");
        guide.ShouldContain("AggregateFn");
        foreach (var fn in new[] { "COUNT", "COUNT_DISTINCT", "SUM", "AVG", "MIN", "MAX" })
        {
            guide.ShouldContain(fn);
        }
        guide.ShouldContain("timeBuckets");
        foreach (var bucket in new[] { "HOUR", "DAY", "WEEK", "MONTH", "QUARTER", "YEAR" })
        {
            guide.ShouldContain(bucket);
        }

        // Metrics (enabled by default), subscriptions & procedures (disabled by default)
        guide.ShouldContain("queryMetrics");
        guide.ShouldNotContain("GraphQL Subscriptions");
        guide.ShouldNotContain("call_procedure");
    }

    [Fact]
    public void Build_WhenMetricsDisabledAndSubscriptionsEnabled_ReflectsActiveSwitches()
    {
        var switches = SwitchesFrom();
        var gql = new GraphQLOptions
        {
            Subscriptions = new GraphQLSubscriptionOptions { Enabled = true },
            EnableIntrospection = true,
            PersistedQueriesOnly = true
        };

        var guide = McpQueryGuideBuilder.Build(switches, gql);

        guide.ShouldContain("queryMetrics");
        guide.ShouldContain("GraphQL Subscriptions");
        guide.ShouldContain("Schema Introspection:** Enabled");
        guide.ShouldContain("Persisted Queries Only:** Enabled");
    }

    [Fact]
    public void Build_WhenToolsOrRestDisabled_NeverNamesDisabledTools()
    {
        var switches = SwitchesFrom(new GatewayOptions
        {
            RestApi = new RestApiOptions { DataEndpoint = new RestDataEndpointOptions { Enabled = false } },
            Mcp = new McpOptions
            {
                Tools = new McpToolGroupOptions { Query = false },
                DisabledTools = ["describe_dataset"]
            }
        });

        var guide = McpQueryGuideBuilder.Build(switches, new GraphQLOptions());

        guide.ShouldNotContain("query_graphql");
        guide.ShouldNotContain("query_sql");
        guide.ShouldNotContain("query_dataset");
        guide.ShouldNotContain("describe_dataset");
        guide.ShouldContain("find_relevant_tables");
        guide.ShouldContain("get_table_schema");
    }

    [Fact]
    public void Build_WhenProceduresEnabled_NamesCallProcedure()
    {
        var switches = SwitchesFrom(new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions { Procedures = new ProcedureEndpointsOptions { Enabled = true } }
        });

        var guide = McpQueryGuideBuilder.Build(switches, new GraphQLOptions());

        guide.ShouldContain("call_procedure");
    }

    [Fact]
    public void Build_NullSwitches_FailsClosedAndNamesNoTools()
    {
        var guide = McpQueryGuideBuilder.Build(switches: null, new GraphQLOptions());

        guide.ShouldContain("POST /graphql");
        guide.ShouldNotContain("query_graphql");
        guide.ShouldNotContain("query_sql");
        guide.ShouldNotContain("query_dataset");
        guide.ShouldNotContain("describe_dataset");
        guide.ShouldNotContain("get_table_schema");
        guide.ShouldNotContain("find_relevant_tables");
        guide.ShouldNotContain("search_catalog");
        guide.ShouldNotContain("list_datasets");
        guide.ShouldNotContain("call_procedure");
    }

    [Fact]
    public async Task McpResourceRegistrationAndRead_ServesDynamicQueryGuide()
    {
        var native = GatewayMcpServer.NativeResourceList();
        native.Select(r => r.Uri).ShouldContain(McpResourceProvider.QueryGuideDocUri);

        var switches = SwitchesFrom(new GatewayOptions
        {
            Mcp = new McpOptions { Tools = new McpToolGroupOptions { Api = true } }
        });
        GatewayMcpServer.FilterResources(native, switches).Select(r => r.Uri)
            .ShouldContain(McpResourceProvider.QueryGuideDocUri);

        var apiOff = SwitchesFrom(new GatewayOptions
        {
            Mcp = new McpOptions { Tools = new McpToolGroupOptions { Api = false } }
        });
        GatewayMcpServer.FilterResources(native, apiOff).Select(r => r.Uri)
            .ShouldNotContain(McpResourceProvider.QueryGuideDocUri);

        var dispatcher = new ApiDispatcherService(
            gatewayOptions: Options.Create(new GatewayOptions()),
            switches: switches);
        var provider = new McpResourceProvider(Substitute.For<ICatalogDiscoveryService>(), dispatcher, switches);

        provider.IsNativeResource(McpResourceProvider.QueryGuideDocUri).ShouldBeTrue();
        McpResourceProvider.IsStaticDocumentation(McpResourceProvider.QueryGuideDocUri).ShouldBeTrue();
        McpResourceProvider.GuardTargets(McpResourceProvider.QueryGuideDocUri).ShouldNotBeNull();

        var content = await provider.ReadAsync(McpResourceProvider.QueryGuideDocUri, Session);
        content.Uri.ShouldBe(McpResourceProvider.QueryGuideDocUri);
        content.MimeType.ShouldBe("text/markdown");
        content.Text.ShouldContain("# Autheris Agent Query Guide");
        content.Text.ShouldContain("_aggregate");
        content.Text.ShouldContain("timeBuckets");
    }
}
