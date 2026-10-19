namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #240: Read paths added in v1.2.x without an off switch:
/// Gating queryMetrics (Gateway:GraphQL:Metrics:Enabled) and
/// POST /api/v1/governance/queries/analyze (Gateway:Governance:QueryAnalysis:Enabled).
/// </summary>
public sealed class ReadPathSwitchesMetricsAndAnalyzeTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ITableRelationRepository _relationRepo = Substitute.For<ITableRelationRepository>();

    public ReadPathSwitchesMetricsAndAnalyzeTests()
    {
        var sampleTableId = new TableIdentifier("sales", "dbo", "metrics_table");
        var sampleTable = new TableMetadata
        {
            Identifier = sampleTableId,
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "val", DataType = "int" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([sampleTable]);
        _relationRepo.GetRelationsForTableAsync(sampleTableId, Arg.Any<CancellationToken>())
            .Returns([]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ReadPathSwitches_DefaultOptions_HasQueryAnalysisEnabledTrue()
    {
        var switches = ReadPathSwitches.From(new GatewayOptions());
        switches.QueryAnalysisEnabled.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ReadPathSwitches_DisabledQueryAnalysis_HasQueryAnalysisEnabledFalse()
    {
        var options = new GatewayOptions
        {
            Governance = new GovernanceOptions
            {
                QueryAnalysis = new GovernanceQueryAnalysisOptions
                {
                    Enabled = false
                }
            }
        };

        var switches = ReadPathSwitches.From(options);
        switches.QueryAnalysisEnabled.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ReadPathSwitches_ConfigurationBinding_BindsBothSections()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GatewaySections.QueryAnalysis + ":Enabled"] = "false",
                [GatewaySections.GraphQLMetrics + ":Enabled"] = "false"
            })
            .Build();

        var gatewayOptions = new GatewayOptions();
        config.GetSection(GatewaySections.Gateway).Bind(gatewayOptions);

        gatewayOptions.Governance.QueryAnalysis.Enabled.ShouldBeFalse();
        gatewayOptions.GraphQL.Metrics.Enabled.ShouldBeFalse();

        var switches = ReadPathSwitches.From(gatewayOptions);
        switches.QueryAnalysisEnabled.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GraphQL_MetricsEnabled_True_ExposesQueryMetricsField()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_metadataRepo);
        services.AddSingleton(_relationRepo);
        services.AddSingleton(Substitute.For<IGovernedTreeQueryService>());

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "TestAuth"));
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton(Options.Create(new GatewayOptions()));

        var executor = await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<ITableRelationRepository>(),
                metricsEnabled: true))
            .BuildRequestExecutorAsync();

        var schema = executor.Schema;
        schema.QueryType.Fields.ContainsName("queryMetrics").ShouldBeTrue();
        schema.Types.Any(t => t.Name == AggregateTypeNames.MetricFilter).ShouldBeTrue();
        schema.Types.Any(t => t.Name == AggregateTypeNames.MetricQueryResult).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GraphQL_MetricsEnabled_False_CompletelyOmitsQueryMetricsField()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_metadataRepo);
        services.AddSingleton(_relationRepo);
        services.AddSingleton(Substitute.For<IGovernedTreeQueryService>());

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "TestAuth"));
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton(Options.Create(new GatewayOptions()));

        var executor = await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<ITableRelationRepository>(),
                metricsEnabled: false))
            .BuildRequestExecutorAsync();

        var schema = executor.Schema;
        schema.QueryType.Fields.ContainsName("queryMetrics").ShouldBeFalse();
        schema.Types.Any(t => t.Name == AggregateTypeNames.MetricFilter).ShouldBeFalse();
        schema.Types.Any(t => t.Name == AggregateTypeNames.MetricQueryResult).ShouldBeFalse();

        // Executing queryMetrics must fail schema validation (field does not exist)
        var result = (OperationResult)await executor.ExecuteAsync("{ queryMetrics(metrics: [\"c\"]) { count } }");
        result.Errors.ShouldNotBeNull();
        result.Errors.Count.ShouldBeGreaterThan(0);
        result.Errors.Any(e => e.Message.Contains("queryMetrics") || e.Code != null).ShouldBeTrue();
    }

    private static WebApplication CreateEndpointApp(bool mapSampleRows, bool mapQueryAnalysis)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Substitute.For<IQueryAnalysisService>());
        builder.Services.AddSingleton(Substitute.For<IBundledConsentRequestService>());
        builder.Services.AddSingleton(Substitute.For<IMcpDatasetCatalog>());
        var app = builder.Build();
        app.MapQueryAnalysisAndProfilingEndpoints(mapSampleRows, mapQueryAnalysis);
        return app;
    }

    [Fact]
    [Trait("Category", "Security")]
    public void EndpointRouting_QueryAnalysisDisabled_AnalyzeRouteIsNotMapped()
    {
        var app = CreateEndpointApp(mapSampleRows: true, mapQueryAnalysis: false);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        endpoints.ShouldNotContain("/api/v1/governance/queries/analyze");
        endpoints.ShouldContain("/api/v1/governance/consents/bundle");
        endpoints.ShouldContain("/api/v1/catalog/datasets/{dataset}/sample-rows");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void EndpointRouting_QueryAnalysisEnabled_AnalyzeRouteIsMapped()
    {
        var app = CreateEndpointApp(mapSampleRows: false, mapQueryAnalysis: true);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        endpoints.ShouldContain("/api/v1/governance/queries/analyze");
        endpoints.ShouldContain("/api/v1/governance/consents/bundle");
        endpoints.ShouldNotContain("/api/v1/catalog/datasets/{dataset}/sample-rows");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ReadPathSwitches_SecurityContract_NoOptionsMonitorOrRequestScopedTypes()
    {
        var forbidden = new HashSet<Type>
        {
            typeof(HttpContext),
            typeof(ClaimsPrincipal),
            typeof(Autheris.Domain.Model.RequestContext)
        };

        foreach (var t in new[] { typeof(IReadPathSwitches), typeof(ReadPathSwitches) })
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                m.GetParameters().Where(p => forbidden.Contains(p.ParameterType)).ShouldBeEmpty($"{t.Name}.{m.Name}");
            }
        }

        foreach (var c in typeof(ReadPathSwitches).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            c.GetParameters().Select(p => p.ParameterType.Name).ShouldNotContain(n => n.StartsWith("IOptionsMonitor", StringComparison.Ordinal));
        }
    }
}
