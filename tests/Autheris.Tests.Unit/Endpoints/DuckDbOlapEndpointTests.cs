namespace Autheris.Tests.Unit.Endpoints;

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Olap;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DuckDbOlapEndpointTests
{
    private sealed class RecordingOlapStagingService : IOlapStagingService
    {
        public int Calls { get; private set; }
        public ClaimsPrincipal? LastUser { get; private set; }

        public Task<OlapStagingOutcome> ExecuteAsync(
            ClaimsPrincipal user,
            SecurityPrincipalContext caller,
            OlapQueryCommand command,
            bool isDevelopment,
            string traceId,
            CancellationToken ct)
        {
            Calls++;
            LastUser = user;
            return Task.FromResult(new OlapStagingOutcome(200, Result: new OlapQueryResult(["ok"], [[1]], 1, System.TimeSpan.Zero)));
        }
    }

    private sealed class UnauthenticatedSchemeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Api";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Disabled_by_default()
    {
        new DuckDbOlapOptions().Enabled.ShouldBeFalse();

        var defaultGateway = new GatewayOptions();
        defaultGateway.DuckDbOlap.Enabled.ShouldBeFalse();

        var repoRoot = LocateRepoRoot();
        var config = new ConfigurationManager();
        config.AddJsonFile(Path.Combine(repoRoot, "src", "Autheris.Api", "appsettings.json"), optional: false);
        config.AddJsonFile(Path.Combine(repoRoot, "src", "Autheris.Api", "appsettings.Development.json"), optional: false);
        var bound = new GatewayOptions();
        config.GetSection(GatewayOptions.SectionName).Bind(bound);
        bound.DuckDbOlap.Enabled.ShouldBeFalse();

        var quickstartConfig = new ConfigurationManager();
        quickstartConfig.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Dev:Preset"] = DevOptions.PresetQuickstart
        });
        DevConfiguration.Apply(quickstartConfig, new StubHostEnvironment(Environments.Development));
        var quickstartBound = new GatewayOptions();
        quickstartConfig.GetSection(GatewayOptions.SectionName).Bind(quickstartBound);
        quickstartBound.DuckDbOlap.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Anonymous_caller_gets_401_in_quickstart_through_full_pipeline()
    {
        var staging = new RecordingOlapStagingService();
        var services = new ServiceCollection();
        var diagnosticListener = new DiagnosticListener("Autheris.Tests");
        services.AddSingleton(diagnosticListener);
        services.AddSingleton<DiagnosticSource>(diagnosticListener);
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(Environments.Development));
        services.AddSingleton<IOlapStagingService>(staging);
        services.AddSingleton(Options.Create(new GatewayOptions
        {
            Dev = new DevOptions { Preset = DevOptions.PresetQuickstart },
            DuckDbOlap = new DuckDbOlapOptions { Enabled = true }
        }));
        services.AddAuthentication("TestBearer")
            .AddScheme<AuthenticationSchemeOptions, UnauthenticatedSchemeHandler>("TestBearer", _ => { });
        services.AddAuthorization(GatewayPolicies.Configure);

        await using var sp = services.BuildServiceProvider();
        var app = new ApplicationBuilder(sp);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        EndpointDataSource? capturedDataSource = null;
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapDuckDbOlapEndpoints();
            capturedDataSource = endpoints.DataSources.First();
        });

        capturedDataSource.ShouldNotBeNull();
        var olapEndpoint = capturedDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/v1/olap/query");
        olapEndpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty();

        var pipeline = app.Build();
        var context = new DefaultHttpContext
        {
            RequestServices = sp,
            User = new ClaimsPrincipal(new ClaimsIdentity())
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/olap/query";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"sql":"SELECT 1"}"""));
        context.Response.Body = new MemoryStream();

        await pipeline(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        staging.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Staging_service_never_called_with_anonymous_principal()
    {
        var staging = new RecordingOlapStagingService();
        var options = Options.Create(new GatewayOptions
        {
            Dev = new DevOptions { Preset = DevOptions.PresetQuickstart },
            DuckDbOlap = new DuckDbOlapOptions { Enabled = true }
        });

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity())
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"sql":"SELECT 1"}"""));
        context.Response.Body = new MemoryStream();

        await DuckDbOlapEndpoints.HandleOlapQueryAsync(context, staging, options, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        staging.Calls.ShouldBe(0);
        staging.LastUser.ShouldBeNull();
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Autheris.sln");
    }
}
