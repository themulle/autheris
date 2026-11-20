using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Hosting;
using Autheris.Api.Middleware;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using Autheris.Modules.Events;
using Autheris.Modules.Lakehouse;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Runtime architecture ratchet for SEC-OI-489-4: enumerates all GET/HEAD <see cref="RouteEndpoint"/>
/// instances registered in the real <see cref="EndpointDataSource"/> (GraphQL GET, /dev, export, SCIM,
/// well-known/callback, OData, Arrow, DuckDB OLAP, and module routes), pins that every route is on the
/// checked-in allowlist where each entry documents "no side effect", pins that proxy method override headers
/// can only restrict, and verifies GraphQL GET mutations are rejected for read-only tokens.
/// </summary>
[Collection("HostStartupSequential")]
public sealed class ReadOnlyTokenGetRouteTests
{
    private const int MinimumEndpoints = 90;

    private static readonly string[] SideEffectingExemptions =
    [
        "/api/dev/login/{persona}|GET"
    ];

    private static string NormalizePattern(RouteEndpoint endpoint)
    {
        var raw = endpoint.RoutePattern.RawText ?? "/";
        return raw.StartsWith('/') ? raw : "/" + raw;
    }

    private static string FormatMethods(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        if (methods is null || methods.Count == 0)
        {
            return "ANY";
        }

        return string.Join(',', methods.OrderBy(m => m, StringComparer.OrdinalIgnoreCase));
    }

    private static bool AcceptsGetOrHead(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        if (methods is null || methods.Count == 0)
        {
            return true;
        }

        return methods.Any(m =>
            string.Equals(m, HttpMethods.Get, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m, HttpMethods.Head, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> EnumerateRuntimeGetHeadRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = RepositoryScan.RepoRoot
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = "Host=localhost;Port=5432;Database=autheris_arch;Username=autheris;Password=test",
            ["Gateway:OData:Enabled"] = "true",
            ["Gateway:Arrow:Enabled"] = "true",
            ["Gateway:DuckDbOlap:Enabled"] = "true",
            ["Gateway:Mcp:Enabled"] = "true",
            ["Gateway:GraphQL:EnableBananaCakePop"] = "true",
            ["Gateway:GraphQL:PersistedQueriesOnly"] = "true",
            ["Gateway:GraphQL:AppScopedTrustedDocuments:Enabled"] = "true",
            ["Gateway:SqlEndpoints:Enabled"] = "false",
            ["Gateway:SqlEndpoints:Procedures:Enabled"] = "true",
            ["Gateway:DataSources:Connections:procedures:Provider"] = "PostgreSql",
            ["Gateway:DataSources:Connections:procedures:ConnectionString"] = "Host=localhost;Port=5432;Database=autheris_proc;Username=autheris;Password=test",
            ["Gateway:DataSources:Connections:procedures:DefaultSchema"] = "public",
            ["Gateway:Plugins:Directory"] = string.Empty
        });

        var devReport = DevConfiguration.Apply(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(devReport);

        builder.Services.AddGatewayTelemetry(builder.Configuration);
        builder.Services.AddDataSourceHierarchicalConfiguration(builder.Configuration);
        var gatewayOptions = builder.Services.AddGatewayOptions(builder.Configuration, builder.Environment);
        builder.Services.AddGatewayInfrastructure(gatewayOptions, builder.Environment);
        builder.Services.AddGatewayAuth(gatewayOptions, builder.Environment);
        var demoDataEnabled = DemoDataSwitch.Resolve(gatewayOptions, builder.Environment.EnvironmentName);
        builder.Services.AddSingleton<IDemoDataSwitch>(new DemoDataSwitch(demoDataEnabled));
        builder.Services.AddGatewayGraphQL(gatewayOptions);
        builder.Services.AddAsyncJobsAndDatasourceTesting(builder.Configuration);
        using var bootstrapLoggerFactory = LoggerFactory.Create(_ => { });
        builder.Services.AddGatewayModules(builder.Configuration, builder.Environment, gatewayOptions, bootstrapLoggerFactory);

        // The events module services are registered so the module handlers bind their services (the module itself is mapped below).
        builder.Services.AddEventsModule(builder.Configuration);

        using var app = builder.Build();
        app.UseGatewayPipeline(gatewayOptions);
        app.MapGatewayEndpoints(gatewayOptions);
        new LakehouseModule().MapEndpoints(new ModuleEndpointRegistrar(app, "lakehouse"));
        new Autheris.Modules.Dbt.DbtModule().MapEndpoints(new ModuleEndpointRegistrar(app, "dbt"));
        new EventsModule().MapEndpoints(new ModuleEndpointRegistrar(app, "events"));

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(AcceptsGetOrHead)
            .Select(ep => $"{NormalizePattern(ep)}|{FormatMethods(ep)}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Every_get_head_route_is_in_side_effect_free_allowlist()
    {
        var actual = EnumerateRuntimeGetHeadRoutes();
        actual.Count.ShouldBeGreaterThanOrEqualTo(MinimumEndpoints, "Runtime EndpointDataSource returned too few GET/HEAD routes.");

        var rawLines = RepositoryScan.ReadBaseline("readonly-get-head-endpoints.txt");
        var allowlist = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in rawLines)
        {
            var parts = line.Split('|', 3);
            parts.Length.ShouldBe(3, $"Malformed allowlist line (expected '<route>|<methods>|no side effect: ...'): {line}");
            parts[2].Contains("no side effect", StringComparison.OrdinalIgnoreCase)
                .ShouldBeTrue($"Allowlist entry must explicitly document 'no side effect': {line}");
            allowlist.Add($"{parts[0]}|{parts[1]}").ShouldBeTrue($"Duplicate allowlist entry: {line}");
        }

        SideEffectingExemptions.Length.ShouldBe(1, "The side-effecting GET exemption set must not grow.");
        actual.ShouldContain(SideEffectingExemptions[0], "The Development-only exempted route no longer exists; update ratchet.");
        allowlist.ShouldNotContain(SideEffectingExemptions[0]);

        var unreviewed = actual.Where(r => !allowlist.Contains(r) && !SideEffectingExemptions.Contains(r)).ToList();
        var stale = allowlist.Where(r => !actual.Contains(r)).ToList();

        unreviewed.ShouldBeEmpty(
            "Runtime GET/HEAD routes missing from Baselines/readonly-get-head-endpoints.txt (confirm no side effect or change method): " +
            string.Join(", ", unreviewed));
        stale.ShouldBeEmpty(
            "Baselines/readonly-get-head-endpoints.txt lists GET/HEAD routes that are no longer mapped: " +
            string.Join(", ", stale));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("GET", "/api/v1/catalog/tables", "X-Original-Method", "POST", false)]
    [InlineData("HEAD", "/api/v1/catalog/tables", "X-Forwarded-Method", "DELETE", false)]
    [InlineData("POST", "/api/governance/sunsetting/rules", "X-Original-Method", "GET", false)]
    [InlineData("POST", "/api/governance/sunsetting/rules", "X-Forwarded-Method", "HEAD", false)]
    [InlineData("GET", "/api/v1/catalog/tables", "X-Original-Method", "GET", true)]
    public async Task ReadOnlyTokenMiddleware_OriginalAndForwardedMethodHeaders_CanOnlyRestrict(
        string httpMethod,
        string path,
        string overrideHeader,
        string overrideMethod,
        bool expectedAllowed)
    {
        var identity = new ClaimsIdentity([new Claim("oid", "33333333-3333-3333-3333-333333333333")], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        var principal = new ClaimsPrincipal(identity);

        bool nextCalled = false;
        var middleware = new ReadOnlyTokenMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, "/graphql", "/mcp");

        var context = new DefaultHttpContext { User = principal };
        context.Request.Method = httpMethod;
        context.Request.Path = path;
        context.Request.Headers[overrideHeader] = overrideMethod;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBe(expectedAllowed);
        if (!expectedAllowed)
        {
            context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GraphQl_get_mutation_is_rejected_for_read_only_token()
    {
        TestMutation.Calls = 0;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Services
            .AddGraphQLServer()
            .AddQueryType<TestQuery>()
            .AddMutationType<TestMutation>()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseRequest<ReadOnlyOperationMiddleware>()
            .UseOperationCompiler()
            .UseOperationVariableCoercion()
            .UseOperationExecution();

        using var app = builder.Build();
        app.MapGraphQL("/graphql");

        var gqlEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
        var gqlEndpoint = gqlEndpoints.FirstOrDefault(e =>
                string.Equals(NormalizePattern(e), "/graphql/{**slug}", StringComparison.Ordinal))
            ?? gqlEndpoints.First(e => NormalizePattern(e).StartsWith("/graphql", StringComparison.Ordinal));
        var requestDelegate = gqlEndpoint.RequestDelegate!;
        var identity = new ClaimsIdentity([new Claim("oid", "44444444-4444-4444-4444-444444444444")], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);

        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(identity)
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/graphql";
        context.Request.QueryString = new QueryString("?query=mutation%20%7B%20touch%20%7D");
        context.Request.Headers["GraphQL-Preflight"] = "1";
        context.Response.Body = new MemoryStream();

        await requestDelegate(context);

        context.Response.StatusCode.ShouldNotBe(StatusCodes.Status200OK);
        TestMutation.Calls.ShouldBe(0);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        body.ShouldNotContain("\"touch\":true");
    }

    public sealed class TestQuery
    {
        public string Ping() => "pong";
    }

    public sealed class TestMutation
    {
        public static int Calls { get; set; }

        public bool Touch()
        {
            Calls++;
            return true;
        }
    }
}
