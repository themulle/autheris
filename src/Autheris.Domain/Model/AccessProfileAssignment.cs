namespace Autheris.Domain.Model;

using System;
using Autheris.Domain.Common;

public sealed class AccessProfileAssignment
{
    public required string ProfileId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Subject { get; init; }       // Benutzername oder Gruppen-SID
    public required string SubjectType { get; init; }   // "User" oder "Role"
    public DateTimeOffset AssignedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }

    public bool IsActive(DateTimeOffset at) =>
        ExpiresAt == null || ExpiresAt.Value > at;
}
