#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Api;

using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Tests.Unit.TestSupport;
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
using Autheris.Api.Middleware;
using Autheris.Api.Mcp;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Olap;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.Policy.Pdp;
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
        IHostEnvironment env = new TestHostEnvironment(development ? Environments.Development : Environments.Production);
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
        var svc = new StubCatalogDiscoveryService
        {
            OnRegisterDatasource = (_, _) => Task.FromException<DatasourceRegistrationResult>(new ArgumentException(Sentinel, "secretRef"))
        };
        var ctx = Ctx(["GovernanceAdmin", "ClusterAdmin"]);

        var c = await RunAsync(await CatalogApiEndpoints.RegisterDatasourceHandler(new DatasourceRegistrationRequest("n", "d"), svc, ctx, CancellationToken.None), ctx);

        AssertGoverned(c, 400, ApiErrorCodes.InvalidSecretRef);
        c.ContentType.ShouldStartWith("application/problem+json");
    }

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_DataContract_InvalidInput()
    {
        var engine = new ThrowingDataContractEngine(() => new ArgumentException(Sentinel));
        var ctx = Ctx(["ClusterAdmin"]);

        var c = await RunAsync(await DataContractEndpoints.HandleApplyAsync(ctx, engine), ctx);

        AssertGoverned(c, 400, ApiErrorCodes.DataContractInvalid);
        c.Json.GetProperty("detail").GetString().ShouldBe("The data contract is invalid.");
    }

    [Fact]
    public async Task UnexpectedException_Returns500_NotMapped400_DataContract()
    {
        var engine = new ThrowingDataContractEngine(() => new NotSupportedException(Sentinel));
        var ctx = Ctx(["ClusterAdmin"]);

        await Should.ThrowAsync<NotSupportedException>(async () => await DataContractEndpoints.HandleApplyAsync(ctx, engine));
    }

    [Fact]
    public async Task Cancellation_IsNotMappedToProblem_DataContract()
    {
        var engine = new ThrowingDataContractEngine(() => new OperationCanceledException(Sentinel));
        var ctx = Ctx(["ClusterAdmin"]);

        await Should.ThrowAsync<OperationCanceledException>(async () => await DataContractEndpoints.HandleApplyAsync(ctx, engine));
    }

    private sealed class Unserializable
    {
        public int Value => throw new InvalidOperationException(Sentinel);
    }

    private static async Task<(DefaultHttpContext Ctx, Captured Result)> RunOlapAsync(OlapStagingOutcome outcome, bool development, bool responseStarted = false)
    {
        var staging = new FixedOlapStagingService(outcome);
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

    [Theory]
    [InlineData(400, "BAD_REQUEST")]
    [InlineData(401, "UNAUTHORIZED")]
    [InlineData(403, "ACCESS_DENIED")]
    [InlineData(404, "RESOURCE_NOT_FOUND")]
    [InlineData(429, "TOO_MANY_REQUESTS")]
    [InlineData(502, "INTERNAL_ERROR")]
    [InlineData(504, "GATEWAY_TIMEOUT")]
    [Trait("Category", "Security")]
    public async Task Olap_Outcome_Statuses_Keep_Status_And_Carry_Governed_Code_Without_Service_Text(int status, string expectedDefaultCode)
    {
        // NL-T-20/21 backfill: the outcome text and detail never reach the body; the code is the outcome's governed code or the status default.
        var outcome = new OlapStagingOutcome(status, Sentinel, Sentinel, ErrorCode: "NOT_A_GOVERNED_CODE_" + status);

        var (_, c) = await RunOlapAsync(outcome, development: false);

        c.Status.ShouldBe(status);
        c.Body.ShouldNotContain(Sentinel);
        c.Json.GetProperty("code").GetString().ShouldBe(expectedDefaultCode);
        c.Json.TryGetProperty("error", out _).ShouldBeFalse();
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
    public async Task RawExceptionMessage_NeverReachesTheClient_RowFilterSyntax_InDevelopment()
    {
        var repo = new Autheris.Infrastructure.Persistence.InMemoryAccessProfileRepository();
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
        var writer = new InMemoryAuditWriter();
        var cache = new StubAccessProfileCache { OnInvalidateTenant = (_, _) => Task.FromException(new InvalidOperationException(Sentinel)) };
        var repo = new Autheris.Infrastructure.Persistence.InMemoryAccessProfileRepository();
        await repo.UpsertProfileAsync(new AccessProfile { ProfileId = "p1", Name = "n", TenantId = new TenantId("tenant-1"), TargetTables = ["a.b"], Assignments = TestAssign.Users() });
        var ctx = Ctx(["GovernanceAdmin"], configure: s => s.AddSingleton<IAuditWriter>(writer).AddSingleton<IAccessProfileCache>(cache));

        var result = await GovernanceEndpoints.DeleteProfileAsync("p1", ctx, repo, CancellationToken.None);
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(503);
        var failure = writer.Entries.Single(e => e.EventType == AuditEventTypes.ProfileInvalidationFailed);
        failure.DetailsJson.ShouldNotContain(Sentinel);
        using var doc = JsonDocument.Parse(failure.DetailsJson);
        doc.RootElement.GetProperty("reasonType").GetString().ShouldBe(nameof(InvalidOperationException));
        doc.RootElement.TryGetProperty("reason", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AuditDetails_ContainTypeNotMessage_BulkConsent()
    {
        var writer = new InMemoryAuditWriter();
        var cache = new StubAccessProfileCache { OnInvalidateTenant = (_, _) => Task.FromException(new InvalidOperationException(Sentinel)) };
        var repo = new Autheris.Infrastructure.Persistence.InMemoryAccessProfileRepository();
        var ctx = Ctx(["GovernanceAdmin"], configure: s => s.AddSingleton<IAuditWriter>(writer).AddSingleton<IAccessProfileCache>(cache));
        var req = new BulkConsentRequest { Subject = "alice", Justification = "A sufficiently long justification", ValidDays = 30, MaskingMode = "Default", Tables = ["sales.*"] };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(req, ctx, repo, CancellationToken.None);
        await result.ExecuteAsync(ctx);

        var failure = writer.Entries.Single(e => e.EventType == AuditEventTypes.ProfileInvalidationFailed);
        failure.DetailsJson.ShouldNotContain(Sentinel);
        using var doc = JsonDocument.Parse(failure.DetailsJson);
        doc.RootElement.GetProperty("reasonType").GetString().ShouldBe(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_Plan_Confirm_SecurityException_Is403()
    {
        var svc = new StubAccessPlanningService
        {
            OnConfirmPlan = (_, _, _) => Task.FromException<AdminConfirmPlanResult>(new SecurityException(Sentinel))
        };
        var ctx = Ctx(["GovernanceAdmin"]);

        var c = await RunAsync(await GovernancePlanEndpoints.HandleConfirmAsync("p1", new AdminConfirmPlanRequest("p1", "123456"), ctx, svc, CancellationToken.None), ctx);

        AssertGoverned(c, 403, ApiErrorCodes.AccessDenied);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task RawExceptionMessage_NeverReachesTheClient_Plan_Confirm_InvalidInput_Is400(Type type)
    {
        var svc = new StubAccessPlanningService
        {
            OnConfirmPlan = (_, _, _) => Task.FromException<AdminConfirmPlanResult>((Exception)Activator.CreateInstance(type, Sentinel)!)
        };
        var ctx = Ctx(["GovernanceAdmin"]);

        var c = await RunAsync(await GovernancePlanEndpoints.HandleConfirmAsync("p1", new AdminConfirmPlanRequest("p1", "123456"), ctx, svc, CancellationToken.None), ctx);

        AssertGoverned(c, 400, ApiErrorCodes.GovernancePlanInvalid);
    }

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_Plan_Apply()
    {
        var svc = new StubAccessPlanningService
        {
            OnApplyAccess = (_, _, _, _) => Task.FromException<AdminApplyAccessResult>(new SecurityException(Sentinel))
        };
        var ctx = Ctx(["GovernanceAdmin"]);

        var c = await RunAsync(await GovernancePlanEndpoints.HandleApplyAsync("p1", new AdminApplyAccessRequest("p1", "tok"), ctx, svc, CancellationToken.None), ctx);

        AssertGoverned(c, 403, ApiErrorCodes.AccessDenied);
    }

    [Theory]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task UnexpectedException_Returns500_NotMapped400_Plan(Type type)
    {
        var svc = new StubAccessPlanningService
        {
            OnConfirmPlan = (_, _, _) => Task.FromException<AdminConfirmPlanResult>((Exception)Activator.CreateInstance(type, Sentinel)!)
        };
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
        var svc = new ThrowingProcedureExecutionService(() => new ArgumentException(Sentinel));
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

    // ---------------------------------------------------------------- MCP HTTP (replaces deleted MCP stdio tests)

    [Fact]
    public async Task McpHttp_GuardrailDecisionFailure_NeverLeaksRawExceptionMessage()
    {
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } });
        var pdp = AccessDecisionPointStubs.Throwing(() => new InvalidOperationException(Sentinel));
        var guardrail = new AiDataGuardrailService(
            registry,
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            decisionPoint: pdp,
            tableMetadataRepository: Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AnyTable());

        var now = DateTimeOffset.UtcNow;
        var session = new McpSessionContext("http-s1", "client-1", "tenant-1", now, now, "S-1-5-21-CALLER", ["Developer"], []);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_customers", "{\"domain\":\"sales\",\"table\":\"customers\",\"sql\":\"SELECT 1\"}"),
            session,
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        (result.ErrorMessage ?? string.Empty).ShouldNotContain(Sentinel);
        (result.ErrorMessage ?? string.Empty).ShouldNotContain("InvalidOperationException");
        result.ContentJson.ShouldNotContain(Sentinel);
        result.ContentJson.ShouldNotContain("InvalidOperationException");
    }

    [Fact]
    public async Task McpHttp_UnhandledException_OnMcpRoute_ReturnsGenericProblemWithoutExceptionText()
    {
        var ctx = Ctx(development: true);
        ctx.Request.Path = "/mcp";
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(ctx, new InvalidOperationException(Sentinel), CancellationToken.None);

        handled.ShouldBeTrue();
        var c = Read(ctx);
        c.Status.ShouldBe(500);
        c.ContentType.ShouldStartWith("application/problem+json");
        c.Json.GetProperty("code").GetString().ShouldBe(ApiErrorCodes.InternalError);
        c.Json.TryGetProperty("traceId", out _).ShouldBeTrue();
        c.Body.ShouldNotContain(Sentinel);
        c.Body.ShouldNotContain("InvalidOperationException");
    }

    [Fact]
    public async Task RawExceptionMessage_NeverReachesTheClient_SqlListError_UnmappedCode_UsesFixedText()
    {
        var ctx = Ctx(development: true);

        await SqlEndpointRoutes.WriteListErrorAsync(ctx, new TrinoSqlEngine.Ast.Emit.ClientListException(Sentinel), CancellationToken.None);

        var c = Read(ctx);
        c.Status.ShouldBe(400);
        // The code is an engine constant (never caller data); the human-readable text must be fixed and free of the marker.
        c.Json.GetProperty("detail").GetString()!.ShouldNotContain(Sentinel);
        // NL-SC-27: an ungoverned code is never echoed; the status default is used.
        c.Json.GetProperty("code").GetString().ShouldBe("BAD_REQUEST");
        c.Body.ShouldNotContain(Sentinel);
    }

    private sealed class ThrowingDataContractEngine(Func<Exception> create) : IDataContractEngine
    {
        public Task RegisterContractAsync(DataContract contract, CancellationToken cancellationToken = default) => Task.FromException(create());
        public Task<DataContract?> GetContractAsync(TableIdentifier table, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DataContract>> ListContractsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateAssertionResultAsync(string contractId, string assertionId, bool isPassing, string? failureReason = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsTableHealthyAsync(TableIdentifier table, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedOlapStagingService(OlapStagingOutcome outcome) : IOlapStagingService
    {
        public Task<OlapStagingOutcome> ExecuteAsync(
            ClaimsPrincipal user,
            SecurityPrincipalContext caller,
            OlapQueryCommand command,
            bool isDevelopment,
            string traceId,
            CancellationToken ct)
            => Task.FromResult(outcome);
    }

    private sealed class ThrowingProcedureExecutionService(Func<Exception> create) : IProcedureExecutionService
    {
        public Task<GovernedProcedureResult> ExecuteAsync(string name, IReadOnlyDictionary<string, object?>? rawInputs, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => Task.FromException<GovernedProcedureResult>(create());

        public Task<bool> IsVisibleAsync(RegisteredProcedure procedure, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
