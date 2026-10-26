using Autheris.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Middleware;

/// <summary>L11 / DEF-SC-16: only the namespaced domain-scope item key is written; the unnamespaced legacy key is gone, the header handling is unchanged.</summary>
public sealed class DomainScopeLegacyItemKeyTests
{
    [Trait("Category", "Security")]
    [Fact]
    public async Task DomainScope_Legacy_Item_Key_Is_Not_Written()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/graphql/finance";
        var middleware = new DomainScopeRewriteMiddleware(_ => Task.CompletedTask, "/graphql");

        await middleware.InvokeAsync(context);

        context.Items["Autheris.DomainScope"].ShouldBe("finance");
        context.Items.ContainsKey("DomainScope").ShouldBeFalse();
        context.Request.Headers["X-Autheris-Domain"].ToString().ShouldBe("finance");
        context.Request.Path.Value.ShouldBe("/graphql");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task DomainScope_Invalid_Slug_Is_Still_Rejected()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/graphql/bad slug!";
        var called = false;
        var middleware = new DomainScopeRewriteMiddleware(_ => { called = true; return Task.CompletedTask; }, "/graphql");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        called.ShouldBeFalse();
        context.Items.ContainsKey("Autheris.DomainScope").ShouldBeFalse();
    }

    [Fact]
    public void GraphQl_Reader_Uses_The_Namespaced_Item_Key()
    {
        var key = typeof(Autheris.GraphQL.Types.Query).GetField("DomainScopeItemKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        key.GetRawConstantValue().ShouldBe(DomainScopeRewriteMiddleware.DomainScopeItemKey);
    }
}
