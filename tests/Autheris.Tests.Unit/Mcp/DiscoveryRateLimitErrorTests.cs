namespace Autheris.Tests.Unit.Mcp;

using System.Text.Json;
using Autheris.Application.Catalog.Discovery;
using Autheris.GraphQL.Mcp;
using Shouldly;
using Xunit;

/// <summary>#814: the discovery rate-limit failure carries one code and the wait time on the MCP and GraphQL surfaces, like the REST 429.</summary>
public sealed class DiscoveryRateLimitErrorTests
{
    private static DiscoveryStatusException RateLimited(int? retryAfter) =>
        new(DiscoveryFailureKind.RateLimited, "RATE_LIMIT_EXCEEDED", "Discovery rate limit exceeded.", retryAfter);

    [Fact]
    public void McpRateLimitError_UsesTheRestCodeAndCarriesRetryAfterSeconds()
    {
        var json = GatewayMcpQueryExecutor.CreateDiscoveryErrorResult("t", "list_datasets", RateLimited(17));

        using var doc = JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("RATE_LIMIT_EXCEEDED");
        error.GetProperty("retryAfterSeconds").GetInt32().ShouldBe(17);
    }

    [Fact]
    public void McpErrorWithoutAWaitTime_OmitsRetryAfterSeconds()
    {
        var json = GatewayMcpQueryExecutor.CreateErrorResult("t", "list_datasets", McpErrorCodes.NotFound, "x");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").TryGetProperty("retryAfterSeconds", out _).ShouldBeFalse();
    }

    [Fact]
    public void GraphQlRateLimitError_UsesTheRestCodeAndCarriesRetryAfterSeconds()
    {
        var ex = Autheris.GraphQL.Types.Query.MapDiscoveryGraphQlError(RateLimited(17));

        var error = ex.Errors.Single();
        error.Code.ShouldBe("RATE_LIMIT_EXCEEDED");
        error.Extensions!["retryAfterSeconds"].ShouldBe(17);
    }
}
