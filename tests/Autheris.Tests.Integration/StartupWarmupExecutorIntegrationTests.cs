using System.Net;
using System.Text;
using Autheris.Api.Hosting.Warmup;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Interfaces;
using HotChocolate.Execution;
using HotChocolate.Execution.Instrumentation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

/// <summary>PLAN-OPS-150 WP-C: the warm-up task against the real gateway host (AC-4, T-OPS150-08/-20/-22).</summary>
public sealed class StartupWarmupExecutorIntegrationTests
{
    private sealed class WarmupRecorder : ExecutionDiagnosticEventListener
    {
        public readonly List<bool> Seen = [];

        public override IDisposable ExecuteRequest(RequestContext context)
        {
            lock (Seen)
            {
                Seen.Add(context.IsWarmupRequest());
            }

            return EmptyScope;
        }
    }

    private static WebApplicationFactory<Program> Factory(Action<IServiceCollection>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-warmupc-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSyntheticSqlData();
            if (configure is not null)
            {
                builder.ConfigureTestServices(configure);
            }
        });

    private static async Task WaitForStartupAsync(WebApplicationFactory<Program> factory)
    {
        foreach (var service in factory.Services.GetServices<IHostedService>().OfType<BackgroundService>()
                     .Where(h => h is GraphQlExecutorGate or StartupWarmupService))
        {
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task FullHost_WarmupTaskProof_IsTheServingExecutor()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        await WaitForStartupAsync(factory);

        var executor = await factory.Services.GetRequiredService<HotChocolate.Execution.IRequestExecutorProvider>().GetExecutorAsync();

        factory.Services.GetRequiredService<GraphQlWarmupProof>().WarmedExecutor.ShouldBeSameAs(executor);
        factory.Services.GetRequiredService<IGraphQlExecutorGate>().State.ShouldBe(ExecutorGateState.Ready);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Startup_AuditChainLengthUnchanged_NoDataSourceQuery_NoConsentResolution()
    {
        // AC-4 / T-OPS150-20 / SEC-OPS150-07: the warm-up reaches no audit writer, data source, consent resolution or rate limiter.
        var audit = Substitute.For<IAuditWriter>();
        var dataSource = Substitute.For<IDataSourceExecutor>();
        var consent = Substitute.For<IConsentResolutionService>();
        var rateLimiter = Substitute.For<IRateLimiterService>();
        await using var factory = Factory(services =>
        {
            services.AddSingleton(audit);
            services.AddSingleton(dataSource);
            services.AddSingleton(consent);
            services.AddSingleton(rateLimiter);
        });
        using var client = factory.CreateClient();

        await WaitForStartupAsync(factory);

        // The only startup audit entries come from pre-existing hosted services (ReBAC seed, catalog governance ingestion);
        // the warm-up adds none (the chain length is that baseline).
        string[] startupBaseline = [AuditEventTypes.RebacTupleSeed, AuditEventTypes.CatalogVisibilityChanged, AuditEventTypes.TenancyDefaultTenantConfigured];
        audit.ReceivedCalls().SelectMany(c => c.GetArguments().OfType<Autheris.Domain.Model.AuditLogEntry>())
            .Select(e => e.EventType).ShouldAllBe(t => startupBaseline.Contains(t));
        dataSource.ReceivedCalls().ShouldBeEmpty();
        consent.ReceivedCalls().ShouldBeEmpty();
        rateLimiter.ReceivedCalls().ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task HttpGraphQlRequest_HeaderExtensionVariableOrBatchItem_NeverIsWarmupRequest()
    {
        // T-OPS150-08 / T-OPS150-22 / SEC-OPS150-17: the marker is in-process only; nothing a client sends on the real HTTP pipeline sets it.
        var recorder = new WarmupRecorder();
        await using var factory = Factory(services => services.AddGraphQLServer().AddDiagnosticEventListener(_ => recorder));
        using var client = factory.CreateClient();
        await WaitForStartupAsync(factory);
        lock (recorder.Seen)
        {
            recorder.Seen.Clear();
        }

        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-WARMUP-USER");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_e2e");
        client.DefaultRequestHeaders.Add("X-Warmup-Request", "true");
        client.DefaultRequestHeaders.Add("IsWarmupRequest", "true");
        var bodies = new[]
        {
            """{"query":"{ __typename }","extensions":{"IsWarmupRequest":true,"isWarmupRequest":true}}""",
            """{"query":"query Q($IsWarmupRequest: Boolean) { __typename }","variables":{"IsWarmupRequest":true}}""",
            """[{"query":"{ __typename }"},{"query":"{ __typename }","extensions":{"IsWarmupRequest":true}}]""",
            """{"id":"IsWarmupRequest","extensions":{"persistedQuery":{"version":1,"sha256Hash":"IsWarmupRequest"}}}""",
        };

        var statuses = new List<string>();
        foreach (var body in bodies)
        {
            using var response = await client.PostAsync("/graphql", new StringContent(body, Encoding.UTF8, "application/json"));
            statuses.Add(((int)response.StatusCode).ToString());
        }

        lock (recorder.Seen)
        {
            recorder.Seen.ShouldNotBeEmpty(string.Join(" | ", statuses));
            recorder.Seen.ShouldAllBe(seen => !seen);
        }
    }
}
