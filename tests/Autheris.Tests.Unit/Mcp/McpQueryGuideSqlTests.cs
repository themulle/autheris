namespace Autheris.Tests.Unit.Mcp;

using System;
using System.IO;
using System.Linq;
using Autheris.Api.Errors;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>The WebSQL section of the agent query guide.</summary>
public sealed class McpQueryGuideSqlTests
{
    private const string Heading = "## SQL Queries (WebSQL)";

    private static readonly string[] ReservedPrefixes = ["__gql_", "__param_", "__rls_ks", "p_rls_", "p_pd_", "gw_tf_"];

    private static WebSqlOptions Enabled() => new() { Enabled = true };

    private static string Build(WebSqlOptions? webSql, GatewayOptions? gateway = null) =>
        McpQueryGuideBuilder.Build(ReadPathSwitches.From(gateway ?? new GatewayOptions()), new GraphQLOptions(), webSqlOptions: webSql);

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Autheris.sln not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Issue1201_SqlSection_OmittedWhenWebSqlDisabledOrOptionsNull()
    {
        foreach (var guide in new[] { Build(null), Build(new WebSqlOptions { Enabled = false }) })
        {
            guide.ShouldNotContain(Heading);
            guide.ShouldNotContain("/api/v1/sql");
        }

        McpQueryGuideBuilder.Build(switches: null, new GraphQLOptions(), webSqlOptions: Enabled()).ShouldContain(Heading);
        McpQueryGuideBuilder.Build(switches: null, new GraphQLOptions(), webSqlOptions: new WebSqlOptions { Enabled = false })
            .ShouldNotContain("/api/v1/sql");
    }

    [Fact]
    public void Issue1201_SqlSection_DescribesDialectParametersPagingRefusalsTimeLimitsErrors()
    {
        var guide = Build(new WebSqlOptions
        {
            Enabled = true,
            MaxListParameterValues = 321,
            MaxListParameters = 6,
            MaxAllowedRows = 7777,
            DefaultMaxRows = 555,
            MaxQueryLength = 12345,
            ExecutionTimeoutSeconds = 47,
            Paging = new WebSqlPagingOptions { MaxOffset = 4321, TokenLifetimeMinutes = 9 }
        });

        foreach (var expected in new[]
        {
            "321", "6 lists", "7777", "555", "12345", "47 s", "4321", "9 minutes",
            "IN (@", "`next`", "`pagingUnavailable`", "order_by_required", "statement_has_limit", "offset_limit",
            "FILTER_ON_MASKED_COLUMN", "AGGREGATION_ON_MASKED_COLUMN", "convert_timezone", "application/problem+json",
            "/api/v1/sql/openapi.json", "SQL_PAGING_TOKEN_INVALID", "ORDER BY"
        })
        {
            guide.ShouldContain(expected);
        }
    }

    [Fact]
    public void Issue1201_SqlSection_NamesQuerySqlAndListDatasetsOnlyWhenEnabled()
    {
        var all = Build(Enabled());
        all.ShouldContain("**MCP Tool:** `query_sql`");
        all.ShouldContain("**Table Names:** `list_datasets`");

        var queryOff = Build(Enabled(), new GatewayOptions { Mcp = new McpOptions { DisabledTools = ["query_sql", "list_datasets"] } });
        queryOff.ShouldContain(Heading);
        queryOff.ShouldNotContain("**MCP Tool:**");
        queryOff.ShouldNotContain("**Table Names:**");
        queryOff.ShouldNotContain("query_sql");
        queryOff.ShouldNotContain("list_datasets");
    }

    [Fact]
    public void Issue1201_SqlSection_ContainsSharedExampleVerbatim()
    {
        var guide = Build(Enabled());

        guide.ShouldContain(WebSqlGuideExample.Sql);
        guide.ShouldContain(WebSqlGuideExample.ParametersJson);
        guide.ShouldContain(WebSqlGuideExample.RequestJson);
    }

    [Fact]
    public void Issue1201_SqlSection_DmlSentenceOnlyWhenAllowed()
    {
        Build(Enabled()).ShouldNotContain("`INSERT`");
        Build(new WebSqlOptions { Enabled = true, AllowDml = true }).ShouldContain("`INSERT`, `UPDATE` and `DELETE` are enabled");
    }

    [Fact]
    public void Issue1201_FeatureDoc_ContainsSharedExample()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "features", "f-data-02-governed-websql.md"));

        doc.ShouldContain(WebSqlGuideExample.Sql);
        doc.ShouldContain(WebSqlGuideExample.ParametersJson);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_Sql_GivenWebSqlDisabledOrNullOrNullSwitches_WhenBuild_ThenNoSqlSectionAndNoSqlRoute()
    {
        var guides = new[]
        {
            Build(null),
            Build(new WebSqlOptions { Enabled = false }),
            McpQueryGuideBuilder.Build(switches: null, new GraphQLOptions(), webSqlOptions: null),
            McpQueryGuideBuilder.Build(switches: null, new GraphQLOptions(), webSqlOptions: new WebSqlOptions { Enabled = false })
        };

        foreach (var guide in guides)
        {
            guide.ShouldNotContain(Heading);
            guide.ShouldNotContain("/api/v1/sql");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_Sql_GivenQuerySqlToolOrGroupOrMcpDisabled_WhenBuild_ThenQuerySqlNotNamedAndHttpExampleStillPresent()
    {
        var variants = new[]
        {
            new GatewayOptions { Mcp = new McpOptions { DisabledTools = ["query_sql"] } },
            new GatewayOptions { Mcp = new McpOptions { Tools = new McpToolGroupOptions { Query = false } } }
        };

        foreach (var gateway in variants)
        {
            var guide = Build(Enabled(), gateway);
            guide.ShouldContain(Heading);
            guide.ShouldContain("POST /api/v1/sql");
            guide.ShouldNotContain("query_sql");
        }

        var nullSwitches = McpQueryGuideBuilder.Build(switches: null, new GraphQLOptions(), webSqlOptions: Enabled());
        nullSwitches.ShouldContain("POST /api/v1/sql");
        nullSwitches.ShouldNotContain("query_sql");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_Sql_GivenQuerySqlEnabled_WhenBuild_ThenStatesNoParametersAndExampleIsHttpOnly()
    {
        var guide = Build(Enabled());

        guide.ShouldContain("it takes no `parameters`");
        guide.ShouldContain("### Example: A Stored Statement Run With Values (HTTP)");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_Sql_WhenBuild_ThenNoDataSourceNamesAllowListsReservedPrefixesOrFunctionNames()
    {
        var guide = Build(new WebSqlOptions
        {
            Enabled = true,
            DefaultDataSourceName = "sentinel-default-source",
            AllowedDataSources = ["sentinel-allowed-source"]
        });

        guide.ShouldNotContain("sentinel-default-source");
        guide.ShouldNotContain("sentinel-allowed-source");
        guide.ShouldNotContain("AllowedDataSources");
        guide.ShouldNotContain("DefaultDataSourceName");
        guide.ShouldNotContain("DataSourceMappings");
        foreach (var prefix in ReservedPrefixes)
        {
            guide.ShouldNotContain(prefix);
        }

        guide.ShouldNotContain("rls_");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_Sql_WhenBuild_ThenLimitIsIntegerLiteralOnlyAndNeverOffersLimitParameter()
    {
        var guide = Build(Enabled());

        guide.ShouldContain(McpQueryGuideBuilder.LimitLiteralSentence);
        guide.Replace(McpQueryGuideBuilder.LimitLiteralSentence, string.Empty).ShouldNotContain("LIMIT @");
        guide.ShouldNotContain("LIMIT @");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_Sql_WhenBuild_ThenErrorSectionListsOnlyAllowListedExtensionMembers()
    {
        var guide = Build(Enabled());
        var start = guide.IndexOf("### Errors", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0);
        var errors = guide[start..];
        var end = errors.IndexOf("### Example", StringComparison.Ordinal);
        errors = end > 0 ? errors[..end] : errors;

        // The optional members named in the text must all be members of the problem allow-list.
        foreach (var member in new[] { "line", "column", "limit", "hint", "retryAfterSeconds" })
        {
            errors.ShouldContain($"`{member}`");
            ApiProblems.AllowedExtensionKeys.ShouldContain(member);
        }

        errors.ShouldNotContain("estimatedCost");
        errors.ShouldNotContain("maxAllowedCost");
    }
}
