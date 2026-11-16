namespace Autheris.Api.Endpoints;

using System.Globalization;
using Autheris.Domain.Identity;
using Microsoft.AspNetCore.Http;

internal static class ScimErrorResult
{
    private static readonly string[] ScimErrorSchemas = [ScimSchemaUris.Error];

    internal static IResult InvalidValue()
    {
        var body = new ScimError(
            ScimErrorSchemas,
            "Invalid SCIM resource payload or attribute value.",
            StatusCodes.Status400BadRequest.ToString(CultureInfo.InvariantCulture),
            "invalidValue");
        return Results.Json(body, statusCode: StatusCodes.Status400BadRequest, contentType: "application/scim+json");
    }

    internal static IResult UniquenessConflict(string attributeName)
    {
        var fixedDetail = attributeName switch
        {
            "objectSid" => "A SCIM resource with the same objectSid already exists in this realm.",
            "onPremisesImmutableId" => "A SCIM resource with the same onPremisesImmutableId already exists in this realm.",
            "entraObjectId" => "A SCIM resource with the same entraObjectId already exists in this realm.",
            "userPrincipalName" => "A SCIM resource with the same userPrincipalName already exists in this realm.",
            "externalId" => "A SCIM resource with the same externalId already exists in this realm.",
            _ => "A SCIM resource with a conflicting unique attribute already exists in this realm."
        };

        var body = new ScimError(
            ScimErrorSchemas,
            fixedDetail,
            StatusCodes.Status409Conflict.ToString(CultureInfo.InvariantCulture),
            "uniqueness");
        return Results.Json(body, statusCode: StatusCodes.Status409Conflict, contentType: "application/scim+json");
    }

    internal static IResult TooMany(string? detail = null)
    {
        var body = new ScimError(
            ScimErrorSchemas,
            string.IsNullOrWhiteSpace(detail)
                ? "SCIM request exceeds configured member, operation, or payload limits."
                : detail,
            StatusCodes.Status400BadRequest.ToString(CultureInfo.InvariantCulture),
            "tooMany");
        return Results.Json(body, statusCode: StatusCodes.Status400BadRequest, contentType: "application/scim+json");
    }

    internal static IResult RateLimited()
    {
        var body = new ScimError(
            ScimErrorSchemas,
            "SCIM rate limit exceeded for realm. Retry after the indicated delay.",
            StatusCodes.Status429TooManyRequests.ToString(CultureInfo.InvariantCulture),
            null);
        return Results.Json(body, statusCode: StatusCodes.Status429TooManyRequests, contentType: "application/scim+json");
    }

    internal static IResult MassRevocationQuarantined()
    {
        var body = new ScimError(
            ScimErrorSchemas,
            "SCIM mass membership revocation blocked pending quarantine review.",
            StatusCodes.Status409Conflict.ToString(CultureInfo.InvariantCulture),
            null);
        return Results.Json(body, statusCode: StatusCodes.Status409Conflict, contentType: "application/scim+json");
    }

    internal static IResult AuditUnavailable()
    {
        var body = new ScimError(
            ScimErrorSchemas,
            "Required SCIM audit logging is unavailable.",
            StatusCodes.Status503ServiceUnavailable.ToString(CultureInfo.InvariantCulture),
            null);
        return Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable, contentType: "application/scim+json");
    }
}
