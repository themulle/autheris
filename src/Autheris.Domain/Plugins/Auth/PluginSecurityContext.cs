namespace Autheris.Domain.Plugins.Auth;

using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using Autheris.Domain.Common;

/// <summary>
/// Sanitized, immutable security context passed to IPluginRoleProvider.
/// Deliberately omits raw tokens, client secrets, passwords, and sensitive headers (SEC-BL08-12).
/// </summary>
public sealed record PluginSecurityContext
{
    public required TenantId TenantId { get; init; }
    public required Sid UserSid { get; init; }
    public required IReadOnlySet<Sid> GroupSids { get; init; }
    public required IReadOnlySet<string> ExistingTenantRoles { get; init; }
    public required IReadOnlyList<Claim> Claims { get; init; }
    public IPAddress? ClientIp { get; init; }
    public required string AuthenticationScheme { get; init; }
}
