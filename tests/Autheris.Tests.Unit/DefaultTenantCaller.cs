using System.Security.Claims;
using Autheris.Domain.Common;

namespace Autheris.Tests.Unit;

/// <summary>
/// Default-tenant refactor (DT-A-3): <c>GetTenantId</c> reads claims only, so unit-test principals that stand for a caller of the
/// (formerly implicit) single tenant carry this explicit tenant claim instead of relying on a fallback.
/// </summary>
internal static class DefaultTenantCaller
{
    public static Claim Claim => new("tenant_id", Autheris.Tests.Common.TestTenants.Default.Value);

    public static ClaimsPrincipal Principal() => new(new ClaimsIdentity([Claim]));

    /// <summary>
    /// DT-A-3: realm bindings are applied once at the ingress (the tenant stamping of the claims transformation); callers then read the
    /// stamped claim only. Stamps <paramref name="identity"/> (which carries the <c>autheris:realm</c> claim) as a realm-bound principal.
    /// </summary>
    public static ClaimsPrincipal StampRealm(Autheris.Domain.Identity.IdentityRealm realm, ClaimsIdentity identity)
    {
        var registry = NSubstitute.Substitute.For<Autheris.Application.Identity.IIdentityRealmRegistry>();
        NSubstitute.SubstituteExtensions.Returns(registry.GetRealm(realm.RealmId), realm);
        var resolver = new Autheris.Application.Tenancy.TenantResolver(
            static () => new Autheris.Domain.Options.TenancyOptions(), registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Autheris.Application.Tenancy.TenantResolver>.Instance);
        Autheris.Api.Security.TenantStamping.Apply(identity, resolver, realmBound: true);
        return new ClaimsPrincipal(identity);
    }
}
