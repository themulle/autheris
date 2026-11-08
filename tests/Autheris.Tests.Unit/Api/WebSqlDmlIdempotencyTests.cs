namespace Autheris.Tests.Unit.Api;

using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class WebSqlDmlIdempotencyTests
{
    private const string Insert = "INSERT INTO t (a) VALUES (1)";

    private static DefaultHttpContext Ctx(string sql, IIdempotencyStore? store, string? key, string path = "/api/v1/sql",
        string sub = "user_1", string tenant = "tenant_a", bool authenticated = true)
    {
        var services = new ServiceCollection();
        if (store != null)
        {
            services.AddSingleton(store);
        }

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Path = path;
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sql }));
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        if (key != null)
        {
            context.Request.Headers["Idempotency-Key"] = key;
        }

        if (path.StartsWith("/v1/statement"))
        {
            context.Request.Headers["X-Trino-User"] = sub;
        }

        context.User = authenticated
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub), new Claim("tenant_id", tenant)], "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", tenant)], "Test"));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int Status, IGovernedSqlExecutionService Sql)> Run(DefaultHttpContext ctx, bool allowDml = true)
    {
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        var options = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = allowDml } });
        await WebSqlEndpoints.HandleWebSqlRequest(ctx, sql, options, NullLoggerFactory.Instance);
        return (ctx.Response.StatusCode, sql);
    }

    private static int ExecutorCalls(IGovernedSqlExecutionService sql) =>
        sql.ReceivedCalls().Count();

    [Fact]
    public async Task Dml_without_idempotency_key_is_refused()
    {
        var (status, sql) = await Run(Ctx(Insert, new InMemoryIdempotencyStore(), key: null));
        status.ShouldBe(400);
        ExecutorCalls(sql).ShouldBe(0);
    }

    [Fact]
    public async Task Select_without_idempotency_key_is_not_refused_by_the_guard()
    {
        var (status, _) = await Run(Ctx("SELECT 1", new InMemoryIdempotencyStore(), key: null));
        status.ShouldNotBe(400);
        status.ShouldNotBe(409);
    }

    [Theory]
    [InlineData("")]
    [InlineData("kéy")]
    public async Task Overlong_or_nonascii_key_is_rejected(string key)
    {
        var (status, sql) = await Run(Ctx(Insert, new InMemoryIdempotencyStore(), key));
        status.ShouldBe(400);
        ExecutorCalls(sql).ShouldBe(0);
    }

    [Fact]
    public async Task Key_over_128_chars_is_rejected_and_128_is_accepted_to_the_store()
    {
        var store = new InMemoryIdempotencyStore();
        (await Run(Ctx(Insert, store, new string('a', 129)))).Status.ShouldBe(400);
        (await Run(Ctx(Insert, store, new string('a', 128)))).Status.ShouldNotBe(400);
    }

    [Fact]
    public async Task Replayed_key_is_refused_with_conflict_before_execution()
    {
        var store = new InMemoryIdempotencyStore();
        await Run(Ctx(Insert, store, "k-1"));
        var (status, sql) = await Run(Ctx(Insert, store, "k-1"));
        status.ShouldBe(409);
        ExecutorCalls(sql).ShouldBe(0);
    }

    [Fact]
    public async Task Key_is_scoped_per_tenant_and_subject()
    {
        var store = new InMemoryIdempotencyStore();
        await Run(Ctx(Insert, store, "k-1"));
        (await Run(Ctx(Insert, store, "k-1", sub: "user_2"))).Status.ShouldNotBe(409);
        (await Run(Ctx(Insert, store, "k-1", tenant: "tenant_b"))).Status.ShouldNotBe(409);
    }

    [Fact]
    public async Task Key_is_not_reserved_for_unauthorized_caller()
    {
        var store = Substitute.For<IIdempotencyStore>();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => Run(Ctx(Insert, store, "k-1", authenticated: false)));
        await store.DidNotReceiveWithAnyArgs().SetIfNotExistsAsync<string>(default!, default!, default);
    }

    [Fact]
    public async Task Reserved_key_is_stored_hashed()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.SetIfNotExistsAsync<string>(default!, default!, default, default).ReturnsForAnyArgs(true);
        await Run(Ctx(Insert, store, "plain-secret-key"));
        var call = store.ReceivedCalls().Single(c => c.GetMethodInfo().Name == "SetIfNotExistsAsync");
        ((string)call.GetArguments()[0]!).ShouldNotContain("plain-secret-key");
    }

    [Fact]
    public async Task Idempotency_store_failure_refuses_dml()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.SetIfNotExistsAsync<string>(default!, default!, default, default)
            .ReturnsForAnyArgs<Task<bool>>(_ => throw new InvalidOperationException("down"));
        var (status, sql) = await Run(Ctx(Insert, store, "k-1"));
        status.ShouldBe(503);
        ExecutorCalls(sql).ShouldBe(0);
    }

    [Fact]
    public async Task Missing_idempotency_store_refuses_dml()
    {
        var (status, sql) = await Run(Ctx(Insert, store: null, key: "k-1"));
        status.ShouldBe(503);
        ExecutorCalls(sql).ShouldBe(0);
    }

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (1)")]
    [InlineData("  /* c */ update t set a = 1")]
    [InlineData("DELETE FROM t")]
    public async Task Trino_route_dml_is_refused_without_query_id(string sql)
    {
        var manager = Substitute.For<IWebSqlStatementManager>();
        var ctx = Ctx(sql, new InMemoryIdempotencyStore(), "k-1", path: "/v1/statement");
        var services = new ServiceCollection().AddSingleton(manager).BuildServiceProvider();
        ctx.RequestServices = services;
        var (status, exec) = await Run(ctx);
        status.ShouldBe(400);
        ExecutorCalls(exec).ShouldBe(0);
        manager.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Guard_is_skipped_and_no_key_reserved_when_dml_is_disabled()
    {
        var store = Substitute.For<IIdempotencyStore>();
        var (status, sql) = await Run(Ctx(Insert, store, key: null), allowDml: false);
        status.ShouldNotBe(400);
        ExecutorCalls(sql).ShouldBeGreaterThan(0);
        store.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("MERGE INTO t USING s ON t.a = s.a WHEN MATCHED THEN DELETE")]
    [InlineData("WITH c AS (SELECT 1 AS a) INSERT INTO t SELECT a FROM c")]
    [InlineData("WITH c AS (SELECT 1 AS a) DELETE FROM t WHERE a IN (SELECT a FROM c)")]
    [InlineData("SELECT 1; DELETE FROM t")]
    [InlineData("INSERT INTO")]
    public async Task Trino_route_refuses_anything_that_is_not_a_plain_read(string sql)
    {
        var manager = Substitute.For<IWebSqlStatementManager>();
        var ctx = Ctx(sql, new InMemoryIdempotencyStore(), "k-1", path: "/v1/statement");
        ctx.RequestServices = new ServiceCollection().AddSingleton(manager).BuildServiceProvider();
        var (status, exec) = await Run(ctx);
        status.ShouldBe(400);
        ExecutorCalls(exec).ShouldBe(0);
        manager.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("MERGE INTO t USING s ON t.a = s.a WHEN MATCHED THEN DELETE")]
    [InlineData("WITH c AS (SELECT 1 AS a) INSERT INTO t SELECT a FROM c")]
    [InlineData("WITH c AS (SELECT 1 AS a) DELETE FROM t WHERE a IN (SELECT a FROM c)")]
    [InlineData("SELECT 1; DELETE FROM t")]
    [InlineData("INSERT INTO")]
    public async Task Web_route_never_executes_unkeyed_dml_like_text(string sql)
    {
        var (status, exec) = await Run(Ctx(sql, new InMemoryIdempotencyStore(), key: null));
        status.ShouldBe(400);
        ExecutorCalls(exec).ShouldBe(0);
    }

    [Fact]
    public async Task Whitespace_only_or_control_character_key_is_rejected()
    {
        var store = new InMemoryIdempotencyStore();
        (await Run(Ctx(Insert, store, "   "))).Status.ShouldBe(400);
        (await Run(Ctx(Insert, store, "a\u007fb"))).Status.ShouldBe(400);
        (await Run(Ctx(Insert, store, "a\tb"))).Status.ShouldBe(400);
    }
}
