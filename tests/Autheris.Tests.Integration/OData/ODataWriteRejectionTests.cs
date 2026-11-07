namespace Autheris.Tests.Integration.OData;

using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ODataWriteRejectionTests : IClassFixture<ODataWriteRejectionTests.Fixture>
{
    private static readonly TableIdentifier ItemsTable = new("erp", "main", "items");
    private static readonly Sid WriterSid = new("S-1-5-21-ODATA-WRITER");
    private const string Tenant = "erp";

    private readonly Fixture _fixture;
    private WebApplicationFactory<Program> _factory => _fixture.Factory;
    private SourceConnectionCounter _connectionCounter => _fixture.ConnectionCounter;

    public ODataWriteRejectionTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class Fixture : IDisposable
    {
        private readonly WebApplicationFactory<Program> _root = new();
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"odata-write-reject-{Guid.NewGuid():N}.db");
        public WebApplicationFactory<Program> Factory { get; }
        internal SourceConnectionCounter ConnectionCounter { get; } = new();
        public bool Seeded { get; set; }

        public Fixture()
        {
            SeedDatabase(_dbPath);

            var counter = ConnectionCounter;
            var dbPath = _dbPath;

            Factory = _root.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Gateway:Tenancy:DefaultTenantId", Tenant);
                builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "5000");
                builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
                builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-odata-write-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
                builder.UseSetting("Gateway:DataSources:Connections:erp:Provider", "Sqlite");
                builder.UseSetting("Gateway:DataSources:Connections:erp:ConnectionString", $"Data Source={dbPath}");
                builder.UseSetting("Gateway:OData:Enabled", "true");
                builder.UseSetting("Gateway:Rebac:Enabled", "false");
                builder.ConfigureServices(services =>
                {
                    var original = services.Single(d => d.ServiceType == typeof(ISqlConnectionFactory));
                    services.Remove(original);
                    services.AddSingleton<ISqlConnectionFactory>(sp =>
                        new CountingSqlConnectionFactory(
                            (ISqlConnectionFactory)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!),
                            counter,
                            dbPath));
                });
            });
        }

        public void Dispose()
        {
            Factory.Dispose();
            _root.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }

    private static void SeedDatabase(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL); INSERT INTO items VALUES (1, 'initial');";
        cmd.ExecuteNonQuery();
    }

    private async Task EnsureSeededAsync()
    {
        if (_fixture.Seeded)
        {
            return;
        }

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table
            {
                Id = Guid.NewGuid(),
                DisplayName = "items",
                TableName = "items",
                SchemaName = "main",
                SourceType = "Sqlite",
                SourceName = "erp",
                IsActive = true
            },
            Identifier = ItemsTable,
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        });

        var meta = (await repo.GetTableMetadataAsync(ItemsTable))!;
        await repo.CreateConsentAsync(new Consent
        {
            TenantId = new TenantId(Tenant),
            TableId = meta.Table.Id,
            TableIdentifier = ItemsTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = WriterSid,
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddHours(24),
            ColumnRules = meta.Columns
                .Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear })
                .ToList()
        });

        _fixture.Seeded = true;
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", WriterSid.Value);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "ClusterAdmin,GovernanceAdmin,DataOwner,Reader");
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        return client;
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("MERGE")]
    [InlineData("DELETE")]
    public async Task OData_write_methods_rejected_without_source_call(string method)
    {
        await EnsureSeededAsync();
        using var client = CreateClient();

        var beforeConnections = _connectionCounter.Count;

        foreach (var path in new[]
        {
            "/odata/v4/erp/main/items",
            "/odata/v4/erp_main_items",
            "/odata/v4/erp/main/items(1)"
        })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = new StringContent("{\"id\":2,\"name\":\"mutated\"}", Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request);

            ((int)response.StatusCode).ShouldBeOneOf(
                (int)HttpStatusCode.MethodNotAllowed,
                (int)HttpStatusCode.Forbidden);
            response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        }

        _connectionCounter.Count.ShouldBe(beforeConnections, "No data source connection may be opened for rejected OData write requests.");
    }

    [Fact]
    public async Task OData_batch_with_write_rejected()
    {
        await EnsureSeededAsync();
        using var client = CreateClient();

        var beforeConnections = _connectionCounter.Count;

        const string jsonBatchPayload = """
            {
                "requests": [
                    {
                        "id": "1",
                        "method": "GET",
                        "url": "erp/main/items"
                    },
                    {
                        "id": "2",
                        "method": "POST",
                        "url": "erp/main/items",
                        "headers": { "Content-Type": "application/json" },
                        "body": { "id": 99, "name": "injected" }
                    }
                ]
            }
            """;

        foreach (var batchPath in new[] { "/odata/v4/$batch", "/odata/$batch" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, batchPath)
            {
                Content = new StringContent(jsonBatchPayload, Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request);

            ((int)response.StatusCode).ShouldBeOneOf(
                (int)HttpStatusCode.MethodNotAllowed,
                (int)HttpStatusCode.Forbidden);
            response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        }

        _connectionCounter.Count.ShouldBe(beforeConnections, "An OData $batch containing a write must be rejected as a whole without touching the data source.");
    }

    internal sealed class SourceConnectionCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class CountingSqlConnectionFactory(
        ISqlConnectionFactory inner,
        SourceConnectionCounter counter,
        string targetDbPath) : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            if (options.ConnectionString?.Contains(targetDbPath, StringComparison.Ordinal) == true)
            {
                counter.Increment();
            }

            return inner.CreateOpenConnectionAsync(options, ct);
        }
    }
}
