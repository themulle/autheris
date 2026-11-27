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
    public void Guide_ListsEnvelopeAndEveryActionableCode()
    {
        var guide = McpQueryGuideBuilder.Build(SwitchesFrom(), new GraphQLOptions());

        guide.ShouldContain("## Tool Errors");
        guide.ShouldContain("structuredContent");
        foreach (var (code, _) in McpErrorCodes.AgentActionable)
        {
            guide.ShouldContain("`" + code + "`");
        }
    }

    [Fact]
    public void Build_QuerySqlEnabled_DocumentsOptionalFilterAndTimestampForms()
    {
        var guide = McpQueryGuideBuilder.Build(SwitchesFrom(), new GraphQLOptions());

        guide.ShouldContain("(@p IS NULL OR col = @p)");
        guide.ShouldContain("converted to UTC");
    }

    [Fact]
    public void Build_Paging_DescribesTotalCountAsFilteredTotalWithCost()
    {
        var guide = McpQueryGuideBuilder.Build(SwitchesFrom(), new GraphQLOptions());

        guide.ShouldContain("Rows matching `where` under your row rules");
        guide.ShouldContain("adds to the query cost");
        guide.ShouldContain("`null` when the count could not be determined");
    }

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
    public void Issue1107_Section5_NamesTheTimeBucketLimitNextToTheRowBudget()
    {
        var guide = McpQueryGuideBuilder.Build(SwitchesFrom(), new GraphQLOptions());

        var section5 = guide[guide.IndexOf("## 5.", StringComparison.Ordinal)..];
        section5 = section5[..section5.IndexOf("```graphql", StringComparison.Ordinal)];
        section5.ShouldContain("MaxAggregateRowBudget");
        section5.ShouldContain("`limit` must not exceed `1000`");
        section5.ShouldContain("AGGREGATION_TIME_BUCKET_LIMIT_EXCEEDED");
    }

    [Fact]
    public void Issue1106_Guide_ExplainsRestrictedScopeAndScopeClearRequiredColumns()
    {
        var guide = McpQueryGuideBuilder.Build(SwitchesFrom(), new GraphQLOptions());

        guide.ShouldContain("restrictedScope");
        guide.ShouldContain("scopeClearRequiredColumns");
        guide.ShouldContain("describe_my_access");
        guide.ShouldContain("masked only for the restricted scope of this token");
        guide.ShouldContain("no access request is needed");
        // #1105: keys over masked columns are listed for planning, but masked columns stay out of where, orderBy and joins.
        guide.ShouldContain("FILTER_ON_MASKED_COLUMN");
    }

    [Fact]
    public void Issue1192_Guide_ExplainsQuerySqlParameters_OnlyWhenToolEnabled()
    {
        var on = McpQueryGuideBuilder.Build(SwitchesFrom(), new GraphQLOptions());
        var off = McpQueryGuideBuilder.Build(SwitchesFrom(new GatewayOptions { Mcp = new McpOptions { DisabledTools = ["query_sql"] } }), new GraphQLOptions());

        on.ShouldContain("`parameters`");
        on.ShouldContain("@name");
        on.ShouldContain("IN (@ids)");
        on.ShouldContain("SQL_LITERAL_NOT_PERMITTED");
        on.ShouldContain("same `parameters`");
        off.ShouldNotContain("`parameters`");
        off.ShouldNotContain("SQL_LITERAL_NOT_PERMITTED");
    }

    [Fact]
    public void Issue1106_Guide_DoesNotNameDescribeMyAccess_WhenTheToolIsDisabled()
    {
        var off = McpQueryGuideBuilder.Build(SwitchesFrom(new GatewayOptions { Mcp = new McpOptions { DisabledTools = ["describe_my_access"] } }), new GraphQLOptions());
        var nullSwitches = McpQueryGuideBuilder.Build(switches: null, new GraphQLOptions());

        off.ShouldNotContain("describe_my_access");
        nullSwitches.ShouldNotContain("describe_my_access");
    }

    [Fact]
    public void Issue1106_DescribeMyAccessToolDescription_ExplainsRestrictedScopeFields()
    {
        var tool = McpDatasetTools.Definitions().Single(t => t.Name == McpDatasetTools.DescribeMyAccess);

        tool.Description.ShouldContain("restrictedScope");
        tool.Description.ShouldContain("scopeClearRequiredColumns");
        tool.Description.ShouldContain("masked only for the restricted scope of this token");
        tool.Description.ShouldContain("no access request is needed");
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
            .ShouldContain(McpResourceProvider.QueryGuideDocUri);

        var queryOff = SwitchesFrom(new GatewayOptions
        {
            Mcp = new McpOptions { Tools = new McpToolGroupOptions { Query = false } }
        });
        GatewayMcpServer.FilterResources(native, queryOff).Select(r => r.Uri)
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

    [Fact]
    public void Guide_DefaultConfig_TimeBucketExamplesShowUtc()
    {
        var guide = McpQueryGuideBuilder.Build(SwitchesFrom(), new GraphQLOptions());

        guide.ShouldContain("timeZone: \"UTC\"");
        guide.ShouldContain("`timeZone` defaults to `\"UTC\"` when omitted.");
    }

    [Fact]
    public void Guide_BerlinDefault_BothExamplesShowBerlin_NoUtcLiteral()
    {
        var guide = McpQueryGuideBuilder.Build(SwitchesFrom(), new GraphQLOptions(), new AggregationOptions { DefaultTimeZone = "Europe/Berlin" });

        guide.Split("timeZone: \"Europe/Berlin\"").Length.ShouldBeGreaterThanOrEqualTo(3);
        guide.ShouldNotContain("timeZone: \"UTC\"");
        guide.ShouldContain("`timeZone` defaults to `\"Europe/Berlin\"` when omitted.");
    }

    [Fact]
    public async Task Dispatcher_GuideUsesAnalyticsDefaultTimeZone()
    {
        var analytics = new AnalyticsOptions();
        analytics.Aggregation.DefaultTimeZone = "America/New_York";
        var dispatcher = new ApiDispatcherService(
            gatewayOptions: Options.Create(new GatewayOptions()),
            switches: SwitchesFrom(),
            analyticsOptions: Options.Create(analytics));

        var guide = await dispatcher.GetQueryGuideDocumentationAsync();

        guide.ShouldContain("timeZone: \"America/New_York\"");
        guide.ShouldNotContain("timeZone: \"UTC\"");
    }
}
