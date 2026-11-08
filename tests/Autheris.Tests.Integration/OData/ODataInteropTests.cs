namespace Autheris.Tests.Integration.OData;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

public sealed class ODataInteropTests : IClassFixture<ODataInteropTests.Fixture>
{
    private static readonly TableIdentifier OrdersTable = new("erp", "main", "orders");
    private static readonly Sid AnalystSid = new("S-1-5-21-ODATA-INTEROP");
    private const string Tenant = "erp";

    private readonly Fixture _fixture;
    private WebApplicationFactory<Program> _factory => _fixture.Factory;

    public ODataInteropTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class Fixture : IDisposable
    {
        private readonly WebApplicationFactory<Program> _root = new();
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"odata-interop-{Guid.NewGuid():N}.db");
        public WebApplicationFactory<Program> Factory { get; }
        public bool Seeded { get; set; }

        public Fixture()
        {
            SeedDatabase(_dbPath);

            var dbPath = _dbPath;
            Factory = _root.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Gateway:Tenancy:DefaultTenantId", Tenant);
                builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "5000");
                builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("Fixture"));
                builder.UseSetting("Gateway:DataSources:Connections:erp:Provider", "Sqlite");
                builder.UseSetting("Gateway:DataSources:Connections:erp:ConnectionString", $"Data Source={dbPath}");
                builder.UseSetting("Gateway:WebSql:DefaultDataSourceName", "erp");
                builder.UseSetting("Gateway:OData:Enabled", "true");
                builder.UseSetting("Gateway:Rebac:Enabled", "false");
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
        cmd.CommandText = """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                customer TEXT NOT NULL,
                region TEXT NOT NULL,
                amount INTEGER NOT NULL
            );
            INSERT INTO orders VALUES
                (1, 'Contoso', 'EU', 120),
                (2, 'Fabrikam', 'US', 80),
                (3, 'Northwind', 'EU', 250),
                (4, 'AdventureWorks', 'EU', 310),
                (5, 'Litware', 'APAC', 95);
            """;
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
                DisplayName = "orders",
                TableName = "orders",
                SchemaName = "main",
                SourceType = "Sqlite",
                SourceName = "erp",
                IsActive = true
            },
            Identifier = OrdersTable,
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "customer", DataType = "varchar" },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "integer" }
            ]
        });

        var meta = (await repo.GetTableMetadataAsync(OrdersTable))!;
        await repo.CreateConsentAsync(new Consent
        {
            TenantId = new TenantId(Tenant),
            TableId = meta.Table.Id,
            TableIdentifier = OrdersTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = AnalystSid,
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddHours(24),
            ColumnRules = meta.Columns
                .Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear })
                .ToList()
        });

        _fixture.Seeded = true;
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", AnalystSid.Value);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        return client;
    }

    [Fact]
    public async Task Service_document_lists_entity_sets()
    {
        await EnsureSeededAsync();
        using var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/odata/v4");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        response.Content.Headers.ContentType?.ToString().Replace(" ", "").ShouldStartWith("application/json;odata.metadata=minimal");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("@odata.context").GetString().ShouldBe("http://localhost/odata/v4/$metadata");
        var values = doc.RootElement.GetProperty("value").EnumerateArray().ToList();
        values.ShouldContain(e =>
            e.GetProperty("name").GetString() == "erp_main_orders" &&
            e.GetProperty("kind").GetString() == "EntitySet" &&
            e.GetProperty("url").GetString() == "erp/main/orders");
    }

    [Fact]
    public async Task Metadata_is_valid_csdl_v4()
    {
        await EnsureSeededAsync();
        using var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/odata/v4/$metadata");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/xml");

        var xml = await response.Content.ReadAsStringAsync();
        var xdoc = XDocument.Parse(xml);
        var edmxNs = XNamespace.Get("http://docs.oasis-open.org/odata/ns/edmx");
        var edmNs = XNamespace.Get("http://docs.oasis-open.org/odata/ns/edm");

        xdoc.Root.ShouldNotBeNull();
        xdoc.Root!.Name.ShouldBe(edmxNs + "Edmx");
        xdoc.Root.Attribute("Version")?.Value.ShouldBe("4.0");

        var entityType = xdoc.Descendants(edmNs + "EntityType")
            .FirstOrDefault(e => e.Attribute("Name")?.Value == "erp_main_orders");
        entityType.ShouldNotBeNull();
        entityType!.Elements(edmNs + "Key").Elements(edmNs + "PropertyRef")
            .Select(p => p.Attribute("Name")?.Value)
            .ShouldContain("id");

        var entitySet = xdoc.Descendants(edmNs + "EntitySet")
            .FirstOrDefault(e => e.Attribute("Name")?.Value == "erp_main_orders");
        entitySet.ShouldNotBeNull();
    }

    [Fact]
    public async Task Server_driven_paging_emits_absolute_next_link()
    {
        await EnsureSeededAsync();
        using var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/odata/v4/erp/main/orders?$top=2&$orderby=id%20asc");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        response.Content.Headers.ContentType?.ToString().Replace(" ", "").ShouldStartWith("application/json;odata.metadata=minimal");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("value").EnumerateArray().ToList();
        rows.Count.ShouldBe(2);
        rows[0].GetProperty("id").GetInt64().ShouldBe(1);
        rows[1].GetProperty("id").GetInt64().ShouldBe(2);

        var nextLink = doc.RootElement.GetProperty("@odata.nextLink").GetString();
        nextLink.ShouldNotBeNullOrWhiteSpace();
        Uri.TryCreate(nextLink, UriKind.Absolute, out var nextUri).ShouldBeTrue();
        nextUri!.Scheme.ShouldBe("http");
        nextUri.AbsolutePath.ShouldBe("/odata/v4/erp/main/orders");
        nextUri.Query.ShouldContain("$skip=2");
        nextUri.Query.ShouldContain("$top=2");

        var page2Response = await client.GetAsync(nextUri.PathAndQuery);
        page2Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var page2Doc = JsonDocument.Parse(await page2Response.Content.ReadAsStringAsync());
        var page2Rows = page2Doc.RootElement.GetProperty("value").EnumerateArray().ToList();
        page2Rows.Count.ShouldBe(2);
        page2Rows[0].GetProperty("id").GetInt64().ShouldBe(3);
        page2Rows[1].GetProperty("id").GetInt64().ShouldBe(4);
    }

    [Fact]
    public async Task Query_options_filter_select_orderby_top_skip_apply()
    {
        await EnsureSeededAsync();
        using var client = CreateAuthenticatedClient();

        var response = await client.GetAsync(
            "/odata/v4/erp/main/orders?$filter=region%20eq%20'EU'%20and%20amount%20ge%20100&$select=id,customer,amount&$orderby=amount%20desc&$top=2&$skip=1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("value").EnumerateArray().ToList();
        rows.Count.ShouldBe(2);

        // EU rows with amount >= 100 ordered by amount desc: (4, 310), (3, 250), (1, 120).
        // With $skip=1&$top=2 we expect (3, 250) and (1, 120).
        rows[0].GetProperty("id").GetInt64().ShouldBe(3);
        rows[0].GetProperty("customer").GetString().ShouldBe("Northwind");
        rows[0].GetProperty("amount").GetInt64().ShouldBe(250);
        rows[0].TryGetProperty("region", out _).ShouldBeFalse();

        rows[1].GetProperty("id").GetInt64().ShouldBe(1);
        rows[1].GetProperty("customer").GetString().ShouldBe("Contoso");
        rows[1].GetProperty("amount").GetInt64().ShouldBe(120);
        rows[1].TryGetProperty("region", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Count_returns_total()
    {
        await EnsureSeededAsync();
        using var client = CreateAuthenticatedClient();

        var inlineResponse = await client.GetAsync("/odata/v4/erp/main/orders?$filter=region%20eq%20'EU'&$top=1&$count=true");
        inlineResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var inlineDoc = JsonDocument.Parse(await inlineResponse.Content.ReadAsStringAsync());
        inlineDoc.RootElement.GetProperty("@odata.count").GetInt64().ShouldBe(3);
        inlineDoc.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);

        var segmentResponse = await client.GetAsync("/odata/v4/erp/main/orders/$count");
        segmentResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        segmentResponse.Headers.GetValues("OData-Version").ShouldContain("4.0");
        (await segmentResponse.Content.ReadAsStringAsync()).Trim().ShouldBe("5");
    }

    [Fact]
    public async Task PowerBi_fixture_requests_succeed()
    {
        await EnsureSeededAsync();
        using var client = CreateAuthenticatedClient();

        var fixtures = LoadHttpFixtures("powerbi");
        fixtures.Count.ShouldBeGreaterThanOrEqualTo(3);

        foreach (var fixturePath in fixtures)
        {
            using var request = ParseHttpFixture(fixturePath);
            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"Fixture failed: {Path.GetFileName(fixturePath)}");
            response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        }
    }

    [Fact]
    public async Task Excel_fixture_requests_succeed()
    {
        await EnsureSeededAsync();
        using var client = CreateAuthenticatedClient();

        var fixtures = LoadHttpFixtures("excel");
        fixtures.Count.ShouldBeGreaterThanOrEqualTo(3);

        foreach (var fixturePath in fixtures)
        {
            using var request = ParseHttpFixture(fixturePath);
            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"Fixture failed: {Path.GetFileName(fixturePath)}");
            response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Unauthenticated_request_gets_challenge()
    {
        await EnsureSeededAsync();
        using var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync("/odata/v4/erp/main/orders");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        response.Headers.WwwAuthenticate.ToString().ShouldNotBeNullOrWhiteSpace();
    }

    private static IReadOnlyList<string> LoadHttpFixtures(string subfolder)
    {
        var baseCandidate = Path.Combine(AppContext.BaseDirectory, "OData", "Fixtures", subfolder);
        if (Directory.Exists(baseCandidate))
        {
            return Directory.GetFiles(baseCandidate, "*.http").OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        var repoPath = Path.Combine(dir!.FullName, "tests", "Autheris.Tests.Integration", "OData", "Fixtures", subfolder);
        return Directory.GetFiles(repoPath, "*.http").OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static HttpRequestMessage ParseHttpFixture(string path)
    {
        var lines = File.ReadAllLines(path)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
            .ToList();

        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var method = new HttpMethod(requestLine[0]);
        var target = requestLine[1];
        var request = new HttpRequestMessage(method, target);

        foreach (var headerLine in lines.Skip(1))
        {
            var colon = headerLine.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = headerLine[..colon].Trim();
            var value = headerLine[(colon + 1)..].Trim();
            if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }
}
