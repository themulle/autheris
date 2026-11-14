namespace Autheris.Application.Identity.Validators;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;

public static class ScimRequestValidator
{
    public const int MaxGroupMembersPerRequest = 1000;

    public static void ValidateUserCreate(ScimUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        ValidateUserCore(
            user.UserName,
            user.ExternalId,
            user.DisplayName,
            user.Email,
            user.ResolvedObjectSid,
            user.ResolvedOnPremisesImmutableId,
            user.ResolvedUserPrincipalName,
            user.ResolvedEntraObjectId);
    }

    public static void ValidateUserUpdate(ScimUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        ValidateUserCore(
            user.UserName,
            user.ExternalId,
            user.DisplayName,
            user.Email,
            user.ResolvedObjectSid,
            user.ResolvedOnPremisesImmutableId,
            user.ResolvedUserPrincipalName,
            user.ResolvedEntraObjectId);
    }

    public static void ValidateUserRequest(ScimUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var primaryEmail = request.Emails?.FirstOrDefault(e => e.Primary)?.Value ?? request.Emails?.FirstOrDefault()?.Value;
        ValidateUserCore(
            request.UserName,
            request.ExternalId,
            request.DisplayName,
            primaryEmail,
            request.ResolvedObjectSid,
            request.ResolvedOnPremisesImmutableId,
            request.ResolvedUserPrincipalName,
            request.ResolvedEntraObjectId);

        if (request.Emails is not null)
        {
            foreach (var email in request.Emails)
            {
                ValidateOptionalBoundedText(email.Value, 256, "email");
            }
        }
    }

    public static void ValidateGroupCreate(ScimGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ValidateGroupCore(group.DisplayName, group.ExternalId, group.ResolvedObjectSid, group.Members);
    }

    public static void ValidateGroupUpdate(ScimGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ValidateGroupCore(group.DisplayName, group.ExternalId, group.ResolvedObjectSid, group.Members);
    }

    public static void ValidateGroupRequest(ScimGroupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGroupCore(request.DisplayName, request.ExternalId, request.ResolvedObjectSid, request.Members);
    }

    public static void ValidatePatchRequest(ScimPatchRequest patch, bool isGroup = false)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Operations is null)
        {
            throw new ValidationException("SCIM PATCH requires Operations.");
        }

        foreach (var op in patch.Operations)
        {
            if (string.IsNullOrWhiteSpace(op.Op))
            {
                throw new ValidationException("SCIM PATCH operation requires 'op'.");
            }

            var path = op.Path?.Trim() ?? string.Empty;
            if (isGroup
                && string.Equals(op.Op, "remove", StringComparison.OrdinalIgnoreCase)
                && IsObjectSidPath(path, isGroup: true))
            {
                throw new ValidationException("Group objectSid cannot be removed.");
            }

            if (string.Equals(op.Op, "add", StringComparison.OrdinalIgnoreCase)
                || string.Equals(op.Op, "replace", StringComparison.OrdinalIgnoreCase))
            {
                ValidatePatchCorrelationValue(path, op.Value, isGroup);
            }
        }
    }

    public static string? NormalizeUpn(string? upn) =>
        upn is null ? null : upn.Trim().ToLowerInvariant();

    public static string? NormalizeEntraObjectId(string? entraObjectId)
    {
        if (entraObjectId is null)
        {
            return null;
        }

        if (!Guid.TryParseExact(entraObjectId.Trim(), "D", out var parsed) || parsed == Guid.Empty)
        {
            throw new ValidationException("User entraObjectId must be a valid non-empty canonical GUID.");
        }

        return parsed.ToString("D");
    }

    public static string NormalizeMemberType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return ScimMemberTypes.User;
        }

        if (string.Equals(type.Trim(), ScimMemberTypes.User, StringComparison.OrdinalIgnoreCase))
        {
            return ScimMemberTypes.User;
        }

        if (string.Equals(type.Trim(), ScimMemberTypes.Group, StringComparison.OrdinalIgnoreCase))
        {
            return ScimMemberTypes.Group;
        }

        throw new ValidationException("Group member type must be 'User' or 'Group'.");
    }

    internal static void ValidateUserCorrelationFields(
        string? objectSid,
        string? onPremisesImmutableId,
        string? userPrincipalName,
        string? entraObjectId)
    {
        if (objectSid is not null)
        {
            ValidateCanonicalNonPrivilegedSid(objectSid, "User");
        }

        if (onPremisesImmutableId is not null)
        {
            if (string.IsNullOrWhiteSpace(onPremisesImmutableId)
                || onPremisesImmutableId.Length > 128
                || !onPremisesImmutableId.All(c => c is >= (char)0x20 and <= (char)0x7E))
            {
                throw new ValidationException("User onPremisesImmutableId must be non-empty printable ASCII up to 128 characters.");
            }
        }

        if (userPrincipalName is not null)
        {
            var trimmed = userPrincipalName.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 256 || trimmed.Any(char.IsControl))
            {
                throw new ValidationException("User userPrincipalName must be a non-empty string up to 256 characters with no control characters.");
            }
        }

        if (entraObjectId is not null)
        {
            _ = NormalizeEntraObjectId(entraObjectId);
        }
    }

    internal static void ValidateGroupObjectSid(string? objectSid)
    {
        if (string.IsNullOrWhiteSpace(objectSid))
        {
            throw new ValidationException("Group objectSid is required in urn:ietf:params:scim:schemas:extension:autheris:2.0:Group.");
        }

        ValidateCanonicalNonPrivilegedSid(objectSid, "Group");
    }

    private static void ValidateUserCore(
        string userName,
        string? externalId,
        string? displayName,
        string? email,
        string? objectSid,
        string? onPremisesImmutableId,
        string? userPrincipalName,
        string? entraObjectId)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 256 || userName.Any(char.IsControl))
        {
            throw new ValidationException("SCIM User requires a valid 'userName'.");
        }

        ValidateOptionalBoundedText(externalId, 128, "externalId");
        ValidateOptionalBoundedText(displayName, 256, "displayName");
        ValidateOptionalBoundedText(email, 256, "email");
        ValidateUserCorrelationFields(objectSid, onPremisesImmutableId, userPrincipalName, entraObjectId);
    }

    private static void ValidateGroupCore(
        string displayName,
        string? externalId,
        string? objectSid,
        IReadOnlyList<ScimGroupMember>? members)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 256 || displayName.Any(char.IsControl))
        {
            throw new ValidationException("SCIM Group requires a valid 'displayName'.");
        }

        ValidateOptionalBoundedText(externalId, 128, "externalId");
        ValidateGroupObjectSid(objectSid);

        if (members is null)
        {
            return;
        }

        if (members.Count > MaxGroupMembersPerRequest)
        {
            throw new ValidationException($"SCIM Group members count cannot exceed {MaxGroupMembersPerRequest}.");
        }

        foreach (var member in members)
        {
            if (member is null || string.IsNullOrWhiteSpace(member.Value) || member.Value.Length > 128 || member.Value.Any(char.IsControl))
            {
                throw new ValidationException("Group member value must be a non-empty string up to 128 characters.");
            }

            _ = NormalizeMemberType(member.Type);
        }
    }

    private static void ValidateCanonicalNonPrivilegedSid(string objectSid, string resourceKind)
    {
        if (string.IsNullOrWhiteSpace(objectSid)
            || objectSid.Length > SidFormat.MaxLength
            || !SidFormat.IsCanonical(objectSid)
            || SidFormat.IsPrivileged(new Sid(objectSid)))
        {
            throw new ValidationException($"{resourceKind} objectSid must be a canonical, non-privileged SID.");
        }
    }

    private static void ValidateOptionalBoundedText(string? value, int maxLength, string attributeName)
    {
        if (value is not null && (value.Length > maxLength || value.Any(char.IsControl)))
        {
            throw new ValidationException($"SCIM '{attributeName}' exceeds length {maxLength} or contains control characters.");
        }
    }

    private static bool IsObjectSidPath(string path, bool isGroup) =>
        string.Equals(path, "objectSid", StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            path,
            isGroup
                ? $"{ScimSchemaUris.AutherisGroupExtension}:objectSid"
                : $"{ScimSchemaUris.AutherisUserExtension}:objectSid",
            StringComparison.OrdinalIgnoreCase);

    private static void ValidatePatchCorrelationValue(string path, object? value, bool isGroup)
    {
        if (IsObjectSidPath(path, isGroup))
        {
            var sidValue = ExtractString(value);
            if (isGroup)
            {
                ValidateGroupObjectSid(sidValue);
            }
            else if (sidValue is not null)
            {
                ValidateCanonicalNonPrivilegedSid(sidValue, "User");
            }
        }
        else if (!isGroup && IsPath(path, "onPremisesImmutableId", ScimSchemaUris.AutherisUserExtension))
        {
            ValidateUserCorrelationFields(null, ExtractString(value), null, null);
        }
        else if (!isGroup && IsPath(path, "userPrincipalName", ScimSchemaUris.AutherisUserExtension))
        {
            ValidateUserCorrelationFields(null, null, ExtractString(value), null);
        }
        else if (!isGroup && IsPath(path, "entraObjectId", ScimSchemaUris.AutherisUserExtension))
        {
            ValidateUserCorrelationFields(null, null, null, ExtractString(value));
        }

        if (isGroup && value is JsonElement arr && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("value", out var valProp) || valProp.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(valProp.GetString()))
                {
                    throw new ValidationException("Group member PATCH item requires a non-empty 'value'.");
                }

                if (item.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
                {
                    _ = NormalizeMemberType(typeProp.GetString());
                }
            }
        }
        else if (isGroup && value is IEnumerable<ScimGroupMember> members)
        {
            foreach (var member in members)
            {
                if (member is null || string.IsNullOrWhiteSpace(member.Value))
                {
                    throw new ValidationException("Group member PATCH item requires a non-empty 'value'.");
                }

                _ = NormalizeMemberType(member.Type);
            }
        }
    }

    private static bool IsPath(string path, string attribute, string extensionUri) =>
        string.Equals(path, attribute, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, $"{extensionUri}:{attribute}", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        JsonElement je when je.ValueKind == JsonValueKind.Null => null,
        _ => value.ToString()
    };
}
