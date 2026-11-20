namespace Autheris.Tests.Integration.Modules;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Modules.OpenMetadata;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class OpenMetadataModuleSecurityTests(OpenMetadataModuleSecurityTests.SecurityHost fixture)
    : IClassFixture<OpenMetadataModuleSecurityTests.SecurityHost>
{
    private const string Secret = "om-module-security-test-secret-0123456789";

    [Fact]
    [Trait("Category", "Security")]
    public async Task Unsigned_webhook_returns_401_when_enabled()
    {
        using var client = fixture.CreateClient();
        using var content = new StringContent("{\"eventType\":\"entityUpdated\",\"entityType\":\"table\"}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/webhooks/openmetadata", content);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Signed_webhook_reaches_handler_when_enabled()
    {
        using var client = fixture.CreateClient();
        var fqn = $"service.db.schema.signed_{Guid.NewGuid():N}";
        var payload = BuildPayload(Guid.NewGuid(), fqn);

        using var request = CreateSignedRequest(payload, Sign(payload));
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.FakeClient.RequestedFqns.ShouldContain(fqn);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Replayed_event_is_rejected_when_enabled()
    {
        using var client = fixture.CreateClient();
        var eventId = Guid.NewGuid();
        var payload = BuildPayload(eventId, $"service.db.schema.replay_{eventId:N}");
        var sig = Sign(payload);

        using var first = CreateSignedRequest(payload, sig);
        var firstResponse = await client.SendAsync(first);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var replay = CreateSignedRequest(payload, sig);
        var replayResponse = await client.SendAsync(replay);
        replayResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Invalid_signature_returns_identical_401_body_when_enabled()
    {
        using var client = fixture.CreateClient();
        var payload = BuildPayload(Guid.NewGuid(), "service.db.schema.customers");

        using var missingReq = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openmetadata")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        var missingRes = await client.SendAsync(missingReq);
        var missingBody = await missingRes.Content.ReadAsStringAsync();

        using var wrongReq = CreateSignedRequest(payload, "sha256=" + new string('0', 64));
        var wrongRes = await client.SendAsync(wrongReq);
        var wrongBody = await wrongRes.Content.ReadAsStringAsync();

        using var nonHexReq = CreateSignedRequest(payload, "sha256=not-valid-hex");
        var nonHexRes = await client.SendAsync(nonHexReq);
        var nonHexBody = await nonHexRes.Content.ReadAsStringAsync();

        missingRes.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        wrongRes.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        nonHexRes.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        wrongBody.ShouldBe(missingBody);
        nonHexBody.ShouldBe(missingBody);
        wrongRes.Content.Headers.ContentType?.MediaType.ShouldBe(missingRes.Content.Headers.ContentType?.MediaType);
        nonHexRes.Content.Headers.ContentType?.MediaType.ShouldBe(missingRes.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Body_over_2_mb_returns_413_when_enabled()
    {
        using var client = fixture.CreateClient();
        var oversized = new string('x', 2 * 1024 * 1024 + 1024);
        using var request = CreateSignedRequest(oversized, "sha256=" + new string('a', 64));

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    private static string BuildPayload(Guid eventId, string fqn)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{{\"id\":\"{eventId}\",\"timestamp\":{nowMs},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"{fqn}\"}}";
    }

    private static string Sign(string payload)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexStringLower(hash)}";
    }

    private static HttpRequestMessage CreateSignedRequest(string payload, string signatureHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openmetadata")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-OpenMetadata-Signature", signatureHeader);
        return request;
    }

    public sealed class SecurityHost : IntegrationHost<SecurityHost>
    {
        public RecordingOpenMetadataClient FakeClient { get; } = new();

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("OpenMetadataModuleSecurity"),
                ["Gateway:OpenMetadata:WebhookSecret"] = Secret,
            };
            OpenMetadataWorkspaceTestSettings.Apply((k, v) => settings[k] = v);
            config.AddInMemoryCollection(settings);
        }

        protected override void ConfigureHostServices(IServiceCollection services)
        {
            services.ReplaceModuleService<IOpenMetadataClient>(FakeClient);
        }
    }

    public sealed class RecordingOpenMetadataClient : IOpenMetadataClient
    {
        public ConcurrentBag<string> RequestedFqns { get; } = [];

        public Task<IReadOnlyList<OpenMetadataTable>> GetTablesAsync(string? service = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenMetadataTable>>([]);

        public Task<OpenMetadataTable?> GetTableByFqnAsync(string fqn, CancellationToken ct = default)
        {
            RequestedFqns.Add(fqn);
            return Task.FromResult<OpenMetadataTable?>(new OpenMetadataTable
            {
                Id = Guid.NewGuid(),
                Name = fqn.Split('.')[^1],
                FullyQualifiedName = fqn
            });
        }

        public Task<IReadOnlyList<OpenMetadataPolicy>> GetPoliciesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([]);

        public Task<IReadOnlyList<OpenMetadataRole>> GetRolesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenMetadataRole>>([]);

        public Task<IReadOnlyList<OpenMetadataTeam>> GetTeamsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]);

        public Task<IReadOnlyList<OpenMetadataUser>> GetUsersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]);
    }
}
