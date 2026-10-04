namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public enum IdentityProviderType
{
    ActiveDirectory = 1,     // On-Premise AD (Kerberos / Negotiate / PrimarySid)
    EntraId = 2,             // Microsoft Entra ID (OIDC / OAuth2 JWT Bearer)
    ServiceAccount = 3,      // OAuth2 Client Credentials / App-Only Token
    ClientCertificate = 4,   // mTLS (X.509 Client Certificate)
    ForwardAuth = 5,         // Reverse Proxy Authentication (Authelia, Keycloak)
    BasicAuth = 6,           // Local Basic Authentication
    TestAuth = 7             // Test Environment
}

public enum SubjectType
{
    User = 1,
    Group = 2,
    Role = 3,
    ServicePrincipal = 4     // Machine-to-Machine (Batch, ETL, Service Account)
}

public sealed record SubjectIdentity(
    string Id,                     // SID, OID or ClientId
    SubjectType Type,              // User, Group, Role, ServicePrincipal
    IdentityProviderType Provider, // Identity Source
    string? DisplayName = null,    // UPN, Email or Client/App Name
    TenantId? Tenant = null,       // Entra tid or resolved tenant
    IReadOnlySet<string>? Roles = null
)
{
    public bool IsServicePrincipal => Type == SubjectType.ServicePrincipal;
    public bool IsUser => Type == SubjectType.User;
}
