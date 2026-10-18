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
    [Range(1, 60)] public int GroupCacheTtlMinutes { get; init; } = 5;
    public bool EnableTestAuthHandler { get; init; }

    public BasicAuthOptions BasicAuth { get; init; } = new();
    public EntraIdAuthOptions EntraId { get; init; } = new();
    public AdfsAuthOptions Adfs { get; init; } = new();
    public ForwardAuthOptions ForwardAuth { get; init; } = new();
}

public sealed class ForwardAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string UserHeader { get; init; } = "X-Forwarded-User";
    public string EmailHeader { get; init; } = "X-Forwarded-Email";
    public string GroupsHeader { get; init; } = "X-Forwarded-Groups";
    public string RolesHeader { get; init; } = "X-Forwarded-Roles";
    public string TenantHeader { get; init; } = "X-Forwarded-Tenant";
    public string? DefaultTenantId { get; init; } = TenantId.LegacySingleTenant.Value;
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
    /// RR-L2-02: When false (default), every group from the groups header is namespaced as
    /// <c>S-1-5-21-FORWARD-GRP-&lt;name&gt;</c>, including values that look like Windows SIDs, so a header can never
    /// impersonate a real AD group SID used in consents. Enable only if the upstream IdP asserts verified SIDs.
    /// </summary>
    public bool TrustUpstreamGroupSids { get; init; } = false;

    /// <summary>
    /// SEC H-1: When true, the proxy-asserted tenant header (X-Forwarded-Tenant) is honored, provided it is in AllowedTenantIds.
    /// When false (default), the tenant header is ignored and DefaultTenantId is used.
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
    /// WP-PF-08: secret reference (resolved through <c>IKeyVaultSecretProvider</c>) of the key that signs the cluster-shared verified-credential
    /// marker. When set and a distributed cache is registered, replicas share the marker (keyed HMAC bound to a credential version) so a client
    /// pays the password hash once per cluster instead of once per replica. Unset: the marker stays per process.
    /// </summary>
    public string? MarkerKeyRef { get; init; }

    /// <summary>DoS protection: Maximum concurrent password hash computations (Argon2id/PBKDF2).</summary>
    public int MaxConcurrentPasswordHashes { get; init; } = Environment.ProcessorCount;

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
    public string? TenantId { get; init; } = Autheris.Domain.Common.TenantId.LegacySingleTenant.Value;
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
    public bool RequireHttpsMetadata { get; init; } = true;
    public string SidClaimType { get; init; } = "oid";
    public string GroupsClaimType { get; init; } = "groups";
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
    /// SEC-GOV113-01: Scopes that permit self-service access requests for read-only tokens.
    /// A token carrying these scopes remains read-only (writes rejected), but is permitted to execute self-service mutations.
    /// </summary>
    public List<string> ReadOnlySelfServiceScopes { get; init; } = ["data.access.request"];

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
    public string SidClaimType { get; init; } = "primarysid";
    public string GroupSidClaimType { get; init; } = "groupsid";
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
    public bool AutoCreateItsmTicket { get; init; } = true;
    public ItsmSystemType PreferredItsmSystem { get; init; } = ItsmSystemType.ServiceNow;
    public bool RequireTotp2Fa { get; init; } = false;
    public bool FailClosedOnClusterPartition { get; init; } = false;
}
