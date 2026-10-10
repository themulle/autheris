namespace Autheris.Tests.Integration;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Table = Autheris.Domain.Model.Table;
using Xunit;

/// <summary>How the mock CRM web-api treats the filter arguments it receives.</summary>
public enum CrmApiMode
{
    /// <summary>Applies ids / path key / body array when present (a well-behaved upstream).</summary>
    HonorFilters,

    /// <summary>Ignores every filter argument and always answers with all rows (the upstream cannot be trusted).</summary>
    IgnoreFilters
}

/// <summary>One request the mock CRM web-api received.</summary>
public sealed record CrmRequest(string Method, string PathAndQuery, string? Body)
{
    /// <summary>The decoded path without the query string (the gateway adds its own <c>limit</c> argument to the query).</summary>
    public string Path => PathAndQuery.Split('?')[0];

    /// <summary>The decoded query arguments, without the gateway's own <c>limit</c>.</summary>
    public IReadOnlyDictionary<string, string> Query =>
        PathAndQuery.Contains('?')
            ? PathAndQuery[(PathAndQuery.IndexOf('?') + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).Where(p => p[0] != "limit")
                .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : string.Empty)
            : new Dictionary<string, string>();
}

/// <summary>
/// F-GOV-14 (WI-12): the mock CRM web-api. It records every request (method, decoded path and query, body) so that
/// tests can assert what the gateway sent upstream, and it can be switched to ignore all filter arguments.
/// </summary>
public sealed class CountingCrmApiHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<CrmRequest> _requests = new();

    public CrmApiMode Mode { get; set; } = CrmApiMode.HonorFilters;

    public IReadOnlyList<CrmRequest> Requests => _requests.ToArray();

    public void Reset()
    {
        _requests.Clear();
        Mode = CrmApiMode.HonorFilters;
    }

    private static readonly (int Id, string Name, string Tenant)[] Customers =
        Enumerable.Range(101, 10).Select(i => (i, "Customer " + i, i % 2 == 1 ? "T1" : "T2")).ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;
        _requests.Enqueue(new CrmRequest(request.Method.Method, Uri.UnescapeDataString(uri.PathAndQuery), body));
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var path = uri.AbsolutePath;
        bool honor = Mode == CrmApiMode.HonorFilters;

        IEnumerable<object> rows;
        if (path == "/customers_mixed")
        {
            // E2E-4: ids arrive as numbers and as text, with a wrong tenant for some.
            rows =
            [
                Row(101L, "A", "T1"), Row("102", "B", "T1"), Row(102L, "C", "T2"), Row(105L, "D", "T2"),
                Row("105", "E", "T1"), Row(103L, "F", "T1"), Row("101", "G", "T1")
            ];
        }
        else if (path.StartsWith("/api/v1/customers/", StringComparison.Ordinal))
        {
            var key = Uri.UnescapeDataString(path["/api/v1/customers/".Length..]);
            rows = honor && int.TryParse(key, out var id)
                ? All().Where(c => c.Id == id).Select(c => Row(c.Id, c.Name, c.Tenant))
                : All().Select(c => Row(c.Id, c.Name, c.Tenant));
        }
        else if (path == "/customers_paged")
        {
            int page = int.Parse(query["page"] ?? "1");
            int size = int.Parse(query["size"] ?? "4");
            rows = All().Skip((page - 1) * size).Take(size).Select(c => Row(c.Id, c.Name, c.Tenant));
        }
        else if (path is "/customers_none" or "/customers_qpl" or "/customers_json/batch" or "/customers_odata")
        {
            var wanted = new List<string>();
            if (honor && query["ids"] is { Length: > 0 } ids)
            {
                wanted.AddRange(ids.Split(','));
            }

            if (honor && body is { Length: > 0 })
            {
                wanted.AddRange(JsonSerializer.Deserialize<string[]>(body)!);
            }

            rows = All().Where(c => wanted.Count == 0 || wanted.Contains(c.Id.ToString())).Select(c => Row(c.Id, c.Name, c.Tenant));
        }
        else
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(rows), Encoding.UTF8, "application/json")
        };
    }

    private static (int Id, string Name, string Tenant)[] All() => Customers;

    private static object Row(object id, string name, string tenant) => new Dictionary<string, object> { ["id"] = id, ["name"] = name, ["tenant_code"] = tenant };
}

[CollectionDefinition(Name)]
public sealed class FederatedVirtualFilterCollection : ICollectionFixture<FederatedVirtualFilterWebApiFixture>
{
    public const string Name = "FederatedVirtualFilterWebApi";
}

/// <summary>
/// F-GOV-14 (WI-12): the whole gateway (real WebSQL endpoint, consent policy, Virtual Filter resolver and planner,
/// staging, DuckDB, SQLite key source and audit store) over a mock CRM web-api. The key source is a SQLite file
/// (<c>authz.main.client_authorization</c>: duplicates, NULL keys, inactive rows); the web-api tables differ in the
/// batch capability of their endpoint descriptor. Tests share the instance and must not run in parallel.
/// </summary>
public sealed class FederatedVirtualFilterWebApiFixture : IAsyncLifetime
{
    public const string AliceSid = "S-1-5-21-VF-ALICE";
    public const string TenantValue = "tenant_vf";
    public const string ProfileName = "alice";

    public static readonly TenantId Tenant = new(TenantValue);
    public static readonly TableIdentifier ClientAuthorization = new("authz", "main", "client_authorization");
    public static readonly TableIdentifier ClientAuthorizationDown = new("authz_down", "main", "client_authorization");
    public static readonly TableIdentifier Orders = new("erp", "main", "orders");

    /// <summary>Names of the web-api tables (all in <c>crm_api.public</c>).</summary>
    public static class Tables
    {
        public const string None = "customers_none";
        public const string QueryParameterList = "customers_qpl";
        public const string JsonBody = "customers_json";
        public const string Path = "customers_path";
        public const string Paged = "customers_paged";
        public const string Mixed = "customers_mixed";
        public const string ODataFormat = "customers_odata";

        /// <summary>A table with a consent row filter (<c>tenant_code = 'T1'</c>) and no Virtual Filter.</summary>
        public const string Filtered = "customers_filtered";
    }

    public CountingCrmApiHandler Crm { get; } = new();

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "autheris-fvf-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    public IServiceProvider Services => _factory!.Services;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_workDir);
        var authz = Path.Combine(_workDir, "authz.db");
        var erp = Path.Combine(_workDir, "erp.db");
        SeedAuthorization(authz);
        SeedErp(erp);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "10000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-FederatedVf-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:Rebac:Enabled", "false");
            b.UseSetting("Gateway:VirtualFilters:GenerationCheckSeconds", "0");
            b.UseSetting("Gateway:VirtualFilters:Federation:KeyCacheTtlSeconds", "1");

            b.UseSetting("Gateway:DataSources:Connections:erp:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:erp:ConnectionString", $"Data Source={erp}");
            b.UseSetting("Gateway:DataSources:Connections:authz:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:authz:ConnectionString", $"Data Source={authz}");
            b.UseSetting("Gateway:DataSources:Connections:authz_down:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:authz_down:ConnectionString", $"Data Source={Path.Combine(_workDir, "missing-dir", "authz.db")};Mode=ReadOnly");

            b.UseSetting("Gateway:WebSql:Enabled", "true");
            b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "erp");
            b.UseSetting("Gateway:WebSql:AllowedDataSources:0", "crm_api");
            b.UseSetting("Gateway:WebSql:CrossSource:Enabled", "true");

            b.ConfigureServices(services =>
            {
                var client = new HttpClient(Crm);
                var factory = Substitute.For<IHttpClientFactory>();
                factory.CreateClient(Arg.Any<string>()).Returns(client);
                services.AddSingleton(factory);
            });
        });

        await SeedGovernanceAsync();
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
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", AliceSid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", TenantValue);
        return client;
    }

    /// <summary>Runs a WebSQL statement as Alice; the result is the status code and the parsed body.</summary>
    public async Task<(HttpStatusCode Status, JsonDocument Body)> SqlAsync(string sql)
    {
        using var response = await Client().PostAsJsonAsync("/api/v1/sql", new { sql });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text.Length == 0 ? "{}" : text));
    }

    /// <summary>The values of one column of a successful result (numbers and text are compared as text).</summary>
    public async Task<List<string>> ColumnAsync(string sql, string column = "id")
    {
        var (status, body) = await SqlAsync(sql);
        using (body)
        {
            if (status != HttpStatusCode.OK)
            {
                throw new Shouldly.ShouldAssertException($"HTTP {(int)status}: {body.RootElement}");
            }

            return RowsOf(body.RootElement)
                .Select(r => r[column]?.ToString() ?? "NULL")
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>Rows of a WebSQL result as dictionaries (the result carries columns and array rows).</summary>
    public static List<Dictionary<string, object?>> RowsOf(JsonElement root)
    {
        var columns = root.GetProperty("columns").EnumerateArray()
            .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString()! : c.GetProperty("name").GetString()!)
            .ToList();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < columns.Count; i++)
            {
                var cell = row.ValueKind == JsonValueKind.Array ? row[i] : row.GetProperty(columns[i]);
                values[columns[i]] = cell.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => cell.GetString(),
                    JsonValueKind.Number => cell.GetRawText(),
                    _ => cell.GetRawText()
                };
            }

            rows.Add(values);
        }

        return rows;
    }

    public static List<string> ColumnNames(JsonElement root) =>
        root.GetProperty("columns").EnumerateArray()
            .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString()! : c.GetProperty("name").GetString()!)
            .ToList();

    /// <summary>Resets what tests change: the recorded upstream requests, the upstream mode and the profile of Alice.</summary>
    public async Task ResetAsync()
    {
        Crm.Reset();
        using var scope = Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IVirtualFilterRepository>();
        await repo.ApplyAsync(new VirtualFilterChangeSet { DeleteProfiles = [(Tenant, ProfileName)] });
    }

    /// <summary>Replaces Alice's profile (scope <c>crm_api.public.*</c>, uncovered tables denied) with these bindings.</summary>
    public async Task UseBindingsAsync(params FilterBinding[] bindings) => await WriteProfileAsync(bindings, validate: true);

    /// <summary>Writes the profile straight into the repository, bypassing the write-time validation (a stored, broken profile).</summary>
    public async Task UseStoredBindingsAsync(params FilterBinding[] bindings) => await WriteProfileAsync(bindings, validate: false);

    private async Task WriteProfileAsync(FilterBinding[] bindings, bool validate)
    {
        var profile = new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = ProfileName,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(AliceSid),
            Scope = "crm_api.public.*",
            Uncovered = UncoveredPolicy.Deny,
            Bindings = bindings
        };
        using var scope = Services.CreateScope();
        if (validate)
        {
            var admin = scope.ServiceProvider.GetRequiredService<VirtualFilterAdministrationService>();
            await admin.SaveProfileAsync(profile, new VirtualFilterActor(new Sid("S-1-5-21-FILTER-ADMIN"), IsSync: false));
        }
        else
        {
            var repo = scope.ServiceProvider.GetRequiredService<IVirtualFilterRepository>();
            await repo.ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [profile] });
        }
    }

    /// <summary>A binding of <paramref name="filter"/> on one web-api table (exact name, regex pattern as in the PRD).</summary>
    public static FilterBinding Binding(
        string table,
        string filter = "vf_clients",
        VirtualFilterExecutionStrategy strategy = VirtualFilterExecutionStrategy.Adaptive,
        PushdownParameterFormat? format = null,
        int maxKeys = 100,
        IReadOnlyDictionary<string, string>? columnMap = null,
        string? pattern = null) => new()
    {
        FilterName = filter,
        TargetPattern = pattern ?? $@"regex:^crm_api\.public\.{table}$",
        ColumnMap = columnMap ?? (filter == "vf_clients_tenant"
            ? new Dictionary<string, string> { ["client_id"] = "id", ["tenant_code"] = "tenant_code" }
            : new Dictionary<string, string> { ["client_id"] = "id" }),
        Strategy = strategy,
        PushdownFormat = format,
        MaxPushdownKeys = maxKeys
    };

    /// <summary>The audit records written since <paramref name="since"/> for the tenant (newest first as stored).</summary>
    public async Task<List<AuditLogEntry>> AuditSinceAsync(DateTimeOffset since)
    {
        using var scope = Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        return (await audit.GetAuditLogEntriesAsync(2000, Tenant)).Where(e => e.OccurredAt >= since).OrderBy(e => e.OccurredAt).ToList();
    }

    private static void SeedAuthorization(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE client_authorization (client_id INTEGER, tenant_code TEXT, is_active INTEGER);
INSERT INTO client_authorization VALUES
  (101, 'T1', 1), (101, 'T1', 1),      -- duplicate
  (102, 'T1', 1),
  (105, 'T2', 1),
  (103, 'T1', 0),                       -- inactive
  (104, 'T2', 0),                       -- inactive
  (NULL, 'T1', 1);                      -- NULL key";
        cmd.ExecuteNonQuery();
    }

    private static void SeedErp(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER);
INSERT INTO orders VALUES (1, 101), (2, 102), (3, 103), (4, 105), (5, 999);";
        cmd.ExecuteNonQuery();
    }

    private async Task SeedGovernanceAsync()
    {
        using var scope = Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        async Task<TableMetadata> RegisterAsync(TableIdentifier id, DataSourceType type, HttpEndpointDescriptor? endpoint, string source, params (string Name, string Type)[] columns)
        {
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table
                {
                    Id = Guid.NewGuid(),
                    DisplayName = id.TableName,
                    TableName = id.TableName,
                    SchemaName = id.Schema,
                    SourceType = type == DataSourceType.HttpDeclarative ? "Http" : "Sqlite",
                    SourceName = source,
                    DataSourceType = type,
                    HttpEndpoint = endpoint,
                    IsActive = true
                },
                Identifier = id,
                Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList()
            });
            return (await repo.GetTableMetadataAsync(id))!;
        }

        var ordersMeta = await RegisterAsync(Orders, DataSourceType.Sql, null, "erp", ("id", "integer"), ("customer_id", "integer"));
        await RegisterAsync(ClientAuthorization, DataSourceType.Sql, null, "authz", ("client_id", "integer"), ("tenant_code", "varchar"), ("is_active", "integer"));
        await RegisterAsync(ClientAuthorizationDown, DataSourceType.Sql, null, "authz_down", ("client_id", "integer"), ("tenant_code", "varchar"), ("is_active", "integer"));

        var metas = new List<TableMetadata> { ordersMeta };
        foreach (var (name, descriptor) in Endpoints())
        {
            metas.Add(await RegisterAsync(
                new TableIdentifier("crm_api", "public", name), DataSourceType.HttpDeclarative, descriptor, "crm_api",
                ("id", "integer"), ("name", "varchar"), ("tenant_code", "varchar")));
        }

        foreach (var meta in metas)
        {
            await repo.CreateConsentAsync(new Consent
            {
                TableId = meta.Table.Id,
                TableIdentifier = meta.Identifier,
                TenantId = Tenant,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = new Sid(AliceSid),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules = meta.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList(),
                RowFilters = meta.Identifier.TableName == Tables.Filtered
                    ? [new ConsentRowFilter { ColumnName = "tenant_code", Operator = "EQ", ValueType = "string", ValueJson = "\"T1\"", ValueSource = "LITERAL" }]
                    : []
            });
        }

        var admin = scope.ServiceProvider.GetRequiredService<VirtualFilterAdministrationService>();
        var actor = new VirtualFilterActor(new Sid("S-1-5-21-FILTER-ADMIN"), IsSync: false);
        await admin.SaveFilterAsync(KeyFilter("vf_clients", "authz", ClientAuthorization, ["ca.client_id"]), actor);
        await admin.SaveFilterAsync(KeyFilter("vf_clients_tenant", "authz", ClientAuthorization, ["ca.client_id", "ca.tenant_code"]), actor);
        await admin.SaveFilterAsync(KeyFilter("vf_clients_none", "authz", ClientAuthorization, ["ca.client_id"], activeValue: "2"), actor);
        await admin.SaveFilterAsync(KeyFilter("vf_clients_down", "authz_down", ClientAuthorizationDown, ["ca.client_id"]), actor);
    }

    private static VirtualFilter KeyFilter(string name, string source, TableIdentifier from, IReadOnlyList<string> keys, string activeValue = "1") => new()
    {
        TenantId = Tenant,
        Name = name,
        Source = source,
        KeyColumns = keys,
        Structured = new StructuredFilterDefinition
        {
            From = from,
            FromAlias = "ca",
            Where = [new FilterCondition("ca.is_active", FilterConditionOperator.Eq, activeValue)]
        }
    };

    private static IEnumerable<(string Name, HttpEndpointDescriptor Descriptor)> Endpoints()
    {
        HttpEndpointDescriptor Endpoint(string path, HttpBatchType batch, string? param = "ids", HttpPaginationConfig? paging = null) => new()
        {
            BaseUrl = "https://mock.crm.internal",
            PathTemplate = path,
            Method = "GET",
            CompleteResponse = true,
            PrimaryKeyField = "id",
            BatchType = batch,
            BatchParamName = param,
            Pagination = paging
        };

        yield return (Tables.None, Endpoint("/customers_none", HttpBatchType.None));
        yield return (Tables.QueryParameterList, Endpoint("/customers_qpl", HttpBatchType.QueryParameterList));
        yield return (Tables.JsonBody, Endpoint("/customers_json/batch", HttpBatchType.JsonBodyArray));
        yield return (Tables.Path, Endpoint("/api/v1/customers/{id}", HttpBatchType.ParallelSingleRequests));
        yield return (Tables.Paged, Endpoint("/customers_paged", HttpBatchType.QueryParameterList, paging: new HttpPaginationConfig(HttpPaginationStrategy.PageNumber, DefaultPageSize: 4)));
        yield return (Tables.Mixed, Endpoint("/customers_mixed", HttpBatchType.None));
        yield return (Tables.Filtered, Endpoint("/customers_none", HttpBatchType.None));
        yield return (Tables.ODataFormat, Endpoint("/customers_odata", HttpBatchType.QueryParameterList));
    }
}
