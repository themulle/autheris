namespace Autheris.Tests.Integration;

using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

public sealed class DocIngestionEndpointsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DocIngestionEndpointsIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    [Fact]
    public async Task UploadZipBundle_RawStream_SucceedsWith200()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        // Create in-memory zip archive
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("runbooks/incident.md");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("# Emergency Incident Procedure\n\nRun emergency shutdown.");
        }

        ms.Position = 0;
        using var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        // Act: POST raw zip stream to /api/v1/docs/apps/{appId}/bundle
        var response = await client.PostAsync("/api/v1/docs/apps/billing-service/bundle", content);

        // Assert: Must return 200 OK without synchronous I/O or Kestrel errors
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responseJson = await response.Content.ReadAsStringAsync();
        responseJson.ShouldContain("\"success\":true");

        // Act: Search for the uploaded document via the new search REST endpoint
        var searchResponse = await client.GetAsync("/api/v1/docs/search?query=Emergency");
        searchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var searchJson = await searchResponse.Content.ReadAsStringAsync();
        using var searchDoc = JsonDocument.Parse(searchJson);
        searchDoc.RootElement.GetProperty("total").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task SyncExistingDocuments_RequiresGovernanceAdmin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-DAVID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "DataConsumer");

        var response = await client.PostAsync("/api/v1/docs/sync", new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
