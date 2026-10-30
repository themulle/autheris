namespace Autheris.Domain.Tenancy;

/// <summary>
/// DT-A-3 / DT-SC-05: the informational claim that records how the request tenant was decided. It is overwritten (all inbound values removed,
/// then one added) at every principal-creating ingress and is read by telemetry and <c>SecurityPrincipalContext.TenantSource</c> only,
/// never by an authorization, scope or visibility decision.
/// </summary>
public static class TenantSourceClaim
{
    public const string ClaimType = "autheris:tenant_source";

    public const string Claim = "claim";

    public const string Realm = "realm";

    public const string Default = "default";

    public const string Header = "header";
}
