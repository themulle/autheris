using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.DataSources;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Api;

public sealed class WebSqlRequestBodyLimitTests
{
    private sealed class NoopSqlService : IGovernedSqlExecutionService
    {
        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
            GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException("Should not execute when request body exceeds limit.");

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static DefaultHttpContext CreateContext()
    {
        var ingress = new IngressTransportContext();
        ingress.Set(DataSourceTransportFlags.WebSql);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IIngressTransportContext>(ingress)
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.PrimarySid, "user-1"),
                new Claim("tenant_id", "tenant-1")
            ], "Test"))
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/sql";
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleWebSqlRequest_BodyExceeding2Mb_Returns413(bool declareContentLength)
    {
        var context = CreateContext();
        var oversized = new byte[(2 * 1024 * 1024) + 128];
        Array.Fill(oversized, (byte)'a');
        context.Request.Body = new MemoryStream(oversized);
        context.Request.ContentLength = declareContentLength ? oversized.Length : null;

        await WebSqlEndpoints.HandleWebSqlRequest(
            context,
            new NoopSqlService(),
            Options.Create(new GatewayOptions()),
            NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
    }
}
