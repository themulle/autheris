namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Apache.Arrow;
using Table = Autheris.Domain.Model.Table;
using Apache.Arrow.Ipc;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 0 (docs/plans/2026-10-08-umsetzungsplan-virtuelle-filter.md): the same correlated consent row
/// filter (the "David case": only rows of clients whose crane is not delivered yet) must yield the same rows in every
/// transport. Real consents (no consent bypass), real SQLite data. The expected set is computed directly in SQLite.
/// ReBAC is disabled: it is an independent gate and not what this test compares.
/// Run: dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~RowFilterChannelParityTests"
/// </summary>
public sealed class RowFilterChannelParityTests : IClassFixture<RowFilterChannelParityTests.Fixture>
{
    private readonly Fixture _fixture;

    public RowFilterChannelParityTests(Fixture fixture) => _fixture = fixture;

    [Fact]
    public void Baseline_ExpectedSet_IsTheNotDeliveredClients()
    {
        // Clients 3, 4, 5 have undelivered cranes; 1 and 2 delivered; 6 has no crane; one row has no client at all.
        _fixture.ExpectedIds.ShouldBe([5L, 6L, 7L, 8L, 9L, 10L], ignoreOrder: true);
    }

    [Theory]
    [InlineData("websql")]
    [InlineData("trino")]
    [InlineData("sql-endpoint")]
    [InlineData("odata")]
    [InlineData("graphql")]
    [InlineData("graphql-tree")]
    [InlineData("mcp-sample-rows")]
    [InlineData("mcp-query-graphql")]
    [InlineData("arrow-export")]
    [InlineData("flight-sql")]
    [InlineData("olap")]
    public async Task Channel_ReturnsExactlyTheRowsOfTheConsentFilter(string channel)
    {
        var ids = await ChannelMatrix.Channels[channel](_fixture);

        ids.ShouldBe(_fixture.ExpectedIds, ignoreOrder: true, customMessage: $"channel '{channel}'");
    }

    [Fact]
    public async Task GraphQl_E2E_SQLite_Air1_ReturnsSameRowsAsODataAndWebSql_UnderTwoSeconds()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var webSqlIds = await ChannelMatrix.Channels["websql"](_fixture);
        var oDataIds = await ChannelMatrix.Channels["odata"](_fixture);
        var graphQlIds = await ChannelMatrix.Channels["graphql"](_fixture);

        sw.Stop();

        graphQlIds.ShouldBe(webSqlIds);
        graphQlIds.ShouldBe(oDataIds);
        graphQlIds.ShouldBe(_fixture.ExpectedIds);
        sw.ElapsedMilliseconds.ShouldBeLessThan(2000, $"Total execution took {sw.ElapsedMilliseconds} ms, expected under 2000 ms");
    }

    /// <summary>One delegate per transport; each returns the <c>air1.id</c> values the caller received.</summary>
    public static class ChannelMatrix
    {
        public static readonly IReadOnlyDictionary<string, Func<Fixture, Task<IReadOnlySet<long>>>> Channels =
            new Dictionary<string, Func<Fixture, Task<IReadOnlySet<long>>>>(StringComparer.Ordinal)
            {
                ["websql"] = WebSqlAsync,
                ["trino"] = TrinoAsync,
                ["sql-endpoint"] = SqlEndpointAsync,
                ["odata"] = ODataAsync,
                ["graphql"] = GraphQlAsync,
                ["graphql-tree"] = GraphQlTreeAsync,
                ["mcp-sample-rows"] = McpSampleRowsAsync,
                ["mcp-query-graphql"] = McpQueryGraphQlAsync,
                ["arrow-export"] = ArrowExportAsync,
                ["flight-sql"] = FlightSqlAsync,
                ["olap"] = OlapAsync
            };

        private static async Task<IReadOnlySet<long>> WebSqlAsync(Fixture f)
        {
            var r = await f.Client().PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT id FROM default.main.air1" });
            using var doc = await JsonOrFailAsync(r);
            return IdsOf(doc.RootElement.GetProperty("rows"), doc.RootElement.GetProperty("columns"));
        }

        private static async Task<IReadOnlySet<long>> TrinoAsync(Fixture f)
        {
            var c = f.Client();
            c.DefaultRequestHeaders.Add("X-Trino-User", "test-user");
            c.DefaultRequestHeaders.Add("X-Trino-Wait-Timeout", "30s");
            var r = await c.PostAsync("/v1/statement", new StringContent("SELECT id FROM default.main.air1", Encoding.UTF8, "text/plain"));
            using var doc = await JsonOrFailAsync(r);
            var root = doc.RootElement;
            root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FINISHED", root.ToString());
            var columns = root.GetProperty("columns").EnumerateArray().Select(col => col.GetProperty("name").GetString()).ToList();
            int idIndex = columns.IndexOf("id");
            return root.TryGetProperty("data", out var data)
                ? data.EnumerateArray().Select(row => row[idIndex].GetInt64()).ToHashSet()
                : new HashSet<long>();
        }

        private static async Task<IReadOnlySet<long>> SqlEndpointAsync(Fixture f)
        {
            var r = await f.Client().GetAsync("/api/v1/queries/air1_ids");
            using var doc = await JsonOrFailAsync(r);
            return IdsOf(doc.RootElement.GetProperty("rows"), doc.RootElement.GetProperty("columns"));
        }

        private static async Task<IReadOnlySet<long>> ODataAsync(Fixture f)
        {
            var r = await f.Client().GetAsync("/odata/v4/default/main/air1?$select=id&$top=1000");
            using var doc = await JsonOrFailAsync(r);
            return IdsOf(doc.RootElement.GetProperty("value"), columns: null);
        }

        private static async Task<IReadOnlySet<long>> GraphQlAsync(Fixture f)
        {
            using var doc = await f.GraphQlAsync("{ default_main_air1(first: 20) { id } }");
            return IdsOf(doc.RootElement.GetProperty("data").GetProperty("default_main_air1"), columns: null);
        }

        private static async Task<IReadOnlySet<long>> GraphQlTreeAsync(Fixture f)
        {
            // Nested child list under the (unfiltered) client table: the child rows must carry the air1 row filter.
            using var doc = await f.GraphQlAsync("{ default_main_client(first: 6) { client_id measurements(first: 2) { id } } }");
            return doc.RootElement.GetProperty("data").GetProperty("default_main_client").EnumerateArray()
                .SelectMany(client => client.GetProperty("measurements").EnumerateArray())
                .Select(row => row.GetProperty("id").GetInt64())
                .ToHashSet();
        }

        private static async Task<IReadOnlySet<long>> McpSampleRowsAsync(Fixture f)
        {
            await using var mcp = await McpTestClient.ConnectAsync(f.Client());
            var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "sample_rows", new { dataset = "default.main.air1", count = 20 });
            isError.ShouldBeFalse(payload.ToString());
            return IdsOf(FindArray(payload, "rows") ?? throw new ShouldAssertException($"no rows in {payload}"), columns: null);
        }

        private static async Task<IReadOnlySet<long>> McpQueryGraphQlAsync(Fixture f)
        {
            await using var mcp = await McpTestClient.ConnectAsync(f.Client());
            var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_graphql", new { query = "{ default_main_air1(first: 20) { id } }" });
            isError.ShouldBeFalse(payload.ToString());
            return IdsOf(payload.GetProperty("data").GetProperty("default_main_air1"), columns: null);
        }

        private static async Task<IReadOnlySet<long>> ArrowExportAsync(Fixture f)
        {
            var r = await f.Client().PostAsync("/api/v1/export/arrow?table=default.main.air1", content: null);
            if (r.StatusCode != HttpStatusCode.OK)
            {
                throw new ShouldAssertException($"HTTP {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
            }

            return await ArrowIdsAsync(await r.Content.ReadAsStreamAsync());
        }

        private static async Task<IReadOnlySet<long>> FlightSqlAsync(Fixture f)
        {
            var c = f.Client();
            var info = await c.PostAsJsonAsync("/api/v1/flight/sql/info", new { query = "SELECT id FROM default.main.air1" });
            using var infoDoc = await JsonOrFailAsync(info);
            var ticket = infoDoc.RootElement.GetProperty("ticket").GetRawText();

            var stream = await c.PostAsync("/api/v1/flight/sql/stream", new StringContent(ticket, Encoding.UTF8, "application/json"));
            if (stream.StatusCode != HttpStatusCode.OK)
            {
                throw new ShouldAssertException($"HTTP {(int)stream.StatusCode}: {await stream.Content.ReadAsStringAsync()}");
            }

            return await ArrowIdsAsync(await stream.Content.ReadAsStreamAsync());
        }

        private static async Task<IReadOnlySet<long>> OlapAsync(Fixture f)
        {
            var r = await f.Client().PostAsJsonAsync("/api/v1/olap/query", new { sql = "SELECT id FROM air1", tableNames = new[] { "default.main.air1" } });
            using var doc = await JsonOrFailAsync(r);
            return IdsOf(doc.RootElement.GetProperty("rows"), doc.RootElement.GetProperty("columns"));
        }

        private static async Task<JsonDocument> JsonOrFailAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new ShouldAssertException($"HTTP {(int)response.StatusCode}: {body}");
            }

            return JsonDocument.Parse(body);
        }

        /// <summary>Rows as objects (property "id") or as arrays (position of "id" in <paramref name="columns"/>).</summary>
        private static IReadOnlySet<long> IdsOf(JsonElement rows, JsonElement? columns)
        {
            var ids = new HashSet<long>();
            int idIndex = columns is { ValueKind: JsonValueKind.Array } cols
                ? cols.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetProperty("name").GetString()).ToList().IndexOf("id")
                : -1;
            foreach (var row in rows.EnumerateArray())
            {
                var value = row.ValueKind == JsonValueKind.Array ? row[idIndex] : row.GetProperty("id");
                ids.Add(value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : value.GetInt64());
            }

            return ids;
        }

        private static JsonElement? FindArray(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.Array)
                {
                    return property.Value;
                }

                if (FindArray(property.Value, name) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }

        private static async Task<IReadOnlySet<long>> ArrowIdsAsync(Stream body)
        {
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer);
            buffer.Position = 0;
            var ids = new HashSet<long>();
            using var reader = new ArrowStreamReader(buffer);
            while (await reader.ReadNextRecordBatchAsync() is { } batch)
            {
                using (batch)
                {
                    var column = batch.Column("id");
                    for (int i = 0; i < batch.Length; i++)
                    {
                        ids.Add(column switch
                        {
                            Int64Array a => a.GetValue(i)!.Value,
                            Int32Array a => a.GetValue(i)!.Value,
                            StringArray a => long.Parse(a.GetString(i), System.Globalization.CultureInfo.InvariantCulture),
                            _ => throw new ShouldAssertException($"unexpected Arrow type {column.GetType().Name} for id")
                        });
                    }
                }
            }

            return ids;
        }
    }

    public class Fixture : IAsyncLifetime
    {
        /// <summary>Virtual filters, phase 5: the David case as an access profile instead of a consent row filter.</summary>
        protected virtual bool UseVirtualFilters => false;

        /// <summary>Virtual filters, phase 7: the same filter written as a sql definition (predicate with target).</summary>
        protected virtual bool UseSqlDefinition => false;

        public const string DavidSid = "S-1-5-21-LWE-DAVID";
        public const string Tenant = "tenant_parity";

        private static readonly TableIdentifier Air1 = new("default", "main", "air1");
        private static readonly TableIdentifier ClientTable = new("default", "main", "client");
        private static readonly TableIdentifier Crane = new("default", "main", "crane");

        private readonly string _workDir = Path.Combine(Path.GetTempPath(), "autheris-parity-" + Guid.NewGuid().ToString("N"));
        private WebApplicationFactory<Program>? _factory;

        public IReadOnlySet<long> ExpectedIds { get; private set; } = new HashSet<long>();

        public async Task InitializeAsync()
        {
            var queriesDir = Path.Combine(_workDir, "queries");
            Directory.CreateDirectory(queriesDir);
            var dbPath = Path.Combine(_workDir, "data.db");
            SeedData(dbPath);
            ExpectedIds = ComputeExpected(dbPath);

            File.WriteAllText(Path.Combine(queriesDir, "air1_ids.sql"),
                "-- @name air1_ids\n-- @datasource default\n-- @summary air1 ids\n-- @method GET\n\nSELECT id FROM air1");

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
                b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-RowFilterParity-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
                b.UseSetting("Gateway:Rebac:Enabled", "false");

                b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
                b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={dbPath}");

                b.UseSetting("Gateway:WebSql:Enabled", "true");
                b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");

                b.UseSetting("Gateway:SqlEndpoints:Enabled", "true");
                b.UseSetting("Gateway:SqlEndpoints:Directory", queriesDir);
                b.UseSetting("Gateway:SqlEndpoints:EnableHotReload", "false");
                b.UseSetting("Gateway:SqlEndpoints:AutoSyncFromDbt", "false");

                b.UseSetting("Gateway:Mcp:Enabled", "true");
                b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            });

            await SeedGovernanceAsync();
            await RefreshGraphQlSchemaAsync();
        }

        public Task DisposeAsync()
        {
            _factory?.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
            return Task.CompletedTask;
        }

        public HttpClient Client()
        {
            var client = _factory!.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", DavidSid);
            client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
            client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
            return client;
        }

        public async Task<JsonDocument> GraphQlAsync(string query)
        {
            var c = Client();
            c.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
            for (int attempt = 0; ; attempt++)
            {
                var r = await c.PostAsJsonAsync("/graphql", new { query });
                var body = await r.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(body);

                // HotChocolate swaps the rebuilt schema in asynchronously.
                if (body.Contains("does not exist", StringComparison.Ordinal) && attempt < 50)
                {
                    doc.Dispose();
                    await Task.Delay(200);
                    continue;
                }

                if (r.StatusCode != HttpStatusCode.OK || doc.RootElement.TryGetProperty("errors", out _))
                {
                    throw new ShouldAssertException($"HTTP {(int)r.StatusCode}: {body}");
                }

                return doc;
            }
        }

        private static void SeedData(string path)
        {
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE crane (serial_number TEXT PRIMARY KEY, is_delivered INTEGER);
CREATE TABLE client (client_id INTEGER PRIMARY KEY, crane_serial_number TEXT);
CREATE TABLE air1 (id INTEGER PRIMARY KEY, client_id INTEGER, value REAL);
INSERT INTO crane VALUES ('C1', 1), ('C2', 1), ('C3', NULL), ('C4', NULL), ('C5', NULL);
INSERT INTO client VALUES (1, 'C1'), (2, 'C2'), (3, 'C3'), (4, 'C4'), (5, 'C5'), (6, 'NO-CRANE');
INSERT INTO air1 VALUES
  (1, 1, 1.0), (2, 1, 1.5), (3, 2, 2.0), (4, 2, 2.5),
  (5, 3, 3.0), (6, 3, 3.5), (7, 4, 4.0), (8, 4, 4.5), (9, 5, 5.0), (10, 5, 5.5),
  (11, 6, 6.0), (12, 6, 6.5), (13, NULL, 7.0);";
            cmd.ExecuteNonQuery();
        }

        private static HashSet<long> ComputeExpected(string path)
        {
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT a.id FROM air1 a
WHERE EXISTS (SELECT 1 FROM client c JOIN crane k ON k.serial_number = c.crane_serial_number
              WHERE c.client_id = a.client_id AND k.is_delivered IS NULL)";
            var ids = new HashSet<long>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }

            return ids;
        }

        private async Task SeedGovernanceAsync()
        {
            using var scope = _factory!.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

            async Task<TableMetadata> RegisterAsync(TableIdentifier id, params (string Name, string Type)[] columns)
            {
                await repo.UpsertTableMetadataAsync(new TableMetadata
                {
                    Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "Sqlite", SourceName = "default" },
                    Identifier = id,
                    Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList()
                });
                return (await repo.GetTableMetadataAsync(id))!;
            }

            var air1 = await RegisterAsync(Air1, ("id", "integer"), ("client_id", "integer"), ("value", "real"));
            var client = await RegisterAsync(ClientTable, ("client_id", "integer"), ("crane_serial_number", "varchar"));
            await RegisterAsync(Crane, ("serial_number", "varchar"), ("is_delivered", "integer"));

            if (UseVirtualFilters)
            {
                await SeedVirtualFiltersAsync(repo, air1, client);
            }
            else
            {
                await SeedConsentRowFilterAsync(repo, air1, client);
            }

            var relations = scope.ServiceProvider.GetRequiredService<ITableRelationRepository>();
            await relations.CreateRelationAsync(new TableRelation
            {
                ParentTableId = client.Table.Id,
                ParentTableIdentifier = ClientTable,
                ChildTableId = air1.Table.Id,
                ChildTableIdentifier = Air1,
                RelationName = "measurements",
                JoinKeysParent = ["client_id"],
                JoinKeysChild = ["client_id"],
                Cardinality = RelationCardinality.OneToMany
            });
        }

        /// <summary>
        /// Phase 5: unrestricted consents (two on air1: the second is the attempt to lift the filter) plus a profile that
        /// binds the David filter to every object with client_id; crane has no client_id and is uncovered (deny).
        /// </summary>
        private async Task SeedVirtualFiltersAsync(IGovernanceRepository repo, TableMetadata air1, TableMetadata client)
        {
            await repo.CreateConsentAsync(Consent(air1, Air1, []));
            await repo.CreateConsentAsync(Consent(air1, Air1, []));
            await repo.CreateConsentAsync(Consent(client, ClientTable, []));
            var crane = (await repo.GetTableMetadataAsync(Crane))!;
            await repo.CreateConsentAsync(Consent(crane, Crane, []));

            using var scope = _factory!.Services.CreateScope();
            var admin = scope.ServiceProvider.GetRequiredService<Autheris.Application.VirtualFilters.VirtualFilterAdministrationService>();
            var actor = new Autheris.Application.VirtualFilters.VirtualFilterActor(new Sid("S-1-5-21-FILTER-ADMIN"), IsSync: false);
            await admin.SaveFilterAsync(UseSqlDefinition
                ? new VirtualFilter
                {
                    TenantId = new TenantId(Tenant),
                    Name = "nicht_ausgelieferte_krane",
                    Source = "default",
                    Sql = """
                        from main.client client
                        join main.crane crane on crane.serial_number = client.crane_serial_number
                        where crane.is_delivered is null and target.client_id = client.client_id
                        """
                }
                : new VirtualFilter
                {
                    TenantId = new TenantId(Tenant),
                    Name = "nicht_ausgelieferte_krane",
                    Source = "default",
                    KeyColumns = ["client.client_id"],
                    Structured = new StructuredFilterDefinition
                    {
                        From = ClientTable,
                        FromAlias = "client",
                        Joins = [new FilterJoin(Crane, "crane", "crane.serial_number", "client.crane_serial_number")],
                        Where = [new FilterCondition("crane.is_delivered", FilterConditionOperator.IsNull)]
                    }
                }, actor);
            await admin.SaveProfileAsync(new VirtualFilterAccessProfile
            {
                TenantId = new TenantId(Tenant),
                Name = "david",
                GranteeType = GranteeType.User,
                GranteeSid = new Sid(DavidSid),
                Scope = "default.main.*",
                Uncovered = UncoveredPolicy.Deny,
                Bindings = [new FilterBinding { FilterName = "nicht_ausgelieferte_krane", TargetPattern = "default.*.*.client_id" }]
            }, actor);
        }

        private static async Task SeedConsentRowFilterAsync(IGovernanceRepository repo, TableMetadata air1, TableMetadata client)
        {
            // The David case as a consent row filter: air1 rows of clients whose crane is not delivered yet.
            await repo.CreateConsentAsync(Consent(air1, Air1,
            [
                new ConsentRowFilter
                {
                    FilterType = RowFilterType.SubqueryCorrelated,
                    DependentTable = ClientTable,
                    DependentTableAlias = "client",
                    PrimaryKeyColumn = "client_id",
                    ForeignKeyColumn = "client_id",
                    AdditionalHops =
                    [
                        new SubqueryJoinHop { Table = Crane, TableAlias = "crane", LeftJoinColumn = "crane.serial_number", RightJoinColumn = "client.crane_serial_number" }
                    ],
                    SubqueryFilterPredicateJson = "{\"crane.is_delivered\": null}"
                }
            ]));

            // The client table is readable without filter: it is the parent of the nested GraphQL query.
            await repo.CreateConsentAsync(Consent(client, ClientTable, []));
        }

        private static Consent Consent(TableMetadata meta, TableIdentifier id, IReadOnlyList<ConsentRowFilter> rowFilters) => new()
        {
            TableId = meta.Table.Id,
            TableIdentifier = id,
            TenantId = new TenantId(Tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(DavidSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = meta.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList(),
            RowFilters = rowFilters
        };

        /// <summary>The catalog was written after start-up; rebuild the GraphQL schema now instead of waiting for the refresh timer.</summary>
        private async Task RefreshGraphQlSchemaAsync()
        {
            var module = _factory!.Services.GetRequiredService<Autheris.GraphQL.Catalog.CatalogGraphQlTypeModule>();
            var check = typeof(Autheris.GraphQL.Catalog.CatalogGraphQlTypeModule).GetMethod(
                "CheckForCatalogChangesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task<bool>)check.Invoke(module, [System.Threading.CancellationToken.None])!;
        }
    }
}
