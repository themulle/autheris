namespace Autheris.Tests.Integration.OData;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

[Trait("Category", "Security")]
public sealed class ODataAccessModelTests : IClassFixture<ODataAccessModelTests.Fixture>
{
    private static readonly TableIdentifier CustomersTable = new("erp", "main", "customers");
    private static readonly Sid EuAnalystSid = new("S-1-5-21-ODATA-EU-ANALYST");
    private static readonly Sid DeniedUserSid = new("S-1-5-21-ODATA-DENIED-USER");
    private const string Tenant = "erp";

    private readonly Fixture _fixture;
    private WebApplicationFactory<Program> _factory => _fixture.Factory;

    public ODataAccessModelTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class Fixture : IDisposable
    {
        private readonly WebApplicationFactory<Program> _root = new();
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"odata-access-{Guid.NewGuid():N}.db");
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
            CREATE TABLE customers (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                region TEXT NOT NULL,
                email TEXT NOT NULL,
                secret_notes TEXT NOT NULL
            );
            INSERT INTO customers VALUES
                (1, 'Alice', 'EU', 'alice@contoso.eu', 'VIP-EU-1'),
                (2, 'Bob', 'US', 'bob@contoso.us', 'VIP-US-2'),
                (3, 'Charlie', 'EU', 'charlie@contoso.eu', 'VIP-EU-3'),
                (4, 'Diana', 'APAC', 'diana@contoso.apac', 'VIP-APAC-4'),
                (5, 'Erik', 'EU', 'erik@contoso.eu', 'VIP-EU-5');
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

        var emailColId = Guid.NewGuid();
        var regionColId = Guid.NewGuid();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table
            {
                Id = Guid.NewGuid(),
                DisplayName = "customers",
                TableName = "customers",
                SchemaName = "main",
                SourceType = "Sqlite",
                SourceName = "erp",
                IsActive = true
            },
            Identifier = CustomersTable,
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "integer" },
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "name", DataType = "varchar" },
                new TableColumn { Id = regionColId, ColumnName = "region", DataType = "varchar" },
                new TableColumn { Id = emailColId, ColumnName = "email", DataType = "varchar", IsSensitive = true },
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "secret_notes", DataType = "varchar", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule
                {
                    TableColumnId = emailColId,
                    RuleType = "REDACT",
                    Replacement = "***REDACTED***"
                }
            }
        });

        var meta = (await repo.GetTableMetadataAsync(CustomersTable))!;
        var persistedRegionCol = meta.Columns.Single(c => c.ColumnName == "region");

        await repo.CreateConsentAsync(new Consent
        {
            TenantId = new TenantId(Tenant),
            TableId = meta.Table.Id,
            TableIdentifier = CustomersTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = EuAnalystSid,
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddHours(24),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "name", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask },
                new ConsentColumnRule { ColumnName = "secret_notes", AccessLevel = ColumnAccessLevel.Deny }
            ],
            RowFilters =
            [
                new ConsentRowFilter
                {
                    FilterGroup = 0,
                    TableColumnId = persistedRegionCol.Id,
                    ColumnName = "region",
                    Operator = "EQ",
                    ValueType = "string",
                    ValueJson = "\"EU\"",
                    ValueSource = "LITERAL"
                }
            ]
        });

        _fixture.Seeded = true;
    }

    private HttpClient CreateClientFor(Sid sid)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid.Value);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        return client;
    }

    [Fact]
    public async Task OData_applies_row_filter()
    {
        await EnsureSeededAsync();
        using var client = CreateClientFor(EuAnalystSid);

        var response = await client.GetAsync("/odata/v4/erp/main/customers?$orderby=id%20asc");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("value").EnumerateArray().ToList();
        rows.Count.ShouldBe(3);
        rows.Select(r => r.GetProperty("id").GetInt64()).ShouldBe([1L, 3L, 5L]);
        rows.ShouldAllBe(r => r.GetProperty("region").GetString() == "EU");

        var countResponse = await client.GetAsync("/odata/v4/erp/main/customers/$count");
        countResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await countResponse.Content.ReadAsStringAsync()).Trim().ShouldBe("3");
    }

    [Fact]
    public async Task OData_masks_pii_columns()
    {
        await EnsureSeededAsync();
        using var client = CreateClientFor(EuAnalystSid);

        var response = await client.GetAsync("/odata/v4/erp/main/customers?$select=id,name,email&$orderby=id%20asc");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var rawBody = await response.Content.ReadAsStringAsync();
        rawBody.ShouldNotContain("alice@contoso.eu");
        rawBody.ShouldNotContain("charlie@contoso.eu");
        rawBody.ShouldNotContain("erik@contoso.eu");

        using var doc = JsonDocument.Parse(rawBody);
        var rows = doc.RootElement.GetProperty("value").EnumerateArray().ToList();
        rows.Count.ShouldBe(3);
        foreach (var row in rows)
        {
            var maskedEmail = row.GetProperty("email").GetString();
            maskedEmail.ShouldNotBeNullOrWhiteSpace();
            maskedEmail.ShouldNotContain("@contoso.eu");
        }
    }

    [Fact]
    public async Task OData_hides_invisible_columns()
    {
        await EnsureSeededAsync();
        using var client = CreateClientFor(EuAnalystSid);

        // 1. Omitted from $metadata
        var metadataResponse = await client.GetAsync("/odata/v4/$metadata");
        metadataResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var metadataXml = await metadataResponse.Content.ReadAsStringAsync();
        metadataXml.ShouldContain("Name=\"email\"");
        metadataXml.ShouldNotContain("secret_notes");

        // 2. Excluded from default entity-set projection (SELECT *)
        var defaultResponse = await client.GetAsync("/odata/v4/erp/main/customers");
        defaultResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var defaultBody = await defaultResponse.Content.ReadAsStringAsync();
        defaultBody.ShouldNotContain("secret_notes");
        defaultBody.ShouldNotContain("VIP-EU-1");

        // 3. Rejected in $select, $filter, and $orderby
        var selectDenied = await client.GetAsync("/odata/v4/erp/main/customers?$select=id,secret_notes");
        selectDenied.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var filterDenied = await client.GetAsync("/odata/v4/erp/main/customers?$filter=secret_notes%20eq%20'VIP-EU-1'");
        filterDenied.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var orderByDenied = await client.GetAsync("/odata/v4/erp/main/customers?$orderby=secret_notes%20asc");
        orderByDenied.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OData_decides_via_access_decision_point()
    {
        await EnsureSeededAsync();
        using var deniedClient = CreateClientFor(DeniedUserSid);

        var entitySetResponse = await deniedClient.GetAsync("/odata/v4/erp/main/customers");
        entitySetResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await entitySetResponse.Content.ReadAsStringAsync()).ShouldContain("ACCESS_DENIED");

        var countResponse = await deniedClient.GetAsync("/odata/v4/erp/main/customers/$count");
        countResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var flatResponse = await deniedClient.GetAsync("/odata/v4/erp_main_customers");
        flatResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var scope = _factory.Services.CreateScope();
        var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        var audits = await auditRepo.GetAuditLogEntriesAsync(500);
        audits.ShouldContain(a =>
            a.ActorSid == DeniedUserSid &&
            a.EventType == AuditEventTypes.TableQuery &&
            a.Decision == "DENY");
    }

    [Fact]
    public async Task OData_next_link_does_not_widen_row_scope()
    {
        await EnsureSeededAsync();
        using var client = CreateClientFor(EuAnalystSid);

        var page1Response = await client.GetAsync("/odata/v4/erp/main/customers?$top=2&$orderby=id%20asc");
        page1Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var page1Doc = JsonDocument.Parse(await page1Response.Content.ReadAsStringAsync());
        var page1Rows = page1Doc.RootElement.GetProperty("value").EnumerateArray().ToList();
        page1Rows.Select(r => r.GetProperty("id").GetInt64()).ShouldBe([1L, 3L]);

        var nextLink = page1Doc.RootElement.GetProperty("@odata.nextLink").GetString();
        nextLink.ShouldNotBeNullOrWhiteSpace();
        var nextUri = new Uri(nextLink!, UriKind.Absolute);

        var page2Response = await client.GetAsync(nextUri.PathAndQuery);
        page2Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var page2Doc = JsonDocument.Parse(await page2Response.Content.ReadAsStringAsync());
        var page2Rows = page2Doc.RootElement.GetProperty("value").EnumerateArray().ToList();
        page2Rows.Select(r => r.GetProperty("id").GetInt64()).ShouldBe([5L]);
        page2Rows.ShouldAllBe(r => r.GetProperty("region").GetString() == "EU");
        page2Doc.RootElement.TryGetProperty("@odata.nextLink", out _).ShouldBeFalse();
    }
}
