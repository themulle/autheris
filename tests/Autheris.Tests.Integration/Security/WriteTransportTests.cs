namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// Write transport matrix: a valid write grant exists for the caller and WebSQL DML is enabled, yet every other ingress (OData,
/// GraphQL, REST data routes, Trino and MCP) refuses a write fail closed, and no connection reaches the source for the refused call.
/// </summary>
[Trait("Category", "Security")]
public sealed class WriteTransportTests : IClassFixture<WriteTransportTests.Fixture>
{
    private const string Source = "src_w";
    private const string Table = "w_items";
    private const string CallerSid = "S-1-5-WTR";

    private readonly Fixture _fixture;

    public WriteTransportTests(Fixture fixture) => _fixture = fixture;

    private async Task EnsureSeededAsync()
    {
        if (_fixture.Seeded)
        {
            return;
        }

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var id = new TableIdentifier(Source, "main", Table);
            var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = Table, TableName = Table, SchemaName = "main", SourceType = "PostgreSql", SourceName = Source },
                Identifier = id,
                CatalogView = CatalogView.Granted,
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "integer" },
                    new TableColumn { ColumnName = "name", DataType = "varchar" },
                    new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
                ]
            });

            // A valid, active write grant for the caller: the transports below must refuse regardless.
            await repo.CreateConsentAsync(new Consent
            {
                TableId = meta.Table.Id,
                TableIdentifier = id,
                TenantId = new TenantId("default"),
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = new Sid(CallerSid),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                WriteRules =
                [
                    new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "name"], WriteRowScope.AllRows),
                    new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["name"], WriteRowScope.AllRows),
                    new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.AllRows)
                ]
            });
        }

        await TestSupport.GraphQlSchemaReadiness.AwaitCatalogSchemaAsync(
            _fixture.Factory.Services, ["src_w_main_w_items"], TestSupport.GraphQlSchemaReadiness.DefaultTimeout);

        // Warm-up so one-time source initialisation is not mistaken for a source call of a refused write.
        using var warm = Client();
        using var read = await warm.PostAsJsonAsync("/api/v1/sql", new { sql = $"SELECT id FROM {Source}.main.{Table}" });
        read.IsSuccessStatusCode.ShouldBeTrue(await read.Content.ReadAsStringAsync());
        _fixture.Seeded = true;
    }

    private HttpClient Client()
    {
        var c = _fixture.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User-Sid", CallerSid);
        c.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "default");
        c.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        c.DefaultRequestHeaders.Add("X-Trino-User", CallerSid);
        return c;
    }

    private async Task<List<string>> NamesAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.SourceConnection);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id::text || ':' || name FROM main.{Table} ORDER BY id";
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = method == HttpMethod.Delete ? null : JsonContent.Create(new { id = 3, name = "written" })
        };
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Write_rejected_fail_closed_on_odata_graphql_rest_trino_mcp()
    {
        await EnsureSeededAsync();
        var seed = await NamesAsync();
        seed.ShouldBe(["1:row-one", "2:row-two"]);
        var opened = _fixture.Connections.Opened;
        using var client = Client();

        // OData: POST / PATCH / DELETE on the entity set and on an entity.
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, $"/odata/v4/{Source}/main/{Table}"),
                     (HttpMethod.Patch, $"/odata/v4/{Source}/main/{Table}(1)"),
                     (HttpMethod.Put, $"/odata/v4/{Source}/main/{Table}(1)"),
                     (HttpMethod.Delete, $"/odata/v4/{Source}/main/{Table}(1)")
                 })
        {
            using var response = await SendAsync(client, method, path);
            response.IsSuccessStatusCode.ShouldBeFalse($"OData {method} {path}");
        }

        // GraphQL: the schema exposes no data mutation, and a mutation document is rejected.
        using var introspection = await client.PostAsJsonAsync("/graphql", new { query = "{ __schema { mutationType { fields { name } } } }" });
        var schemaBody = await introspection.Content.ReadAsStringAsync();
        schemaBody.ToLowerInvariant().ShouldNotContain(Table);
        using var mutation = await client.PostAsJsonAsync("/graphql", new { query = "mutation { src_w_main_w_items_insert(id: 3, name: \"written\") { id } }" });
        var mutationBody = await mutation.Content.ReadAsStringAsync();
        (mutation.IsSuccessStatusCode is false || JsonDocument.Parse(mutationBody).RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            .ShouldBeTrue(mutationBody);

        // REST data routes: write verbs.
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, $"/api/v1/data/{Source}/main/{Table}"),
                     (HttpMethod.Put, $"/api/v1/data/{Source}/main/{Table}/1"),
                     (HttpMethod.Patch, $"/api/v1/data/{Source}/main/{Table}/1"),
                     (HttpMethod.Delete, $"/api/v1/data/{Source}/main/{Table}/1")
                 })
        {
            using var response = await SendAsync(client, method, path);
            response.IsSuccessStatusCode.ShouldBeFalse($"REST {method} {path}");
        }

        // Trino: INSERT on /v1/statement.
        using var trinoRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/statement")
        {
            Content = new StringContent($"INSERT INTO {Source}.main.{Table} VALUES (3, 'written', 'default')", Encoding.UTF8, "text/plain")
        };
        using var trino = await client.SendAsync(trinoRequest);
        var trinoBody = await trino.Content.ReadAsStringAsync();
        (trino.IsSuccessStatusCode is false || JsonDocument.Parse(trinoBody).RootElement.TryGetProperty("error", out _)).ShouldBeTrue(trinoBody);

        // MCP: query_sql INSERT.
        await using (var mcp = await McpTestClient.ConnectAsync(client))
        {
            var (isError, text) = await McpTestClient.CallAsync(mcp, "query_sql", new { query = $"INSERT INTO {Source}.main.{Table} VALUES (3, 'written', 'default')" });
            isError.ShouldBeTrue(text);
        }

        (await NamesAsync()).ShouldBe(seed, "no refused write may change the source");
        _fixture.Connections.Opened.ShouldBe(opened, "a refused write must not open a source connection");
    }

    internal sealed class Counter
    {
        private int _opened;

        public int Opened => Volatile.Read(ref _opened);

        public void Increment() => Interlocked.Increment(ref _opened);
    }

    private sealed class CountingFactory(ISqlConnectionFactory inner, Counter counter, string marker) : ISqlConnectionFactory
    {
        public Task<System.Data.Common.DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            if (options.ConnectionString?.Contains(marker, StringComparison.Ordinal) == true || options.WriteConnectionString?.Contains(marker, StringComparison.Ordinal) == true)
            {
                counter.Increment();
            }

            return inner.CreateOpenConnectionAsync(options, ct);
        }
    }

    public sealed class Fixture : IDisposable
    {
        private readonly WebApplicationFactory<Program> _root = new();

        public Fixture()
        {
            SourceConnection = PostgresDataSource.Create("wtr", $"CREATE TABLE {Table} (id INTEGER PRIMARY KEY, name TEXT NOT NULL, tenant_id TEXT NOT NULL); INSERT INTO {Table} VALUES (1,'row-one','default'),(2,'row-two','default');");
            var marker = new NpgsqlConnectionStringBuilder(SourceConnection).Database!;
            var connections = Connections;
            Factory = _root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Tenancy:DefaultTenantId", "default");
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxTokensCapacity", "1000000");
                b.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:TokenRefillRatePerSecond", "100000");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "5000");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("WriteTransport"));
                b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
                b.UseSetting("Gateway:DataSources:Connections:" + Source + ":Provider", "PostgreSql");
                b.UseSetting("Gateway:DataSources:Connections:" + Source + ":DefaultSchema", "main");
                b.UseSetting("Gateway:DataSources:Connections:" + Source + ":ConnectionString", SourceConnection);
                b.UseSetting("Gateway:DataSources:Connections:" + Source + ":WriteConnectionString", SourceConnection);
                b.UseSetting("Gateway:WebSql:Enabled", "true");
                b.UseSetting("Gateway:WebSql:AllowDml", "true");
                b.UseSetting("Gateway:DataSources:RequireTenantColumn", "true");
                b.UseSetting("Gateway:WebSql:DefaultDataSourceName", Source);
                b.UseSetting("Gateway:WebSql:AllowedDataSources:0", Source);
                b.UseSetting("Gateway:OData:Enabled", "true");
                b.UseSetting("Gateway:Mcp:Enabled", "true");
                b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
                b.ConfigureServices(services =>
                {
                    var original = services.Single(d => d.ServiceType == typeof(ISqlConnectionFactory));
                    services.Remove(original);
                    services.AddSingleton<ISqlConnectionFactory>(sp =>
                        new CountingFactory(
                            (ISqlConnectionFactory)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!),
                            connections,
                            marker));
                });
            });
        }

        public WebApplicationFactory<Program> Factory { get; }

        public string SourceConnection { get; }

        internal Counter Connections { get; } = new();

        internal bool Seeded { get; set; }

        public void Dispose()
        {
            Factory.Dispose();
            _root.Dispose();
            NpgsqlConnection.ClearAllPools();
        }
    }
}
