namespace Autheris.Domain.Security;

using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Options;

/// <summary>Which token scopes are restricted (REQ-GOV-143): their holders need a scope-specific <c>Clear</c> for sensitive columns.</summary>
public interface IRestrictedScopePolicy
{
    /// <summary>The restricted scope names (bare names, ordinal-ignore-case). Empty when the restriction is disabled.</summary>
    IReadOnlySet<string> Scopes { get; }

    /// <summary>Stable fingerprint of the effective list (SHA-256 of the sorted names); <c>"off"</c> when disabled. Guards the shared decision cache.</summary>
    string Fingerprint { get; }

    /// <summary>The restricted scopes held by the caller (bare names of <see cref="Scopes"/>); empty when none is held or <paramref name="callerScopes"/> is null.</summary>
    IReadOnlySet<string> HeldBy(IReadOnlySet<string>? callerScopes);
}

/// <summary>The default implementation, built once from <see cref="RestrictedScopeOptions"/>.</summary>
public sealed class RestrictedScopePolicy : IRestrictedScopePolicy
{
    private static readonly HashSet<string> NoScopes = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _set;

    /// <summary>The secure default: <c>Agent.Read</c> is restricted.</summary>
    public static RestrictedScopePolicy Default { get; } = new([RestrictedScopeOptions.DefaultScope]);

    /// <summary>No restricted scope.</summary>
    public static RestrictedScopePolicy Disabled { get; } = new(null);

    private RestrictedScopePolicy(IEnumerable<string>? names)
    {
        if (names is null)
        {
            _set = NoScopes;
            Fingerprint = "off";
            return;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var bare = ScopeNames.ToBareName(name);
            if (bare.Length > 0)
            {
                set.Add(bare);
            }
        }

        _set = set;
        var sorted = set.Select(static s => s.ToUpperInvariant()).Order(StringComparer.Ordinal);
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', sorted))));
    }

    public IReadOnlySet<string> Scopes => _set;

    public string Fingerprint { get; }

    /// <summary>The policy of <paramref name="options"/>: disabled, the configured list, or the default for a null or empty list.</summary>
    public static RestrictedScopePolicy FromOptions(RestrictedScopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return Disabled;
        }

        return options.Scopes is { Count: > 0 } list ? new RestrictedScopePolicy(list) : Default;
    }

    public IReadOnlySet<string> HeldBy(IReadOnlySet<string>? callerScopes)
    {
        if (_set.Count == 0 || callerScopes is null || callerScopes.Count == 0)
        {
            return NoScopes;
        }

        HashSet<string>? held = null;
        foreach (var scope in callerScopes)
        {
            if (scope is null)
            {
                continue;
            }

            var bare = ScopeNames.ToBareName(scope);
            if (bare.Length > 0 && _set.TryGetValue(bare, out var configured))
            {
                (held ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(configured);
            }
        }

        return held ?? NoScopes;
    }
}
