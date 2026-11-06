using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Autheris.Tests.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Databricks;

/// <summary>
/// WP-SIMPL-21 (SEC-SIMPL-92, SEC-SIMPL-93): PR contract suite for the Databricks Statement Execution API
/// using an in-memory HTTP handler (no live workspace or credentials required).
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "DatabricksContract")]
public sealed class DatabricksStatementExecutionContractTests
{
    private const string Host = "adb-123.azuredatabricks.net";
    private const string StatementId = "01ef1234-5678-9abc-def0-123456789abc";
    private const string AccessToken = "tok-contract-m2m-123";
    private const string ClientSecret = "sp-secret-contract-xyz";

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, string? Authorization);

    private sealed class ContractDatabricksHandler(Func<RecordedRequest, HttpResponseMessage> router) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString());
            lock (Requests)
            {
                Requests.Add(recorded);
            }

            return router(recorded);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ok(string json) => Json(HttpStatusCode.OK, json);

    private static HttpResponseMessage TokenOk(string token = AccessToken) =>
        Ok("{\"access_token\":\"" + token + "\",\"token_type\":\"Bearer\",\"expires_in\":3600}");

    private static string Succeeded(string id, string rowsJson, string? nextLink = null, bool truncated = false) =>
        "{\"statement_id\":\"" + id + "\",\"status\":{\"state\":\"SUCCEEDED\"}," +
        "\"manifest\":{\"format\":\"JSON_ARRAY\",\"truncated\":" + (truncated ? "true" : "false") + "," +
        "\"schema\":{\"column_count\":2,\"columns\":[{\"name\":\"id\",\"type_name\":\"INT\",\"position\":0},{\"name\":\"name\",\"type_name\":\"STRING\",\"position\":1}]}}," +
        "\"result\":{\"chunk_index\":0,\"row_offset\":0,\"data_array\":" + rowsJson +
        (nextLink is null ? string.Empty : ",\"next_chunk_internal_link\":\"" + nextLink + "\"") + "}}";

    private static string State(string id, string state) =>
        "{\"statement_id\":\"" + id + "\",\"status\":{\"state\":\"" + state + "\"}}";

    private static string CreateMockJwt(string subject, DateTimeOffset expiresAt)
    {
        const string header = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0";
        var payloadJson = $"{{\"sub\":\"{subject}\",\"exp\":{expiresAt.ToUnixTimeSeconds()}}}";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{payload}.sig";
    }

    private static GatewayOptions CreateOptions(
        DatabricksAuthMode mode = DatabricksAuthMode.OAuthM2M,
        int commandTimeoutSeconds = 30,
        int pollIntervalMs = 10)
    {
        var connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbx"] = new()
            {
                Provider = "databricks",
                CommandTimeoutSeconds = commandTimeoutSeconds,
                Databricks = new DatabricksDataSourceOptions
                {
                    Host = Host,
                    WarehouseId = "wh-1",
                    Catalog = "main",
                    Schema = "default",
                    ByteLimit = 26214400,
                    PollIntervalMilliseconds = pollIntervalMs,
                    Auth = mode == DatabricksAuthMode.OAuthObo
                        ? new DatabricksAuthOptions
                        {
                            Mode = DatabricksAuthMode.OAuthObo,
                            Obo = new DatabricksOboOptions
                            {
                                TenantId = "tenant-123",
                                ClientId = "obo-client-id",
                                ClientSecretRef = "kv://dbx-obo-secret",
                                Scopes = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default"
                            }
                        }
                        : new DatabricksAuthOptions
                        {
                            Mode = DatabricksAuthMode.OAuthM2M,
                            ClientId = "sp-id",
                            ClientSecretRef = "kv://dbx-sp-secret"
                        }
                }
            }
        };
        return new GatewayOptions { DataSources = new SqlDataSourceOptions { Connections = connections } };
    }

    private static IKeyVaultSecretProvider CreateVault()
    {
        var vault = Substitute.For<IKeyVaultSecretProvider>();
        vault.GetSecretBytes("kv://dbx-sp-secret").Returns(Encoding.UTF8.GetBytes(ClientSecret));
        vault.GetSecretBytes("kv://dbx-obo-secret").Returns(Encoding.UTF8.GetBytes(ClientSecret));
        return vault;
    }

    private static IHostEnvironment CreateEnv(string name = "Production")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static DatabricksStatementExecutionClient CreateClient(
        ContractDatabricksHandler handler,
        GatewayOptions? options = null,
        DatabricksTokenCache? cache = null,
        ILogger<DatabricksStatementExecutionClient>? logger = null,
        TimeProvider? timeProvider = null) =>
        new(
            new HttpClient(handler),
            Options.Create(options ?? CreateOptions()),
            CreateVault(),
            CreateEnv(),
            cache ?? new DatabricksTokenCache(),
            logger,
            timeProvider);

    private static DatabricksStatementRequest CreateRequest(
        string statement = "SELECT id, name FROM t WHERE a = :p0",
        IEnumerable<DatabricksParameter>? parameters = null,
        DelegationContext? delegation = null) =>
        new(
            statement,
            [.. parameters ?? [new DatabricksParameter("p0", "1", "INT")]],
            "main",
            "default",
            TimeSpan.FromSeconds(30),
            Delegation: delegation);

    [Fact]
    [Trait("Category", "Security")]
    public async Task Uses_obo_token_per_caller()
    {
        var aliceAssertion = CreateMockJwt("alice", DateTimeOffset.UtcNow.AddHours(1));
        var bobAssertion = CreateMockJwt("bob", DateTimeOffset.UtcNow.AddHours(1));
        int oboExchanges = 0;

        var handler = new ContractDatabricksHandler(r =>
        {
            if (r.Uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref oboExchanges);
                var token = r.Body.Contains(Uri.EscapeDataString(aliceAssertion), StringComparison.Ordinal)
                    ? "obo-tok-alice"
                    : "obo-tok-bob";
                return TokenOk(token);
            }

            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                var who = r.Authorization == "Bearer obo-tok-alice" ? "Alice" : "Bob";
                return Ok(Succeeded(StatementId, $"[[\"1\",\"{who}\"]]"));
            }

            return Json(HttpStatusCode.NotFound, "{}");
        });

        var client = CreateClient(handler, CreateOptions(DatabricksAuthMode.OAuthObo));

        var aliceResult = await client.ExecuteAsync(
            CreateRequest("SELECT id, name FROM t", [], new DelegationContext(CallerToken: aliceAssertion)),
            CancellationToken.None);
        aliceResult.Rows[0][1].ShouldBe("Alice");
        oboExchanges.ShouldBe(1);

        // Cached for Alice
        await client.ExecuteAsync(
            CreateRequest("SELECT id, name FROM t", [], new DelegationContext(CallerToken: aliceAssertion)),
            CancellationToken.None);
        oboExchanges.ShouldBe(1);

        // Bob gets a separate OBO exchange and uses Bob's token
        var bobResult = await client.ExecuteAsync(
            CreateRequest("SELECT id, name FROM t", [], new DelegationContext(CallerToken: bobAssertion)),
            CancellationToken.None);
        bobResult.Rows[0][1].ShouldBe("Bob");
        oboExchanges.ShouldBe(2);

        // Missing caller token fails closed with no fallback to M2M
        var missingEx = await Should.ThrowAsync<DatabricksProviderException>(() =>
            client.ExecuteAsync(CreateRequest("SELECT id, name FROM t", [], new DelegationContext(CallerToken: null)), CancellationToken.None));
        missingEx.Code.ShouldBe(DatabricksErrorCodes.AuthFailed);
        oboExchanges.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Binds_parameters_not_literals()
    {
        const string hostileValue = "x'; DROP TABLE main.default.t; --";
        var handler = new ContractDatabricksHandler(r =>
        {
            if (r.Uri.AbsolutePath == "/oidc/v1/token")
            {
                return TokenOk();
            }

            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                return Ok(Succeeded(StatementId, """[["1","safe"]]"""));
            }

            return Json(HttpStatusCode.NotFound, "{}");
        });

        var client = CreateClient(handler);
        var result = await client.ExecuteAsync(
            CreateRequest(parameters: [new DatabricksParameter("p0", hostileValue, "STRING"), new DatabricksParameter("p1", null, "INT")]),
            CancellationToken.None);

        result.Rows.Count.ShouldBe(1);
        var post = handler.Requests.Single(r => r.Uri.AbsolutePath == "/api/2.0/sql/statements/");
        using var doc = JsonDocument.Parse(post.Body);
        var root = doc.RootElement;
        root.GetProperty("statement").GetString().ShouldBe("SELECT id, name FROM t WHERE a = :p0");
        root.GetProperty("statement").GetString()!.ShouldNotContain(hostileValue);

        var parameters = root.GetProperty("parameters");
        parameters.GetArrayLength().ShouldBe(2);
        parameters[0].GetProperty("name").GetString().ShouldBe("p0");
        parameters[0].GetProperty("value").GetString().ShouldBe(hostileValue);
        parameters[0].GetProperty("type").GetString().ShouldBe("STRING");
        parameters[1].GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);

        // Invalid parameter name or type is rejected before any network call
        var invalidEx = await Should.ThrowAsync<DatabricksProviderException>(() =>
            client.ExecuteAsync(CreateRequest(parameters: [new DatabricksParameter("p0; DROP", "1", "STRING")]), CancellationToken.None));
        invalidEx.Code.ShouldBe(DatabricksErrorCodes.InvalidRequest);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Pages_until_completion()
    {
        var chunk1 = $"/api/2.0/sql/statements/{StatementId}/result/chunks/1";
        var chunk2 = $"/api/2.0/sql/statements/{StatementId}/result/chunks/2";
        int polls = 0;

        var handler = new ContractDatabricksHandler(r =>
        {
            if (r.Uri.AbsolutePath == "/oidc/v1/token")
            {
                return TokenOk();
            }

            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                return Ok(State(StatementId, "RUNNING"));
            }

            if (r.Method == HttpMethod.Get && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}")
            {
                return ++polls < 2
                    ? Ok(State(StatementId, "RUNNING"))
                    : Ok(Succeeded(StatementId, """[["1","a"],["2","b"]]""", chunk1));
            }

            if (r.Uri.AbsolutePath == chunk1)
            {
                return Ok("{\"chunk_index\":1,\"data_array\":[[\"3\",null]],\"next_chunk_internal_link\":\"" + chunk2 + "\"}");
            }

            if (r.Uri.AbsolutePath == chunk2)
            {
                return Ok("""{"chunk_index":2,"data_array":[["4","d"]]}""");
            }

            return Json(HttpStatusCode.NotFound, "{}");
        });

        var client = CreateClient(handler);
        var result = await client.ExecuteAsync(CreateRequest(), CancellationToken.None);

        polls.ShouldBe(2);
        result.StatementId.ShouldBe(StatementId);
        result.Rows.Select(r => string.Join('|', r.Select(c => c ?? "<null>"))).ShouldBe(["1|a", "2|b", "3|<null>", "4|d"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Maps_errors_to_typed_errors()
    {
        var failedHandler = new ContractDatabricksHandler(r =>
            r.Uri.AbsolutePath == "/oidc/v1/token"
                ? TokenOk()
                : Ok("{\"statement_id\":\"" + StatementId + "\",\"status\":{\"state\":\"FAILED\",\"error\":{\"error_code\":\"BAD_REQUEST\",\"message\":\"org.apache.spark.SparkException at /internal/path secret_table\"}}}"));

        var failedClient = CreateClient(failedHandler);
        var failedEx = await Should.ThrowAsync<DatabricksProviderException>(() =>
            failedClient.ExecuteAsync(CreateRequest(), CancellationToken.None));

        failedEx.Code.ShouldBe(DatabricksErrorCodes.StatementFailed);
        failedEx.Message.ShouldContain("BAD_REQUEST");
        failedEx.Message.ShouldNotContain("SparkException");
        failedEx.Message.ShouldNotContain("secret_table");
        failedEx.Message.ShouldNotContain("/internal/path");

        var canceledHandler = new ContractDatabricksHandler(r =>
            r.Uri.AbsolutePath == "/oidc/v1/token"
                ? TokenOk()
                : Ok(State(StatementId, "CANCELED")));

        var canceledClient = CreateClient(canceledHandler);
        var canceledEx = await Should.ThrowAsync<DatabricksProviderException>(() =>
            canceledClient.ExecuteAsync(CreateRequest(), CancellationToken.None));

        canceledEx.Code.ShouldBe(DatabricksErrorCodes.StatementFailed);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Fails_closed_on_401_403_and_timeout()
    {
        // 1. 401 and 403 fail closed with AuthFailed and evict cached token
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            var cache = new DatabricksTokenCache();
            var handler = new ContractDatabricksHandler(r =>
                r.Uri.AbsolutePath == "/oidc/v1/token"
                    ? TokenOk()
                    : Json(status, """{"message":"denied"}"""));

            var client = CreateClient(handler, cache: cache);
            var ex = await Should.ThrowAsync<DatabricksProviderException>(() =>
                client.ExecuteAsync(CreateRequest(), CancellationToken.None));

            ex.Code.ShouldBe(DatabricksErrorCodes.AuthFailed);
            cache.EntryCount.ShouldBe(0);
        }

        // 2. Statement exceeding command timeout cancels statement and fails closed with Timeout
        var cancelCalled = false;
        var timeoutHandler = new ContractDatabricksHandler(r =>
        {
            if (r.Uri.AbsolutePath == "/oidc/v1/token")
            {
                return TokenOk();
            }

            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}/cancel")
            {
                cancelCalled = true;
                return Ok("{}");
            }

            return Ok(State(StatementId, "RUNNING"));
        });

        var timeoutClient = CreateClient(timeoutHandler, CreateOptions(commandTimeoutSeconds: 1, pollIntervalMs: 50));
        var timeoutEx = await Should.ThrowAsync<DatabricksProviderException>(() =>
            timeoutClient.ExecuteAsync(CreateRequest(), CancellationToken.None));

        timeoutEx.Code.ShouldBe(DatabricksErrorCodes.Timeout);
        cancelCalled.ShouldBeTrue();
    }
}
