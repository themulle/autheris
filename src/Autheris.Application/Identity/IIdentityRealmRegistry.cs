namespace Autheris.Application.Identity;

using System.Collections.Generic;
using Autheris.Domain.Identity;

public interface IIdentityRealmRegistry
{
    IdentityRealm? ResolveByIssuer(string issuer);
    IdentityRealm? ResolveRealmByIssuer(string issuer) => ResolveByIssuer(issuer);
    IdentityRealm? GetRealm(string realmId);
    IdentityRealm? GetDefaultRealm();
    IReadOnlyList<IdentityRealm> GetAllRealms();
    void RegisterRealm(IdentityRealm realm);
    bool TryGetRealm(string realmId, out IdentityRealm? realm);
}
