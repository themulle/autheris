using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Autheris.Api.Configuration;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// Unified dev switches (Gateway:Dev): preset resolution, legacy aliases, Development-only validation.
/// Concept: docs/architecture/concept-unified-dev-switches-2026-10-05.md
/// </summary>
public sealed class DevSwitchTests
{
    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static (ConfigurationManager Config, DevConfigurationReport Report) Resolve(
        string environment, params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
        var report = DevConfiguration.Apply(config, Env(environment));
        return (config, report);
    }

    private static GatewayOptions Bind(IConfiguration config) =>
        config.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();

    // ---------- Presets ----------

    [Fact]
    public void Standard_InDevelopment_EnablesConvenienceAndMapsLegacyKeys()
    {
        var (config, _) = Resolve("Development");
        var options = Bind(config);

        options.Authentication.EnableTestAuthHandler.ShouldBeTrue();
        options.GraphQL.EnableBananaCakePop.ShouldBeTrue();
        options.GraphQL.EnableIntrospection.ShouldBeTrue();
        options.GovernanceDb.SeedDemoData.ShouldBe(true);
        options.IsAllCorsAllowed.ShouldBeFalse();
        options.IsOpenSchemaAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Strict_DisablesTestAuthAndIntrospection()
    {
        var (config, _) = Resolve("Development", ("Gateway:Dev:Preset", "Strict"));
        var options = Bind(config);

        options.Authentication.EnableTestAuthHandler.ShouldBeFalse();
        options.GraphQL.EnableIntrospection.ShouldBeFalse();
        options.GraphQL.EnableBananaCakePop.ShouldBeTrue();
    }

    [Fact]
    public void Quickstart_ExpandsIntoConcreteInsecureKeys_VisibleAsBypasses()
    {
        var (config, report) = Resolve("Development", ("Gateway:Dev:Preset", "Quickstart"));
        var options = Bind(config);

        options.Insecure.warn_allow_all_cors_origins.ShouldBeTrue();
        options.Insecure.warn_enable_introspection.ShouldBeTrue();
        options.Insecure.warn_auto_approve_access_requests.ShouldBeTrue();
        options.OpenSchema.ShouldBeTrue();
        options.GetAllActiveBypasses().ShouldContain("WARN:warn_allow_all_cors_origins");
        options.GetAllActiveBypasses().ShouldContain("DANGER:warn_auto_approve_access_requests");
        report.Applied.Keys.ShouldContain("Gateway:Insecure:warn_allow_all_cors_origins");
    }

    [Fact]
    public void Quickstart_ExplicitValueBeatsExpansion()
    {
        var (config, _) = Resolve("Development",
            ("Gateway:Dev:Preset", "Quickstart"),
            ("Gateway:Insecure:warn_allow_all_cors_origins", "false"));

        Bind(config).Insecure.warn_allow_all_cors_origins.ShouldBeFalse();
    }

    [Fact]
    public void LegacyProfileQuickstart_IsAliasForPreset_WithNote()
    {
        var (config, report) = Resolve("Development", ("Gateway:Profile", "Quickstart"));
        var options = Bind(config);

        report.Preset.ShouldBe("Quickstart");
        report.Notes.ShouldContain(n => n.Contains("Gateway:Profile is deprecated"));
        options.Insecure.warn_allow_all_cors_origins.ShouldBeTrue();
    }

    // ---------- Legacy aliases ----------

    [Fact]
    public void LegacyKey_ExplicitlySet_IsHonoredWithDeprecationNote()
    {
        var (config, report) = Resolve("Development", ("Gateway:Authentication:EnableTestAuthHandler", "false"));
        var options = Bind(config);

        options.Authentication.EnableTestAuthHandler.ShouldBeFalse();
        options.Dev.TestAuthHandler.ShouldBe(false);
        report.Notes.ShouldContain(n => n.Contains("EnableTestAuthHandler is deprecated") && n.Contains("Gateway:Dev:TestAuthHandler"));
    }

    [Fact]
    public void DevKey_ExplicitlySet_IsMappedToLegacyKey()
    {
        var (config, report) = Resolve("Development", ("Gateway:Dev:Tooling:Introspection", "false"));
        var options = Bind(config);

        options.GraphQL.EnableIntrospection.ShouldBeFalse();
        report.Notes.ShouldBeEmpty();
    }

    [Fact]
    public void DevKey_ContradictingLegacyKey_FailsStart()
    {
        var ex = Should.Throw<ValidationException>(() => Resolve("Development",
            ("Gateway:Dev:TestAuthHandler", "true"),
            ("Gateway:Authentication:EnableTestAuthHandler", "false")));

        ex.Message.ShouldContain("Conflicting configuration");
    }

    // ---------- Persistence ----------

    [Fact]
    public void Persist_OverridesConnectionString_AndCreatesDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autheris-dev-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (config, report) = Resolve("Development",
                ("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared"),
                ("Gateway:Dev:Persist:Enabled", "true"),
                ("Gateway:Dev:Persist:Directory", dir));

            Directory.Exists(dir).ShouldBeTrue();
            Bind(config).GovernanceDb.ConnectionString.ShouldBe($"Data Source={Path.Combine(dir, "dev.db")};Cache=Shared");
            report.Notes.ShouldContain(n => n.Contains("scripts/dev-reset.sh"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Persist_Disabled_KeepsConfiguredConnectionString()
    {
        var (config, _) = Resolve("Development",
            ("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared"));

        Bind(config).GovernanceDb.ConnectionString.ShouldContain("Mode=Memory");
    }

    // ---------- Outside Development ----------

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void OutsideDevelopment_NothingIsExpanded(string environment)
    {
        var (config, report) = Resolve(environment);
        var options = Bind(config);

        report.Applied.ShouldBeEmpty();
        options.Authentication.EnableTestAuthHandler.ShouldBeFalse();
        options.GraphQL.EnableIntrospection.ShouldBeFalse();
        options.GovernanceDb.SeedDemoData.ShouldBeNull();
    }

    private static readonly (string Name, Func<DevOptions> Risky)[] RiskyValues =
    [
        ("Preset", () => new DevOptions { Preset = "Quickstart" }),
        ("Persist:Enabled", () => new DevOptions { Persist = new DevPersistOptions { Enabled = true } }),
        ("DemoData", () => new DevOptions { DemoData = true }),
        ("TestAuthHandler", () => new DevOptions { TestAuthHandler = true }),
        ("Tooling:BananaCakePop", () => new DevOptions { Tooling = new DevToolingOptions { BananaCakePop = true } }),
        ("Tooling:Introspection", () => new DevOptions { Tooling = new DevToolingOptions { Introspection = true } })
    ];

    [Fact]
    public void Validate_OutsideDevelopment_RejectsEveryRiskyValue_AllowedInDevelopment()
    {
        foreach (var (name, risky) in RiskyValues)
        {
            var options = new GatewayOptions { Dev = risky() };

            DevOptionsValidator.Validate(options, isDevelopment: false)
                .ShouldContain(e => e.Contains($"Gateway:Dev:{name}"), $"{name} must be rejected outside Development");
            DevOptionsValidator.Validate(options, isDevelopment: true).ShouldBeEmpty($"{name} is valid in Development");
        }
    }

    [Fact]
    public void Validate_OutsideDevelopment_IgnoresRuntimeConvenienceFlagsAndDefaults()
    {
        DevOptionsValidator.Validate(new GatewayOptions(), isDevelopment: false).ShouldBeEmpty();
        DevOptionsValidator.Validate(
            new GatewayOptions { Dev = new DevOptions { Banner = false, VerboseErrors = false, PersonaLogin = false, Info = false } },
            isDevelopment: false).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_UnknownPreset_IsRejectedEverywhere()
    {
        var options = new GatewayOptions { Dev = new DevOptions { Preset = "Turbo" } };

        DevOptionsValidator.Validate(options, isDevelopment: true).ShouldContain(e => e.Contains("Preset 'Turbo' is invalid"));
    }

    [Fact]
    public void EveryDevOptionsProperty_HasAnExplicitOutsideDevelopmentPolicy()
    {
        // A new Gateway:Dev switch must be added either to the risky values (rejected outside Development) or to the
        // ignored list (resolves to off outside Development). This keeps the "Development only" rule from eroding.
        string[] ignoredOutsideDevelopment = ["Banner", "VerboseErrors", "PersonaLogin", "Info"];
        var covered = RiskyValues.Select(r => r.Name.Split(':')[0]).Concat(ignoredOutsideDevelopment).ToHashSet();

        var properties = typeof(DevOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

        properties.Where(p => !covered.Contains(p)).ShouldBeEmpty();
    }

    [Fact]
    public void BaseAppSettings_DoesNotSetKeysOwnedByTheDevPreset()
    {
        // An explicit value in appsettings.json counts as "explicitly configured" and would defeat the preset
        // default in Development. The C# defaults are already false, so these keys must not appear in the base file.
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        var gateway = doc.RootElement.GetProperty("Gateway");

        string[][] owned =
        [
            ["Authentication", "EnableTestAuthHandler"],
            ["GraphQL", "EnableBananaCakePop"],
            ["GraphQL", "EnableIntrospection"],
            ["GovernanceDb", "SeedDemoData"]
        ];

        foreach (var path in owned)
        {
            var found = gateway.TryGetProperty(path[0], out var section) && section.TryGetProperty(path[1], out _);
            found.ShouldBeFalse($"Gateway:{path[0]}:{path[1]} must not be set in the base appsettings.json");
        }
    }

    // ---------- DevFeatures ----------

    [Fact]
    public void DevFeatures_AreOffOutsideDevelopment_AndReportDevSecurityInDevelopment()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions { EnableTestAuthHandler = true },
            GraphQL = new GraphQLOptions { EnableIntrospection = true },
            Dev = new DevOptions { VerboseErrors = false }
        };

        DevFeatures.Resolve(options, isDevelopment: false).ShouldBe(DevFeatures.Off);

        var dev = DevFeatures.Resolve(options, isDevelopment: true);
        dev.Banner.ShouldBeTrue();
        dev.VerboseErrors.ShouldBeFalse();
        dev.DevSecurity.ShouldBe(["test_auth_handler", "graphql_introspection"]);
    }

    // ---------- Architecture guard ----------

    /// <summary>
    /// warn_/danger_ switches belong in Insecure. These domain-local duplicates exist today (Phase 4 of the concept is
    /// deferred); the list may only shrink. A new warn_/danger_ property outside Insecure fails this test.
    /// </summary>
    private static readonly HashSet<string> KnownDomainLocalSwitches =
    [
        "AuthenticationOptions.danger_allow_anonymous_access",
        "GovernanceDbOptions.danger_bypass_consent_checks",
        "GovernanceDbOptions.warn_auto_approve_access_requests",
        "RateLimitingOptions.warn_disable_rate_limiting",
        "GraphQLOptions.warn_allow_all_cors_origins",
        "GraphQLOptions.warn_relaxed_query_limits",
        "GraphQLOptions.warn_enable_introspection",
        "DataMaskingOptions.danger_disable_column_masking",
        "OpenMetadataOptions.danger_bypass_webhook_signature_validation",
        "OpenMetadataOptions.warn_ignore_webhook_timestamp_tolerance",
        "OpenMetadataOptions.danger_allow_untrusted_certificates",
        "ItsmOptions.danger_bypass_webhook_signature_validation",
        "ItsmOptions.warn_ignore_webhook_timestamp_tolerance",
        "ItsmOptions.warn_fallback_default_tenant_for_webhooks",
        "ItsmOptions.warn_mock_external_systems_if_unreachable",
        "ItsmOptions.danger_allow_untrusted_certificates",
        "McpOptions.warn_allow_unmasked_ai_access",
        "McpOptions.danger_bypass_mcp_auth",
        "LakehouseOptions.warn_allow_unsigned_s3_requests",
        "LakehouseOptions.danger_bypass_lakehouse_auth",
        "DbtOptions.danger_bypass_webhook_signature_validation",
        "WebSqlOptions.warn_allow_dml",
        "WebSqlOptions.danger_bypass_sql_governance"
    ];

    [Fact]
    public void NoNewWarnOrDangerSwitches_OutsideInsecureSection()
    {
        var actual = typeof(GatewayOptions).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(GatewayOptions).Namespace && t != typeof(InsecureGettingStartedOptions))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.StartsWith("warn_", StringComparison.Ordinal) || p.Name.StartsWith("danger_", StringComparison.Ordinal))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToHashSet();

        actual.Except(KnownDomainLocalSwitches).ShouldBeEmpty("new warn_/danger_ switches belong in Gateway:Insecure");
        KnownDomainLocalSwitches.Except(actual).ShouldBeEmpty("remove migrated switches from the allowlist");
    }
}
