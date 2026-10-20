namespace Autheris.Domain.Identity;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public sealed record ScimUser(
    string? Id,
    string? ExternalId,
    string UserName,
    string? DisplayName,
    string? Email,
    bool Active,
    DateTimeOffset? DeactivatedAt,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt
)
{
    public string UserKey => Id ?? string.Empty;
}

public sealed record ScimGroupMember(
    string Value,
    string? Display = null,
    string? Type = null
);

public sealed record ScimGroup(
    string? Id,
    string? ExternalId,
    string DisplayName,
    IReadOnlyList<ScimGroupMember>? Members,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt
)
{
    public string GroupId => Id ?? string.Empty;
}

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
