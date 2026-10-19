namespace Autheris.Domain.Options;

/// <summary>
/// <c>Gateway:Governance:RestrictedScopes</c> (REQ-GOV-143, PLAN-GOV-143 D-1/D-15): token scopes whose holders get a catalog-sensitive column
/// in clear only when a consent column rule with exactly that scope grants <c>Clear</c>. Read through <c>IOptions</c> (not reloadable).
/// </summary>
public sealed class RestrictedScopeOptions
{
    public const string SectionName = "Gateway:Governance:RestrictedScopes";

    /// <summary>The restricted scope when <see cref="Scopes"/> is null or empty.</summary>
    public const string DefaultScope = "Agent.Read";

    /// <summary>Upper bound of <see cref="Scopes"/> (SEC-GOV143-11).</summary>
    public const int MaxScopes = 64;

    /// <summary>Longest accepted scope entry.</summary>
    public const int MaxScopeLength = 256;

    /// <summary>The explicit master switch. <c>false</c> is only accepted in Development (SEC-GOV143-06).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Restricted scopes (bare or full form). <c>null</c> or empty (the binder maps <c>[]</c> to <c>null</c>) means <c>["Agent.Read"]</c>.</summary>
    public List<string>? Scopes { get; set; }
}
