namespace Autheris.Api.Endpoints;

using System.Globalization;
using System.Threading.Tasks;
using Autheris.Application.Identity.Services;
using Microsoft.AspNetCore.Http;

/// <summary>WAVE3-SCIM-SC-04: fixed 429 SCIM error for a throttled deprovisioning; no user keys, counts or limit values.</summary>
internal static class ScimMassChangeResult
{
    /// <summary>NL-SC-36: a compile-time constant; no principal or tenant is echoed.</summary>
    internal const string ThrottledDetail = "Too many deprovisioning requests. Retry later.";

    internal static IResult TooManyRequests(ScimMassChangeBlockedException blocked) =>
        new ThrottledResult(blocked.RetryAfterSeconds);

    private sealed class ThrottledResult(int retryAfterSeconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Json(
                new
                {
                    schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" },
                    status = "429",
                    detail = ThrottledDetail
                },
                statusCode: StatusCodes.Status429TooManyRequests).ExecuteAsync(httpContext);
        }
    }
}
