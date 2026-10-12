namespace Autheris.Tests.Unit.Governance;

using System.IO;
using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Application.Governance.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Main-merge note (X1): the GitOps consent apply endpoints answer an unparsable manifest with a curated 400. The text of the
/// <c>JsonException</c> can quote the offending value, a type name or a path, so it is never part of the response.
/// </summary>
public sealed class ConsentManifestApplyEndpointTests
{
    private const string Marker = "SECRET-MANIFEST-VALUE-42";

    private static DefaultHttpContext Request(string body)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).BuildServiceProvider() };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "S-1"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "Test"));
        return context;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Theory]
    [InlineData("{ \"version\": \"1\", \"policies\": [ { \"effect\": \"" + Marker + "\" ")]   // truncated: the parser quotes the position
    [InlineData("{ \"version\": \"" + Marker + "\", \"policies\": 7 }")]                        // a value that cannot be converted: the parser names the type and the path
    [InlineData("not json at all " + Marker)]
    public async Task UnparsableManifest_Returns400_WithACuratedMessage(string body)
    {
        var context = Request(body);

        var (status, text) = await ExecuteAsync(
            await GovernanceEndpoints.ApplyConsentManifestAsync(context, Substitute.For<IDeclarativeConsentApplyService>(), CancellationToken.None), context);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        text.ShouldContain("Invalid JSON in consent manifest.");
        text.ShouldNotContain(Marker);
        text.ShouldNotContain("could not be converted");
        text.ShouldNotContain("System.");
        text.ShouldNotContain("Path:");
    }
}
