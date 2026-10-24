namespace Autheris.Tests.Unit.Databricks;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

    // ---- INF-07: 401/403 evicts exactly the cache entry the failed call used ----

    private sealed class EvictionHarness
    {
        public Dictionary<string, int> Exchanges { get; } = [];
        public Func<string, HttpStatusCode> StatementStatus { get; set; } = _ => HttpStatusCode.OK;
        public DatabricksTokenCache Cache { get; } = new();
        public CapturingLogger<DatabricksStatementExecutionClient> Logger { get; } = new();
        public DatabricksStatementExecutionClient Client { get; }
        public GatewayOptions Options { get; }

        public EvictionHarness(DatabricksAuthMode mode, string? authClientId = null)
        {
            Options = DatabricksTestSupport.Options(mode, oboModeAuthClientId: authClientId);
            var http = new HttpClient(new FakeHttpMessageHandler((req, ct) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/oidc/v1/token", StringComparison.OrdinalIgnoreCase))
                {
                    var body = req.Content is null ? string.Empty : req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                    var key = path.EndsWith("/oidc/v1/token", StringComparison.OrdinalIgnoreCase) ? "m2m" : ExtractAssertion(body);
                    Exchanges[key] = Exchanges.GetValueOrDefault(key) + 1;
                    var token = $"tok-{key.GetHashCode():x}-{Exchanges[key]}";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"access_token\":\"" + token + "\",\"expires_in\":3600}", Encoding.UTF8, "application/json")
                    });
                }

                var status = StatementStatus(req.Headers.Authorization?.Parameter ?? string.Empty);
                return Task.FromResult(status == HttpStatusCode.OK
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Succeeded(StatementId, "[[\"1\",\"x\"]]"), Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(status));
            }));
            Client = new DatabricksStatementExecutionClient(http, Microsoft.Extensions.Options.Options.Create(Options), Vault(), Env("Production"), Cache, Logger);
        }

        public Task ExecuteAsync(string? callerToken) => Client.ExecuteAsync(
            new DatabricksStatementRequest("SELECT 1", ImmutableArray<DatabricksParameter>.Empty, "main", "default", TimeSpan.FromSeconds(30),
                Delegation: callerToken is null ? null : new DelegationContext(CallerToken: callerToken)),
            CancellationToken.None);

        private static string ExtractAssertion(string body) =>
            body.Split('&').FirstOrDefault(p => p.StartsWith("assertion=", StringComparison.Ordinal)) ?? "none";
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Inf07_Obo403_EvictsOnlyCallerEntry_NextCallReExchanges()
    {
        var h = new EvictionHarness(DatabricksAuthMode.OAuthObo);
        var alice = CreateMockJwt("alice", DateTimeOffset.UtcNow.AddHours(1));
        var denyOnce = true;
        h.StatementStatus = _ => denyOnce ? HttpStatusCode.Forbidden : HttpStatusCode.OK;

        var ex = await Should.ThrowAsync<DatabricksProviderException>(() => h.ExecuteAsync(alice));
        ex.Code.ShouldBe(DatabricksErrorCodes.AuthFailed);
        h.Cache.EntryCount.ShouldBe(0);

        denyOnce = false;
        await h.ExecuteAsync(alice);
        h.Exchanges.Values.Single().ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Inf07_Obo403ForUserA_KeepsUserBCachedToken()
    {
        var h = new EvictionHarness(DatabricksAuthMode.OAuthObo);
        var alice = CreateMockJwt("alice", DateTimeOffset.UtcNow.AddHours(1));
        var bob = CreateMockJwt("bob", DateTimeOffset.UtcNow.AddHours(1));
        await h.ExecuteAsync(bob);
        h.StatementStatus = auth => auth == BobTokenOf(h, bob) ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

        await Should.ThrowAsync<DatabricksProviderException>(() => h.ExecuteAsync(alice));
        h.Cache.EntryCount.ShouldBe(1);

        h.StatementStatus = _ => HttpStatusCode.OK;
        await h.ExecuteAsync(bob);
        h.Exchanges.Count.ShouldBe(2);
        h.Exchanges.Values.Sum().ShouldBe(2);
    }

    private static string BobTokenOf(EvictionHarness h, string bob) =>
        $"tok-{$"assertion={Uri.EscapeDataString(bob)}".GetHashCode():x}-1";

    [Fact]
    [Trait("Category", "Security")]
    public async Task Inf07_M2M401_EvictsM2MEntry()
    {
        var h = new EvictionHarness(DatabricksAuthMode.OAuthM2M);
        var deny = true;
        h.StatementStatus = _ => deny ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;

        await Should.ThrowAsync<DatabricksProviderException>(() => h.ExecuteAsync(null));
        h.Cache.EntryCount.ShouldBe(0);

        deny = false;
        await h.ExecuteAsync(null);
        h.Exchanges["m2m"].ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Inf07_SameClientIdForAuthAndObo_DoesNotPurgeOtherCallers()
    {
        var h = new EvictionHarness(DatabricksAuthMode.OAuthObo, "obo-client-id");
        var alice = CreateMockJwt("alice", DateTimeOffset.UtcNow.AddHours(1));
        var bob = CreateMockJwt("bob", DateTimeOffset.UtcNow.AddHours(1));
        await h.ExecuteAsync(bob);
        await h.ExecuteAsync(alice);
        h.Cache.EntryCount.ShouldBe(2);

        var db = h.Options.DataSources.Connections["dbx"].Databricks!;
        var provider = new DatabricksOboTokenProvider(new HttpClient(), Microsoft.Extensions.Options.Options.Create(h.Options), Vault(), Env("Production"), h.Cache);
        provider.EvictToken(db, new DelegationContext(CallerToken: alice));

        h.Cache.EntryCount.ShouldBe(1);
        h.Exchanges.Values.Sum().ShouldBe(2);
        await h.ExecuteAsync(bob);
        h.Exchanges.Values.Sum().ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Inf07_EvictWithoutCallerToken_IsNoOp()
    {
        var h = new EvictionHarness(DatabricksAuthMode.OAuthObo);
        await h.ExecuteAsync(CreateMockJwt("alice", DateTimeOffset.UtcNow.AddHours(1)));
        var db = h.Options.DataSources.Connections["dbx"].Databricks!;
        var provider = new DatabricksOboTokenProvider(new HttpClient(), Microsoft.Extensions.Options.Options.Create(h.Options), Vault(), Env("Production"), h.Cache);

        provider.EvictToken(db, null);
        provider.EvictToken(db, new DelegationContext(CallerToken: null));
        provider.EvictToken(db, new DelegationContext(CallerToken: "   "));
        provider.EvictToken(db, new DelegationContext(CallerToken: "Bearer "));

        h.Cache.EntryCount.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Inf07_EvictWithoutCallerToken_NeverPurgesByClientId()
    {
        var h = new EvictionHarness(DatabricksAuthMode.OAuthObo, "obo-client-id");
        await h.ExecuteAsync(CreateMockJwt("alice", DateTimeOffset.UtcNow.AddHours(1)));
        await h.ExecuteAsync(CreateMockJwt("bob", DateTimeOffset.UtcNow.AddHours(1)));
        var db = h.Options.DataSources.Connections["dbx"].Databricks!;
        var provider = new DatabricksOboTokenProvider(new HttpClient(), Microsoft.Extensions.Options.Options.Create(h.Options), Vault(), Env("Production"), h.Cache);

        Should.NotThrow(() => provider.EvictToken(db, new DelegationContext(CallerToken: null)));

        h.Cache.EntryCount.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Inf07_EvictionDoesNotLogAssertionHash()
    {
        var h = new EvictionHarness(DatabricksAuthMode.OAuthObo);
        var alice = CreateMockJwt("alice", DateTimeOffset.UtcNow.AddHours(1));
        h.StatementStatus = _ => HttpStatusCode.Forbidden;
        await Should.ThrowAsync<DatabricksProviderException>(() => h.ExecuteAsync(alice));

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(alice)));
        h.Logger.Lines.ShouldNotContain(l => l.Contains(hash, StringComparison.OrdinalIgnoreCase) || l.Contains(alice, StringComparison.Ordinal));
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
