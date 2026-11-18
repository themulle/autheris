using Autheris.Tests.Unit.Policy;
namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Endpoints.Interfaces;
using Autheris.Application.Sql.Endpoints.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Idempotency;
using Autheris.Tests.Unit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

[Trait("Category", "Security")]
public sealed class SqlEndpointDmlGetGuardTests
{
    private sealed class RecordingExecutionService : ISqlEndpointExecutionService
    {
        public int Calls { get; private set; }
        public SqlEndpointInvocation? LastInvocation { get; private set; }

        public Task<GovernedSqlResult> ExecuteEndpointAsync(
            string endpointName,
            IReadOnlyDictionary<string, object?>? rawInputs,
            ClaimsPrincipal user,
            TenantId tenantId,
            SqlEndpointInvocation invocation,
            CancellationToken ct = default)
        {
            Calls++;
            LastInvocation = invocation;
            return Task.FromResult(new GovernedSqlResult("SELECT 1", ["id"], [], 0, 0));
        }
    }

    private sealed class CapturingAuditWriter : IAuditWriter
    {
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static ClaimsPrincipal WriteUser(string? role = null, bool readOnly = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
            new("sub", "user-1"),
            new("tenant_id", BypassSemanticsAndDmlGuardrailTests.Tenant),
            new("scp", "Sql.Write")
        };
        if (role != null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, "Test");
        if (readOnly)
        {
            TokenAccessScope.MarkReadOnly(identity);
        }

        return new ClaimsPrincipal(identity);
    }

    private static DefaultHttpContext CreateContext(
        ISqlEndpointRegistry registry,
        ClaimsPrincipal user,
        IIdempotencyStore? store = null,
        string? idempotencyKey = null,
        string method = "GET")
    {
        var services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton<IAuditWriter>(new CapturingAuditWriter());
        if (store != null)
        {
            services.AddSingleton(store);
        }

        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = user
        };
        ctx.Request.Method = method;
        if (idempotencyKey != null)
        {
            ctx.Request.Headers[WebSqlEndpoints.IdempotencyKeyHeader] = idempotencyKey;
        }
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("INSERT INTO orders (id, amount, tenant_id) VALUES (99, 10, 'tenant_a')")]
    [InlineData("UPDATE orders SET amount = 2 WHERE id = 1")]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("MERGE INTO orders USING orders s ON orders.id = s.id WHEN MATCHED THEN DELETE")]
    [InlineData("DROP TABLE orders")]
    [InlineData("CREATE TABLE evil (id INT)")]
    public async Task Get_on_dml_endpoint_returns_405_for_write_scoped_token(string sql)
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("mutating_ep", "Mutate", sql, HttpMethod: "POST"));
        var exec = new RecordingExecutionService();
        var ctx = CreateContext(registry, WriteUser());
        var opts = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = true } });

        await SqlEndpointRoutes.HandleGetEndpoint("mutating_ep", ctx, exec, opts, NullLoggerFactory.Instance);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status405MethodNotAllowed);
        ctx.Response.Headers.Allow.ToString().ShouldBe("POST");
        exec.Calls.ShouldBe(0);
        using var doc = JsonDocument.Parse(ReadBody(ctx));
        doc.RootElement.GetProperty("code").GetString().ShouldBe(GovernedSqlErrorCodes.MethodNotAllowed);
        doc.RootElement.GetProperty("detail").GetString().ShouldBe("This SQL endpoint cannot be executed via HTTP GET.");
    }

    [Fact]
    public async Task Get_on_dml_endpoint_returns_405_for_cluster_admin()
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("admin_delete", "Delete", "DELETE FROM orders WHERE id = 1", HttpMethod: "POST"));
        var exec = new RecordingExecutionService();
        var ctx = CreateContext(registry, WriteUser(role: "ClusterAdmin"));
        var opts = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = true } });

        await SqlEndpointRoutes.HandleGetEndpoint("admin_delete", ctx, exec, opts, NullLoggerFactory.Instance);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status405MethodNotAllowed);
        ctx.Response.Headers.Allow.ToString().ShouldBe("POST");
        exec.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Get_on_post_method_endpoint_returns_405()
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("post_only_select", "Select on POST", "SELECT id FROM orders", HttpMethod: "POST"));
        var exec = new RecordingExecutionService();
        var ctx = CreateContext(registry, WriteUser());

        await SqlEndpointRoutes.HandleGetEndpoint("post_only_select", ctx, exec, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status405MethodNotAllowed);
        ctx.Response.Headers.Allow.ToString().ShouldBe("POST");
        exec.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Get_405_body_does_not_echo_endpoint_name()
    {
        const string endpointName = "canary_dml_endpoint_name_9f42";
        const string secretSql = "DELETE FROM canary_secret_table_x7 WHERE id = 1";
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition(endpointName, "Secret", secretSql, HttpMethod: "GET"));
        var exec = new RecordingExecutionService();
        var ctx = CreateContext(registry, WriteUser());

        await SqlEndpointRoutes.HandleGetEndpoint(endpointName, ctx, exec, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status405MethodNotAllowed);
        string body = ReadBody(ctx);
        body.ShouldNotContain(endpointName);
        body.ShouldNotContain("canary_secret_table_x7");
        body.ShouldContain("This SQL endpoint cannot be executed via HTTP GET.");
    }

    [Theory]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("INSERT INTO orders (id, amount, tenant_id) VALUES (99, 10, 'tenant_a')")]
    [InlineData("UPDATE orders SET amount = 2 WHERE id = 1")]
    [InlineData("CREATE TABLE evil (id INT)")]
    public async Task Executor_refuses_dml_when_read_only_required_even_if_route_check_skipped(string sql)
    {
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var governed = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, allowDml: true, permissiveWrite: true);
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("bypass_ep", "Bypass attempt", sql, DataSource: "default", HttpMethod: "GET"));
        var endpointService = new SqlEndpointExecutionService(registry, governed, Options.Create(new GatewayOptions()));
        var user = BypassSemanticsAndDmlGuardrailTests.CreateWriter();

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            endpointService.ExecuteEndpointAsync("bypass_ep", new Dictionary<string, object?>(), user, new TenantId(BypassSemanticsAndDmlGuardrailTests.Tenant), SqlEndpointInvocation.Get, CancellationToken.None));

        ex.Code.ShouldBe(GovernedSqlErrorCodes.MethodNotAllowed);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);

        var directEx = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            governed.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest(sql, DataSourceName: "default", Transport: SqlTransports.SqlEndpoints, ReadOnlyRequired: true),
                user,
                new TenantId(BypassSemanticsAndDmlGuardrailTests.Tenant),
                CancellationToken.None));

        directEx.Code.ShouldBe(GovernedSqlErrorCodes.MethodNotAllowed);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
    }

    [Theory]
    [InlineData("/* block */ DELETE FROM orders WHERE id = 1", false)]
    [InlineData("-- line\nUPDATE orders SET amount = 2 WHERE id = 1;", false)]
    [InlineData("SELECT id FROM orders WHERE id = 1;", true)]
    [InlineData("SELECT id FROM orders; DELETE FROM orders WHERE id = 1", false)]
    [InlineData("WITH c AS (SELECT id FROM orders) DELETE FROM orders WHERE id IN (SELECT id FROM c)", false)]
    [InlineData("dElEtE FROM orders WHERE id = 1", false)]
    [InlineData("sElEcT id FROM orders WHERE id = 1", true)]
    public async Task Comment_and_trailing_statement_variants_classified_identically_by_guard_and_executor(string rawSql, bool expectedRead)
    {
        var statementClass = SqlStatementClassifier.ClassifyEndpointSql(rawSql, NullLogger.Instance);
        (statementClass == RequestStatementClass.Read).ShouldBe(expectedRead);

        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var governed = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, allowDml: true, permissiveWrite: true);
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("variant_ep", "Variant", rawSql, DataSource: "default", HttpMethod: "GET"));
        var endpointService = new SqlEndpointExecutionService(registry, governed, Options.Create(new GatewayOptions()));
        var user = BypassSemanticsAndDmlGuardrailTests.CreateWriter();

        if (expectedRead)
        {
            var result = await endpointService.ExecuteEndpointAsync("variant_ep", null, user, new TenantId(BypassSemanticsAndDmlGuardrailTests.Tenant), SqlEndpointInvocation.Get, CancellationToken.None);
            result.ShouldNotBeNull();
        }
        else
        {
            await Should.ThrowAsync<Exception>(() =>
                endpointService.ExecuteEndpointAsync("variant_ep", null, user, new TenantId(BypassSemanticsAndDmlGuardrailTests.Tenant), SqlEndpointInvocation.Get, CancellationToken.None));
            (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
        }
    }

    [Fact]
    public async Task Post_dml_without_idempotency_key_returns_400()
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("delete_order", "Delete", "DELETE FROM orders WHERE id = 1", HttpMethod: "POST"));
        var store = new InMemoryIdempotencyStore();
        var opts = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = true } });

        var missingKeyExec = new RecordingExecutionService();
        var missingKeyCtx = CreateContext(registry, WriteUser(), store, idempotencyKey: null, method: "POST");
        await SqlEndpointRoutes.HandlePostEndpoint("delete_order", missingKeyCtx, missingKeyExec, opts, NullLoggerFactory.Instance);
        missingKeyCtx.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        missingKeyExec.Calls.ShouldBe(0);

        var validExec = new RecordingExecutionService();
        var validCtx = CreateContext(registry, WriteUser(), store, idempotencyKey: "ep-key-1", method: "POST");
        await SqlEndpointRoutes.HandlePostEndpoint("delete_order", validCtx, validExec, opts, NullLoggerFactory.Instance);
        validCtx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        validExec.Calls.ShouldBe(1);
        validExec.LastInvocation.ShouldBe(SqlEndpointInvocation.Post);

        var replayExec = new RecordingExecutionService();
        var replayCtx = CreateContext(registry, WriteUser(), store, idempotencyKey: "ep-key-1", method: "POST");
        await SqlEndpointRoutes.HandlePostEndpoint("delete_order", replayCtx, replayExec, opts, NullLoggerFactory.Instance);
        replayCtx.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        replayExec.Calls.ShouldBe(0);

        var readOnlyExec = new RecordingExecutionService();
        var readOnlyCtx = CreateContext(registry, WriteUser(readOnly: true), store, idempotencyKey: null, method: "POST");
        await SqlEndpointRoutes.HandlePostEndpoint("delete_order", readOnlyCtx, readOnlyExec, opts, NullLoggerFactory.Instance);
        readOnlyCtx.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        readOnlyExec.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Register_get_with_dml_returns_400(bool withLoader)
    {
        var registry = new InMemorySqlEndpointRegistry();
        var loader = withLoader ? new SqlEndpointLoader(registry, NullLogger<SqlEndpointLoader>.Instance) : null;
        var ctx = CreateContext(registry, WriteUser(role: "ClusterAdmin"), method: "POST");
        var req = new RegisterSqlEndpointRequest("bad_dml_get", "DELETE FROM orders WHERE id = 1", HttpMethod: "GET");

        var result = await SqlEndpointRoutes.HandleRegisterEndpoint(req, registry, ctx, loader);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        registry.TryGet("bad_dml_get", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("PUT", true)]
    [InlineData("DELETE", true)]
    [InlineData("PATCH", false)]
    [InlineData("GET\r\n", false)]
    public async Task Register_unknown_method_returns_400(string method, bool withLoader)
    {
        var registry = new InMemorySqlEndpointRegistry();
        var loader = withLoader ? new SqlEndpointLoader(registry, NullLogger<SqlEndpointLoader>.Instance) : null;
        var ctx = CreateContext(registry, WriteUser(role: "ClusterAdmin"), method: "POST");
        var req = new RegisterSqlEndpointRequest("bad_method_ep", "SELECT id FROM orders", HttpMethod: method);

        var result = await SqlEndpointRoutes.HandleRegisterEndpoint(req, registry, ctx, loader);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        registry.TryGet("bad_method_ep", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Register_dml_without_method_defaults_to_post(bool withLoader)
    {
        var registry = new InMemorySqlEndpointRegistry();
        var loader = withLoader ? new SqlEndpointLoader(registry, NullLogger<SqlEndpointLoader>.Instance) : null;
        var ctx = CreateContext(registry, WriteUser(role: "ClusterAdmin"), method: "POST");
        var req = new RegisterSqlEndpointRequest("dml_default_post", "DELETE FROM orders WHERE id = 1", HttpMethod: null);

        var result = await SqlEndpointRoutes.HandleRegisterEndpoint(req, registry, ctx, loader);

        result.ShouldBeOfType<Created<object>>();
        registry.TryGet("dml_default_post", out var def).ShouldBeTrue();
        def!.HttpMethod.ShouldBe("POST");
    }

    [Fact]
    public void Loader_startup_skips_invalid_method_or_get_dml_files_fail_closed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sqlep_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad_get_dml.sql"), "-- @method GET\nDELETE FROM orders WHERE id = 1");
            File.WriteAllText(Path.Combine(dir, "bad_put.sql"), "-- @method PUT\nSELECT id FROM orders");
            File.WriteAllText(Path.Combine(dir, "valid_post_dml.sql"), "UPDATE orders SET amount = 1 WHERE id = @id");
            File.WriteAllText(Path.Combine(dir, "valid_get_select.sql"), "SELECT id FROM orders WHERE id = @id");

            var registry = new InMemorySqlEndpointRegistry();
            using var loader = new SqlEndpointLoader(registry, NullLogger<SqlEndpointLoader>.Instance);
            int count = loader.LoadFromDirectory(dir, enableHotReload: false);

            count.ShouldBe(2);
            registry.TryGet("bad_get_dml", out _).ShouldBeFalse();
            registry.TryGet("bad_put", out _).ShouldBeFalse();
            registry.TryGet("valid_post_dml", out var postDef).ShouldBeTrue();
            postDef!.HttpMethod.ShouldBe("POST");
            registry.TryGet("valid_get_select", out var getDef).ShouldBeTrue();
            getDef!.HttpMethod.ShouldBe("GET");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
