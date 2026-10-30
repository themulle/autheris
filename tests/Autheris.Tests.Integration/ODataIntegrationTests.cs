namespace Autheris.Tests.Integration;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public class ODataIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    /// <summary>A real SQLite table behind the default data source (entity sets read real rows; there is no synthetic data).</summary>
    private static readonly TableIdentifier RowsTable = new("default", "main", "odata_rows");

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDbPath = Path.Combine(Path.GetTempPath(), $"odata-rows-{Guid.NewGuid():N}.db");

    public ODataIntegrationTests(WebApplicationFactory<Program> factory)
    {
        SeedRowsDatabase(_dataDbPath);

        // WebSql:Enabled stays off: it switches the client SQL endpoints, not the table reads OData plans itself.
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
            builder.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={_dataDbPath}");
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dataDbPath); } catch (IOException) { }
    }

    private static void SeedRowsDatabase(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE odata_rows (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO odata_rows VALUES (1, 'alpha'), (2, 'beta');";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Registers <see cref="RowsTable"/> in the catalog and grants the user a clear consent on every column.</summary>
    private async Task GrantRowsTableAsync(Sid userSid)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = RowsTable.TableName, TableName = RowsTable.TableName, SchemaName = RowsTable.Schema, SourceType = "Sqlite", SourceName = "default" },
            Identifier = RowsTable,
            Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }, new TableColumn { ColumnName = "name", DataType = "varchar" }]
        });
        var meta = (await repo.GetTableMetadataAsync(RowsTable))!;
        await repo.CreateConsentAsync(new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableId = meta.Table.Id,
            TableIdentifier = RowsTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddHours(24),
            ColumnRules = meta.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList()
        });
    }

    [Fact]
    public async Task ODataServiceDocument_RequiresAuthentication()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/odata/v4");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");
    }

    [Fact]
    public async Task ODataServiceDocument_WhenAuthenticated_Returns200WithEntitySets()
    {
        var userSid = new Sid("S-1-5-21-USER-1");
        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");

        using (var scope = _factory.Services.CreateScope())
        {
            var consentRepo = scope.ServiceProvider.GetRequiredService<IConsentRepository>();
            var metaRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();

            var meta = await metaRepo.GetTableMetadataAsync(tableId);
            if (meta != null)
            {
                await consentRepo.CreateConsentAsync(new Consent
                {
                    TenantId = Autheris.Tests.Common.TestTenants.Default,
                    TableId = meta.Table.Id,
                    TableIdentifier = tableId,
                    Effect = ConsentEffect.Allow,
                    GranteeType = GranteeType.User,
                    GranteeSid = userSid,
                    ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddHours(24)
                });
            }
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value);

        var response = await client.GetAsync("/odata/v4");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("$metadata");
        body.ShouldContain("EntitySet");
    }

    [Fact]
    public async Task ODataServiceDocument_WithTrailingSlash_Returns200AndODataVersionHeader()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-1");

        var response = await client.GetAsync("/odata/v4/");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public async Task ODataMetadataDocument_ReturnsValidCsdlXml()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-1");

        var response = await client.GetAsync("/odata/v4/$metadata");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/xml");

        var xml = await response.Content.ReadAsStringAsync();
        xml.ShouldContain("<edmx:Edmx Version=\"4.0\"");
        xml.ShouldContain("<Schema Namespace=\"Autheris.OData\"");
        xml.ShouldContain("<EntityContainer Name=\"Container\"");
    }

    [Fact]
    public async Task ODataEntitySet_WithoutConsent_Returns403WithODataError()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-NO-ACCESS");

        var response = await client.GetAsync("/odata/v4/finance/dbo/finance_table_1?$top=10");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("ACCESS_DENIED");
    }

    [Fact]
    public async Task ODataEntitySet_WithActiveConsent_Returns200AndRows()
    {
        var userSid = new Sid("S-1-5-21-ODATA-ANALYST");
        await GrantRowsTableAsync(userSid);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value);

        var response = await client.GetAsync("/odata/v4/default/main/odata_rows?$top=5&$skip=0");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("$metadata#default_main_odata_rows");
        body.ShouldContain("alpha");
        body.ShouldContain("beta");
    }

    [Theory]
    [InlineData("?$top=-1", "InvalidQueryOption")]
    [InlineData("?$top=notanumber", "InvalidQueryOption")]
    [InlineData("?$skip=-5", "InvalidQueryOption")]
    public async Task ODataEntitySet_WithInvalidQueryOptions_Returns400BadRequest(string queryString, string expectedErrorCode)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ODATA-ANALYST");

        var response = await client.GetAsync($"/odata/v4/finance/dbo/finance_table_1{queryString}");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain(expectedErrorCode);
    }

    [Fact]
    public async Task ExcelCompatibility_RootODataPath_Unauthenticated_Returns401WithODataJsonError_NotHtml()
    {
        var client = _factory.CreateClient();
        // Excel and WinInet send text/html in the Accept header by default
        client.DefaultRequestHeaders.Add("Accept", "text/html, application/xhtml+xml, application/json;odata.metadata=minimal, */*");

        var response = await client.GetAsync("/odata");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Basic");

        // Power Query requires JSON error response rather than HTML, even when Accept includes text/html
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("<!DOCTYPE html>");
        body.ShouldContain("\"error\"");
        body.ShouldContain("\"UNAUTHORIZED\"");
    }

    [Fact]
    public async Task ExcelCompatibility_RootODataPath_WhenAuthenticated_Returns200WithMatchingServiceRootContext()
    {
        var userSid = new Sid("S-1-5-21-EXCEL-USER");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value);

        var response = await client.GetAsync("/odata");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");

        var body = await response.Content.ReadAsStringAsync();
        // Power Query strictly validates that @odata.context matches the requested service root
        body.ShouldContain("/odata/$metadata");
        body.ShouldNotContain("/odata/v4/$metadata");
    }

    [Fact]
    public async Task ExcelCompatibility_ODataMetadataPath_ReturnsValidXml()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-EXCEL-USER");

        var response = await client.GetAsync("/odata/$metadata");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/xml");

        var xml = await response.Content.ReadAsStringAsync();
        xml.ShouldContain("<edmx:Edmx Version=\"4.0\"");
        xml.ShouldContain("<Schema Namespace=\"Autheris.OData\"");
    }

    [Fact]
    public async Task ExcelCompatibility_RootODataPath_EntitySet_Returns200AndRows()
    {
        var userSid = new Sid("S-1-5-21-EXCEL-ANALYST");
        await GrantRowsTableAsync(userSid);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value);

        var response = await client.GetAsync("/odata/default/main/odata_rows?$top=5");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("OData-Version").ShouldContain("4.0");

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("/odata/$metadata#default_main_odata_rows");
        body.ShouldContain("alpha");
    }
}
