using System.Diagnostics.Metrics;

namespace Autheris.Api.Security;

/// <summary>Counters of the authentication edge, on the shared <c>Autheris.Core</c> meter.</summary>
internal static class AuthMetrics
{
    private static readonly Meter Meter = new("Autheris.Core");

    public static readonly Counter<long> IdentityHeadersStripped = Meter.CreateCounter<long>(
        "autheris_auth_identity_headers_stripped_total", description: "ForwardAuth identity headers removed from Direct-mode requests.");

    public static readonly Counter<long> ForwardAuthRoleDiscarded = Meter.CreateCounter<long>(
        "autheris_forwardauth_role_discarded_total", description: "ForwardAuth header roles discarded, by fixed reason.");
}
