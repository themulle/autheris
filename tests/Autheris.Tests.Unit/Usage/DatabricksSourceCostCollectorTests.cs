namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class DatabricksSourceCostCollectorTests
{
    private const string Host = "adb-123.azuredatabricks.net";
    private static readonly TenantId Tenant = new("t1");
    private static readonly UsageSeriesKey SeriesKey = new(Tenant, new DateOnly(2026, 11, 15), "u1", "gs1", "default", Domain.Options.DataSources.DataSourceTransportFlags.Rest, "app", "", "dbx-main", "sales.dbo.orders");

    [Fact]
    public async Task CollectAsync_queries_history_api_with_statement_ids_and_parses_metrics()
    {
        var requests = new List<(Uri Uri, string? Auth)>();
        var handler = new FakeHttpHandler(req =>
        {
            requests.Add((req.RequestUri!, req.Headers.Authorization?.ToString()));
            return JsonResponse(HttpStatusCode.OK, """
            {
              "res": [
                {
                  "statement_id": "01ef1234-5678-9abc-def0-123456789abc",
                  "metrics": { "total_time_ms": 1450, "cost": 3.25 }
                },
                {
                  "query_id": "01ef2222-5678-9abc-def0-123456789abc",
                  "metrics": { "total_time_ms": 800 }
                }
              ]
            }
            """);
        });

        using var http = new HttpClient(handler);
        var creds = new FakeCredentialProvider((_, delegation) => $"token-for-{delegation?.TenantId?.Value}");
        var collector = new DatabricksSourceCostCollector(http, CreateOptions(), creds);

        var samples = await collector.CollectAsync(
            [
                MakePending("01ef1234-5678-9abc-def0-123456789abc"),
                MakePending("01ef2222-5678-9abc-def0-123456789abc")
            ],
            CancellationToken.None);

        requests.Count.ShouldBe(1);
        requests[0].Uri.AbsolutePath.ShouldBe("/api/2.0/sql/history/queries");
        requests[0].Uri.Query.ShouldContain("include_metrics=true");
        requests[0].Uri.Query.ShouldContain("filter_by.statement_ids=01ef1234-5678-9abc-def0-123456789abc");
        requests[0].Uri.Query.ShouldContain("filter_by.statement_ids=01ef2222-5678-9abc-def0-123456789abc");
        requests[0].Auth.ShouldBe("Bearer token-for-t1");

        samples.Count.ShouldBe(2);
        samples[0].SourceQueryId.ShouldBe("01ef1234-5678-9abc-def0-123456789abc");
        samples[0].SourceComputeMs.ShouldBe(1450);
        samples[0].SourceCost.ShouldBe(3.25m);
        samples[0].CostUnit.ShouldBe("DBU");

        samples[1].SourceQueryId.ShouldBe("01ef2222-5678-9abc-def0-123456789abc");
        samples[1].SourceComputeMs.ShouldBe(800);
        samples[1].SourceCost.ShouldBeNull();
    }

    [Fact]
    public async Task CollectAsync_chunks_more_than_100_statement_ids_across_calls()
    {
        int callCount = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            callCount++;
            return JsonResponse(HttpStatusCode.OK, "{\"res\":[]}");
        });

        using var http = new HttpClient(handler);
        var creds = new FakeCredentialProvider((_, _) => "tok");
        var collector = new DatabricksSourceCostCollector(http, CreateOptions(), creds);

        var pending = new List<PendingSourceQuery>(105);
        for (int i = 0; i < 105; i++)
        {
            pending.Add(MakePending($"stmt-{i}"));
        }

        await collector.CollectAsync(pending, CancellationToken.None);
        callCount.ShouldBe(2);
    }

    [Fact]
    public async Task CollectAsync_ignores_unrequested_statement_ids_and_retries_once_on_401()
    {
        int attempt = 0;
        var handler = new FakeHttpHandler(req =>
        {
            attempt++;
            if (attempt == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            req.Headers.Authorization?.Parameter.ShouldBe("fresh-token");
            return JsonResponse(HttpStatusCode.OK, """
            {
              "res": [
                { "statement_id": "stmt-unexpected", "metrics": { "total_time_ms": 999 } },
                { "statement_id": "stmt-expected", "metrics": { "total_time_ms": 250 } }
              ]
            }
            """);
        });

        int acquireCount = 0;
        var creds = new FakeCredentialProvider((_, _) => (++acquireCount == 1) ? "stale-token" : "fresh-token");
        using var http = new HttpClient(handler);
        var collector = new DatabricksSourceCostCollector(http, CreateOptions(), creds);

        var samples = await collector.CollectAsync([MakePending("stmt-expected")], CancellationToken.None);

        creds.EvictCount.ShouldBe(1);
        creds.InvalidateCount.ShouldBe(1);
        samples.ShouldHaveSingleItem().SourceQueryId.ShouldBe("stmt-expected");
        samples[0].SourceComputeMs.ShouldBe(250);
    }

    [Fact]
    public async Task CollectAsync_rejects_redirect_without_following()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect));
        using var http = new HttpClient(handler);
        var creds = new FakeCredentialProvider((_, _) => "tok");
        var collector = new DatabricksSourceCostCollector(http, CreateOptions(), creds);

        var ex = await Should.ThrowAsync<DatabricksProviderException>(() =>
            collector.CollectAsync([MakePending("stmt-1")], CancellationToken.None));

        ex.Code.ShouldBe(DatabricksErrorCodes.Http);
    }

    private static PendingSourceQuery MakePending(string id) =>
        new(Tenant, "dbx-main", id, new DateOnly(2026, 11, 15), new byte[32], SeriesKey, Array.Empty<string>(), new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));

    private static IOptions<GatewayOptions> CreateOptions() => Options.Create(new GatewayOptions
    {
        DataSources = new SqlDataSourceOptions
        {
            Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbx-main"] = new()
                {
                    Provider = "Databricks",
                    Databricks = new DatabricksDataSourceOptions
                    {
                        Host = Host,
                        WarehouseId = "wh-1",
                        Auth = new DatabricksAuthOptions
                        {
                            Mode = DatabricksAuthMode.OAuthM2M,
                            Tenants = new Dictionary<string, DatabricksTenantPrincipalOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                [Tenant.Value] = new() { ClientId = "c1", ClientSecretRef = "env:S1" }
                            }
                        }
                    }
                }
            }
        }
    });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class FakeCredentialProvider(Func<DatabricksDataSourceOptions, DelegationContext?, string> factory) : IDatabricksCredentialProvider
    {
        public int EvictCount { get; private set; }
        public int InvalidateCount { get; private set; }

        public Task<string> AcquireTokenAsync(DatabricksDataSourceOptions db, DelegationContext? context, CancellationToken ct) =>
            Task.FromResult(factory(db, context));

        public void EvictToken(DatabricksDataSourceOptions db, DelegationContext? context) => EvictCount++;

        public void InvalidateSecret(DatabricksDataSourceOptions db, string? tenantId = null) => InvalidateCount++;
    }
}
