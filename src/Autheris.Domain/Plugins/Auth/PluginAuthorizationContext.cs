namespace Autheris.Domain.Plugins.Auth;

using System.Collections.Generic;
using System.Net;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed record PluginAuthorizationContext
{
    public required TenantId TenantId { get; init; }
    public required Sid UserSid { get; init; }
    public required IReadOnlySet<Sid> GroupSids { get; init; }
    public required IReadOnlySet<string> Roles { get; init; }
    public required TableIdentifier TableIdentifier { get; init; }
    public IReadOnlyList<string>? RequestedColumns { get; init; }
    public ProtectionClass TableClass { get; init; } = ProtectionClass.Confidential;
    public IPAddress? ClientIp { get; init; }
    public IReadOnlyDictionary<string, object?>? ExtraAttributes { get; init; }
}
