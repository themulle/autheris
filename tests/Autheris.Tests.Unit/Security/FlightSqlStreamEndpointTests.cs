namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Types;
using Autheris.Api.Endpoints;
using Autheris.Application.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Flight SQL stream endpoint: a batch without schema metadata (every complete result) is streamed, not 500.</summary>
public sealed class FlightSqlStreamEndpointTests
{
    private static RecordBatch Batch(IReadOnlyDictionary<string, string>? metadata)
    {
        var schema = new Schema([new Field("id", Int64Type.Default, true)], metadata);
        return new RecordBatch(schema, [new Int64Array.Builder().Append(1).Build()], 1);
    }

    private static async IAsyncEnumerable<RecordBatch> Yield(RecordBatch batch, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return batch;
    }

    private static async Task<HttpContext> StreamAsync(RecordBatch batch)
    {
        var server = Substitute.For<IArrowFlightSqlServer>();
        server.DoGetStreamAsync(Arg.Any<FlightSqlTicket>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Yield(batch));
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1"), new Claim("tenant_id", "tenant-1")], "Test"));
        context.Response.Body = new MemoryStream();

        var result = await ArrowFlightSqlEndpoints.HandleStreamAsync(new FlightSqlTicket("t", "tenant-1", "SELECT 1", DateTimeOffset.UtcNow, "sig"), context, server);
        await result.ExecuteAsync(context);
        return context;
    }

    [Fact]
    public async Task CompleteResult_WithoutSchemaMetadata_IsStreamed()
    {
        var context = await StreamAsync(Batch(metadata: null));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers.ContainsKey("X-Autheris-Truncated").ShouldBeFalse();
        context.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task TruncatedResult_SetsTheHeader()
    {
        var context = await StreamAsync(Batch(new Dictionary<string, string> { [ArrowFlightSqlServer.TruncatedMetadataKey] = "true" }));

        context.Response.Headers["X-Autheris-Truncated"].ToString().ShouldBe("true");
    }
}
