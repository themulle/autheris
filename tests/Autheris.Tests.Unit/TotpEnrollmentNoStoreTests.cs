using Autheris.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>EP-12: the 2FA enrollment response (TOTP seed) is never cacheable.</summary>
[Trait("Category", "Security")]
public sealed class TotpEnrollmentNoStoreTests
{
    [Fact]
    public void ApplyNoStore_SetsCacheControlAndPragma_EP12()
    {
        var context = new DefaultHttpContext();

        GovernanceApiEndpoints.ApplyNoStore(context.Response);

        context.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        context.Response.Headers.Pragma.ToString().ShouldBe("no-cache");
    }
}
