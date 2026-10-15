namespace Autheris.Tests.Integration;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// WP-ARC-A-03 end to end: POST a subscription as an admin, ingest a Debezium event through <c>/api/v1/cdc/events</c> and receive the masked
/// CloudEvent, signed, from the durable outbox. The receiver listens on the container's private address because the egress policy never
/// allows loopback (private addresses are allowed in Development only).
/// Run: dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~CdcWebhookEndToEnd"
/// </summary>
public sealed class CdcWebhookEndToEndTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Tenant = "tenant_e2e";
    private static readonly string Secret = new('k', 40);
    private static readonly string Kek = new('e', 48);

    // Source timestamp after every subscription's creation time: events older than the subscription are skipped (history replay).
    private static readonly long EventMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();

    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "autheris-cdcwh-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public CdcWebhookEndToEndTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* best effort */ }
        }

        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
    }

    private sealed record Delivery(Dictionary<string, string> Headers, string Body);

    private sealed class Receiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        public ConcurrentQueue<Delivery> Received { get; } = new();
        public string Url { get; }

        public Receiver()
        {
            var ip = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .First(a => a.AddressFamily == AddressFamily.InterNetwork);
            using var probe = new TcpListener(IPAddress.Any, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://{ip}:{port}/hook";
            _listener.Prefixes.Add($"http://+:{port}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var headers = ctx.Request.Headers.AllKeys.Where(k => k is not null)
                    .ToDictionary(k => k!, k => ctx.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
                Received.Enqueue(new Delivery(headers, body));
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
        }

        public async Task<bool> WaitForAsync(int count, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (Received.Count >= count)
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return Received.Count >= count;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
        }
    }

    private Receiver NewReceiver()
    {
        var r = new Receiver();
        _disposables.Add(r);
        return r;
    }

    private WebApplicationFactory<Program> NewHost(string dbPath)
    {
        var host = _baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source={dbPath}");
            b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            b.UseSetting("Gateway:OpenSchema", "true");
            b.UseSetting("Gateway:Rebac:Enabled", "false");
            b.UseSetting("Gateway:CdcWebhooks:Enabled", "true");
            b.UseSetting("Gateway:CdcWebhooks:SubscriptionCacheSeconds", "1");
            b.UseSetting("Gateway:Webhooks:PollIntervalSeconds", "1");
            // Operator-provisioned KEK of the secret protector (resolved by the default secret provider).
            b.UseSetting("CDC_WEBHOOK_KEK", Kek);
        });
        _disposables.Add(host);
        return host;
    }

    private static HttpClient Client(WebApplicationFactory<Program> host, string roles, string sid)
    {
        var c = host.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        c.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        c.DefaultRequestHeaders.Add("X-Test-Group-Sids", "S-1-5-E2E-GROUP");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        return c;
    }

    private static async Task RegisterCatalogAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "customers", TableName = "customers", SchemaName = "crm", SourceType = "Sqlite", SourceName = "salesdb" },
            Identifier = new TableIdentifier("salesdb", "crm", "customers"),
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
                // "ssn" is deliberately not in the catalog: columns unknown to the catalog are never streamed.
            ]
        });
    }

    private static async Task<string> CreateSubscriptionAsync(HttpClient admin, string url)
    {
        var body = JsonSerializer.Serialize(new
        {
            id = "client-chosen",
            tenantId = "other-tenant",
            targetUrl = url,
            filterTable = "salesdb.crm.customers",
            filterOperations = Array.Empty<int>(),
            hmacSecret = Secret,
            isEnabled = true
        });
        var response = await admin.PostAsync("/api/v1/cdc/subscriptions", new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadAsStringAsync();
        json.ShouldNotContain(Secret);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetString().ShouldNotBe("client-chosen");
        doc.RootElement.GetProperty("tenantId").GetString().ShouldBe(Tenant);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task IngestAsync(HttpClient ingest, string name = "Alice")
    {
        var payload = $$$"""
        {"op":"c","source":{"schema":"crm","table":"customers","name":"salesdb","ts_ms":{{{EventMs}}}},
         "after":{"id":1,"name":"{{{name}}}","ssn":"123-45-6789","tenant_id":"{{{Tenant}}}"}}
        """;
        var response = await ingest.PostAsync("/api/v1/cdc/events", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
    }

    private static string DbPath(string dir) => Path.Combine(dir, "gov.db");

    [Fact]
    public async Task RegisteredSubscription_ReceivesMaskedInsertEvent_ViaOutbox()
    {
        var receiver = NewReceiver();
        var host = NewHost(DbPath(_workDir));
        await RegisterCatalogAsync(host);
        var admin = Client(host, "GovernanceAdmin", "S-1-5-E2E-ADMIN");
        await CreateSubscriptionAsync(admin, receiver.Url);

        await IngestAsync(Client(host, "ClusterAdmin", "S-1-5-E2E-INGEST"));

        (await receiver.WaitForAsync(1, TimeSpan.FromSeconds(30))).ShouldBeTrue("no webhook delivery received");
        var delivery = receiver.Received.First();
        WebhookSignature.TryVerify(
            Secret, delivery.Headers["X-Autheris-Timestamp"], delivery.Body, delivery.Headers["X-Autheris-Signature-256"], DateTimeOffset.UtcNow)
            .ShouldBeTrue("signature must verify with the plaintext secret the admin supplied");
        delivery.Body.ShouldContain("Alice");
        delivery.Body.ShouldNotContain("123-45-6789");
        delivery.Body.ShouldNotContain("ssn");
        using var doc = JsonDocument.Parse(delivery.Body);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("autheris.cdc.insert");
        doc.RootElement.GetProperty("tenantid").GetString().ShouldBe(Tenant);
        doc.RootElement.GetProperty("id").GetString()!.ShouldStartWith("cdc-");
        delivery.Headers["ce-id"].ShouldBe(doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task DuplicateIngestion_YieldsOneDelivery()
    {
        var receiver = NewReceiver();
        var host = NewHost(DbPath(_workDir));
        await RegisterCatalogAsync(host);
        await CreateSubscriptionAsync(Client(host, "GovernanceAdmin", "S-1-5-E2E-ADMIN"), receiver.Url);
        var ingest = Client(host, "ClusterAdmin", "S-1-5-E2E-INGEST");

        await IngestAsync(ingest);
        await IngestAsync(ingest);
        await IngestAsync(ingest);

        (await receiver.WaitForAsync(1, TimeSpan.FromSeconds(30))).ShouldBeTrue();
        await Task.Delay(TimeSpan.FromSeconds(4));
        receiver.Received.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Subscription_SurvivesRestart()
    {
        var receiver = NewReceiver();
        var db = DbPath(_workDir);
        var first = NewHost(db);
        await RegisterCatalogAsync(first);
        await CreateSubscriptionAsync(Client(first, "GovernanceAdmin", "S-1-5-E2E-ADMIN"), receiver.Url);
        first.Dispose();
        SqliteConnection.ClearAllPools();

        var second = NewHost(db);
        await RegisterCatalogAsync(second);
        await IngestAsync(Client(second, "ClusterAdmin", "S-1-5-E2E-INGEST"));

        (await receiver.WaitForAsync(1, TimeSpan.FromSeconds(30))).ShouldBeTrue("subscription must be loaded from the durable store after a restart");
        var delivery = receiver.Received.First();
        WebhookSignature.TryVerify(
            Secret, delivery.Headers["X-Autheris-Timestamp"], delivery.Body, delivery.Headers["X-Autheris-Signature-256"], DateTimeOffset.UtcNow)
            .ShouldBeTrue("the KEK-protected secret must be decrypted after the restart");
    }

    [Fact]
    public async Task Delivery_Stops_AfterSubscriptionDeleted()
    {
        var receiver = NewReceiver();
        var host = NewHost(DbPath(_workDir));
        await RegisterCatalogAsync(host);
        var admin = Client(host, "GovernanceAdmin", "S-1-5-E2E-ADMIN");
        var id = await CreateSubscriptionAsync(admin, receiver.Url);

        (await admin.DeleteAsync($"/api/v1/cdc/subscriptions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Task.Delay(TimeSpan.FromSeconds(2)); // subscription cache TTL
        await IngestAsync(Client(host, "ClusterAdmin", "S-1-5-E2E-INGEST"));

        (await receiver.WaitForAsync(1, TimeSpan.FromSeconds(6))).ShouldBeFalse("a deleted subscription must not receive events");
    }
}
