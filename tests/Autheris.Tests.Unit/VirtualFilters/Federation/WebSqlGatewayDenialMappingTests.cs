namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 WI-15 (ST-35, HTTP half): a gateway denial (stable code) is HTTP 403 with the generic message on the WebSQL
/// endpoint; the code and the exception message never reach the response (they are in the audit record).
/// </summary>
public sealed class WebSqlGatewayDenialMappingTests
{
    [Theory]
    [InlineData("VF_SCAN_NOT_PERMITTED")]
    [InlineData("VIRTUAL_FILTER_SOURCE_UNAVAILABLE")]
    [InlineData("VF_PATTERN_EVALUATION_FAILED")]
    [InlineData("ROW_LIMIT_EXCEEDED")]
    [InlineData("TABLE_DENIED")]
    public async Task GatewaySecurityException_Returns403_WithTheGenericMessage(string code)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-403";

        await WebSqlEndpoints.WriteWebSqlErrorAsync(
            context, new GatewaySecurityException("secret-detail for filter vf_x", code), NullLogger.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("Forbidden");
        doc.RootElement.GetProperty("message").GetString().ShouldBe(WebSqlEndpoints.GenericForbiddenMessage);
        doc.RootElement.ToString().ShouldNotContain("secret-detail");
        doc.RootElement.ToString().ShouldNotContain(code);
    }
}
