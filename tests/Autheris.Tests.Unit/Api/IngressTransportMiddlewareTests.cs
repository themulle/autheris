namespace Autheris.Tests.Unit.Api;

using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.DataSources;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>Issue #447 (SEC-ATR-02): the ingress comes from endpoint metadata only; nothing in the request can set it.</summary>
[Trait("Category", "Security")]
public sealed class IngressTransportMiddlewareTests
{
    private static (DefaultHttpContext Context, IIngressTransportContext Ingress) NewContext(DataSourceTransportFlags? metadata)
    {
        var services = new ServiceCollection().AddScoped<IIngressTransportContext, IngressTransportContext>().BuildServiceProvider();
        var scope = services.CreateScope();
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        if (metadata is { } flags)
        {
            ctx.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new IngressTransportMetadata(flags)), "test"));
        }
        else
        {
            ctx.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "test"));
        }

        return (ctx, scope.ServiceProvider.GetRequiredService<IIngressTransportContext>());
    }

    [Theory]
    [InlineData(DataSourceTransportFlags.GraphQL)]
    [InlineData(DataSourceTransportFlags.Mcp)]
    [InlineData(DataSourceTransportFlags.ArrowFlight)]
    public async Task EndpointMetadata_StampsIngress(DataSourceTransportFlags flag)
    {
        var (ctx, ingress) = NewContext(flag);
        var nextCalled = false;
        await new IngressTransportMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }).InvokeAsync(ctx);

        ingress.Current.ShouldBe(flag);
        nextCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task EndpointWithoutMetadata_LeavesIngressNone()
    {
        var (ctx, ingress) = NewContext(null);
        await new IngressTransportMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx);
        ingress.Current.ShouldBe(DataSourceTransportFlags.None);
    }

    [Fact]
    public async Task ClientHeader_CannotSetIngress()
    {
        var (ctx, ingress) = NewContext(null);
        ctx.Request.Headers["X-Autheris-Transport"] = "WebSql";
        ctx.Request.Headers["X-Forwarded-Proto"] = "Mcp";
        ctx.Request.Headers["X-Trino-Source"] = "Trino";
        ctx.Request.QueryString = new QueryString("?transport=WebSql");
        ctx.Request.Path = "/api/v1/sql";

        await new IngressTransportMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx);

        ingress.Current.ShouldBe(DataSourceTransportFlags.None);
    }

    [Fact]
    public async Task ConflictingSecondStamp_FailsTheRequest_NoFallback()
    {
        var (ctx, ingress) = NewContext(DataSourceTransportFlags.GraphQL);
        ingress.Set(DataSourceTransportFlags.WebSql);

        await Should.ThrowAsync<InvalidOperationException>(() => new IngressTransportMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx));
        ingress.Current.ShouldBe(DataSourceTransportFlags.WebSql);
    }

    [Theory]
    [InlineData(DataSourceTransportFlags.None)]
    [InlineData(DataSourceTransportFlags.All)]
    public void Metadata_RejectsNoneAndMultiBit(DataSourceTransportFlags flag) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new IngressTransportMetadata(flag));
}
