namespace Autheris.Tests.Unit;

using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Endpoints.Interfaces;
using Autheris.Application.Sql.Endpoints.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class SqlEndpointGetSelectOnlyTests
{
    private static HttpContext CreateContext(IServiceCollection services, params string[] roles)
    {
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-SQL-USER"), new("tenant_id", "tenant-1") };
        foreach (var role in roles) { claims.Add(new Claim(ClaimTypes.Role, role)); }
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return context;
    }

    private static async Task<(int Status, ISqlEndpointExecutionService Exec)> GetAsync(string sql, string? method = "GET")
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("ep", "s", sql, HttpMethod: method));
        var context = CreateContext(new ServiceCollection().AddSingleton<ISqlEndpointRegistry>(registry));
        var exec = Substitute.For<ISqlEndpointExecutionService>();
        await SqlEndpointRoutes.HandleGetEndpoint("ep", context, exec, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);
        return (context.Response.StatusCode, exec);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE")]
    [InlineData("CALL sys.proc()")]
    [InlineData("DROP TABLE t")]
    [InlineData("SELECT 1; DELETE FROM t")]
    [InlineData("CREATE TABLE x AS SELECT 1")]
    [InlineData("not sql at all (")]
    public async Task Get_NonSelectDefinition_Returns405AndNeverExecutes(string sql)
    {
        var (status, exec) = await GetAsync(sql);

        status.ShouldBe(StatusCodes.Status405MethodNotAllowed);
        await exec.DidNotReceiveWithAnyArgs().ExecuteEndpointAsync(default!, default, default!, default, default);
    }

    [Fact]
    public async Task Get_PostOnlyDefinition_Returns405()
    {
        var (status, exec) = await GetAsync("SELECT 1", "POST");

        status.ShouldBe(StatusCodes.Status405MethodNotAllowed);
        await exec.DidNotReceiveWithAnyArgs().ExecuteEndpointAsync(default!, default, default!, default, default);
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("WITH c AS (SELECT 1 AS a) SELECT a FROM c")]
    public async Task Get_SelectDefinition_StillExecutes(string sql)
    {
        var (status, exec) = await GetAsync(sql);

        status.ShouldNotBe(StatusCodes.Status405MethodNotAllowed);
        await exec.ReceivedWithAnyArgs(1).ExecuteEndpointAsync(default!, default, default!, default, default);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("DELETE FROM t")]
    public async Task Register_GetDefinitionWithNonSelect_IsRejected(string sql)
    {
        var registry = new InMemorySqlEndpointRegistry();
        var context = CreateContext(new ServiceCollection(), "ClusterAdmin");
        var loader = new SqlEndpointLoader(registry, NullLogger<SqlEndpointLoader>.Instance);

        var result = await SqlEndpointRoutes.HandleRegisterEndpoint(new RegisterSqlEndpointRequest(Name: "ep", Sql: sql), registry, context, loader);

        result.ShouldNotBeOfType<Created<object>>();
        registry.TryGet("ep", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Register_PostDefinitionWithDml_IsUnchanged()
    {
        var registry = new InMemorySqlEndpointRegistry();
        var context = CreateContext(new ServiceCollection().AddAuditWriterOver(Substitute.For<IAuditLogRepository>()), "ClusterAdmin");
        var loader = new SqlEndpointLoader(registry, NullLogger<SqlEndpointLoader>.Instance);

        var result = await SqlEndpointRoutes.HandleRegisterEndpoint(
            new RegisterSqlEndpointRequest(Name: "ep", Sql: "-- @method POST\nINSERT INTO t VALUES (1)"),registry, context, loader);

        result.ShouldBeOfType<Created<object>>();
    }
}
