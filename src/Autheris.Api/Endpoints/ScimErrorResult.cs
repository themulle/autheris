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
}
