using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Webhooks;

[Trait("Category", "Security")]
public sealed class WebhookErrorLeakageTests
{
    private const string SecretDetail = "INTERNAL-PARSER-DETAIL-0xDEADBEEF";

    private static DefaultHttpContext CreateContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.Headers["X-ITSM-Signature"] = "sig";
        context.Request.Headers["X-ITSM-Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return context;
    }

    private static IOptions<GatewayOptions> Options() => Microsoft.Extensions.Options.Options.Create(new GatewayOptions());

    private static void AssertFixed(IResult result, string expectedCode)
    {
        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ProblemDetails.Detail.ShouldNotBeNullOrWhiteSpace();
        problem.ProblemDetails.Detail.ShouldNotContain(SecretDetail);
        problem.ProblemDetails.Detail.ShouldNotContain("JSON");
        problem.ProblemDetails.Extensions["code"].ShouldBe(expectedCode);
        problem.ProblemDetails.Extensions.ContainsKey("traceId").ShouldBeTrue();
    }

    [Fact]
    public async Task Webhook_MalformedJson_ReturnsFixedMessage()
    {
        var handler = Substitute.For<IItsmWebhookHandler>();
        var context = CreateContext("{ not json");

        var result = await WebhookEndpoints.ProcessItsmWebhookAsync(context, handler, Options(), "ITSM");

        AssertFixed(result, "WEBHOOK_PAYLOAD_INVALID");
        await handler.DidNotReceiveWithAnyArgs().HandleStatusChangeAsync(default!, default!, default, default!, default!, default);
    }

    [Theory]
    [InlineData(typeof(ArgumentException), "WEBHOOK_SIGNATURE_OR_FORMAT_INVALID")]
    [InlineData(typeof(FormatException), "WEBHOOK_SIGNATURE_OR_FORMAT_INVALID")]
    [InlineData(typeof(System.Text.Json.JsonException), "WEBHOOK_PAYLOAD_INVALID")]
    public async Task Webhook_HandlerThrows_ReturnsFixedMessageWithoutExceptionText(Type exceptionType, string expectedCode)
    {
        var handler = Substitute.For<IItsmWebhookHandler>();
        var exception = (Exception)Activator.CreateInstance(exceptionType, SecretDetail)!;
        handler.HandleStatusChangeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw exception);
        var context = CreateContext("{\"ticket\":\"T-1\"}");

        var result = await WebhookEndpoints.ProcessItsmWebhookAsync(context, handler, Options(), "ITSM");

        AssertFixed(result, expectedCode);
    }
}
