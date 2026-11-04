namespace Autheris.Tests.Unit.Api;

using System.Text.Json;
using Autheris.Api.Middleware;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options.DataSources;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #447 (SEC-ATR-08): TRANSPORT_NOT_ALLOWED maps to a 403 problem and a fixed GraphQL text without the source name.</summary>
[Trait("Category", "Security")]
public sealed class TransportNotAllowedMappingTests
{
    [Fact]
    public async Task TransportNotAllowed_Maps403WithCode()
    {
        var denial = new TransportNotAllowedException("sales", DataSourceTransportFlags.WebSql);
        var ctx = new DefaultHttpContext { TraceIdentifier = "t" };
        ctx.Response.Body = new MemoryStream();

        await GatewayDenialStatusMapper.WriteWebSqlResponseAsync(ctx, denial, NullLogger.Instance, default);

        ctx.Response.StatusCode.ShouldBe(403);
        ctx.Response.Body.Position = 0;
        using var doc = JsonDocument.Parse(new StreamReader(ctx.Response.Body).ReadToEnd());
        doc.RootElement.GetProperty("code").GetString().ShouldBe("TRANSPORT_NOT_ALLOWED");
    }

    [Fact]
    public void TransportNotAllowed_FixedTextAndCode()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var filter = new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance);
        var error = ErrorBuilder.New()
            .SetMessage("placeholder")
            .SetException(new TransportNotAllowedException("secret_source", DataSourceTransportFlags.GraphQL))
            .Build();

        var sanitized = filter.OnError(error);

        sanitized.Code.ShouldBe("TRANSPORT_NOT_ALLOWED");
        sanitized.Message.ShouldBe("Access to the data source is not allowed over this transport.");
        sanitized.Message.ShouldNotContain("secret_source");
        sanitized.Exception.ShouldBeNull();
    }
}
