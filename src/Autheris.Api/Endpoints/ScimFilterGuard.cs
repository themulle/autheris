namespace Autheris.Api.Endpoints;

using Microsoft.AspNetCore.Http;

/// <summary>
/// PERS-08: the SCIM list endpoints do not evaluate filter expressions. Silently ignoring one returns unfiltered pages (an IdP
/// reconciliation would act on the wrong users), so a supplied filter is rejected with 400 invalidFilter.
/// </summary>
internal static class ScimFilterGuard
{
    /// <summary>NL-SC-36: a compile-time constant; the filter text is never echoed.</summary>
    internal const string UnsupportedFilterDetail = "Filter expressions are not supported by this service provider.";

    internal static IResult? RejectUnsupportedFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        return Results.Json(
            new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" },
                status = "400",
                scimType = "invalidFilter",
                detail = UnsupportedFilterDetail
            },
            statusCode: StatusCodes.Status400BadRequest);
    }
}
