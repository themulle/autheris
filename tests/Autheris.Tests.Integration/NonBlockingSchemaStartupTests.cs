namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Hosting.Warmup;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Tests.Integration.Governance;
using HotChocolate;
using HotChocolate.Execution.Configuration;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1109: host start (and so <c>/health/live</c>) must not wait for the GraphQL schema build; <c>/health/ready</c>
/// reports Warming until the executor and the MCP schema discovery are done.
/// </summary>
[Trait("Category", "Security")]
public sealed class NonBlockingSchemaStartupTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(90);

    /// <summary>Test seam: an extra type module whose <c>CreateTypesAsync</c> holds the schema build until released.</summary>
    private sealed class HeldTypeModule : ITypeModule
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

#pragma warning disable CS0067 // the module never changes its types
        public event EventHandler<EventArgs>? TypesChanged;
#pragma warning restore CS0067

        public async ValueTask<IReadOnlyCollection<ITypeSystemMember>> CreateTypesAsync(IDescriptorContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return [];
        }
    }

    private static WebApplicationFactory<Program> Factory(HeldTypeModule module) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("NonBlockingSchemaStart"));
            builder.UseSyntheticSqlData();
            builder.ConfigureServices(services => services.AddGraphQLServer().AddTypeModule(_ => module));
        });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task HostStart_DoesNotWaitForSchemaBuild_LiveIs200_ReadyFlipsAfterBuild()
    {
        var module = new HeldTypeModule();
        await using var factory = Factory(module);

        try
        {
            // Host start must complete while the build is still held.
            var client = await Task.Run(factory.CreateClient).WaitAsync(Bound);
            await module.Entered.Task.WaitAsync(Bound);

            var (liveStatus, _) = await GetAsync(client, "/health/live");
            liveStatus.ShouldBe(HttpStatusCode.OK);

            var (readyStatus, readyBody) = await GetAsync(client, "/health/ready");
            readyStatus.ShouldBe(HttpStatusCode.ServiceUnavailable);
            readyBody.GetProperty("status").GetString().ShouldBe("Warming");
            factory.Services.GetRequiredService<IGraphQlExecutorGate>().State.ShouldBe(ExecutorGateState.Pending);
            factory.Services.GetRequiredService<IMcpSchemaDiscoveryState>().Phase.ShouldBe(McpSchemaDiscoveryPhase.Pending);

            // A GraphQL request during the warm-up never sees a partial schema: it waits for the one full build (or is refused).
            using var gqlClient = factory.CreateClient();
            gqlClient.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
            gqlClient.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-WARMING-USER");
            gqlClient.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
            gqlClient.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_e2e");
            var duringWarmup = gqlClient.PostAsync("/graphql", new StringContent("""{"query":"{ __typename }"}""", Encoding.UTF8, "application/json"));
            try
            {
                await duringWarmup.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // Expected: the request waits for the held build.
            }

            if (duringWarmup.IsCompleted)
            {
                using var early = await duringWarmup;
                early.StatusCode.ShouldNotBe(HttpStatusCode.OK, "a GraphQL request answered while the schema build was held");
            }

            module.Release.TrySetResult();

            if (!duringWarmup.IsCompleted)
            {
                using var late = await duringWarmup.WaitAsync(Bound);
                late.StatusCode.ShouldBe(HttpStatusCode.OK, await late.Content.ReadAsStringAsync());
            }

            var hosted = factory.Services.GetServices<IHostedService>().OfType<BackgroundService>()
                .Where(h => h is GraphQlExecutorGate or StartupWarmupService || h is IMcpSchemaDiscoveryState);
            foreach (var service in hosted)
            {
                await service.ExecuteTask!.WaitAsync(Bound);
            }

            factory.Services.GetRequiredService<IMcpSchemaDiscoveryState>().Phase.ShouldBe(McpSchemaDiscoveryPhase.Ready);
            var (finalStatus, finalBody) = await GetAsync(client, "/health/ready");
            finalStatus.ShouldBe(HttpStatusCode.OK, finalBody.ToString());
        }
        finally
        {
            module.Release.TrySetResult();
        }
    }
}
