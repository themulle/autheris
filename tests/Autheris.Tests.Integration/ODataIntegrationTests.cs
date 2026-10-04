namespace Autheris.Tests.Integration;

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public class ODataIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ODataIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
        });
    }

    [Fact]
    public async Task ODataServiceDocument_RequiresAuthentication()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/odata/v4");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
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
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("$metadata");
        body.ShouldContain("EntitySet");
    }

    [Fact]
    public async Task ODataMetadataDocument_ReturnsValidCsdlXml()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-1");

        var response = await client.GetAsync("/odata/v4/$metadata");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("ACCESS_DENIED");
    }

    [Fact]
    public async Task ODataEntitySet_WithActiveConsent_Returns200AndRows()
    {
        var userSid = new Sid("S-1-5-21-ODATA-ANALYST");
        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");

        // Seed consent
        using (var scope = _factory.Services.CreateScope())
        {
            var consentRepo = scope.ServiceProvider.GetRequiredService<IConsentRepository>();
            var metaRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();

            var meta = await metaRepo.GetTableMetadataAsync(tableId);
            if (meta != null)
            {
                await consentRepo.CreateConsentAsync(new Consent
                {
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

        var response = await client.GetAsync("/odata/v4/finance/dbo/finance_table_1?$top=5&$skip=0&$count=true");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("$metadata#finance_dbo_finance_table_1");
        body.ShouldContain("value");
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
}
