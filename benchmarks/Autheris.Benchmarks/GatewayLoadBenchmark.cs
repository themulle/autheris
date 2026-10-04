using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Autheris.Benchmarks;

public class GatewayLoadBenchmark : IDisposable
{
    private readonly WebApplicationFactory<global::Program> _factory;
    private readonly HttpClient _client;
    private readonly object _queryObject = new
    {
        query = @"query { table(domain: ""finance"", name: ""finance_table_1"", first: 5) { tableName totalCount jsonRows } }"
    };
    private readonly byte[] _queryBytes = Encoding.UTF8.GetBytes("{\"query\": \"query { table(domain: \\\"finance\\\", name: \\\"finance_table_1\\\", first: 5) { tableName totalCount jsonRows } }\"}");
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    public GatewayLoadBenchmark()
    {
        var apiPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Autheris.Api"));
        if (!Directory.Exists(apiPath))
        {
            apiPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src/Autheris.Api"));
        }

        _factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            if (Directory.Exists(apiPath))
            {
                builder.UseContentRoot(apiPath);
            }
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.UseSetting("Logging:LogLevel:Microsoft", "Warning");
            builder.UseSetting("Logging:LogLevel:HotChocolate", "Warning");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
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
                    TableId = meta.Table.Id,
                    TableIdentifier = meta.Identifier,
                    Effect = ConsentEffect.Allow,
                    GranteeType = GranteeType.User,
                    GranteeSid = userSid,
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                    ColumnRules = new[]
                    {
                        new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
                    }
                };
                repo.CreateConsentAsync(consent).GetAwaiter().GetResult();
            }
        }

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value);
        _client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");

        // Warm up pipeline
        var warmupRes = _client.PostAsJsonAsync("/graphql", _queryObject).GetAwaiter().GetResult();
        if (!warmupRes.IsSuccessStatusCode)
        {
            var body = warmupRes.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new InvalidOperationException($"Warmup failed with {warmupRes.StatusCode}: {body}");
        }
    }

    public async Task<BenchmarkResult> RunConcurrentLoadAsync(int totalRequests, int concurrencyLevel)
    {
        var latencies = new ConcurrentBag<double>();
        int completed = 0;
        int failures = 0;

        var semaphore = new SemaphoreSlim(concurrencyLevel, concurrencyLevel);
        var tasks = new Task[totalRequests];

        var totalWatch = Stopwatch.StartNew();

        for (int i = 0; i < totalRequests; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    using var content = new ByteArrayContent(_queryBytes);
                    content.Headers.ContentType = JsonMediaType;
                    var sw = Stopwatch.StartNew();
                    var response = await _client.PostAsync("/graphql", content);
                    sw.Stop();

                    latencies.Add(sw.Elapsed.TotalMilliseconds);

                    if (response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref completed);
                    }
                    else
                    {
                        if (Interlocked.Increment(ref failures) == 1)
                        {
                            var err = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            Console.WriteLine($"[BENCHMARK FIRST FAILURE {response.StatusCode}]: {err}");
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
                finally
                {
                    semaphore.Release();
                }
            });
        }

        await Task.WhenAll(tasks);
        totalWatch.Stop();

        return BenchmarkResult.Compute(latencies.ToArray(), totalWatch.Elapsed.TotalSeconds, completed, failures);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }
}

public record BenchmarkResult(
    int TotalRequests,
    int SuccessfulRequests,
    int FailedRequests,
    double TotalTimeSeconds,
    double RequestsPerSecond,
    double MeanMs,
    double MinMs,
    double P50Ms,
    double P90Ms,
    double P95Ms,
    double P99Ms,
    double P999Ms,
    double MaxMs)
{
    public static BenchmarkResult Compute(double[] latencies, double totalTimeSec, int success, int failures)
    {
        Array.Sort(latencies);
        int n = latencies.Length;
        if (n == 0)
        {
            return new BenchmarkResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        double sum = 0;
        for (int i = 0; i < n; i++) sum += latencies[i];
        double mean = sum / n;

        double p50 = latencies[(int)(n * 0.50)];
        double p90 = latencies[(int)(n * 0.90)];
        double p95 = latencies[(int)(n * 0.95)];
        double p99 = latencies[(int)(Math.Min(n - 1, (int)(n * 0.99)))];
        double p999 = latencies[(int)(Math.Min(n - 1, (int)(n * 0.999)))];

        double rps = success / totalTimeSec;

        return new BenchmarkResult(
            n,
            success,
            failures,
            totalTimeSec,
            rps,
            mean,
            latencies[0],
            p50,
            p90,
            p95,
            p99,
            p999,
            latencies[n - 1]);
    }
}
