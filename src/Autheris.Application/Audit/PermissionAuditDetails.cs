namespace Autheris.Application.Audit;

using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autheris.Application.Catalog.External;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Structured audit payload emitted by permission request, decision, grant, and self-grant operations.
/// Never carries data values, sample values, secrets, or raw webhook URLs.
/// </summary>
public sealed record PermissionAuditDetails(
    string Actor,
    string Subject,
    [property: JsonPropertyName("Object")] string TargetObject,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    string? Justification,
    ApproverCapacity? Capacity,
    bool SelfGrant,
    Guid? RequestId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    PermissionAuditActorKind ActorKind = PermissionAuditActorKind.User,
    string? GrantKind = null,
    string? ErrorCode = null,
    int? ExpectedVersion = null)
{
    public static PermissionAuditActorKind ResolveActorKind(SecurityPrincipalContext? context)
    {
        if (context is null)
        {
            return PermissionAuditActorKind.User;
        }

        if (context.Principal is not null && AppOnlyPrincipal.Is(context.Principal))
        {
            return PermissionAuditActorKind.ServicePrincipal;
        }

        var claims = context.Claims ?? context.Principal?.Claims;
        if (claims is not null)
        {
            var claimList = claims as System.Collections.Generic.IReadOnlyCollection<System.Security.Claims.Claim> ?? claims.ToList();
            if (claimList.Any(c => string.Equals(c.Type, "idtyp", StringComparison.OrdinalIgnoreCase) && string.Equals(c.Value, "app", StringComparison.OrdinalIgnoreCase)))
            {
                return PermissionAuditActorKind.ServicePrincipal;
            }

            if (claimList.Any(c => string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase) && string.Equals(c.Value, "AppRole", StringComparison.Ordinal))
                && !claimList.Any(c => string.Equals(c.Type, "scp", StringComparison.OrdinalIgnoreCase) || string.Equals(c.Type, "http://schemas.microsoft.com/identity/claims/scope", StringComparison.OrdinalIgnoreCase)))
            {
                return PermissionAuditActorKind.ServicePrincipal;
            }
        }

        return PermissionAuditActorKind.User;
    }

    public string ToDetailsJson() => JsonSerializer.Serialize(this);
}
