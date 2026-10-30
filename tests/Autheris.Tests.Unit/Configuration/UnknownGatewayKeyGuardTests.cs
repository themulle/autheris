namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>ARCH-MNT-06: unknown keys under the Gateway root fail startup (T-01, T-02, T-07, T-08, SC-04..SC-06).</summary>
public sealed class UnknownGatewayKeyGuardTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            map[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(map).Build();
    }

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ARCH_MNT_06_T_01_UnknownKey_FailsStartup_InDevelopmentAndProduction(string environment)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Insecure:danger_bypass_mcp_authh"] = "true" });
        DevConfiguration.Apply(configuration, Env(environment));

        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration));

        ex.Message.ShouldContain("Gateway:Insecure:danger_bypass_mcp_authh");
        ex.Message.ShouldContain("Gateway__Insecure__danger_bypass_mcp_authh");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("LocalOnnxModelPath")]
    [InlineData("LocalOnnxModelSha256")]
    [InlineData("OllamaModel")]
    [InlineData("OllamaBaseUrl")]
    [InlineData("ModelName")]
    public void CatalogSearch_Alias_Keys_Are_Unknown(string alias)
    {
        // L8 / DEF-SC-15: the removed compat aliases are rejected by the unknown-key guard, not silently ignored.
        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(Config(($"Gateway:CatalogSearch:{alias}", "SECRET-VALUE"))));

        ex.Message.ShouldContain($"Gateway:CatalogSearch:{alias}");
        ex.Message.ShouldNotContain("SECRET-VALUE");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Gateway:Itsm:LegacyGlobalWebhookSecret")]
    [InlineData("Gateway:Catalog:AllowLegacyPayloadOnlySignature")]
    public void Removed_Legacy_Webhook_Options_Are_Unknown(string key)
    {
        // L4/L5: the global ITSM webhook secret fallback and the payload-only catalog signature are removed.
        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(Config((key, "false"))));

        ex.Message.ShouldContain(key);
    }

    [Fact]
    public void GatewayConfigurationSchema_AcceptsDbtImportDataOwners()
    {
        Should.NotThrow(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(Config(("Gateway:Dbt:ImportDataOwners", "true"))));
        Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(Config(("Gateway:Dbt:ImportDataOwner", "true"))));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ARCH_MNT_06_T_01_UnknownKey_IsNotBypassedByTheLegacyConfigSwitch()
    {
        var previous = Environment.GetEnvironmentVariable("AUTHERIS_ALLOW_LEGACY_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("AUTHERIS_ALLOW_LEGACY_CONFIG", "true");
            Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(Config(("Gateway:Nope", "x"))));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTHERIS_ALLOW_LEGACY_CONFIG", previous);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ARCH_MNT_06_T_02_UnknownKey_ErrorNeverContainsValue()
    {
        const string secretValue = "sk-live-9f8e7d6c5b4a-VALUE-MUST-NOT-LEAK";
        const string secretLookingKey = "Gateway:Authentication:ForwardAuth:SharedSecrett";
        var configuration = Config((secretLookingKey, secretValue), ("Gateway:Mcp:Enabledd", "VALUE-2"));

        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration));

        ex.Message.ShouldNotContain(secretValue);
        ex.Message.ShouldNotContain("VALUE-2");
        ex.Message.ShouldNotContain("sk-live");
        ex.Message.ShouldContain(secretLookingKey);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ARCH_MNT_06_T_02_UnknownKey_NamesAreSanitisedTruncatedSortedAndCapped()
    {
        var pairs = new List<(string, string?)>();
        for (var i = 0; i < 25; i++)
        {
            pairs.Add(($"Gateway:Zz{i:D2}", "v"));
        }

        pairs.Add(("Gateway:Aa\r\nFORGED LOG LINE\u001b[31m", "v"));
        pairs.Add(("Gateway:" + new string('k', 500), "v"));

        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(Config([.. pairs])));

        ex.Message.ShouldNotContain("\r");
        ex.Message.ShouldNotContain("\n");
        ex.Message.ShouldNotContain("\u001b");
        ex.Message.ShouldContain(" and 7 more");
        ex.Message.ShouldNotContain(new string('k', 129));
        ex.Message.ShouldContain("(27)");
        ex.Message.IndexOf("Gateway:Aa", StringComparison.Ordinal).ShouldBeLessThan(ex.Message.IndexOf("Gateway:Zz00", StringComparison.Ordinal));
        ex.Message.ShouldNotContain("Gateway:Zz19", Case.Sensitive, "only the first 20 sorted keys are listed");
    }

    [Fact]
    public void KnownKeys_PassTheGuard()
    {
        var configuration = Config(
            ("Gateway:Mcp:Enabled", "true"),
            ("Gateway:MCP:enabled", "true"),
            ("Gateway:Insecure:danger_bypass_mcp_auth", "false"),
            ("Gateway:Hosting:AllowedHosts:0", "localhost"),
            ("Gateway:DuckDbOlap:Enabled", "true"),
            ("Gateway:State:StateDirectory", "/tmp/x"),
            ("Gateway:TableCache:Encryption:KeyVaultKeyName", "k"),
            ("Gateway:AsyncJobs:ScratchDirectory", "/tmp"),
            ("Gateway:Startup:Warmup:Enabled", "true"),
            ("Gateway:Startup:ColdPathTiming:Enabled", "true"),
            ("Gateway:Catalog:Enabled", "true"),
            ("Gateway:Telemetry:Otlp:Endpoint", "https://collector"),
            ("Gateway:DataSources:Connections:anyName:ConnectionString", "x"));

        UnknownGatewayKeyGuard.FindUnknownKeys(configuration).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Gateway:Mcp:Nope")]
    [InlineData("Gateway:Hosting:AllowedHosts:first")]
    [InlineData("Gateway:Hosting:AllowedHosts:0:Extra")]
    [InlineData("Gateway:Mcp:Enabled:Sub")]
    [InlineData("Gateway:Startup:Nope")]
    [InlineData("Gateway:Startup:Warmup:Nope")]
    [InlineData("Gateway:DataSources:Connections:anyName:NoSuchField")]
    public void UnknownKeys_AreDetected_AtAnyDepth(string key) =>
        UnknownGatewayKeyGuard.FindUnknownKeys(Config((key, "x"))).ShouldNotBeEmpty();

    [Fact]
    public void OnlyTheShortestUnknownPrefix_IsReported()
    {
        var unknown = UnknownGatewayKeyGuard.FindUnknownKeys(Config(("Gateway:Bogus:A:B", "1"), ("Gateway:Bogus:C", "2")));

        unknown.ShouldBe(["Gateway:Bogus"]);
    }

    [Theory]
    [InlineData("Gateway:WebSql:SqlRewriterEngine")]
    [InlineData("Gateway:WebSql:PlanCache:Enabled")]
    [InlineData("Gateway:WebSql:CrossSource:MaxMemory")]
    public void KeysOwnedByASpecificGuard_AreNotPreempted(string key) =>
        UnknownGatewayKeyGuard.FindUnknownKeys(Config((key, "x"))).ShouldBeEmpty();

    [Fact]
    public void DevPresetKeys_AreKnown_AfterDevConfigurationApply()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Dev:Preset"] = "Quickstart" });
        DevConfiguration.Apply(configuration, Env("Development"));

        UnknownGatewayKeyGuard.FindUnknownKeys(configuration).ShouldBeEmpty();
    }

    [Fact]
    public void EnvironmentVariableForm_IsCheckedLikeTheColonForm()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway__Mcp__Enabledd"] = "true" })
            .Build();
        // The environment variable provider maps "__" to ":" before the key reaches IConfiguration; emulate that here.
        var mapped = new ConfigurationBuilder()
            .AddInMemoryCollection(configuration.AsEnumerable().ToDictionary(kv => kv.Key.Replace("__", ":", StringComparison.Ordinal), kv => kv.Value))
            .Build();

        UnknownGatewayKeyGuard.FindUnknownKeys(mapped).ShouldBe(["Gateway:Mcp:Enabledd"]);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Authentication:EntraId:SidClaimType")]
    [InlineData("Authentication:EntraId:GroupsClaimType")]
    [InlineData("Authentication:Adfs:SidClaimType")]
    [InlineData("Authentication:Adfs:GroupSidClaimType")]
    [InlineData("Mcp:RequirePiiMasking")]
    [InlineData("Mcp:AllowAnonymousDiscovery")]
    [InlineData("Casbin:EnforceInQueryPipeline")]
    [InlineData("Rebac:MaxCachedDecisions")]
    [InlineData("Audit:StoreStatementText")]
    [InlineData("Classification:MaskingRules")]
    [InlineData("Classification:StepUpAuthThresholdRank")]
    [InlineData("Classification:AutoApproveMaxRank")]
    [InlineData("Telemetry:Identity:PseudonymKeyRef")]
    [InlineData("TableCache:Encryption:KeyDerivationContext")]
    [InlineData("TableCache:AllowStaleOnOutage")]
    [InlineData("Caching:L1MemoryCache:SensitiveTableTtlSeconds")]
    [InlineData("RateLimiting:PostAuthSidRateLimit:MaxCostPerMinute")]
    public void ARCH_MNT_06_T_07_DeletedKeys_FailStartup(string relativeKey)
    {
        var configuration = Config(("Gateway:" + relativeKey, "x"));

        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration));

        ex.Message.ShouldContain("Gateway:" + relativeKey);
    }

    [Fact]
    public void SideBoundPairs_CoverEverySectionNameConstant()
    {
        var assemblies = new[]
        {
            typeof(Autheris.Domain.Options.GatewayOptions).Assembly,
            typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
            typeof(Program).Assembly
        };
        var declared = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && t.IsPublic || t.IsClass && t.IsNestedPublic)
            .Select(t => (Type: t, Field: t.GetField("SectionName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)))
            .Where(x => x.Field is { IsLiteral: true } && x.Field.FieldType == typeof(string)
                        && ((string)x.Field.GetRawConstantValue()!).StartsWith("Gateway:", StringComparison.Ordinal))
            .Select(x => (Path: (string)x.Field!.GetRawConstantValue()!, x.Type))
            .ToList();

        declared.Count.ShouldBeGreaterThan(10);
        var rootBound = typeof(Autheris.Domain.Options.GatewayOptions).GetProperties().Select(p => p.PropertyType).ToHashSet();
        foreach (var (path, type) in declared)
        {
            var bound = GatewayConfigurationSchema.SideBound.Any(s => s.Type == type
                && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
            (bound || rootBound.Contains(type) || type == typeof(Autheris.Domain.Options.GatewayOptions))
                .ShouldBeTrue($"{type.Name} ({path}) is neither a GatewayOptions property type nor a side-bound pair in GatewayConfigurationSchema.");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ARCH_MNT_06_T_08_FreeFormSubtrees_ExcludeSecurityRoots()
    {
        var securityRoots = new[]
        {
            "Gateway:Insecure", "Gateway:Authentication", "Gateway:Casbin", "Gateway:Mcp", "Gateway:Audit", "Gateway:Consent",
            "Gateway:Tls", "Gateway:Rebac", "Gateway:Rls", "Gateway:RowFilters", "Gateway:DataMasking", "Gateway:Jwt"
        };

        var freeForm = GatewayConfigurationSchema.FreeFormSubtrees();

        freeForm.Where(p => securityRoots.Any(r => p.Equals(r, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(r + ":", StringComparison.OrdinalIgnoreCase)))
            .ShouldBeEmpty("No free-form (object, IConfigurationSection, scalar dictionary) subtree may sit under a security root.");
        freeForm.ShouldBe(ExpectedFreeFormSubtrees, "A new free-form subtree needs a review: add it here deliberately.");
    }

    private static readonly string[] ExpectedFreeFormSubtrees =
    [
        "Gateway:Catalog:Alation:DataSourceToDomainMap",
        "Gateway:Catalog:TagToMaskingRuleMap",
        "Gateway:Classification:Workflow:Profiles:OwnerResolution:RuleBasedMappings",
        "Gateway:Dbt:DatabaseMappings",
        "Gateway:Federation:FieldClassifications",
        "Gateway:FinOps:ScopeMonthlyBudgets",
        "Gateway:FinOps:TenantMonthlyBudgets",
        "Gateway:Itsm:InstanceToTenantMap",
        "Gateway:OpenMetadata:RoleToGatewayRoleMap",
        "Gateway:OpenMetadata:ServiceDatabaseToDomainMap",
        "Gateway:OpenMetadata:ServiceDatabaseToTenantMap",
        "Gateway:OpenMetadata:TagToMaskingRuleMap",
        "Gateway:OpenMetadata:TeamToGroupSidMap",
        "Gateway:OpenMetadata:UserToUserSidMap",
        "Gateway:Plugins:TrustedPluginHashes",
        "Gateway:RateLimiting:ClientTiers:ApiKeys",
        "Gateway:RateLimiting:ClientTiers:RoleTierMappings",
        "Gateway:RateLimiting:ClientTiers:ScopeTierMappings",
        "Gateway:WebSql:DataSourceMappings"
    ];
}
