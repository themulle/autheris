namespace Autheris.Domain.Audit;

using System;
using System.Text.Json;
using Autheris.Domain.Model;

/// <summary>Reads the actor kind recorded in the structured details of a permission audit entry.</summary>
public static class AuditActorKindReader
{
    /// <summary>The recorded actor kind (<c>User</c> or <c>ServicePrincipal</c>); null for entries without one or with unparsable details.</summary>
    public static string? Read(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var json = entry.DetailsJson;
        if (string.IsNullOrEmpty(json) || json.Length > 64 * 1024 || !json.Contains("ActorKind", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ActorKind", out var kind)
                && kind.ValueKind == JsonValueKind.String)
            {
                return Enum.TryParse<PermissionAuditActorKind>(kind.GetString(), ignoreCase: true, out var parsed)
                    ? parsed.ToString()
                    : null;
            }
        }
        catch (JsonException)
        {
            // unparsable details never match a filtered view
        }

        return null;
    }
}
