namespace Autheris.Domain.Identity;

using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;

public sealed record ResolvedIdentity(
    string UserKey,
    string? Upn,
    string? DisplayName,
    string? Email,
    TenantId TenantId,
    string RealmId,
    IReadOnlyList<Claim> RawClaims
);
