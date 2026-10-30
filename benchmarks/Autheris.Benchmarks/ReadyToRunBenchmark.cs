using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Autheris.Benchmarks;

/// <summary>
/// PLAN-OPS-174 Tier 1 BenchmarkDotNet harness: measures startup method JIT / in-process pipeline
/// execution, CPU, and heap allocations for non-R2R vs R2R runtime qualification (resolving #150 OQ-9).
/// </summary>
[MemoryDiagnoser]
[WarmupCount(10)]
[IterationCount(30)]
public class ReadyToRunBenchmark : IDisposable
{
    private static readonly byte[] TypenameQueryBytes =
        Encoding.UTF8.GetBytes("{\"query\":\"{ __typename }\"}");

    private static readonly byte[] CatalogQueryBytes =
        Encoding.UTF8.GetBytes("{\"query\":\"query { table(domain: \\\"finance\\\", name: \\\"finance_table_1\\\", first: 5) { tableName totalCount jsonRows } }\"}");

    private static readonly byte[] McpDescribeBytes =
        Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"describe_dataset\",\"arguments\":{\"dataset\":\"finance.dbo.finance_table_1\"}}}");

    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    private WebApplicationFactory<global::Program>? _factory;
    private HttpClient? _client;

    [GlobalSetup]
    public void Setup()
    {
        string root = FindRepoRoot();
        string apiPath = Path.Combine(root, "src", "Autheris.Api");

        _factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            if (Directory.Exists(apiPath))
            {
                builder.UseContentRoot(apiPath);
            }

            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.UseSetting("Logging:LogLevel:Microsoft", "Warning");
            builder.UseSetting("Logging:LogLevel:HotChocolate", "Warning");
            builder.UseSetting("Gateway:Insecure:danger_bypass_mcp_auth", "true");
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:Mcp:Enabled", "true");
            builder.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            builder.UseSetting("Gateway:WebSql:AllowedDataSources:0", "finance");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "100000");
            builder.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokenBucketCapacity", "100000");
            builder.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokensPerSecond", "10000");
            builder.UseSetting("Gateway:ResourceGroups:Interactive:MaxConcurrentRequests", "5000");
            builder.UseSetting("Gateway:ResourceGroups:Interactive:MaxQueueSize", "50000");
            builder.UseSetting("Gateway:ResourceGroups:Interactive:QueueTimeoutSeconds", "30");

            builder.ConfigureServices(services =>
            {
                var mockTierResolver = Substitute.For<IClientTierResolver>();
                var unlimitedPolicy = new ClientQuotaPolicy(
                    ClientTier.Internal,
                    MaxCostPerQuery: 1_000_000,
                    MaxComplexityDepth: 100,
                    MaxTokensCapacity: 1_000_000,
                    TokenRefillRatePerSecond: 1_000_000,
                    ExposeCostExtensions: true);
                mockTierResolver.ResolveAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(new ClientQuotaContext("bench_user", ClientTier.Internal, unlimitedPolicy));
                services.AddSingleton<IClientTierResolver>(mockTierResolver);
            });
        });

        var userSid = new Sid("S-1-5-21-9999");
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var meta = repo.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1")).GetAwaiter().GetResult();
            if (meta != null)
            {
                var consent = new Consent
                {
                    TenantId = new Autheris.Domain.Common.TenantId("bench-tenant"),
                    TableId = meta.Table.Id,
                    TableIdentifier = meta.Identifier,
                    Effect = ConsentEffect.Allow,
                    GranteeType = GranteeType.User,
                    GranteeSid = userSid,
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                    ColumnRules =
                    [
                        new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
                    ]
                };
                repo.CreateConsentAsync(consent).GetAwaiter().GetResult();
            }
        }

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value);
        _client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // Pipeline pre-warm
        _ = WarmQuery_TypenameIntrospection().GetAwaiter().GetResult();
        _ = WarmQuery_CatalogTableQuery().GetAwaiter().GetResult();
        _ = WarmMcp_DescribeDataset().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Hot path 1: Introspection-free { __typename } query execution.
    /// </summary>
    [Benchmark]
    public async Task<string> WarmQuery_TypenameIntrospection()
    {
        using var content = new ByteArrayContent(TypenameQueryBytes);
        content.Headers.ContentType = JsonMediaType;
        using var response = await _client!.PostAsync("/graphql", content).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Hot path 2: Governed single-table GraphQL query.
    /// </summary>
    [Benchmark]
    public async Task<string> WarmQuery_CatalogTableQuery()
    {
        using var content = new ByteArrayContent(CatalogQueryBytes);
        content.Headers.ContentType = JsonMediaType;
        using var response = await _client!.PostAsync("/graphql", content).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Hot path 3: MCP describe_dataset tool call serialization & schema inspection.
    /// </summary>
    [Benchmark]
    public async Task<string> WarmMcp_DescribeDataset()
    {
        using var content = new ByteArrayContent(McpDescribeBytes);
        content.Headers.ContentType = JsonMediaType;
        using var response = await _client!.PostAsync("/mcp", content).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Cold path 4: Execution on a fresh uncached executor to observe method JIT frequency.
    /// </summary>
    [Benchmark]
    public async Task<IExecutionResult> ColdPipeline_FreshExecutorExecution()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGraphQLServer().AddQueryType<Query>();
        await using var sp = services.BuildServiceProvider();
        var executor = await sp.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync().ConfigureAwait(false);
        var request = OperationRequestBuilder.New().SetDocument("{ __typename }").Build();
        return await executor.ExecuteAsync(request).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    public static async Task RunSelfTestAsync()
    {
        Console.WriteLine("Executing ReadyToRunBenchmark self-test...");
        using var bench = new ReadyToRunBenchmark();
        bench.Setup();

        string r1 = await bench.WarmQuery_TypenameIntrospection();
        if (string.IsNullOrWhiteSpace(r1) || !r1.Contains("__typename"))
        {
            throw new InvalidOperationException($"WarmQuery_TypenameIntrospection failed: {r1}");
        }

        string r2 = await bench.WarmQuery_CatalogTableQuery();
        if (string.IsNullOrWhiteSpace(r2) || !r2.Contains("finance_table_1"))
        {
            throw new InvalidOperationException($"WarmQuery_CatalogTableQuery failed: {r2}");
        }

        string r3 = await bench.WarmMcp_DescribeDataset();
        if (string.IsNullOrWhiteSpace(r3))
        {
            throw new InvalidOperationException($"WarmMcp_DescribeDataset failed: {r3}");
        }

        var r4 = await bench.ColdPipeline_FreshExecutorExecution();
        if (r4 is not OperationResult { Errors: null or { Count: 0 } })
        {
            throw new InvalidOperationException($"ColdPipeline_FreshExecutorExecution failed: {r4}");
        }

        Console.WriteLine("ReadyToRunBenchmark self-test PASSED (all 4 methods verified).");
    }

    public static void Run(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("self-test", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTestAsync().GetAwaiter().GetResult();
            return;
        }

        Console.WriteLine("Running ReadyToRunBenchmark under BenchmarkDotNet...");
        string repoRoot = FindRepoRoot();
        string resultsDir = Path.Combine(repoRoot, "benchmarks", "results");
        Directory.CreateDirectory(resultsDir);

        string[] effectiveArgs = args.Length > 0
            ? args
            : ["--job", "short", "--exporters", "json", "--artifacts", resultsDir];

        var switcher = BenchmarkSwitcher.FromTypes([typeof(ReadyToRunBenchmark)]);
        switcher.Run(effectiveArgs);

        var fullReports = Directory.GetFiles(resultsDir, "*ReadyToRunBenchmark-report-full*.json", SearchOption.AllDirectories);
        if (fullReports.Length > 0)
        {
            string newest = fullReports.OrderByDescending(File.GetLastWriteTimeUtc).First();
            string targetPath = Path.Combine(resultsDir, "bdn-r2r-results.json");
            File.Copy(newest, targetPath, overwrite: true);
            Console.WriteLine($"ReadyToRun BDN results copied to: {targetPath}");
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Autheris.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Autheris.sln not found");
    }
}
