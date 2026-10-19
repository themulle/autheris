namespace Autheris.Tests.Unit.Databricks;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.DatabricksTestSupport;

/// <summary>
/// Issue #238 (PLAN-DATA-238, WP-SC-15): Comprehensive unit and security tests for Azure Databricks
/// OAuth 2.0 On-Behalf-Of (OBO) token exchange, Unity Catalog delegation, fail-closed enforcement (SEC-OBO238-01),
/// zero token leakage (SEC-OBO238-02), user cache isolation (SEC-OBO238-03), and query tagging (SEC-OBO238-05).
/// </summary>
public sealed class DatabricksOboSecurityTests
{
    private static string CreateMockJwt(string subject, DateTimeOffset expiresAt)
    {
        const string header = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0";
        var payloadJson = $"{{\"sub\":\"{subject}\",\"exp\":{expiresAt.ToUnixTimeSeconds()}}}";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{payload}.sig";
    }

    [Fact]
    public async Task OboTokenExchange_Succeeds_AndExecutesStatementUnderUserIdentity()
    {
        var recordedRequests = new List<HttpRequestMessage>();
        var userAssertion = CreateMockJwt("user-alice", DateTimeOffset.UtcNow.AddHours(1));
        const string downstreamOboToken = "obo-downstream-token-alice-12345";

        var fakeHandler = new FakeHttpMessageHandler((req, ct) =>
        {
            recordedRequests.Add(req);
            if (req.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase))
            {
                var json = "{\"access_token\":\"" + downstreamOboToken + "\",\"token_type\":\"Bearer\",\"expires_in\":3600}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }

            if (req.RequestUri.AbsolutePath.EndsWith("/api/2.0/sql/statements/", StringComparison.OrdinalIgnoreCase))
            {
                // Verify the statement API call uses the OBO downstream token
                req.Headers.Authorization?.Parameter.ShouldBe(downstreamOboToken);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Succeeded(StatementId, "[[\"1\",\"Alice\"]]"), Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var http = new HttpClient(fakeHandler);
        var options = Options(DatabricksAuthMode.OAuthObo);
        var vault = Vault();
        var cache = new DatabricksTokenCache();
        var client = new DatabricksStatementExecutionClient(http, Microsoft.Extensions.Options.Options.Create(options), vault, Env("Production"), cache);

        var delegation = new DelegationContext(
            CallerToken: userAssertion,
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Alice")])),
            CorrelationId: "corr-user-999",
            TraceId: "trace-abc-123");

        var request = new DatabricksStatementRequest(
            "SELECT 1, 'Alice'",
            ImmutableArray<DatabricksParameter>.Empty,
            "main",
            "default",
            TimeSpan.FromSeconds(30),
            Delegation: delegation);

        var result = await client.ExecuteAsync(request, CancellationToken.None);

        result.ShouldNotBeNull();
        result.StatementId.ShouldBe(StatementId);
        result.Rows.Count.ShouldBe(1);
        result.Rows[0][1].ShouldBe("Alice");

        // Verify OBO exchange happened
        recordedRequests.Count.ShouldBe(2);
        var oboExchange = recordedRequests[0];
        oboExchange.RequestUri!.ToString().ShouldContain("login.microsoftonline.com");
    }

    [Fact]
    public async Task MissingCallerToken_FailsClosed_ZeroFallbackToServicePrincipal()
    {
        var recordedRequests = new List<HttpRequestMessage>();
        var fakeHandler = new FakeHttpMessageHandler((req, ct) =>
        {
            recordedRequests.Add(req);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var http = new HttpClient(fakeHandler);
        var options = Options(DatabricksAuthMode.OAuthObo);
        var client = new DatabricksStatementExecutionClient(http, Microsoft.Extensions.Options.Options.Create(options), Vault(), Env("Production"));

        // Delegation context without CallerToken
        var delegationWithoutToken = new DelegationContext(CallerToken: null);

        var request = new DatabricksStatementRequest(
            "SELECT 1",
            ImmutableArray<DatabricksParameter>.Empty,
            "main",
            "default",
            TimeSpan.FromSeconds(30),
            Delegation: delegationWithoutToken);

        var ex = await Should.ThrowAsync<DatabricksProviderException>(() => client.ExecuteAsync(request, CancellationToken.None));

        ex.Code.ShouldBe(DatabricksErrorCodes.AuthFailed);
        ex.Message.ShouldContain("caller token assertion is missing or empty");

        // CRITICAL SEC-OBO238-01: Zero outbound requests! No M2M client credentials fallback!
        recordedRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExpiredCallerToken_FailsClosed_ZeroOutboundCalls()
    {
        var recordedRequests = new List<HttpRequestMessage>();
        var fakeHandler = new FakeHttpMessageHandler((req, ct) =>
        {
            recordedRequests.Add(req);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var http = new HttpClient(fakeHandler);
        var options = Options(DatabricksAuthMode.OAuthObo);
        var client = new DatabricksStatementExecutionClient(http, Microsoft.Extensions.Options.Options.Create(options), Vault(), Env("Production"));

        // Expired assertion (1 hour in the past)
        var expiredToken = CreateMockJwt("expired-user", DateTimeOffset.UtcNow.AddHours(-1));
        var delegation = new DelegationContext(CallerToken: expiredToken);

        var request = new DatabricksStatementRequest(
            "SELECT 1",
            ImmutableArray<DatabricksParameter>.Empty,
            "main",
            "default",
            TimeSpan.FromSeconds(30),
            Delegation: delegation);

        var ex = await Should.ThrowAsync<DatabricksProviderException>(() => client.ExecuteAsync(request, CancellationToken.None));

        ex.Code.ShouldBe(DatabricksErrorCodes.AuthFailed);
        ex.Message.ShouldContain("expired");

        // SEC-OBO238-01: Zero requests made to Azure AD or Databricks
        recordedRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task DownstreamOAuthError_FailsClosed_NoServicePrincipalFallback()
    {
        var recordedRequests = new List<HttpRequestMessage>();
        var userAssertion = CreateMockJwt("user-bob", DateTimeOffset.UtcNow.AddHours(1));

        var fakeHandler = new FakeHttpMessageHandler((req, ct) =>
        {
            recordedRequests.Add(req);
            if (req.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase))
            {
                // Azure AD returns invalid_grant (e.g. conditional access / MFA required)
                var errorBody = "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS50076: Due to a configuration change made by your administrator, you must use multi-factor authentication to access this resource.\"}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(errorBody, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var http = new HttpClient(fakeHandler);
        var options = Options(DatabricksAuthMode.OAuthObo);
        var client = new DatabricksStatementExecutionClient(http, Microsoft.Extensions.Options.Options.Create(options), Vault(), Env("Production"));

        var delegation = new DelegationContext(CallerToken: userAssertion);
        var request = new DatabricksStatementRequest(
            "SELECT 1",
            ImmutableArray<DatabricksParameter>.Empty,
            "main",
            "default",
            TimeSpan.FromSeconds(30),
            Delegation: delegation);

        var ex = await Should.ThrowAsync<DatabricksProviderException>(() => client.ExecuteAsync(request, CancellationToken.None));

        ex.Code.ShouldBe(DatabricksErrorCodes.AuthFailed);
        ex.Message.ShouldContain("invalid_grant");

        // Verified: only the single failed OBO request occurred, NO subsequent M2M fallback call
        recordedRequests.Count.ShouldBe(1);
        recordedRequests[0].RequestUri!.ToString().ShouldContain("login.microsoftonline.com");
    }

    [Fact]
    public async Task QueryTags_EmittedInStatementPayload_ForUnityCatalogAuditCorrelation()
    {
        string? statementRequestBody = null;
        var userAssertion = CreateMockJwt("user-audit", DateTimeOffset.UtcNow.AddHours(1));

        var fakeHandler = new FakeHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"tok-audit\",\"expires_in\":3600}", Encoding.UTF8, "application/json")
                };
            }

            if (req.RequestUri.AbsolutePath.EndsWith("/api/2.0/sql/statements/", StringComparison.OrdinalIgnoreCase))
            {
                statementRequestBody = await req.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Succeeded(StatementId, "[[\"1\"]]"), Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(fakeHandler);
        var client = new DatabricksStatementExecutionClient(
            http,
            Microsoft.Extensions.Options.Options.Create(Options(DatabricksAuthMode.OAuthObo)),
            Vault(),
            Env("Production"));

        var delegation = new DelegationContext(
            CallerToken: userAssertion,
            CorrelationId: "corr-audit-777",
            TraceId: "trace-audit-888");

        var request = new DatabricksStatementRequest(
            "SELECT 1",
            ImmutableArray<DatabricksParameter>.Empty,
            "main",
            "default",
            TimeSpan.FromSeconds(30),
            Delegation: delegation);

        await client.ExecuteAsync(request, CancellationToken.None);

        statementRequestBody.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(statementRequestBody);
        var root = doc.RootElement;
        root.TryGetProperty("query_tags", out var queryTags).ShouldBeTrue();
        queryTags.GetProperty("autheris_correlation_id").GetString().ShouldBe("corr-audit-777");
        queryTags.GetProperty("autheris_trace_id").GetString().ShouldBe("trace-audit-888");
    }

    [Fact]
    public async Task CacheUserIsolation_DistinctCallersGetIndependentCacheEntries()
    {
        int oboCalls = 0;
        var tokenAlice = CreateMockJwt("alice", DateTimeOffset.UtcNow.AddHours(1));
        var tokenBob = CreateMockJwt("bob", DateTimeOffset.UtcNow.AddHours(1));

        var fakeHandler = new FakeHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref oboCalls);
                var content = await req.Content!.ReadAsStringAsync(ct);
                var returnedToken = content.Contains("alice") ? "token-for-alice" : "token-for-bob";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"access_token\":\"{returnedToken}\",\"expires_in\":3600}}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Succeeded(StatementId, "[[\"1\"]]"), Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(fakeHandler);
        var cache = new DatabricksTokenCache();
        var client = new DatabricksStatementExecutionClient(
            http,
            Microsoft.Extensions.Options.Options.Create(Options(DatabricksAuthMode.OAuthObo)),
            Vault(),
            Env("Production"),
            cache);

        // First call by Alice: should hit Azure AD
        await client.ExecuteAsync(new DatabricksStatementRequest("SELECT 1", ImmutableArray<DatabricksParameter>.Empty, "main", "default", TimeSpan.FromSeconds(30), Delegation: new DelegationContext(CallerToken: tokenAlice)), CancellationToken.None);
        oboCalls.ShouldBe(1);

        // Second call by Alice: should use cached token, no Azure AD call
        await client.ExecuteAsync(new DatabricksStatementRequest("SELECT 1", ImmutableArray<DatabricksParameter>.Empty, "main", "default", TimeSpan.FromSeconds(30), Delegation: new DelegationContext(CallerToken: tokenAlice)), CancellationToken.None);
        oboCalls.ShouldBe(1);

        // Third call by Bob: must NOT use Alice's cache entry; must hit Azure AD
        await client.ExecuteAsync(new DatabricksStatementRequest("SELECT 1", ImmutableArray<DatabricksParameter>.Empty, "main", "default", TimeSpan.FromSeconds(30), Delegation: new DelegationContext(CallerToken: tokenBob)), CancellationToken.None);
        oboCalls.ShouldBe(2);
    }

    [Fact]
    public async Task ZeroTokenLeakage_TokensAndSecretsNeverEmittedInLogsOrExceptions()
    {
        var rawAssertion = "RAW-CALLER-ASSERTION-SECRET-KEY-ABC123XYZ";
        var clientSecret = ClientSecret;
        var logger = new CapturingLogger<DatabricksOboTokenProvider>();

        var fakeHandler = new FakeHttpMessageHandler((req, ct) =>
        {
            // Fail with 500 error
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"server_error\",\"error_description\":\"Internal crash\"}", Encoding.UTF8, "application/json")
            });
        });

        using var http = new HttpClient(fakeHandler);
        var provider = new DatabricksOboTokenProvider(
            http,
            Microsoft.Extensions.Options.Options.Create(Options(DatabricksAuthMode.OAuthObo)),
            Vault(),
            Env("Production"),
            new DatabricksTokenCache(),
            logger);

        var dbOptions = Options(DatabricksAuthMode.OAuthObo).DataSources.Connections["dbx"].Databricks!;
        var delegation = new DelegationContext(CallerToken: rawAssertion);

        var ex = await Should.ThrowAsync<DatabricksProviderException>(() => provider.AcquireTokenAsync(dbOptions, delegation, CancellationToken.None));

        // Exception message must NOT contain raw assertion or client secret
        ex.Message.ShouldNotContain(rawAssertion);
        ex.Message.ShouldNotContain(clientSecret);

        // All logger lines must NOT contain raw assertion or client secret
        foreach (var line in logger.Lines)
        {
            line.ShouldNotContain(rawAssertion);
            line.ShouldNotContain(clientSecret);
        }
    }

    [Fact]
    public void DelegationCapability_DeclaresUnityCatalogSupport()
    {
        var fakeHandler = new FakeHttpMessageHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var http = new HttpClient(fakeHandler);
        var provider = new DatabricksOboTokenProvider(http);

        provider.SupportedDelegationKinds.ShouldContain(DelegationKind.TokenDelegation);
        provider.SourceEnforcesRowSecurity.ShouldBeTrue();
        provider.SourceEnforcesMasking.ShouldBeTrue();
        provider.SupportsCorrelationTag.ShouldBeTrue();
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
