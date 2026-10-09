namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Application.SqlEndpoints.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class SqlEndpointDynamicRegistrationTests
{
    private static HttpContext CreateContext(bool isAdmin)
    {
        var context = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
            new("tenant_id", "tenant-1")
        };
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "ClusterAdmin"));
        }
        else
        {
            claims.Add(new Claim(ClaimTypes.Role, "User"));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return context;
    }

    [Fact]
    public async Task HandleRegisterEndpoint_WhenAdmin_RegistersEndpointSuccessfully()
    {
        var registry = new InMemorySqlEndpointRegistry();
        var context = CreateContext(isAdmin: true);
        var loader = new SqlEndpointLoader(registry, NullLogger<SqlEndpointLoader>.Instance);

        var request = new RegisterSqlEndpointRequest(
            Name: "active_users",
            Sql: "SELECT id, username FROM users WHERE active = true",
            Summary: "Get all active users",
            DataSource: "default");

        var result = await SqlEndpointRoutes.HandleRegisterEndpoint(request, registry, context, loader);

        result.ShouldBeOfType<Created<object>>();
        registry.TryGet("active_users", out var def).ShouldBeTrue();
        def.ShouldNotBeNull();
        def.Name.ShouldBe("active_users");
        def.Summary.ShouldBe("Get all active users");
        def.RawSql.ShouldContain("SELECT id, username FROM users");
    }

    [Fact]
    public async Task HandleRegisterEndpoint_WhenNonAdmin_ReturnsForbidden()
    {
        var registry = new InMemorySqlEndpointRegistry();
        var context = CreateContext(isAdmin: false);

        var request = new RegisterSqlEndpointRequest(
            Name: "active_users",
            Sql: "SELECT 1");

        var result = await SqlEndpointRoutes.HandleRegisterEndpoint(request, registry, context);

        result.ShouldBeOfType<ForbidHttpResult>();
        registry.TryGet("active_users", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task HandleRegisterEndpoint_WhenAdmin_ButReadOnly_ReturnsForbidden()
    {
        var registry = new InMemorySqlEndpointRegistry();
        var context = CreateContext(isAdmin: true);
        if (context.User.Identity is ClaimsIdentity identity)
        {
            TokenAccessScope.MarkReadOnly(identity);
        }

        var request = new RegisterSqlEndpointRequest(
            Name: "active_users",
            Sql: "SELECT 1");

        var result = await SqlEndpointRoutes.HandleRegisterEndpoint(request, registry, context);

        result.ShouldBeOfType<ForbidHttpResult>();
        registry.TryGet("active_users", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("", "SELECT 1")]
    [InlineData("   ", "SELECT 1")]
    [InlineData("valid_name", "")]
    [InlineData("valid_name", "   ")]
    [InlineData("../escape", "SELECT 1")]
    [InlineData("name with spaces", "SELECT 1")]
    [InlineData("openapi.json", "SELECT 1")]
    public async Task HandleRegisterEndpoint_WhenInvalidInput_ReturnsBadRequest(string name, string sql)
    {
        var registry = new InMemorySqlEndpointRegistry();
        var context = CreateContext(isAdmin: true);

        var request = new RegisterSqlEndpointRequest(
            Name: name,
            Sql: sql);

        var result = await SqlEndpointRoutes.HandleRegisterEndpoint(request, registry, context);

        result.ShouldBeOfType<BadRequest<object>>();
    }
}
