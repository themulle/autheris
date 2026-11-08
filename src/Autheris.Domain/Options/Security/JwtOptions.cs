using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Domain.Options;

public sealed class AuthenticationOptions
{
    [Required] public string Domain { get; init; } = "CORP.LOCAL";
    [Required] public string ServicePrincipalName { get; init; } = "HTTP/gql-gateway.corp.local";
    public bool RequireKerberosOnly { get; init; } = true;
    public bool EnableKerberos { get; init; } = false;
    public bool IsKerberosConfigured => EnableKerberos;
    public bool EnableTestAuthHandler { get; init; }

    /// <summary>
    /// Deployment mode. Unset (<c>Unset</c>) behaves as <c>Direct</c> but is rejected
    /// at startup when <c>ForwardAuth:Enabled</c> is true: a ForwardAuth deployment must name its mode explicitly.
    /// </summary>
    public AuthenticationMode Mode { get; init; } = AuthenticationMode.Unset;

    /// <summary>The mode that applies to requests; an unset mode never selects ForwardAuth.</summary>
    public AuthenticationMode EffectiveMode => Mode == AuthenticationMode.Unset ? AuthenticationMode.Direct : Mode;

    public BasicAuthOptions BasicAuth { get; init; } = new();
    public EntraIdAuthOptions EntraId { get; init; } = new();
    public AdfsAuthOptions Adfs { get; init; } = new();
    public ForwardAuthOptions ForwardAuth { get; init; } = new();
}

/// <summary>How callers reach Autheris: with their own credential, through a trusted upstream proxy, or both (never mixed per request).</summary>
public enum AuthenticationMode
{
    Unset = 0,
    Direct = 1,
    Upstream = 2,
    Both = 3
}

public sealed class ForwardAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string UserHeader { get; init; } = "X-Forwarded-User";
    public string EmailHeader { get; init; } = "X-Forwarded-Email";
    public string GroupsHeader { get; init; } = "X-Forwarded-Groups";
    public string RolesHeader { get; init; } = "X-Forwarded-Roles";
    public string TenantHeader { get; init; } = "X-Forwarded-Tenant";
    public string? SharedSecretKeyVaultRef { get; init; }
    public string? SharedSecret { get; init; }
    public string SharedSecretHeader { get; init; } = "X-Forwarded-Secret";
    public bool RequireTrustedProxy { get; init; } = true;
    public List<string> TrustedProxies { get; init; } = [];
    public List<string> TrustedNetworks { get; init; } = [];

    /// <summary>
    /// RR-L2-02: Allowlist of roles accepted from the (proxy-asserted) roles header. Matching uses the role name
    /// after an optional "tenant:" prefix. Administrative roles (ClusterAdmin, PlatformAdmin, GatewayAdmin,
    /// GovernanceAdmin, SecurityAdmin, PrivacyAdmin, BreakGlassOperator) are never accepted from headers.
    /// When not configured, GatewayUser, Consumer, Analyst and Developer are accepted; a configured list replaces that
    /// default entirely. Nullable on purpose: the configuration binder appends to pre-filled lists, so a non-empty
    /// default could not be narrowed. An empty JSON array aborts the start (it would silently apply the default); a list containing only a
    /// role name that no proxy asserts accepts no header role.
    /// </summary>
    public List<string>? AllowedRoles { get; init; }

    private static readonly string[] DefaultAllowedRoles = ["GatewayUser", "Consumer", "Analyst", "Developer"];

    /// <summary>Configured <see cref="AllowedRoles"/> or the default list.</summary>
    public IReadOnlyList<string> GetEffectiveAllowedRoles() => AllowedRoles ?? (IReadOnlyList<string>)DefaultAllowedRoles;

    /// <summary>
    /// Header carrying the user SID asserted by the upstream proxy. Honoured only from a trusted ForwardAuth peer with
    /// <c>TrustUpstreamSids</c> and only when the value is a canonical SID; otherwise it is ignored.
    /// </summary>
    public string? UserSidHeader { get; init; }

    /// <summary>
    /// When false (default), raw SIDs from upstream headers are never accepted. Replaces the legacy <c>TrustUpstreamGroupSids</c> key.
    /// </summary>
    public bool? TrustUpstreamSids { get; init; }

    /// <summary>Legacy key of <c>TrustUpstreamSids</c>, bound as an alias for one release (startup warns; conflicting values fail).</summary>
    public bool? TrustUpstreamGroupSids { get; init; }

    /// <summary>The effective upstream SID trust: the new key, else the legacy alias, else false.</summary>
    public bool IsUpstreamSidTrusted => TrustUpstreamSids ?? TrustUpstreamGroupSids ?? false;

    private string[]? _identityHeaderNames;
    private HashSet<string>? _allowedRoleSet;
    private byte[]? _sharedSecretDigest;

    /// <summary>Configured, non-blank identity header names (user, email, groups, roles, tenant, user SID); computed once from options only.</summary>
    public IReadOnlyList<string> IdentityHeaderNames => _identityHeaderNames ??=
        [.. new[] { UserHeader, EmailHeader, GroupsHeader, RolesHeader, TenantHeader, UserSidHeader }.Where(h => !string.IsNullOrWhiteSpace(h))!];

    /// <summary>The effective allowed roles as a case-insensitive set, built once.</summary>
    public IReadOnlySet<string> AllowedRoleSet => _allowedRoleSet ??= new HashSet<string>(GetEffectiveAllowedRoles(), StringComparer.OrdinalIgnoreCase);

    // SHA-256 of the plain shared secret (UTF-8), computed once; null when no plain secret is configured.
    public byte[]? SharedSecretDigest => string.IsNullOrWhiteSpace(SharedSecret)
        ? null
        : _sharedSecretDigest ??= System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SharedSecret));

    /// <summary>
    /// SEC H-1: When true, the proxy-asserted tenant header (X-Forwarded-Tenant) is honored, provided it is in AllowedTenantIds.
    /// When false (default), the tenant header is ignored and the principal falls back to Gateway:Tenancy:DefaultTenantId.
    /// </summary>
    public bool TrustUpstreamTenant { get; init; } = false;

    /// <summary>SEC H-1: Allowlist of tenant IDs accepted from the tenant header when TrustUpstreamTenant is true. Must not be empty when TrustUpstreamTenant is true.</summary>
    public List<string> AllowedTenantIds { get; init; } = [];
}

public sealed class BasicAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string Realm { get; init; } = "Autheris";
    public List<BasicAuthUserConfig> Users { get; init; } = [];

    /// <summary>RR-L2-03: Minimum PBKDF2-SHA256 iterations enforced at startup outside Development (OWASP 2023: 600 000; floor 210 000).</summary>
    public int MinimumPbkdf2Iterations { get; init; } = 210_000;

    /// <summary>RR-L2-03: Failed attempts per (user, client IP) within <see cref="FailureWindowSeconds"/> before the pair is locked out.</summary>
    public int MaxFailedAttempts { get; init; } = 10;

    /// <summary>Review E-13: Failed attempts from one client address (IPv6: /64) over all user names within <see cref="FailureWindowSeconds"/> before that address is locked out.</summary>
    public int MaxFailedAttemptsPerIp { get; init; } = 50;

    /// <summary>RR-L2-03: Sliding failure window and lockout duration in seconds.</summary>
    public int FailureWindowSeconds { get; init; } = 300;

    /// <summary>RR-L2-03: Lifetime of a verified-credential cache entry in seconds (0 disables the cache).</summary>
    public int SuccessCacheSeconds { get; init; } = 30;

    /// <summary>
    /// secret reference (resolved through<c>IKeyVaultSecretProvider</c>) of the key that signs the cluster-shared verified-credential
    /// marker. When set and a distributed cache is registered, replicas share the marker (keyed HMAC bound to a credential version) so a client
    /// pays the password hash once per cluster instead of once per replica. Unset: the marker stays per process.
    /// </summary>
    public string? MarkerKeyRef { get; init; }

    /// <summary>DoS protection: Maximum concurrent password hash computations (Argon2id/PBKDF2).</summary>
    public int MaxConcurrentPasswordHashes { get; init; } = 16;

    /// <summary>
    /// F-AUTH-DX: Optional cookie session issued after a successful Basic login (developer experience).
    /// Only permitted in the environments listed in <see cref="BasicAuthSessionOptions.AllowedEnvironments"/>,
    /// never in Production.
    /// </summary>
    public BasicAuthSessionOptions Session { get; init; } = new();
}

/// <summary>
/// F-AUTH-DX: Cookie session for Basic authentication. After one successful Basic login the gateway issues an
/// encrypted, HttpOnly, SameSite=Strict cookie so browsers, cookie jars (curl/Postman/PowerShell), WebSockets and
/// EventSource keep the identity without re-sending (and re-verifying) the password.
/// </summary>
public sealed class BasicAuthSessionOptions
{
    /// <summary>Authentication scheme (and identity authentication type) of the session cookie.</summary>
    public const string SchemeName = "BasicSession";

    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Environments in which the session cookie may be enabled. "Production" is never accepted. When not configured,
    /// only Development is allowed; a configured list replaces that default entirely. Nullable on purpose: the
    /// configuration binder appends to pre-filled lists, so a non-empty default could not be narrowed. An empty JSON
    /// array aborts the start (it would silently apply the default); to disable the session everywhere, set
    /// <see cref="Enabled"/> to false.
    /// </summary>
    public List<string>? AllowedEnvironments { get; init; }

    private static readonly string[] DefaultAllowedEnvironments = ["Development"];

    /// <summary>Configured <see cref="AllowedEnvironments"/> or the default list (Development).</summary>
    public IReadOnlyList<string> GetEffectiveAllowedEnvironments() => AllowedEnvironments ?? (IReadOnlyList<string>)DefaultAllowedEnvironments;

    /// <summary>Cookie name; must use the <c>__Host-</c> prefix (Secure, Path=/, no Domain).</summary>
    public string CookieName { get; init; } = "__Host-Autheris.Session";

    /// <summary>Sliding idle timeout of the session in minutes.</summary>
    [Range(5, 1440)] public int SlidingExpirationMinutes { get; init; } = 480;

    /// <summary>Absolute session lifetime in minutes, independent of activity.</summary>
    [Range(5, 10080)] public int AbsoluteExpirationMinutes { get; init; } = 1440;

    /// <summary>
    /// Directory for the ASP.NET Data Protection key ring. Required with multiple replicas (shared volume);
    /// otherwise the framework default (user profile) is used so cookies survive restarts.
    /// </summary>
    public string? KeyDirectory { get; init; }
}

public sealed class BasicAuthUserConfig
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? Sid { get; init; }
    public string? TenantId { get; init; }
    public List<string> Roles { get; init; } = [];
    public List<string> GroupSids { get; init; } = [];
}

public sealed class EntraIdAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string Instance { get; init; } = "https://login.microsoftonline.com/";
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string MetadataAddress { get; init; } = string.Empty;
    public string Authority => string.IsNullOrWhiteSpace(TenantId) ? Instance : $"{Instance.TrimEnd('/')}/{TenantId.Trim('/')}";
    public bool RequireHttpsMetadata { get; init; } = true;
    public string RolesClaimType { get; init; } = "roles";

    /// <summary>
    /// Optional allowlist of authorized client application IDs (azp / appid) for app-only tokens.
    /// If empty, any app-only token with valid assigned application roles from the tenant is allowed.
    /// </summary>
    public List<string> AllowedClientIds { get; init; } = [];

    /// <summary>
    /// Delegated scopes (<c>scp</c>) that only permit reading, e.g. an agent scope. A user token whose scopes all appear
    /// here is read-only: writes over HTTP, GraphQL mutations and WebSQL DML are rejected.
    /// When not configured, "Agent.Read" is the read-only scope; a configured list replaces that default entirely.
    /// Nullable on purpose: the configuration binder appends to pre-filled lists, so a non-empty default could not be
    /// replaced. An empty JSON array is logged as a startup warning and treated as not configured: the default
    /// (Agent.Read) is more restrictive than no read-only scope, so the start is not aborted.
    /// </summary>
    public List<string>? ReadOnlyScopes { get; init; }

    private static readonly string[] DefaultReadOnlyScopes = ["Agent.Read", "App.Read"];

    /// <summary>Configured <see cref="ReadOnlyScopes"/> or the default list (Agent.Read, App.Read).</summary>
    public IReadOnlyList<string> GetEffectiveReadOnlyScopes() => ReadOnlyScopes ?? (IReadOnlyList<string>)DefaultReadOnlyScopes;

    /// <summary>
    /// 01 / Issue #226 / #237: Global switch for self-service access requests and reviews.
    /// When false, self-service operations are disabled.
    /// </summary>
    public bool SelfServiceEnabled { get; init; } = true;

    /// <summary>
    /// Scopes that permit self-service access requests for read-only tokens.
    /// A token carrying these scopes remains read-only (writes rejected), but is permitted to execute self-service mutations.
    /// </summary>
    public List<string> ReadOnlySelfServiceScopes { get; init; } = ["data.access.request"];

    /// <summary>
    /// 02 / Issue #226: Scopes that permit reviewing (approving or rejecting) access requests.
    /// </summary>
    public List<string> AccessReviewScopes { get; init; } = ["data.access.review"];

    /// <summary>How app-only tokens (client credentials, no signed-in person) are treated. Defaults to ReadOnly.</summary>
    public AppOnlyTokenAccess AppOnlyTokens { get; init; } = AppOnlyTokenAccess.ReadOnly;
}

public enum AppOnlyTokenAccess
{
    ReadWrite,
    ReadOnly,
    Deny
}

public sealed class AdfsAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string Authority { get; init; } = string.Empty;
    public string MetadataAddress { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public bool RequireHttpsMetadata { get; init; } = true;
    public string RolesClaimType { get; init; } = "role";
}

/// <summary>
/// SR-M-05 / SR-L-05: TOTP attempt throttling, enrollment lifetime and seed encryption at rest.
/// </summary>
public sealed class TotpOptions
{
    /// <summary>Failed TOTP verifications per user within <see cref="LockoutMinutes"/> before further attempts are rejected.</summary>
    public int MaxFailedAttempts { get; init; } = 5;

    /// <summary>Length of the failed-attempt window and of the resulting lockout.</summary>
    public int LockoutMinutes { get; init; } = 15;

    /// <summary>Lifetime of a server-side pending (not yet verified) enrollment seed.</summary>
    public int PendingEnrollmentMinutes { get; init; } = 10;

    /// <summary>
    /// Maximum age of the interactive authentication for a user's first TOTP enrollment. Without an active secret there
    /// is no second factor to prove, so a stolen long-lived credential must not be able to bind an authenticator (trust
    /// on first use). JWT bearer identities (Entra ID / ADFS) must carry <c>auth_time</c>; <c>iat</c> is not accepted
    /// for JWTs because a silent token refresh resets it. Basic session cookies are judged by their <c>iat</c> (the login
    /// time, kept across sliding refreshes). ForwardAuth (proxy) identities carry no authentication time and their first
    /// enrollment is always denied. Per-request authentication (Negotiate/Kerberos/NTLM, raw Basic header) is allowed and
    /// audited; unknown authentication types are rejected. The verify step accepts an authentication age of up to this
    /// value plus <see cref="PendingEnrollmentMinutes"/> (the pending seed was created by a begin that passed this check).
    /// Not enforced in the Development environment.
    /// </summary>
    public int FirstEnrollmentMaxAuthAgeMinutes { get; init; } = 5;

    /// <summary>
    /// Encrypts TOTP seeds at rest with ASP.NET Data Protection. Requires a persisted key ring shared by all nodes
    /// (otherwise seeds become unreadable after a restart or on other nodes), hence off by default.
    /// </summary>
    public bool EncryptSecretsAtRest { get; init; } = false;
}

public sealed class HitLStepUpOptions
{
    public bool Enabled { get; init; } = true;
    public int ApprovalTimeoutSeconds { get; init; } = 15;
    public bool RequireDifferentApprover { get; init; } = true;
    public bool RequireTotp2Fa { get; init; } = true;
    public bool FailClosedOnClusterPartition { get; init; } = false;
}
