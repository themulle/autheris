namespace Autheris.Tests.Unit.Databricks;

using System.Net;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Autheris.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.DatabricksTestSupport;

/// <summary>WP-X0i: Databricks Statement Execution client against an HTTP fake (no workspace, S-4). Includes every SEC-ADG-09 control.</summary>
public sealed class DatabricksStatementExecutionClientTests
{
    private static JsonElement Body(RecordedRequest r) => JsonDocument.Parse(r.Body).RootElement;

    [Fact]
    public async Task SendsNamedTypedParameters_NeverInlinesValues()
    {
        const string evil = "x'; DROP TABLE t;--";
        var handler = new FakeDatabricksHandler(Router(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/2.0/sql/statements/"
            ? Ok(Succeeded(StatementId, """[["1","a"]]"""))
            : null));
        var client = Client(handler);

        var result = await client.ExecuteAsync(Request(parameters: [new DatabricksParameter("p0", evil, "STRING"), new DatabricksParameter("p1", null, "INT")]), CancellationToken.None);

        result.Rows.Count.ShouldBe(1);
        var post = handler.Requests.Single(r => r.Uri.AbsolutePath == "/api/2.0/sql/statements/");
        post.Authorization.ShouldBe($"Bearer {AccessToken}");
        var body = Body(post);
        body.GetProperty("statement").GetString().ShouldBe("SELECT id, name FROM t WHERE a = :p0");
        body.GetProperty("statement").GetString()!.ShouldNotContain(evil);
        var parameters = body.GetProperty("parameters");
        parameters.GetArrayLength().ShouldBe(2);
        parameters[0].GetProperty("name").GetString().ShouldBe("p0");
        parameters[0].GetProperty("value").GetString().ShouldBe(evil);
        parameters[0].GetProperty("type").GetString().ShouldBe("STRING");
        parameters[1].TryGetProperty("value", out var nullValue).ShouldBeTrue();
        nullValue.ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("disposition").GetString().ShouldBe("INLINE");
        body.GetProperty("format").GetString().ShouldBe("JSON_ARRAY");
        body.GetProperty("warehouse_id").GetString().ShouldBe("wh-1");
        body.GetProperty("catalog").GetString().ShouldBe("main");
        body.GetProperty("schema").GetString().ShouldBe("default");
        body.GetProperty("on_wait_timeout").GetString().ShouldBe("CONTINUE");
        body.GetProperty("byte_limit").GetInt64().ShouldBe(26214400);
    }

    [Theory]
    [InlineData("p0", "STRING; DROP TABLE x")]
    [InlineData("p0", "")]
    [InlineData("p0 or 1=1", "STRING")]
    [InlineData("", "STRING")]
    public async Task InvalidParameterNameOrType_Rejected_NoRequestSent(string name, string type)
    {
        var handler = new FakeDatabricksHandler(Router(_ => null));
        var client = Client(handler);

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(parameters: [new DatabricksParameter(name, "1", type)]), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksProviderException>().Code.ShouldBe(DatabricksErrorCodes.InvalidRequest);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task PollsUntilSucceeded_ReadsInlineChunks()
    {
        var chunk1 = $"/api/2.0/sql/statements/{StatementId}/result/chunks/1";
        var chunk2 = $"/api/2.0/sql/statements/{StatementId}/result/chunks/2";
        int polls = 0;
        var handler = new FakeDatabricksHandler(Router(r =>
        {
            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                return Ok(State(StatementId, "RUNNING"));
            }

            if (r.Method == HttpMethod.Get && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}")
            {
                return ++polls < 2 ? Ok(State(StatementId, "RUNNING")) : Ok(Succeeded(StatementId, """[["1","a"],["2","b"]]""", chunk1));
            }

            if (r.Uri.AbsolutePath == chunk1)
            {
                return Ok("{\"chunk_index\":1,\"data_array\":[[\"3\",null]],\"next_chunk_internal_link\":\"" + chunk2 + "\"}");
            }

            return r.Uri.AbsolutePath == chunk2 ? Ok("""{"chunk_index":2,"data_array":[["4","d"]]}""") : null;
        }));
        var client = Client(handler);

        var result = await client.ExecuteAsync(Request(), CancellationToken.None);

        result.StatementId.ShouldBe(StatementId);
        result.Columns.Select(c => c.Name).ShouldBe(["id", "name"]);
        result.Columns.Select(c => c.TypeName).ShouldBe(["INT", "STRING"]);
        result.Rows.Select(r => string.Join('|', r.Select(c => c ?? "<null>"))).ShouldBe(["1|a", "2|b", "3|<null>", "4|d"]);
        polls.ShouldBe(2);
    }

    [Fact]
    public async Task FailedState_MapsToProviderError_WithoutDriverText()
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok("{\"statement_id\":\"" + StatementId + "\",\"status\":{\"state\":\"FAILED\",\"error\":{\"error_code\":\"BAD_REQUEST\",\"message\":\"org.apache.spark.SparkException at /internal/path tok-leak table secret_table\"}}}")));
        var client = Client(handler);

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        var provider = ex.ShouldBeOfType<DatabricksProviderException>();
        provider.Code.ShouldBe(DatabricksErrorCodes.StatementFailed);
        provider.Message.ShouldNotContain("SparkException");
        provider.Message.ShouldNotContain("secret_table");
        provider.Message.ShouldNotContain("/internal/path");
        provider.Message.ShouldContain("BAD_REQUEST");
    }

    [Fact]
    public async Task Cancellation_CallsCancelEndpoint()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeDatabricksHandler(Router(r =>
        {
            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                return Ok(State(StatementId, "RUNNING"));
            }

            if (r.Method == HttpMethod.Get)
            {
                cts.Cancel();
                return Ok(State(StatementId, "RUNNING"));
            }

            return r.Method == HttpMethod.Post && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}/cancel" ? Ok("{}") : null;
        }));
        var client = Client(handler);

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), cts.Token));

        ex.ShouldBeAssignableTo<OperationCanceledException>();
        handler.Requests.ShouldContain(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}/cancel");
    }

    [Fact]
    public async Task CancelAsync_PostsToCancelEndpoint_OfThatStatement()
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok("{}")));
        var client = Client(handler);

        await client.CancelAsync(StatementId, CancellationToken.None);

        handler.Requests.Last().Method.ShouldBe(HttpMethod.Post);
        handler.Requests.Last().Uri.AbsoluteUri.ShouldBe($"https://{Host}/api/2.0/sql/statements/{StatementId}/cancel");
    }

    [Fact]
    public async Task ResultOverByteLimit_FailsClosed_BodyTooLarge()
    {
        var big = "[" + string.Join(",", Enumerable.Range(0, 400).Select(i => $"[\"{i}\",\"{new string('x', 40)}\"]")) + "]";
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, big))));
        var client = Client(handler, Options(byteLimit: 4096));

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksResultTooLargeException>().Code.ShouldBe(DatabricksErrorCodes.ResultTooLarge);
    }

    [Fact]
    public async Task ResultOverByteLimit_FailsClosed_ResponseBufferCap()
    {
        var huge = "[[\"1\",\"" + new string('z', DatabricksStatementExecutionClient.ResponseOverheadBytes + 8192) + "\"]]";
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, huge))));
        var client = Client(handler, Options(byteLimit: 4096));

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksResultTooLargeException>();
    }

    [Fact]
    public async Task ResultOverByteLimit_FailsClosed_TruncatedManifest()
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, """[["1","a"]]""", truncated: true))));
        var client = Client(handler);

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksResultTooLargeException>();
    }

    [Fact]
    public async Task ResultOverByteLimit_FailsClosed_AccumulatedChunksTooLarge()
    {
        var chunk1 = $"/api/2.0/sql/statements/{StatementId}/result/chunks/1";
        var rows = "[" + string.Join(",", Enumerable.Range(0, 60).Select(i => $"[\"{i}\",\"{new string('y', 40)}\"]")) + "]";
        var handler = new FakeDatabricksHandler(Router(r => r.Method == HttpMethod.Post
            ? Ok(Succeeded(StatementId, rows, chunk1))
            : Ok("{\"chunk_index\":1,\"data_array\":" + rows + ",\"next_chunk_internal_link\":\"" + chunk1 + "\"}")));
        var client = Client(handler, Options(byteLimit: 4096));

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksResultTooLargeException>();
    }

    [Fact]
    public async Task Token_OnlyViaKeyVaultOutsideDevelopment()
    {
        var plaintextVault = NSubstitute.Substitute.For<Autheris.Application.Common.Interfaces.IKeyVaultSecretProvider>();
        plaintextVault.GetSecretBytes(NSubstitute.Arg.Any<string>()).Returns(ci => System.Text.Encoding.UTF8.GetBytes((string)ci[0]));
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler, vault: plaintextVault, environment: "Production");

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<SecurityException>();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Token_PlaintextSecret_AllowedInDevelopment()
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler, vault: null, environment: "Development");

        await client.ExecuteAsync(Request(), CancellationToken.None);

        handler.Requests.First().Uri.AbsolutePath.ShouldBe("/oidc/v1/token");
    }

    [Fact]
    public async Task Token_CachedPerHostAndClient_FetchedOnce()
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler);

        await client.ExecuteAsync(Request(), CancellationToken.None);
        await client.ExecuteAsync(Request(), CancellationToken.None);

        handler.Requests.Count(r => r.Uri.AbsolutePath == "/oidc/v1/token").ShouldBe(1);
    }

    [Theory]
    [InlineData("http://adb-123.azuredatabricks.net")]
    [InlineData("https://adb-123.azuredatabricks.net")]
    [InlineData("adb-123.azuredatabricks.net/evil")]
    [InlineData("adb-123.azuredatabricks.net:8443")]
    [InlineData("*.azuredatabricks.net")]
    [InlineData("user@adb-123.azuredatabricks.net")]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    [InlineData("localhost")]
    [InlineData("")]
    public async Task Host_NotAPlainDnsName_Rejected_NoRequestSent(string host)
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler, Options(host: host));

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksProviderException>().Code.ShouldBe(DatabricksErrorCodes.Configuration);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Host_HttpsOnly_AllRequestsGoToConfiguredHost()
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler);

        await client.ExecuteAsync(Request(), CancellationToken.None);

        handler.Requests.ShouldAllBe(r => r.Uri.Scheme == "https" && r.Uri.Host == Host && r.Uri.IsDefaultPort);
    }

    [Fact]
    public async Task Host_PassesSsrfPolicy_PrivateResolutionBlocked_ForDatabricksIntegration()
    {
        // The connect-time decision of the hardened primary handler (used by AddSecureOutboundHandlers("Databricks")).
        var allowlist = EgressAllowlist.Create(new OutboundEgressOptions(), EgressIntegrations.Databricks);

        await Should.ThrowAsync<SecurityException>(() => SecureOutboundHttp.ResolvePermittedAddressesAsync(
            Host, false, allowlist, (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }), CancellationToken.None));
        await Should.ThrowAsync<SecurityException>(() => SecureOutboundHttp.ResolvePermittedAddressesAsync(
            Host, false, allowlist, (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }), CancellationToken.None));
        var ok = await SecureOutboundHttp.ResolvePermittedAddressesAsync(
            Host, false, allowlist, (_, _) => Task.FromResult(new[] { IPAddress.Parse("20.50.1.1") }), CancellationToken.None);
        ok.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("../../api/2.0/clusters")]
    [InlineData("")]
    [InlineData("0123456789012345678901234567890123456")]
    [InlineData("01ef1234-5678-9abc-def0-123456789abc/../x")]
    [InlineData("01ef1234%2f5678-9abc-def0-123456789abc")]
    [InlineData("zzef1234-5678-9abc-def0-123456789abc")]
    public async Task StatementId_NotMatchingFormat_Rejected_NoRequestSent(string id)
    {
        // Server returns a hostile statement id: nothing but token + POST may be sent, never a follow-up with that id.
        var handler = new FakeDatabricksHandler(Router(_ => Ok(State(id, "RUNNING"))));
        var client = Client(handler);

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksProviderException>().Code.ShouldBe(DatabricksErrorCodes.ProtocolViolation);
        handler.Requests.Count(r => r.Uri.AbsolutePath != "/oidc/v1/token").ShouldBe(1);

        // CancelAsync validates its argument locally: no request at all.
        var cancelHandler = new FakeDatabricksHandler(Router(_ => Ok("{}")));
        var cancelEx = await CaptureAsync(() => Client(cancelHandler).CancelAsync(id, CancellationToken.None));
        cancelEx.ShouldBeOfType<DatabricksProviderException>().Code.ShouldBe(DatabricksErrorCodes.InvalidRequest);
        cancelHandler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://evil.example/api/2.0/sql/statements/01ef1234-5678-9abc-def0-123456789abc/result/chunks/1")]
    [InlineData("//evil.example/api/2.0/sql/statements/01ef1234-5678-9abc-def0-123456789abc/result/chunks/1")]
    [InlineData("/api/2.0/sql/statements/01ef9999-5678-9abc-def0-123456789abc/result/chunks/1")]
    [InlineData("/api/2.0/sql/statements/01ef1234-5678-9abc-def0-123456789abc/result/chunks/1/../../../../clusters")]
    [InlineData("/api/2.0/sql/statements/01ef1234-5678-9abc-def0-123456789abc/result/chunks/1?token=x")]
    [InlineData("/api/2.0/clusters/list")]
    [InlineData("result/chunks/1")]
    public async Task ChunkLink_AbsoluteOrForeignStatement_Rejected(string link)
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, """[["1","a"]]""", link))));
        var client = Client(handler);

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksProviderException>().Code.ShouldBe(DatabricksErrorCodes.ProtocolViolation);
        handler.Requests.ShouldAllBe(r => r.Uri.Host == Host);
        handler.Requests.Count(r => r.Uri.AbsolutePath != "/oidc/v1/token").ShouldBe(1);
    }

    [Fact]
    public async Task ExternalLinks_InResponse_Rejected()
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]", extra: ",\"external_links\":[{\"external_link\":\"https://evil.example/x\"}]"))));
        var client = Client(handler);

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksProviderException>().Code.ShouldBe(DatabricksErrorCodes.ProtocolViolation);
        handler.Requests.ShouldAllBe(r => r.Uri.Host == Host);
    }

    [Fact]
    public async Task Redirect_NotFollowed()
    {
        var handler = new FakeDatabricksHandler(Router(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/steal");
            return response;
        }));
        var client = Client(handler);

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksProviderException>();
        handler.Requests.ShouldAllBe(r => r.Uri.Host == Host);
    }

    [Fact]
    public void Redirect_PrimaryHandler_AutoRedirectDisabled_AndSsrfHandlerInChain()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options()));
        services.AddDatabricksStatementExecution();
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(DatabricksStatementExecutionClient));

        var chain = new List<HttpMessageHandler>();
        for (var h = handler; h is not null; h = (h as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(h);
        }

        chain.ShouldContain(h => h is SsrfProtectionHandler);
        chain.OfType<SocketsHttpHandler>().Single().AllowAutoRedirect.ShouldBeFalse();
    }

    [Fact]
    public void AuthorizationHeader_Redacted_InHttpClientLogs()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options()));
        services.AddDatabricksStatementExecution();
        using var provider = services.BuildServiceProvider();

        var factoryOptions = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(nameof(DatabricksStatementExecutionClient));

        factoryOptions.ShouldRedactHeaderValue("Authorization").ShouldBeTrue();
        factoryOptions.ShouldRedactHeaderValue("Content-Type").ShouldBeFalse();
    }

    [Fact]
    public async Task Pat_AcceptedInProduction()
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler, Options(DatabricksAuthMode.Pat), environment: "Production");

        var response = await client.ExecuteAsync(Request(), CancellationToken.None);

        response.StatementId.ShouldBe(StatementId);
        handler.Requests.Single().Authorization.ShouldBe($"Bearer {Pat}");
    }

    [Fact]
    public async Task Unauthorized_ReResolvesSecret_AndRetriesOnce()
    {
        var attempts = 0;
        var handler = new FakeDatabricksHandler(Router(r =>
        {
            if (r.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                attempts++;
                return attempts == 1
                    ? Json(HttpStatusCode.Unauthorized, "{}")
                    : Ok(Succeeded(StatementId, "[]"));
            }
            return null;
        }));
        var client = Client(handler);

        var result = await client.ExecuteAsync(Request(), CancellationToken.None);

        result.StatementId.ShouldBe(StatementId);
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task SecondUnauthorized_FailsWithAuthFailed()
    {
        var attempts = 0;
        var handler = new FakeDatabricksHandler(Router(r =>
        {
            if (r.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                attempts++;
                return Json(HttpStatusCode.Unauthorized, "{}");
            }
            return null;
        }));
        var client = Client(handler);

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeOfType<DatabricksProviderException>().Code.ShouldBe(DatabricksErrorCodes.AuthFailed);
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task PatAuth_AllowedInDevelopment_UsesPatAsBearer_NoTokenEndpoint()
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler, Options(DatabricksAuthMode.Pat), environment: "Development");

        await client.ExecuteAsync(Request(), CancellationToken.None);

        handler.Requests.ShouldAllBe(r => r.Uri.AbsolutePath != "/oidc/v1/token");
        handler.Requests.Single().Authorization.ShouldBe($"Bearer {Pat}");
    }

    [Fact]
    public async Task TokenEndpoint_DerivedFromHost_NotConfigurable()
    {
        new DatabricksAuthOptions().TokenEndpoint.ShouldBeNull();

        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        await Client(handler).ExecuteAsync(Request(), CancellationToken.None);

        var tokenRequest = handler.Requests.First();
        tokenRequest.Method.ShouldBe(HttpMethod.Post);
        tokenRequest.Uri.AbsoluteUri.ShouldBe($"https://{Host}/oidc/v1/token");
        tokenRequest.Authorization.ShouldStartWith("Basic ");
        tokenRequest.Body.ShouldContain("grant_type=client_credentials");
    }

    [Theory]
    [InlineData(1, "0s")]
    [InlineData(5, "5s")]
    [InlineData(30, "30s")]
    [InlineData(50, "50s")]
    [InlineData(120, "50s")]
    public async Task WaitTimeout_ClampedToZeroOr5To50s_OnWaitTimeoutContinue(int seconds, string expected)
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler, Options(commandTimeoutSeconds: 120));

        await client.ExecuteAsync(Request(wait: TimeSpan.FromSeconds(seconds)), CancellationToken.None);

        var body = Body(handler.Requests.Single(r => r.Uri.AbsolutePath == "/api/2.0/sql/statements/"));
        body.GetProperty("wait_timeout").GetString().ShouldBe(expected);
        body.GetProperty("on_wait_timeout").GetString().ShouldBe("CONTINUE");
    }

    [Fact]
    public async Task Body_UsesOnWaitTimeoutContinue()
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler);

        await client.ExecuteAsync(Request(), CancellationToken.None);

        var body = Body(handler.Requests.Single(r => r.Uri.AbsolutePath == "/api/2.0/sql/statements/"));
        body.GetProperty("on_wait_timeout").GetString().ShouldBe("CONTINUE");
    }

    [Theory]
    [InlineData(30, 20, "18s")]
    [InlineData(30, 30, "28s")]
    [InlineData(50, 120, "50s")]
    [InlineData(120, 120, "50s")]
    [InlineData(30, 7, "5s")]
    public async Task Body_WaitTimeout_BoundedByCommandTimeout(int waitSeconds, int commandTimeoutSeconds, string expected)
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler, Options(commandTimeoutSeconds: commandTimeoutSeconds));

        await client.ExecuteAsync(Request(wait: TimeSpan.FromSeconds(waitSeconds)), CancellationToken.None);

        var body = Body(handler.Requests.Single(r => r.Uri.AbsolutePath == "/api/2.0/sql/statements/"));
        body.GetProperty("wait_timeout").GetString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(30, 1)]
    [InlineData(30, 5)]
    [InlineData(30, 6)]
    [InlineData(4, 30)]
    [InlineData(1, 30)]
    public async Task Body_ShortCommandTimeout_SendsZeroWait(int waitSeconds, int commandTimeoutSeconds)
    {
        var handler = new FakeDatabricksHandler(Router(_ => Ok(Succeeded(StatementId, "[]"))));
        var client = Client(handler, Options(commandTimeoutSeconds: commandTimeoutSeconds));

        await client.ExecuteAsync(Request(wait: TimeSpan.FromSeconds(waitSeconds)), CancellationToken.None);

        var body = Body(handler.Requests.Single(r => r.Uri.AbsolutePath == "/api/2.0/sql/statements/"));
        body.GetProperty("wait_timeout").GetString().ShouldBe("0s");
    }

    [Fact]
    public async Task RunningAfterFirstWait_IsPolledUntilSucceeded()
    {
        var time = new PollTrackingTimeProvider();
        int polls = 0;
        var handler = new FakeDatabricksHandler(Router(r =>
        {
            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                return Ok(State(StatementId, "RUNNING"));
            }

            if (r.Method == HttpMethod.Get && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}")
            {
                // 12 polls * 5s = 60s (> 50s API wait cap, < 120s command timeout)
                return ++polls < 12
                    ? Ok(State(StatementId, "RUNNING"))
                    : Ok(Succeeded(StatementId, """[["42","long-ok"]]"""));
            }

            return null;
        }));
        var client = Client(handler, Options(commandTimeoutSeconds: 120, pollIntervalMilliseconds: 5000), timeProvider: time);

        var executeTask = client.ExecuteAsync(Request(wait: TimeSpan.FromSeconds(50)), CancellationToken.None);
        for (int i = 0; i < 12 && !executeTask.IsCompleted; i++)
        {
            (await time.DelayEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
            time.Advance(TimeSpan.FromSeconds(5));
        }

        var result = await executeTask.WaitAsync(TimeSpan.FromSeconds(5));

        polls.ShouldBe(12);
        result.StatementId.ShouldBe(StatementId);
        result.Rows.Single().ShouldBe(["42", "long-ok"]);
        var postBody = Body(handler.Requests.Single(r => r.Uri.AbsolutePath == "/api/2.0/sql/statements/"));
        postBody.GetProperty("wait_timeout").GetString().ShouldBe("50s");
        postBody.GetProperty("on_wait_timeout").GetString().ShouldBe("CONTINUE");
        handler.Requests.ShouldNotContain(r => r.Uri.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deadline_CancelsStatement_AndThrowsTimeout()
    {
        var time = new PollTrackingTimeProvider();
        var handler = new FakeDatabricksHandler(Router(r =>
        {
            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                return Ok(State(StatementId, "RUNNING"));
            }

            if (r.Method == HttpMethod.Get && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}")
            {
                return Ok(State(StatementId, "RUNNING"));
            }

            return r.Method == HttpMethod.Post && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}/cancel"
                ? Ok("{}")
                : null;
        }));
        var client = Client(handler, Options(commandTimeoutSeconds: 15, pollIntervalMilliseconds: 5000), timeProvider: time);

        var executeTask = client.ExecuteAsync(Request(wait: TimeSpan.FromSeconds(30)), CancellationToken.None);
        for (int i = 0; i < 3 && !executeTask.IsCompleted; i++)
        {
            if (await time.DelayEntered.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                time.Advance(TimeSpan.FromSeconds(5));
            }
        }

        if (!executeTask.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var ex = await CaptureAsync(() => executeTask.WaitAsync(TimeSpan.FromSeconds(5)));

        ex.ShouldBeOfType<DatabricksProviderException>().Code.ShouldBe(DatabricksErrorCodes.Timeout);
        handler.Requests.Count(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}/cancel").ShouldBe(1);
    }

    [Theory]
    [InlineData("changed-id", DatabricksErrorCodes.ProtocolViolation)]
    [InlineData("bad-json", DatabricksErrorCodes.ProtocolViolation)]
    [InlineData("unknown-state", DatabricksErrorCodes.ProtocolViolation)]
    [InlineData("http-500", DatabricksErrorCodes.Http)]
    [InlineData("too-large", DatabricksErrorCodes.ResultTooLarge)]
    public async Task ProtocolErrorWhileRunning_CancelsStatement(string scenario, string expectedCode)
    {
        var handler = new FakeDatabricksHandler(Router(r =>
        {
            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/2.0/sql/statements/")
            {
                return Ok(State(StatementId, "RUNNING"));
            }

            if (r.Method == HttpMethod.Get && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}")
            {
                return scenario switch
                {
                    "changed-id" => Ok(State(OtherStatementId, "RUNNING")),
                    "bad-json" => Ok("not-valid-json"),
                    "unknown-state" => Ok(State(StatementId, "WAITING_FOR_UNICORN")),
                    "http-500" => Json(HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}"),
                    "too-large" => Ok("{\"statement_id\":\"" + StatementId + "\",\"status\":{\"state\":\"RUNNING\"},\"pad\":\"" + new string('x', DatabricksStatementExecutionClient.ResponseOverheadBytes + 8192) + "\"}"),
                    _ => null
                };
            }

            return r.Method == HttpMethod.Post && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}/cancel"
                ? Ok("{}")
                : null;
        }));
        var client = Client(handler, Options(byteLimit: 4096));

        var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

        ex.ShouldBeAssignableTo<DatabricksProviderException>().Code.ShouldBe(expectedCode);
        handler.Requests.Count(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == $"/api/2.0/sql/statements/{StatementId}/cancel").ShouldBe(1);
    }

    [Fact]
    public async Task ReadColumns_ReadsPrecisionAndScale()
    {
        const string json = "{\"statement_id\":\"" + StatementId + "\",\"status\":{\"state\":\"SUCCEEDED\"}," +
                            "\"manifest\":{\"format\":\"JSON_ARRAY\",\"truncated\":false,\"schema\":{\"column_count\":3,\"columns\":[" +
                            "{\"name\":\"amt18\",\"type_name\":\"DECIMAL\",\"type_precision\":18,\"type_scale\":2,\"position\":0}," +
                            "{\"name\":\"amt38\",\"type_name\":\"DECIMAL\",\"type_precision\":38,\"type_scale\":10,\"position\":1}," +
                            "{\"name\":\"id\",\"type_name\":\"INT\",\"position\":2}]}}," +
                            "\"result\":{\"chunk_index\":0,\"row_offset\":0,\"data_array\":[[\"12.34\",\"12345678901234567890.1234567890\",\"7\"]]}}";
        var handler = new FakeDatabricksHandler(Router(_ => Ok(json)));
        var client = Client(handler);

        var result = await client.ExecuteAsync(Request(), CancellationToken.None);

        result.Columns.Length.ShouldBe(3);
        result.Columns[0].ShouldBe(new DatabricksColumn("amt18", "DECIMAL", 18, 2));
        result.Columns[1].ShouldBe(new DatabricksColumn("amt38", "DECIMAL", 38, 10));
        result.Columns[2].ShouldBe(new DatabricksColumn("id", "INT", null, null));
    }

    private sealed class PollTrackingTimeProvider : FakeTimeProvider
    {
        public SemaphoreSlim DelayEntered { get; } = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(5))
            {
                DelayEntered.Release();
            }

            return timer;
        }
    }

    [Fact]
    public async Task ErrorAndLogText_NeverContainToken()
    {
        var scenarios = new Func<RecordedRequest, HttpResponseMessage?>[]
        {
            r => Json(HttpStatusCode.Unauthorized, $"{{\"message\":\"bad token {AccessToken} {ClientSecret} {Pat}\"}}"),
            r => Json(HttpStatusCode.InternalServerError, $"echo Bearer {AccessToken} secret {ClientSecret}"),
            r => Ok("{\"statement_id\":\"" + StatementId + "\",\"status\":{\"state\":\"FAILED\",\"error\":{\"error_code\":\"X\",\"message\":\"" + AccessToken + " " + ClientSecret + "\"}}}"),
            r => Ok($"not json {AccessToken}"),
        };

        foreach (var scenario in scenarios)
        {
            var logger = new CapturingLogger<DatabricksStatementExecutionClient>();
            var handler = new FakeDatabricksHandler(Router(scenario));
            var client = Client(handler, logger: logger);

            var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));

            ex.ShouldBeAssignableTo<Exception>();
            var text = ex.ToString() + string.Join('\n', logger.Lines);
            text.ShouldNotContain(AccessToken);
            text.ShouldNotContain(ClientSecret);
            text.ShouldNotContain(Pat);
        }

        // Token endpoint failure echoing the secret.
        var tokenLogger = new CapturingLogger<DatabricksStatementExecutionClient>();
        var tokenHandler = new FakeDatabricksHandler(_ => Json(HttpStatusCode.BadRequest, $"invalid client secret {ClientSecret}"));
        var tokenEx = await CaptureAsync(() => Client(tokenHandler, logger: tokenLogger).ExecuteAsync(Request(), CancellationToken.None));
        (tokenEx.ToString() + string.Join('\n', tokenLogger.Lines)).ShouldNotContain(ClientSecret);
    }

    [Fact]
    public void Options_EnumDefaultsToOAuthM2M()
    {
        new DatabricksAuthOptions().Mode.ShouldBe(DatabricksAuthMode.OAuthM2M);
    }
}
