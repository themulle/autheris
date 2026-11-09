using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class AuthenticationPipelineOrderTests
{
    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AUTH-02a")]
    public void Edge_stripping_and_pre_auth_rate_limit_run_before_authentication()
    {
        var text = RepositoryScan.ReadText("src/Autheris.Api/Extensions/GatewayApplicationBuilderExtensions.cs");

        var edge = text.IndexOf("UseMiddleware<EdgeHeaderStrippingMiddleware>", StringComparison.Ordinal);
        var preAuth = text.IndexOf("UseMiddleware<PreAuthIpRateLimitingMiddleware>", StringComparison.Ordinal);
        var authentication = text.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);

        edge.ShouldBeGreaterThan(-1);
        preAuth.ShouldBeGreaterThan(edge);
        authentication.ShouldBeGreaterThan(preAuth);
    }
}
