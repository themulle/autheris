namespace Autheris.Tests.Unit.Databricks;

using System.Net;
using System.Net.Http;
using Autheris.Infrastructure.Databricks;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.DatabricksTestSupport;

/// <summary>
/// SEC-OI-609-3: the Databricks client never writes the OAuth access token, the client secret, a PAT or an Authorization header value
/// to a log or to an exception message, neither on the success path nor on the failure paths the live G9 suite can hit.
/// </summary>
[Trait("Category", "Security")]
public sealed class DatabricksTokenLoggingTests
{
    private static readonly string[] Forbidden = [AccessToken, ClientSecret, Pat, "Bearer "];

    private static void AssertClean(string text, string scenario)
    {
        foreach (var secret in Forbidden)
        {
            text.Contains(secret, StringComparison.Ordinal).ShouldBeFalse($"{scenario}: output leaked '{secret}'");
        }
    }

    private static async Task<string> RunAsync(Func<RecordedRequest, HttpResponseMessage?> statementRoutes, bool expectFailure)
    {
        var logger = new CapturingLogger<DatabricksStatementExecutionClient>();
        var handler = new FakeDatabricksHandler(Router(statementRoutes));
        var client = Client(handler, logger: logger);

        string thrown = string.Empty;
        if (expectFailure)
        {
            var ex = await CaptureAsync(() => client.ExecuteAsync(Request(), CancellationToken.None));
            thrown = ex.ToString();
        }
        else
        {
            var result = await client.ExecuteAsync(Request(), CancellationToken.None);
            result.Rows.Count.ShouldBe(1);
        }

        return thrown + "\n" + string.Join('\n', logger.Lines);
    }

    [Fact]
    public async Task Token_and_secret_never_logged()
    {
        AssertClean(
            await RunAsync(_ => Ok(Succeeded(StatementId, """[["1","a"]]""")), expectFailure: false),
            "success");

        AssertClean(
            await RunAsync(_ => Json(HttpStatusCode.Unauthorized, $"{{\"message\":\"bad token {AccessToken} {ClientSecret} {Pat}\"}}"), expectFailure: true),
            "http 401");

        AssertClean(
            await RunAsync(_ => Json(HttpStatusCode.InternalServerError, $"echo Bearer {AccessToken} secret {ClientSecret}"), expectFailure: true),
            "http 500");

        AssertClean(
            await RunAsync(
                _ => Ok("{\"statement_id\":\"" + StatementId + "\",\"status\":{\"state\":\"FAILED\",\"error\":{\"error_code\":\"X\",\"message\":\"" + AccessToken + " " + ClientSecret + "\"}}}"),
                expectFailure: true),
            "statement failed");

        var tokenLogger = new CapturingLogger<DatabricksStatementExecutionClient>();
        var tokenHandler = new FakeDatabricksHandler(_ => Json(HttpStatusCode.BadRequest, $"invalid client secret {ClientSecret} Bearer {AccessToken}"));
        var tokenEx = await CaptureAsync(() => Client(tokenHandler, logger: tokenLogger).ExecuteAsync(Request(), CancellationToken.None));
        AssertClean(tokenEx + "\n" + string.Join('\n', tokenLogger.Lines), "token endpoint failure");
    }
}
