namespace Autheris.Domain.Options;

/// <summary>
/// Development-only switches (<c>Gateway:Dev</c>). Class A switches (convenience) and class B switches
/// (development security) live here; security bypasses (<c>warn_*</c> / <c>danger_*</c>) stay in
/// <see cref="InsecureGettingStartedOptions"/>. Outside Development every non-default value is a startup error
/// (see <see cref="DevOptionsValidator"/>). Concept: docs/architecture/concept-unified-dev-switches-2026-10-05.md
/// </summary>
public sealed class DevOptions
{
    public const string PresetStandard = "Standard";
    public const string PresetQuickstart = "Quickstart";
    public const string PresetStrict = "Strict";

    /// <summary>
    /// <c>Standard</c> (convenience on, security strict), <c>Quickstart</c> (Standard plus relaxed CORS, introspection,
    /// auto-approve and OpenSchema) or <c>Strict</c> (no test auth, no introspection).
    /// </summary>
    public string Preset { get; init; } = PresetStandard;

    /// <summary>Class A: startup banner with links and personas.</summary>
    public bool Banner { get; init; } = true;

    /// <summary>Class A: print plaintext passwords in the startup banner (default: false).</summary>
    public bool ShowPasswords { get; init; } = false;

    /// <summary>Class A: diagnostic problem+json for 403 responses and unhandled exceptions.</summary>
    public bool VerboseErrors { get; init; } = true;

    /// <summary>Class A: <c>/api/dev/personas</c> and <c>/api/dev/login/{persona}</c>.</summary>
    public bool PersonaLogin { get; init; } = true;

    /// <summary>Class A: <c>/api/dev/info</c> (effective configuration without secrets).</summary>
    public bool Info { get; init; } = true;

    /// <summary>Class A: seed demo catalog data. Null = preset default.</summary>
    public bool? DemoData { get; init; }

    /// <summary>Class A: keep the governance database in a file instead of memory.</summary>
    public DevPersistOptions Persist { get; init; } = new();

    /// <summary>Class B: header-based test identities (<c>X-Test-User-Sid</c>). Null = preset default.</summary>
    public bool? TestAuthHandler { get; init; }

    public DevToolingOptions Tooling { get; init; } = new();
}

public sealed class DevPersistOptions
{
    public bool Enabled { get; init; } = false;

    /// <summary>Directory of <c>dev.db</c> (and its audit anchor). Created on start.</summary>
    public string Directory { get; init; } = ".data";
}

public sealed class DevToolingOptions
{
    /// <summary>Banana Cake Pop IDE on the GraphQL endpoint. Null = preset default.</summary>
    public bool? BananaCakePop { get; init; }

    /// <summary>Class B: GraphQL schema introspection. Null = preset default.</summary>
    public bool? Introspection { get; init; }
}

/// <summary>
/// Effective development features, resolved once. Everything is off outside Development.
/// </summary>
public sealed record DevFeatures(
    bool Banner,
    bool VerboseErrors,
    bool PersonaLogin,
    bool Info,
    bool PersistDb,
    IReadOnlyList<string> DevSecurity,
    bool ShowPasswords = false)
{
    public static DevFeatures Off { get; } = new(false, false, false, false, false, [], false);

    /// <summary>
    /// Class B switches that are active (they change who can access what, development only).
    /// Reported next to the security bypasses so a developer sees everything that is relaxed.
    /// </summary>
    public static DevFeatures Resolve(GatewayOptions options, bool isDevelopment)
    {
        if (!isDevelopment)
        {
            return Off;
        }

        var devSecurity = new List<string>();
        if (options.Authentication.EnableTestAuthHandler) devSecurity.Add("test_auth_handler");
        if (options.GraphQL.EnableIntrospection) devSecurity.Add("graphql_introspection");

        return new DevFeatures(
            options.Dev.Banner,
            options.Dev.VerboseErrors,
            options.Dev.PersonaLogin,
            options.Dev.Info,
            options.Dev.Persist.Enabled,
            devSecurity,
            options.Dev.ShowPasswords);
    }
}

/// <summary>Startup validation for <see cref="DevOptions"/>.</summary>
public static class DevOptionsValidator
{
    private static readonly string[] Presets = [DevOptions.PresetStandard, DevOptions.PresetQuickstart, DevOptions.PresetStrict];

    public static IReadOnlyList<string> Validate(GatewayOptions options, bool isDevelopment)
    {
        var errors = new List<string>();
        var dev = options.Dev;

        if (!Presets.Contains(dev.Preset, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Gateway:Dev:Preset '{dev.Preset}' is invalid. Allowed: {string.Join(", ", Presets)}.");
        }

        if (isDevelopment)
        {
            return errors;
        }

        // Outside Development only the defaults are acceptable. Banner, VerboseErrors, PersonaLogin and Info are
        // ignored there (they resolve to off), so they are not an error.
        var violations = new List<string>();
        if (!string.Equals(dev.Preset, DevOptions.PresetStandard, StringComparison.OrdinalIgnoreCase)) violations.Add("Preset");
        if (dev.Persist.Enabled) violations.Add("Persist:Enabled");
        if (dev.DemoData == true) violations.Add("DemoData");
        if (dev.TestAuthHandler == true) violations.Add("TestAuthHandler");
        if (dev.Tooling.BananaCakePop == true) violations.Add("Tooling:BananaCakePop");
        if (dev.Tooling.Introspection == true) violations.Add("Tooling:Introspection");

        foreach (var name in violations)
        {
            errors.Add($"Security violation: Gateway:Dev:{name} may ONLY be set in the Development environment!");
        }

        return errors;
    }
}
