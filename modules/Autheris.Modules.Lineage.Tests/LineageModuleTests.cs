namespace Autheris.Modules.Lineage.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Hosting;
using Autheris.Api.Middleware;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lineage;
using Autheris.Modules.Abstractions;
using Autheris.Modules.Lineage;
using Autheris.Modules.Lineage.Domain;
using Autheris.Modules.Lineage.Endpoints;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class LineageModuleTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void OpenLineage_client_uses_secure_outbound_handlers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ILineageGraphStore>());
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(Environments.Production));
        services.AddSingleton(Options.Create(new GatewayOptions()));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Lineage:OpenLineageEndpoint"] = "https://openlineage.example.com/api/v1/lineage"
            })
            .Build();

        services.AddLineageModule(config);

        using var provider = services.BuildServiceProvider();
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        var handler = handlerFactory.CreateHandler(nameof(OpenLineageClient));

        ContainsSsrfHandler(handler).ShouldBeTrue();
        var primary = GetPrimaryHandler(handler).ShouldBeOfType<SocketsHttpHandler>();
        primary.AllowAutoRedirect.ShouldBeFalse();
        primary.ConnectCallback.ShouldNotBeNull();

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOpenLineageClient>().ShouldBeOfType<OpenLineageClient>();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("https://169.254.169.254/api/v1/lineage")]
    [InlineData("https://10.1.2.3/api/v1/lineage")]
    [InlineData("https://127.0.0.1/api/v1/lineage")]
    public async Task OpenLineage_endpoint_to_private_address_is_blocked(string privateUrl)
    {
        var graphStore = Substitute.For<ILineageGraphStore>();
        graphStore.GetAllNodes().Returns([
            new LineageNode("finance.dbo.invoices", "dbo.invoices", LineageNodeType.Table, [], TenantId: "tenant-a")
        ]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(graphStore);
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(Environments.Production));
        services.AddSingleton(Options.Create(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = "tenant-a" }
        }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Lineage:OpenLineageEndpoint"] = privateUrl
            })
            .Build();
        services.AddLineageModule(config);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolvedClient = scope.ServiceProvider.GetRequiredService<IOpenLineageClient>();
        (await resolvedClient.PushLineageGraphAsync(new TenantId("tenant-a"))).ShouldBeFalse();

        // Defense-in-depth check directly through SsrfProtectionHandler even when an endpoint passes startup URI parsing:
        var probeHandler = new RecordingHttpHandler();
        var ssrfHandler = new SsrfProtectionHandler(
            new StubHostEnvironment(Environments.Production),
            Options.Create(new GatewayOptions()),
            EgressIntegrations.Lineage)
        {
            InnerHandler = probeHandler
        };

        using var directHttp = new HttpClient(ssrfHandler);
        var validUri = new Uri(privateUrl);
        await Should.ThrowAsync<System.Security.SecurityException>(() =>
            directHttp.SendAsync(new HttpRequestMessage(HttpMethod.Post, validUri)));
        probeHandler.CallCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Lineage_sync_route_requires_governance_admin_and_audits()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Substitute.For<IOpenLineageClient>());
        builder.Services.AddRequestContext();
        await using var inspectionApp = builder.Build();

        new LineageModule().MapEndpoints(new ModuleEndpointRegistrar(inspectionApp, LineageModule.ModuleId));
        ModuleEndpointRegistrar.VerifyModuleEndpoints(inspectionApp, LineageModule.ModuleId);

        var endpoints = ((IEndpointRouteBuilder)inspectionApp).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ModuleEndpointMetadata>()?.ModuleId == LineageModule.ModuleId)
            .ToList();

        endpoints.Count.ShouldBe(1);
        var syncEndpoint = endpoints[0];
        syncEndpoint.RoutePattern.RawText.ShouldBe("/modules/lineage/openlineage/sync");
        syncEndpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
        syncEndpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .ShouldContain(a => string.Equals(a.Policy, LineageEndpoints.GovernanceAdminPolicy, StringComparison.Ordinal));
        var auditPolicy = syncEndpoint.Metadata.GetMetadata<AuditPolicy>();
        auditPolicy.ShouldNotBeNull();
        auditPolicy.Level.ShouldBe(AuditLevel.Full);
        auditPolicy.EventType.ShouldBe(AuditEventTypes.AuditConfigChanged);
        syncEndpoint.Metadata.GetMetadata<IngressTransportMetadata>().ShouldNotBeNull();

        var lineageClient = Substitute.For<IOpenLineageClient>();
        lineageClient.PushLineageGraphAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns(true);
        var auditRepo = Substitute.For<IAuditLogRepository>();

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                    services.AddAuthentication(HeaderTestAuthHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, HeaderTestAuthHandler>(HeaderTestAuthHandler.SchemeName, _ => { });
                    services.AddAuthorization(o =>
                        o.AddPolicy(LineageEndpoints.GovernanceAdminPolicy, p => p.RequireRole("GovernanceAdmin", "ClusterAdmin")));
                    services.AddSingleton(lineageClient);
                    services.AddRequestContext();
                    services.AddAuditWriterOver(auditRepo);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(routeBuilder =>
                        new LineageModule().MapEndpoints(new ModuleEndpointRegistrar(routeBuilder, LineageModule.ModuleId)));
                }))
            .StartAsync();

        var client = host.GetTestClient();

        // 1. Anonymous request rejected (401)
        var anonResponse = await client.PostAsync("/modules/lineage/openlineage/sync", null);
        anonResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // 2. Authenticated non-GovernanceAdmin rejected (403)
        using var ownerReq = new HttpRequestMessage(HttpMethod.Post, "/modules/lineage/openlineage/sync");
        ownerReq.Headers.Add(HeaderTestAuthHandler.RoleHeader, "DataOwner");
        var ownerResponse = await client.SendAsync(ownerReq);
        ownerResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await lineageClient.DidNotReceiveWithAnyArgs().PushLineageGraphAsync(default, default);

        // 3. GovernanceAdmin allowed (200) and audited (never records query secrets)
        const string querySecret = "query-api-key-secret-xyz";
        using var adminReq = new HttpRequestMessage(HttpMethod.Post, $"/modules/lineage/openlineage/sync?api-key={querySecret}");
        adminReq.Headers.Add(HeaderTestAuthHandler.RoleHeader, "GovernanceAdmin");
        var adminResponse = await client.SendAsync(adminReq);
        adminResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        await lineageClient.Received(1).PushLineageGraphAsync(
            Arg.Is<TenantId>(t => t.Value == "tenant-alpha"),
            Arg.Any<CancellationToken>());
        await auditRepo.Received(1).RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        var auditEntry = auditRepo.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<AuditLogEntry>()
            .Single();
        auditEntry.EventType.ShouldBe(AuditEventTypes.AuditConfigChanged);
        auditEntry.Decision.ShouldBe(AuditDecisions.Allow);
        auditEntry.DetailsJson.ShouldNotBeNull();
        auditEntry.DetailsJson.ShouldContain("lineage");
        auditEntry.DetailsJson.ShouldNotContain(querySecret);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task OpenLineage_api_key_not_in_logs_or_config_dump()
    {
        const string secretKey = "ol-api-key-super-secret-value-987654";
        var options = new LineageModuleOptions
        {
            OpenLineageEndpoint = "https://openlineage.example.com/api/v1/lineage?token=query-secret",
            OpenLineageApiKey = secretKey
        };

        options.ToString().ShouldNotContain(secretKey);
        options.ToString().ShouldNotContain("query-secret");

        AuditDetailsBuilder.IsRedactedKey("OpenLineageApiKey").ShouldBeTrue();
        AuditDetailsBuilder.IsRedactedKey("apikey").ShouldBeTrue();
        AuditDetailsBuilder.IsRedactedKey("api-key").ShouldBeTrue();
        AuditDetailsBuilder.IsRedactedKey("signing").ShouldBeTrue();
        AuditDetailsBuilder.IsRedactedKey("kek").ShouldBeTrue();

        var auditDetails = new AuditDetailsBuilder()
            .WithField("openLineageApiKey", secretKey)
            .WithField("signingSecret", secretKey)
            .WithField("kek", secretKey)
            .WithField("api-key", secretKey)
            .Build();
        auditDetails.ShouldNotContain(secretKey);
        auditDetails.ShouldContain("[redacted]");

        SecretScrubber.Scrub($"api_key={secretKey}").ShouldNotContain(secretKey);
        SecretScrubber.Scrub($"api-key={secretKey}").ShouldNotContain(secretKey);

        var filter = new ErrorSanitizingFilter(
            new StubHostEnvironment(Environments.Production),
            NullLogger<ErrorSanitizingFilter>.Instance);
        foreach (var sensitiveField in new[] { "apikey=", "api-key=", "signing_secret=", "kek=" })
        {
            var error = HotChocolate.ErrorBuilder.New()
                .SetMessage($"upstream failed with {sensitiveField}{secretKey}")
                .SetCode("BAD_REQUEST")
                .Build();
            filter.OnError(error).Message.ShouldNotContain(secretKey);
        }

        var logger = new RecordingLogger<OpenLineageClient>();
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var client = new OpenLineageClient(
            new HttpClient(handler),
            Substitute.For<ILineageGraphStore>(),
            Options.Create(new LineageModuleOptions
            {
                OpenLineageEndpoint = "https://openlineage.example.com/api/v1/lineage",
                OpenLineageApiKey = "kv://openlineage-api-key"
            }),
            logger,
            Options.Create(new GatewayOptions()),
            new StubHostEnvironment(Environments.Production),
            secretProvider: new StubKeyVaultSecretProvider(secretKey));

        await client.PushLineageEventAsync(new OpenLineageRunEvent(
            "COMPLETE",
            DateTimeOffset.UtcNow,
            "https://github.com/themulle/gql/autheris",
            "https://openlineage.io/spec/1-0-2/OpenLineage.json",
            new { @namespace = "tenant-a", name = "sync" },
            [],
            []));

        logger.Messages.ShouldNotContain(m => m.Contains(secretKey, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Lineage_module_loads_under_the_service_guard()
    {
        var root = FindRepositoryRoot();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = LineageModule.ModuleId,
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
                ["Gateway:Modules:WorkspacePath"] = Path.Combine(root, "modules", "Autheris.Modules.Lineage", "bin"),
                ["Gateway:Lineage:OpenLineageEndpoint"] = "https://openlineage.example.com/api/v1/lineage"
            })
            .Build();
        var env = new StubHostEnvironment(Environments.Development);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ILineageGraphStore>());
        services.AddSingleton<IHostEnvironment>(env);
        services.AddSingleton(Options.Create(new GatewayOptions()));

        var result = new ModuleLoader().LoadAndConfigureServices(services, config, env);

        result.LoadedModuleIds.ShouldBe([LineageModule.ModuleId]);
        result.ClaimedConfigurationSections.ShouldBe([LineageModuleOptions.SectionName]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Lineage_module_has_no_data_source_or_governance_connection_reference()
    {
        var assembly = typeof(LineageModule).Assembly;
        // Built from parts so this file does not trip the repository-wide scan for the same identifiers.
        string[] forbiddenTypes =
        [
            "Db" + "Connection",
            "ISql" + "ConnectionFactory",
            "Governance" + "Repository",
            "IDb" + "Connection"
        ];

        var typeReferences = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        typeReferences.ShouldNotContain("Autheris.Api");
        typeReferences.ShouldNotContain("Autheris.Infrastructure");
        typeReferences.ShouldNotContain("Autheris.Extensions");

        foreach (var type in assembly.GetTypes())
        {
            foreach (var ctor in type.GetConstructors())
            {
                foreach (var param in ctor.GetParameters())
                {
                    forbiddenTypes.ShouldNotContain(param.ParameterType.Name);
                }
            }

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            {
                forbiddenTypes.ShouldNotContain(field.FieldType.Name);
            }
        }

        var moduleRoot = System.IO.Path.Combine(FindRepositoryRoot(), "modules", "Autheris.Modules.Lineage");
        var sourceViolations = Directory.EnumerateFiles(moduleRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f =>
            {
                var content = File.ReadAllText(f);
                return forbiddenTypes.Where(token => content.Contains(token, StringComparison.Ordinal))
                    .Select(token => $"{f}: {token}");
            })
            .ToList();
        sourceViolations.ShouldBeEmpty();
    }

    private static bool ContainsSsrfHandler(HttpMessageHandler handler)
    {
        var current = handler;
        while (current is DelegatingHandler delegating)
        {
            if (current is SsrfProtectionHandler)
            {
                return true;
            }

            if (delegating.InnerHandler is null)
            {
                break;
            }

            current = delegating.InnerHandler;
        }

        return current is SsrfProtectionHandler;
    }

    private static HttpMessageHandler GetPrimaryHandler(HttpMessageHandler handler)
    {
        var current = handler;
        while (current is DelegatingHandler delegating && delegating.InnerHandler is { } inner)
        {
            current = inner;
        }

        return current;
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class StubKeyVaultSecretProvider(string resolvedSecret) : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) => System.Text.Encoding.UTF8.GetBytes(resolvedSecret);
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Modules.Lineage.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder is not null ? responder(request) : new HttpResponseMessage(HttpStatusCode.Created));
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class HeaderTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "LineageTestScheme";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var roleHeader) || string.IsNullOrWhiteSpace(roleHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-LINEAGE-TEST"),
                new Claim(ClaimTypes.Role, roleHeader.ToString()),
                new Claim("tenant_id", "tenant-alpha")
            ], SchemeName);

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
