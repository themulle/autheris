namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Olap;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

/// <summary>
/// ARCH-MNT-08 (SC-01..SC-09): no exception text reaches a client in any environment. Every site throws an exception whose
/// message carries a sentinel; the response must hold the governed code and status and never the sentinel.
/// </summary>
[Trait("Category", "Security")]
public sealed class RawExceptionMessageNeverReachesClientTests
{
    private static readonly string Sentinel = "SENTINEL-" + Guid.NewGuid().ToString("N");

    private sealed record Captured(int Status, string ContentType, string Body, IHeaderDictionary Headers)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    private static DefaultHttpContext Ctx(string[]? roles = null, bool development = false, Action<IServiceCollection>? configure = null, string body = "{}")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "caller"),
            new(ClaimTypes.NameIdentifier, "S-1-5-21-CALLER"),
            new("client_id", "client-1"),
            new("user_sid", "S-1-5-21-CALLER")
        };
        claims.AddRange((roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));
        var services = new ServiceCollection().AddLogging();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(development ? Environments.Development : Environments.Production);
        services.AddSingleton(env);
        configure?.Invoke(services);
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
            RequestServices = services.BuildServiceProvider()
        };
        ctx.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-CALLER"),
            TenantId = new TenantId("tenant-1"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(roles ?? []),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<Captured> RunAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        return Read(ctx);
    }

    private static Captured Read(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        var text = new StreamReader(ctx.Response.Body).ReadToEnd();
        return new Captured(ctx.Response.StatusCode, ctx.Response.ContentType ?? string.Empty, text, ctx.Response.Headers);
    }

    private static void AssertGoverned(Captured c, int status, string code)
    {
        c.Status.ShouldBe(status);
        c.Body.ShouldNotContain(Sentinel);
        c.Headers.Any(h => h.Value.ToString().Contains(Sentinel, StringComparison.Ordinal)).ShouldBeFalse();
        c.Json.GetProperty("code").GetString().ShouldBe(code);
        c.Json.TryGetProperty("correlationId", out _).ShouldBeTrue();
    }

    // ---------------------------------------------------------------- ARCH-MNT-08-T-01 / T-02: per-site

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_Catalog_InvalidSecretRef()
    {
        var svc = Substitute.For<ICatalogDiscoveryService>();
        svc.RegisterDatasourceAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new ArgumentException(Sentinel, "secretRef"));
        var ctx = Ctx(["GovernanceAdmin"]);

        var c = await RunAsync(await CatalogApiEndpoints.RegisterDatasourceHandler(new DatasourceRegistrationRequest("n", "d"), svc, ctx, CancellationToken.None), ctx);

        AssertGoverned(c, 400, ApiErrorCodes.InvalidSecretRef);
        c.ContentType.ShouldStartWith("application/problem+json");
    }

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_DataContract_InvalidInput()
    {
        var engine = Substitute.For<IDataContractEngine>();
        engine.RegisterContractAsync(default!, default).ThrowsAsyncForAnyArgs(new ArgumentException(Sentinel));
        var ctx = Ctx(["GovernanceAdmin"]);

        var c = await RunAsync(await DataContractEndpoints.HandleApplyAsync(ctx, engine), ctx);

        AssertGoverned(c, 400, ApiErrorCodes.DataContractInvalid);
        c.Json.GetProperty("detail").GetString().ShouldBe("The data contract is invalid.");
    }

    [Fact]
    public async Task UnexpectedException_Returns500_NotMapped400_DataContract()
    {
        var engine = Substitute.For<IDataContractEngine>();
        engine.RegisterContractAsync(default!, default).ThrowsAsyncForAnyArgs(new NotSupportedException(Sentinel));
        var ctx = Ctx(["GovernanceAdmin"]);

        await Should.ThrowAsync<NotSupportedException>(async () => await DataContractEndpoints.HandleApplyAsync(ctx, engine));
    }

    [Fact]
    public async Task Cancellation_IsNotMappedToProblem_DataContract()
    {
        var engine = Substitute.For<IDataContractEngine>();
        engine.RegisterContractAsync(default!, default).ThrowsAsyncForAnyArgs(new OperationCanceledException(Sentinel));
        var ctx = Ctx(["GovernanceAdmin"]);

        await Should.ThrowAsync<OperationCanceledException>(async () => await DataContractEndpoints.HandleApplyAsync(ctx, engine));
    }

    private sealed class Unserializable
    {
        public int Value => throw new InvalidOperationException(Sentinel);
    }

    private static async Task<(DefaultHttpContext Ctx, Captured Result)> RunOlapAsync(OlapStagingOutcome outcome, bool development, bool responseStarted = false)
    {
        var staging = Substitute.For<IOlapStagingService>();
        staging.ExecuteAsync(default!, default!, default!, default, default!, default).ReturnsForAnyArgs(outcome);
        var ctx = Ctx(["GovernanceAdmin"], development, body: "{\"sql\":\"SELECT 1\"}");
        if (responseStarted)
        {
            ctx.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        }

        await DuckDbOlapEndpoints.HandleOlapQueryAsync(
            ctx, staging, Options.Create(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { Enabled = true } }), NullLoggerFactory.Instance);
        return (ctx, Read(ctx));
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawExceptionMessage_NeverReachesTheClient_Olap_WriteFailure(bool development)
    {
        var outcome = new OlapStagingOutcome(200, Result: new OlapQueryResult(["x"], [new List<object?> { new Unserializable() }], 1, TimeSpan.Zero));

        var (_, c) = await RunOlapAsync(outcome, development);

        AssertGoverned(c, 400, ApiErrorCodes.OlapQueryFailed);
        c.Json.GetProperty("detail").GetString().ShouldBe("Analytical query execution failed.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RawExceptionMessage_NeverReachesTheClient_Olap_ServiceDetailsAreNeverWritten(bool development)
    {
        var outcome = new OlapStagingOutcome(502, "The analytical request could not be completed.", Sentinel, ErrorCode: "OLAP_FAILED");

        var (_, c) = await RunOlapAsync(outcome, development);

        c.Status.ShouldBe(502);
        c.Body.ShouldNotContain(Sentinel);
    }

    [Fact]
    public async Task OlapError_AfterResponseStarted_DoesNotAppendMessage()
    {
        var outcome = new OlapStagingOutcome(200, Result: new OlapQueryResult(["x"], [new List<object?> { new Unserializable() }], 1, TimeSpan.Zero));

        var (_, c) = await RunOlapAsync(outcome, development: true, responseStarted: true);

        c.Body.ShouldNotContain(Sentinel);
        c.Body.ShouldNotContain("Analytical query execution failed.");
    }

    [Fact]
    public async Task PrivacyBudget429_KeepsExtensions_NoMessage()
    {
        var engine = Substitute.For<IDifferentialPrivacyEngine>();
        engine.PerturbAsync(default!, default).ThrowsAsyncForAnyArgs(new PrivacyBudgetExhaustedException("client-1", 4.5, 5.0));
        var ctx = Ctx();

        var c = await RunAsync(await GovernanceEndpoints.HandlePerturbAsync(new DifferentialPrivacyPerturbationRequest("client-1", 1.0), engine, ctx), ctx);

        AssertGoverned(c, 429, ApiErrorCodes.PrivacyBudgetExhausted);
        c.Headers["X-Privacy-Budget-Exhausted"].ToString().ShouldBe("true");
        c.Json.GetProperty("clientId").GetString().ShouldBe("client-1");
        c.Json.GetProperty("consumed").GetDouble().ShouldBe(4.5);
        c.Json.GetProperty("totalBudget").GetDouble().ShouldBe(5.0);
        c.Body.ShouldNotContain("Epsilon privacy budget exhausted");
    }

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_RowFilterSyntax_InDevelopment()
    {
        var repo = Substitute.For<IAccessProfileRepository>();
        var ctx = Ctx(["GovernanceAdmin"], development: true);
        var req = new BulkConsentRequest { Subject = "alice", Justification = "A sufficiently long justification", ValidDays = 30, MaskingMode = "Default", Tables = ["sales.*"], RowFilter = $"x = '{Sentinel}' AND (" };

        var c = await RunAsync(await GovernanceEndpoints.CreateBulkConsentAsync(req, ctx, repo, CancellationToken.None), ctx);

        AssertGoverned(c, 400, ApiErrorCodes.RowFilterSyntaxInvalid);
        c.Json.GetProperty("detail").GetString().ShouldBe("The row filter predicate is not valid SQL.");
        c.Body.ShouldNotContain("Invalid row filter syntax");
    }

    [Fact]
    public async Task AuditDetails_ContainTypeNotMessage_DeleteProfile()
    {
        var entries = new List<AuditLogEntry>();
        var writer = Substitute.For<IAuditWriter>();
        writer.RecordRequiredAsync(Arg.Do<AuditLogEntry>(entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var cache = Substitute.For<IAccessProfileCache>();
        cache.InvalidateTenantAsync(default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException(Sentinel));
        var repo = Substitute.For<IAccessProfileRepository>();
        repo.GetProfileAsync(default, default!, default).ReturnsForAnyArgs(new AccessProfile { ProfileId = "p1", Name = "n", TenantId = new TenantId("tenant-1"), TargetTables = ["a.b"], Assignments = TestAssign.Users() });
        var ctx = Ctx(["GovernanceAdmin"], configure: s => s.AddSingleton(writer).AddSingleton(cache));

        var result = await GovernanceEndpoints.DeleteProfileAsync("p1", ctx, repo, CancellationToken.None);
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(503);
        var failure = entries.Single(e => e.EventType == AuditEventTypes.ProfileInvalidationFailed);
        failure.DetailsJson.ShouldNotContain(Sentinel);
        using var doc = JsonDocument.Parse(failure.DetailsJson);
        doc.RootElement.GetProperty("reasonType").GetString().ShouldBe(nameof(InvalidOperationException));
        doc.RootElement.TryGetProperty("reason", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AuditDetails_ContainTypeNotMessage_BulkConsent()
    {
        var entries = new List<AuditLogEntry>();
        var writer = Substitute.For<IAuditWriter>();
        writer.RecordRequiredAsync(Arg.Do<AuditLogEntry>(entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var cache = Substitute.For<IAccessProfileCache>();
        cache.InvalidateTenantAsync(default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException(Sentinel));
        var repo = Substitute.For<IAccessProfileRepository>();
        var ctx = Ctx(["GovernanceAdmin"], configure: s => s.AddSingleton(writer).AddSingleton(cache));
        var req = new BulkConsentRequest { Subject = "alice", Justification = "A sufficiently long justification", ValidDays = 30, MaskingMode = "Default", Tables = ["sales.*"] };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(req, ctx, repo, CancellationToken.None);
        await result.ExecuteAsync(ctx);

        var failure = entries.Single(e => e.EventType == AuditEventTypes.ProfileInvalidationFailed);
        failure.DetailsJson.ShouldNotContain(Sentinel);
        using var doc = JsonDocument.Parse(failure.DetailsJson);
        doc.RootElement.GetProperty("reasonType").GetString().ShouldBe(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task AuditDetails_ContainTypeNotMessage_Rebac()
    {
        var writer = Substitute.For<IAuditWriter>();
        AuditLogEntry? captured = null;
        writer.RecordAsync(Arg.Do<AuditLogEntry>(e => captured = e), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var ctx = Ctx(["GovernanceAdmin"]);

        await RebacEndpoints.AuditInvalidationFailureAsync(writer, ctx, "tenant-1", new InvalidOperationException(Sentinel));

        captured.ShouldNotBeNull();
        captured!.DetailsJson.ShouldNotContain(Sentinel);
        using var doc = JsonDocument.Parse(captured.DetailsJson);
        doc.RootElement.GetProperty("reasonType").GetString().ShouldBe(nameof(InvalidOperationException));
        doc.RootElement.GetProperty("tenant").GetString().ShouldBe("tenant-1");
    }

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_Plan_Confirm_SecurityException_Is403()
    {
        var svc = Substitute.For<IAccessPlanningService>();
        svc.ConfirmPlanAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(new SecurityException(Sentinel));
        var ctx = Ctx(["GovernanceAdmin"]);

        var c = await RunAsync(await GovernancePlanEndpoints.HandleConfirmAsync("p1", new AdminConfirmPlanRequest("p1", "123456"), ctx, svc, CancellationToken.None), ctx);

        AssertGoverned(c, 403, ApiErrorCodes.AccessDenied);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task RawExceptionMessage_NeverReachesTheClient_Plan_Confirm_InvalidInput_Is400(Type type)
    {
        var svc = Substitute.For<IAccessPlanningService>();
        svc.ConfirmPlanAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs((Exception)Activator.CreateInstance(type, Sentinel)!);
        var ctx = Ctx(["GovernanceAdmin"]);

        var c = await RunAsync(await GovernancePlanEndpoints.HandleConfirmAsync("p1", new AdminConfirmPlanRequest("p1", "123456"), ctx, svc, CancellationToken.None), ctx);

        AssertGoverned(c, 400, ApiErrorCodes.GovernancePlanInvalid);
    }

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_Plan_Apply()
    {
        var svc = Substitute.For<IAccessPlanningService>();
        svc.ApplyAccessAsync(default!, default!, default, default, default).ThrowsAsyncForAnyArgs(new SecurityException(Sentinel));
        var ctx = Ctx(["GovernanceAdmin"]);

        var c = await RunAsync(await GovernancePlanEndpoints.HandleApplyAsync("p1", new AdminApplyAccessRequest("p1", "tok"), ctx, svc, CancellationToken.None), ctx);

        AssertGoverned(c, 403, ApiErrorCodes.AccessDenied);
    }

    [Theory]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task UnexpectedException_Returns500_NotMapped400_Plan(Type type)
    {
        var svc = Substitute.For<IAccessPlanningService>();
        svc.ConfirmPlanAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs((Exception)Activator.CreateInstance(type, Sentinel)!);
        var ctx = Ctx(["GovernanceAdmin"]);

        var thrown = await Record.ExceptionAsync(async () => await GovernancePlanEndpoints.HandleConfirmAsync("p1", new AdminConfirmPlanRequest("p1", "123456"), ctx, svc, CancellationToken.None));

        thrown.ShouldNotBeNull();
        type.IsInstanceOfType(thrown).ShouldBeTrue();
    }

    // ---------------------------------------------------------------- Iceberg (ruling 11)

    [Theory]
    [InlineData(typeof(SecurityException), 403, "ACCESS_DENIED", "Access denied.")]
    [InlineData(typeof(ArgumentException), 400, "BAD_REQUEST", "The request is invalid.")]
    [InlineData(typeof(NotSupportedException), 501, "NOT_IMPLEMENTED", "Not implemented.")]
    [InlineData(typeof(TimeoutException), 500, "INTERNAL_ERROR", "An unexpected error occurred.")]
    public async Task Iceberg_ErrorsUseErrorModel_GenericMessage(Type type, int status, string code, string message)
    {
        foreach (var development in new[] { true, false })
        {
            var ctx = Ctx(development: development);
            var ex = (Exception)Activator.CreateInstance(type, Sentinel)!;

            var c = await RunAsync(IcebergRestCatalogEndpoints.HandleIcebergError(ctx, ex), ctx);

            c.Status.ShouldBe(status);
            c.Body.ShouldNotContain(Sentinel);
            var error = c.Json.GetProperty("error");
            error.GetProperty("message").GetString().ShouldBe(message);
            error.GetProperty("type").GetString().ShouldBe(code);
            error.GetProperty("code").GetInt32().ShouldBe(status);
        }
    }

    // ---------------------------------------------------------------- Procedures

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_Procedure_ArgumentInvalid()
    {
        var svc = Substitute.For<IProcedureExecutionService>();
        svc.ExecuteAsync(default!, default, default!, default, default).ThrowsAsyncForAnyArgs(new ArgumentException(Sentinel));
        var ctx = Ctx();

        await ProcedureEndpointRoutes.ExecuteAsync("proc", null, ctx, svc, NullLoggerFactory.Instance);

        var c = Read(ctx);
        AssertGoverned(c, 400, ApiErrorCodes.ProcedureArgumentInvalid);
        c.ContentType.ShouldStartWith("application/problem+json");
    }

    // ---------------------------------------------------------------- Virtual filters

    private static async Task<Captured> MapAsync(Exception ex, bool development = true)
    {
        var ctx = Ctx(development: development);
        var result = await VirtualFilterEndpoints.MapErrorsAsync(() => throw ex, ctx);
        return await RunAsync(result, ctx);
    }

    [Theory]
    [InlineData(typeof(VirtualFilterConflictException), 409, "VIRTUAL_FILTER_CONFLICT")]
    [InlineData(typeof(ManagedResourceLockedException), 409, "RESOURCE_LOCKED")]
    [InlineData(typeof(KeyNotFoundException), 404, "RESOURCE_NOT_FOUND")]
    [InlineData(typeof(InvalidOperationException), 400, "VIRTUAL_FILTER_INVALID")]
    [InlineData(typeof(ArgumentException), 400, "BAD_REQUEST")]
    [InlineData(typeof(FormatException), 400, "BAD_REQUEST")]
    public async Task RawExceptionMessage_NeverReachesTheClient_VirtualFilter(Type type, int status, string code)
    {
        var c = await MapAsync((Exception)Activator.CreateInstance(type, Sentinel)!);

        AssertGoverned(c, status, code);
    }

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_VirtualFilter_JsonException_IsInvalidJson()
    {
        var c = await MapAsync(new JsonException(Sentinel));

        AssertGoverned(c, 400, ApiErrorCodes.InvalidJson);
    }

    [Fact]
    public async Task VirtualFilter_GovernedBindingCode_IsReturnedWithFixedDetail()
    {
        var c = await MapAsync(new VirtualFilterBindingException(VirtualFilterErrorCodes.BindingInvalid, Sentinel));

        AssertGoverned(c, 400, VirtualFilterErrorCodes.BindingInvalid);
        c.Json.GetProperty("detail").GetString().ShouldBe("The virtual filter binding is invalid.");
    }

    [Fact]
    public async Task VirtualFilter_UnknownCode_MapsToBadRequest()
    {
        var c = await MapAsync(new VirtualFilterBindingException("CODE-" + Sentinel, "m"));

        AssertGoverned(c, 400, ApiErrorCodes.BadRequest);
        c.Body.ShouldNotContain("CODE-");
    }

    [Fact]
    public async Task Cancellation_IsNotMappedToProblem_VirtualFilter()
    {
        await Should.ThrowAsync<OperationCanceledException>(async () => await MapAsync(new OperationCanceledException(Sentinel)));
    }

    [Fact]
    public void VirtualFilterErrorCodes_GovernedList_CoversEveryPublicCode()
    {
        var all = typeof(VirtualFilterErrorCodes).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();

        all.ShouldAllBe(code => VirtualFilterErrorCodes.IsGoverned(code));
        VirtualFilterErrorCodes.IsGoverned("NOPE").ShouldBeFalse();
        VirtualFilterErrorCodes.IsGoverned(null).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- MCP stdio (ruling 13)

    private static async Task<string> RunMcpAsync(Exception ex, params string[] inputs)
    {
        var handler = Substitute.For<IMcpProtocolHandler>();
        var now = DateTimeOffset.UtcNow;
        handler.CreateSession(Arg.Any<string>(), Arg.Any<string>()).Returns(new McpSessionContext("s1", "cli", "default", now, now, "S-1", ["Developer"], []));
#pragma warning disable CA2012
        handler.HandleMessageAsync("s1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<ValueTask<string>>(_ => throw ex);
#pragma warning restore CA2012
        var output = new StringWriter();
        await new McpStdioRunner(handler).RunAsync(new StringReader(string.Join(Environment.NewLine, inputs) + Environment.NewLine), output, tenantId: "tenant-a");
        return output.ToString();
    }

    [Fact]
    public async Task McpStdio_InternalError_IsGenericWithCorrelationId()
    {
        var text = await RunMcpAsync(new InvalidOperationException(Sentinel), "{\"jsonrpc\":\"2.0\",\"method\":\"x\",\"id\":1}");

        text.ShouldNotContain(Sentinel);
        using var doc = JsonDocument.Parse(text.Trim());
        var root = doc.RootElement;
        root.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
        root.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetInt32().ShouldBe(-32603);
        error.GetProperty("message").GetString().ShouldBe("Internal error");
        Guid.TryParse(error.GetProperty("data").GetProperty("correlationId").GetString(), out _).ShouldBeTrue();
    }

    [Fact]
    public async Task McpStdio_NothingExceptJsonRpcOnStdout()
    {
        var text = await RunMcpAsync(new InvalidOperationException(Sentinel), "{\"a\":1}", "{\"b\":2}");

        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        lines.ShouldAllBe(l => JsonDocument.Parse(l).RootElement.GetProperty("jsonrpc").GetString() == "2.0");
        text.ShouldNotContain(Sentinel);
        text.ShouldNotContain("InvalidOperationException");
    }

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_SqlListError_UnmappedCode_UsesFixedText()
    {
        var ctx = Ctx(development: true);

        await SqlEndpointRoutes.WriteListErrorAsync(ctx, new TrinoSqlEngine.Ast.Emit.ClientListException(Sentinel), CancellationToken.None);

        var c = Read(ctx);
        c.Status.ShouldBe(400);
        // The code is an engine constant (never caller data); the human-readable text must be fixed and free of the marker.
        c.Json.GetProperty("error").GetString()!.ShouldNotContain(Sentinel);
    }
}
