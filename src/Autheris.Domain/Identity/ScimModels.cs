namespace Autheris.Domain.Identity;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Autheris.Domain.Common;

public static class ScimSchemaUris
{
    public const string CoreUser = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string CoreGroup = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string AutherisUserExtension = "urn:ietf:params:scim:schemas:extension:autheris:2.0:User";
    public const string AutherisGroupExtension = "urn:ietf:params:scim:schemas:extension:autheris:2.0:Group";
    public const string PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";
    public const string ServiceProviderConfig = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";
    public const string ResourceType = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
    public const string Schema = "urn:ietf:params:scim:schemas:core:2.0:Schema";
}

public static class ScimMemberTypes
{
    public const string User = "User";
    public const string Group = "Group";
}

public sealed record ScimUserAutherisExtension(
    [property: JsonPropertyName("objectSid")] string? ObjectSid = null,
    [property: JsonPropertyName("onPremisesImmutableId")] string? OnPremisesImmutableId = null,
    [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName = null,
    [property: JsonPropertyName("entraObjectId")] string? EntraObjectId = null);

public sealed record ScimGroupAutherisExtension(
    [property: JsonPropertyName("objectSid")] string? ObjectSid = null);

public sealed record ScimUser(
    string? Id,
    string? ExternalId,
    string UserName,
    string? DisplayName,
    string? Email,
    bool Active,
    DateTimeOffset? DeactivatedAt,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? ObjectSid = null,
    string? OnPremisesImmutableId = null,
    string? UserPrincipalName = null,
    string? EntraObjectId = null
)
{
    private readonly ScimUserAutherisExtension? _autherisExtension;

    public string UserKey => Id ?? string.Empty;

    [JsonPropertyName(ScimSchemaUris.AutherisUserExtension)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScimUserAutherisExtension? AutherisExtension
    {
        get => ObjectSid is null && OnPremisesImmutableId is null && UserPrincipalName is null && EntraObjectId is null
            ? _autherisExtension
            : new ScimUserAutherisExtension(ObjectSid, OnPremisesImmutableId, UserPrincipalName, EntraObjectId);
        init => _autherisExtension = value;
    }

    [JsonIgnore]
    public string? ResolvedObjectSid => _autherisExtension?.ObjectSid ?? ObjectSid;

    [JsonIgnore]
    public string? ResolvedOnPremisesImmutableId => _autherisExtension?.OnPremisesImmutableId ?? OnPremisesImmutableId;

    [JsonIgnore]
    public string? ResolvedUserPrincipalName => _autherisExtension?.UserPrincipalName ?? UserPrincipalName;

    [JsonIgnore]
    public string? ResolvedEntraObjectId => _autherisExtension?.EntraObjectId ?? EntraObjectId;
}

public sealed record ScimGroupMember(
    string Value,
    string? Display = null,
    [property: JsonPropertyName("type")] string? Type = ScimMemberTypes.User
);

public sealed record ScimGroup(
    string? Id,
    string? ExternalId,
    string DisplayName,
    IReadOnlyList<ScimGroupMember>? Members,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? ObjectSid = null
)
{
    private readonly ScimGroupAutherisExtension? _autherisExtension;

    public string GroupId => Id ?? string.Empty;

    [JsonPropertyName(ScimSchemaUris.AutherisGroupExtension)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScimGroupAutherisExtension? AutherisExtension
    {
        get => ObjectSid is null ? _autherisExtension : new ScimGroupAutherisExtension(ObjectSid);
        init => _autherisExtension = value;
    }

    [JsonIgnore]
    public string? ResolvedObjectSid => _autherisExtension?.ObjectSid ?? ObjectSid;
}

public sealed record ScimName(
    string? Formatted = null,
    string? FamilyName = null,
    string? GivenName = null);

public sealed record ScimEmail(
    string Value,
    string? Type = null,
    bool Primary = false);

public sealed record ScimUserRequest(
    IReadOnlyList<string>? Schemas,
    string? ExternalId,
    string UserName,
    string? DisplayName,
    ScimName? Name,
    IReadOnlyList<ScimEmail>? Emails,
    bool Active = true,
    [property: JsonPropertyName(ScimSchemaUris.AutherisUserExtension)]
    ScimUserAutherisExtension? AutherisExtension = null,
    string? ObjectSid = null,
    string? OnPremisesImmutableId = null,
    string? UserPrincipalName = null,
    string? EntraObjectId = null)
{
    [JsonIgnore]
    public string? ResolvedObjectSid => AutherisExtension?.ObjectSid ?? ObjectSid;

    [JsonIgnore]
    public string? ResolvedOnPremisesImmutableId => AutherisExtension?.OnPremisesImmutableId ?? OnPremisesImmutableId;

    [JsonIgnore]
    public string? ResolvedUserPrincipalName => AutherisExtension?.UserPrincipalName ?? UserPrincipalName;

    [JsonIgnore]
    public string? ResolvedEntraObjectId => AutherisExtension?.EntraObjectId ?? EntraObjectId;
}

public sealed record ScimGroupRequest(
    IReadOnlyList<string>? Schemas,
    string? ExternalId,
    string DisplayName,
    IReadOnlyList<ScimGroupMember>? Members,
    [property: JsonPropertyName(ScimSchemaUris.AutherisGroupExtension)]
    ScimGroupAutherisExtension? AutherisExtension = null,
    string? ObjectSid = null)
{
    [JsonIgnore]
    public string? ResolvedObjectSid => AutherisExtension?.ObjectSid ?? ObjectSid;
}

public sealed record ScimGroupNestingEdge(
    string ParentGroupId,
    string ChildGroupId);

public sealed record ScimRealmSyncState(
    TenantId TenantId,
    string RealmId,
    DateTimeOffset? LastContactAt,
    DateTimeOffset? LastWriteAt,
    long DeprovisionEpoch = 0);

public sealed class ScimUniquenessConflictException(string attributeName, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string AttributeName { get; } = attributeName;
}

public sealed record ScimError(
    [property: JsonPropertyName("schemas")] IReadOnlyList<string> Schemas,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("scimType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ScimType = null);

public sealed record ScimLock(
    TenantId TenantId,
    string RealmId,
    string UserKey,
    string Reason,
    string Source,
    DateTimeOffset LockedAt
);

public sealed record ScimPatchOperation(
    string Op,
    string? Path,
    object? Value
);

public sealed record ScimPatchRequest(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<ScimPatchOperation> Operations
);

public sealed record ScimListResponse<T>(
    int TotalResults,
    int StartIndex,
    int ItemsPerPage,
    IReadOnlyList<T> Resources,
    IReadOnlyList<string>? Schemas = null
);

/// <summary>Result of a bounded lock count: <see cref="Count"/> is capped at the requested cap; <see cref="Oldest"/> is null when empty.</summary>
public sealed record ScimLockWindow(int Count, DateTimeOffset? Oldest);
