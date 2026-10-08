using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Hosting;

namespace Autheris.Api.Configuration;

/// <summary>What <see cref="DevConfiguration"/> did to the configuration (for the banner and <c>/api/dev/info</c>).</summary>
public sealed record DevConfigurationReport(
    string Preset,
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<string, string> Applied);

/// <summary>
/// Resolves <c>Gateway:Dev</c> into the underlying option keys before the options are bound: preset defaults,
/// the Quickstart expansion into concrete <c>Insecure.*</c> values, the aliases for the legacy switches and
/// Dev persistence. Runs only in Development; elsewhere nothing is expanded and
/// <see cref="DevOptionsValidator"/> rejects non-default values.
/// </summary>
/// <remarks>
/// Precedence per switch: explicit <c>Gateway:Dev:*</c> value, then an explicitly set legacy key (deprecated alias),
/// then the preset default. Explicit values of any source always beat expanded defaults, because the defaults are
/// inserted as the lowest-priority configuration source. A <c>Dev</c> value that contradicts its legacy key is an error.
/// </remarks>
public static class DevConfiguration
{
    private const string G = "Gateway:";

    public static DevConfigurationReport Apply(ConfigurationManager configuration, IHostEnvironment environment)
    {
        var notes = new List<string>();
        var applied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var preset = configuration[G + "Dev:Preset"];

        if (!environment.IsDevelopment())
        {
            return new DevConfigurationReport(preset ?? DevOptions.PresetStandard, notes, applied);
        }

        if (preset == null && string.Equals(configuration[G + "Profile"], "Quickstart", StringComparison.OrdinalIgnoreCase))
        {
            preset = DevOptions.PresetQuickstart;
            notes.Add("Gateway:Profile is deprecated; use Gateway:Dev:Preset=Quickstart.");
        }

        preset ??= DevOptions.PresetStandard;
        var fill = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        void Fill(string key, string value)
        {
            if (configuration[key] == null && fill.TryAdd(key, value))
            {
                applied[key] = value;
            }
        }

        void Pair(string devKey, string legacyKey, bool presetDefault)
        {
            var dev = configuration[G + devKey];
            var legacy = configuration[G + legacyKey];
            if (dev != null && legacy != null && !SameBool(dev, legacy))
            {
                throw new ValidationException(
                    $"Conflicting configuration: {G}{devKey}={dev} contradicts {G}{legacyKey}={legacy}. Remove the legacy key.");
            }

            if (dev != null)
            {
                Fill(G + legacyKey, dev);
            }
            else if (legacy != null)
            {
                notes.Add($"{G}{legacyKey} is deprecated; use {G}{devKey}.");
                Fill(G + devKey, legacy);
            }
            else
            {
                Fill(G + devKey, ToText(presetDefault));
                Fill(G + legacyKey, ToText(presetDefault));
            }
        }

        var strict = string.Equals(preset, DevOptions.PresetStrict, StringComparison.OrdinalIgnoreCase);
        Pair("Dev:TestAuthHandler", "Authentication:EnableTestAuthHandler", !strict);
        Pair("Dev:Tooling:BananaCakePop", "GraphQL:EnableBananaCakePop", true);
        Pair("Dev:Tooling:Introspection", "GraphQL:EnableIntrospection", !strict);
        Pair("Dev:DemoData", "GovernanceDb:SeedDemoData", true);

        if (string.Equals(preset, DevOptions.PresetQuickstart, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var key in QuickstartExpansion)
            {
                Fill(G + key, "true");
            }
        }

        // Dev personas: only supply default development personas if no BasicAuth users were configured
        // in any custom configuration source or environment variable. This prevents ASP.NET Core list
        // index-merging where custom env users (e.g. Users:0:Username) would inherit roles/tenants from dev personas.
        var usersSection = configuration.GetSection(G + "Authentication:BasicAuth:Users");
        if (!usersSection.GetChildren().Any())
        {
            var defaultUsers = new Dictionary<string, string?>
            {
                [G + "Authentication:BasicAuth:Users:0:Username"] = "dev-admin",
                [G + "Authentication:BasicAuth:Users:0:Password"] = "dev",
                [G + "Authentication:BasicAuth:Users:0:Roles:0"] = "ClusterAdmin",

                [G + "Authentication:BasicAuth:Users:1:Username"] = "owner",
                [G + "Authentication:BasicAuth:Users:1:Password"] = "dev",
                [G + "Authentication:BasicAuth:Users:1:Sid"] = "S-1-5-21-DATAOWNER-1",
                [G + "Authentication:BasicAuth:Users:1:Roles:0"] = "DataOwner",

                [G + "Authentication:BasicAuth:Users:2:Username"] = "approver-a",
                [G + "Authentication:BasicAuth:Users:2:Password"] = "dev",
                [G + "Authentication:BasicAuth:Users:2:Sid"] = "S-1-5-21-APPROVER-A",
                [G + "Authentication:BasicAuth:Users:2:Roles:0"] = "DataOwner",

                [G + "Authentication:BasicAuth:Users:3:Username"] = "approver-b",
                [G + "Authentication:BasicAuth:Users:3:Password"] = "dev",
                [G + "Authentication:BasicAuth:Users:3:Sid"] = "S-1-5-21-APPROVER-B",
                [G + "Authentication:BasicAuth:Users:3:Roles:0"] = "DataOwner",

                [G + "Authentication:BasicAuth:Users:4:Username"] = "analyst",
                [G + "Authentication:BasicAuth:Users:4:Password"] = "dev",
                [G + "Authentication:BasicAuth:Users:4:Roles:0"] = "Analyst",

                [G + "Authentication:BasicAuth:Users:5:Username"] = "analyst-b",
                [G + "Authentication:BasicAuth:Users:5:Password"] = "dev",
                [G + "Authentication:BasicAuth:Users:5:TenantId"] = "tenant-b",
                [G + "Authentication:BasicAuth:Users:5:Roles:0"] = "Analyst",
            };

            foreach (var (k, v) in defaultUsers)
            {
                Fill(k, v!);
            }
        }

        var builder = (IConfigurationBuilder)configuration;
        if (fill.Count > 0)
        {
            builder.Sources.Insert(0, new MemoryConfigurationSource { InitialData = fill });
        }

        if (SameBool(configuration[G + "Dev:Persist:Enabled"], "true"))
        {
            var directory = configuration[G + "Dev:Persist:Directory"] is { Length: > 0 } d ? d : ".data";
            System.IO.Directory.CreateDirectory(directory);
            var connectionString = $"Data Source={System.IO.Path.Combine(directory, "dev.db")};Cache=Shared";
            var key = G + "GovernanceDb:ConnectionString";
            builder.Add(new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?> { [key] = connectionString }
            });
            applied[key] = connectionString;
            notes.Add($"Dev persistence is on: governance database in '{directory}' (reset with scripts/dev-reset.sh).");
        }

        return new DevConfigurationReport(preset, notes, applied);
    }

    /// <summary>
    /// Insecure/OpenSchema keys (relative to <c>Gateway:</c>) that the Quickstart preset sets when they are not
    /// configured explicitly. They show up under their real names in <c>GetAllActiveBypasses</c>.
    /// </summary>
    public static IReadOnlyList<string> QuickstartExpansion { get; } =
    [
        "Insecure:warn_allow_all_cors_origins",
        "Insecure:warn_enable_introspection",
        "Insecure:warn_auto_approve_access_requests",
        "OpenSchema"
    ];

    private static bool SameBool(string? a, string? b) =>
        bool.TryParse(a, out var x) && bool.TryParse(b, out var y) && x == y;

    private static string ToText(bool value) => value ? "true" : "false";
}
