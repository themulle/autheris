namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autheris.Api.Hosting;
using Autheris.Modules.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// Architectural gates for the out-of-core module host and module contracts (WP-SIMPL-25).
/// </summary>
public sealed partial class ModuleBoundaryArchitectureTests
{
    private static readonly string[]AllowedModuleProjectReferences =
    [
        "Autheris.Modules.Abstractions",
        "Autheris.Domain",
        "Autheris.Application",
    ];

    private static readonly string[] ForbiddenDataSourceConnectionTypes =
    [
        "ISqlConnectionFactory",
        "SqlConnectionFactory",
        "NpgsqlConnection",
        "SqlConnection",
        "OracleConnection",
        "SqliteConnection",
        "DuckDBConnection",
        "DatabricksStatementClient",
        // Governance store type: modules resolve the core ports (ICdcStreamStore, IWebhookOutbox, ...) and never see the store itself.
        "GovernanceRepository",
    ];

    [GeneratedRegex(@"<ProjectReference\s+Include\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectReferenceRegex();

    [Fact]
    [Trait("Category", "Security")]
    public void Modules_reference_only_abstractions_domain_and_application()
    {
        // 1. Autheris.Modules.Abstractions itself may only reference Autheris.Domain (and ASP.NET Core / BCL).
        var abstractionsCsproj = Path.Combine(
            RepositoryScan.FindRepositoryRoot(),
            "src",
            "Autheris.Modules.Abstractions",
            "Autheris.Modules.Abstractions.csproj");
        File.Exists(abstractionsCsproj).ShouldBeTrue("Missing src/Autheris.Modules.Abstractions/Autheris.Modules.Abstractions.csproj");

        var abstractionsRefs = ExtractProjectReferences(File.ReadAllText(abstractionsCsproj));
        abstractionsRefs.ShouldBe(["Autheris.Domain"]);

        var abstractionsAssembly = typeof(IAutherisModule).Assembly;
        var referencedAssemblyNames = abstractionsAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        referencedAssemblyNames.ShouldNotContain("Autheris.Infrastructure");
        referencedAssemblyNames.ShouldNotContain("Autheris.Api");
        referencedAssemblyNames.ShouldNotContain("Autheris.Extensions");
        referencedAssemblyNames.ShouldNotContain("Autheris.GraphQL");
        referencedAssemblyNames.ShouldNotContain("FastSqlEngine");
        referencedAssemblyNames.ShouldNotContain("TrinoSqlEngine");

        // 2. Any src/Autheris.Modules.* project (or modules/*/*.csproj) may only reference Abstractions, Domain, and Application.
        var root = RepositoryScan.FindRepositoryRoot();
        var moduleProjects = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => Path.GetFileNameWithoutExtension(p).StartsWith("Autheris.Modules.", StringComparison.Ordinal) &&
                        !string.Equals(Path.GetFileNameWithoutExtension(p), "Autheris.Modules.Abstractions", StringComparison.Ordinal))
            .ToList();

        var modulesRootDir = Path.Combine(root, "modules");
        if (Directory.Exists(modulesRootDir))
        {
            moduleProjects.AddRange(
                Directory.EnumerateFiles(modulesRootDir, "*.csproj", SearchOption.AllDirectories)
                    .Where(p => !p.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var csproj in moduleProjects)
        {
            var projectName = Path.GetFileNameWithoutExtension(csproj);
            var refs = ExtractProjectReferences(File.ReadAllText(csproj));
            var forbidden = refs.Where(r => !AllowedModuleProjectReferences.Contains(r, StringComparer.Ordinal)).ToList();
            forbidden.ShouldBeEmpty(
                $"Module project '{projectName}' references forbidden projects: {string.Join(", ", forbidden)}. " +
                $"Modules may only reference {string.Join(", ", AllowedModuleProjectReferences)}.");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Modules_open_no_data_source_connections()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var dirsToScan = new List<string>
        {
            Path.Combine(root, "src", "Autheris.Modules.Abstractions"),
        };

        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(root, "src"), "Autheris.Modules.*", SearchOption.TopDirectoryOnly))
        {
            if (!dirsToScan.Contains(dir, StringComparer.Ordinal))
            {
                dirsToScan.Add(dir);
            }
        }

        var modulesDir = Path.Combine(root, "modules");
        if (Directory.Exists(modulesDir))
        {
            dirsToScan.Add(modulesDir);
        }

        var violations = new List<string>();
        foreach (var dir in dirsToScan)
        {
            foreach (var csFile in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, csFile).Replace('\\', '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                var content = File.ReadAllText(csFile);
                foreach (var forbidden in ForbiddenDataSourceConnectionTypes)
                {
                    if (Regex.IsMatch(content, $@"\b{Regex.Escape(forbidden)}\b"))
                    {
                        violations.Add($"{rel} references forbidden data-source connection type '{forbidden}'");
                    }
                }
            }
        }

        violations.ShouldBeEmpty(
            "Modules and module abstractions must never open or reference governed data-source connections:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Governance_connection_factory_returns_governance_store_only()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var factoryPath = Path.Combine(root, "src", "Autheris.Api", "Hosting", "GovernanceConnectionFactory.cs");
        File.Exists(factoryPath).ShouldBeTrue("Missing src/Autheris.Api/Hosting/GovernanceConnectionFactory.cs");

        var source = File.ReadAllText(factoryPath);
        source.ShouldContain("GovernanceDb");
        source.ShouldNotContain("DataSources");
        source.ShouldNotContain("DataSourceConnectionOptions");
        source.ShouldNotContain("ISqlConnectionFactory");

        var factoryType = typeof(GovernanceConnectionFactory);
        typeof(IGovernanceConnectionFactory).IsAssignableFrom(factoryType).ShouldBeTrue();

        var fields = factoryType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var field in fields)
        {
            field.FieldType.Name.ShouldNotContain("DataSource");
            field.FieldType.Name.ShouldNotBe("ISqlConnectionFactory");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_host_context_exposes_no_governance_connection()
    {
        var members = typeof(IModuleHostContext).GetProperties()
            .Select(p => (p.Name, TypeName: p.PropertyType.Name))
            .Concat(typeof(IModuleHostContext).GetMethods().Where(m => !m.IsSpecialName)
                .Select(m => (m.Name, TypeName: m.ReturnType.Name)))
            .ToList();

        members.Where(m => m.Name.Contains("Connection", StringComparison.OrdinalIgnoreCase) ||
                           m.TypeName.Contains("Connection", StringComparison.OrdinalIgnoreCase))
            .ShouldBeEmpty("A module must not receive a raw governance connection (WP-SIMPL-50 B13).");
    }

    [Fact]
    public void No_module_graphql_extension_point()
    {
        var abstractionsTypes = typeof(IModuleHostContext).Assembly.GetTypes().Select(t => t.Name).ToList();
        abstractionsTypes.ShouldNotContain("IAutherisGraphQLModule");
        abstractionsTypes.ShouldNotContain("IModuleGraphQLRegistrar");

        typeof(ModuleLoader).Assembly.GetTypes().Select(t => t.Name).ShouldNotContain("ModuleGraphQLRegistrar");
        typeof(ModuleLoadResult).GetMethod("ConfigureGraphQL").ShouldBeNull();
    }

    private static readonly (string Name, Regex Pattern)[] ForbiddenTenantResolution =
    [
        ("TenantIdItemKey", new Regex(@"\bTenantIdItemKey\b", RegexOptions.CultureInvariant)),
        ("SecurityPrincipalContext.ItemKey", new Regex(@"\bSecurityPrincipalContext\s*\.\s*ItemKey\b", RegexOptions.CultureInvariant)),
        ("using static SecurityPrincipalContext", new Regex(@"\busing\s+static\s+[\w.]*\bSecurityPrincipalContext\b", RegexOptions.CultureInvariant)),
        ("OfType<SecurityPrincipalContext>", new Regex(@"\bOfType\s*<\s*[\w.]*\bSecurityPrincipalContext\s*>", RegexOptions.CultureInvariant)),
        ("GetTenantId", new Regex(@"\bGetTenantId\s*\(", RegexOptions.CultureInvariant)),
        ("claim lookup", new Regex(@"\.\s*(FindFirst|FindFirstValue|FindAll|HasClaim)\s*[(<]", RegexOptions.CultureInvariant)),
        (".Claims", new Regex(@"(?<!\bSecurity)\.\s*Claims\b", RegexOptions.CultureInvariant)),
        ("Items indexer read", new Regex(@"\bItems\s*\[[^\]]*\](?!\s*=(?!=))", RegexOptions.CultureInvariant)),
        ("Items lookup", new Regex(@"\bItems\s*\.\s*(TryGetValue|Values|Keys|ContainsKey|GetValueOrDefault)\b", RegexOptions.CultureInvariant)),
    ];

    private static readonly Regex ModuleRequestContextImplementation = new(
        @"\b(class|record|struct|interface)\b[^{;=]*:[^{;]*\bIModuleRequestContext\b",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static IEnumerable<(string Rel, string Content)> ModuleSources()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        foreach (var csFile in Directory.EnumerateFiles(Path.Combine(root, "modules"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, csFile).Replace('\\', '/');
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal) ||
                rel.Split('/')[1].EndsWith(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (rel, File.ReadAllText(csFile));
        }
    }

    [Theory]
    [InlineData("TenantIdItemKey", "var t = ctx.Items[SecurityPrincipalContext.TenantIdItemKey];")]
    [InlineData("TenantIdItemKey", "const string k = TenantIdItemKey;")]
    [InlineData("SecurityPrincipalContext.ItemKey", "x = SecurityPrincipalContext . ItemKey;")]
    [InlineData("using static SecurityPrincipalContext", "using static Autheris.Domain.Security.SecurityPrincipalContext;")]
    [InlineData("OfType<SecurityPrincipalContext>", "ctx.Items.Values.OfType<Autheris.Domain.Security.SecurityPrincipalContext>()")]
    [InlineData("GetTenantId", "var t = user.GetTenantId();")]
    [InlineData("claim lookup", "user.FindFirst(\"tid\")")]
    [InlineData("claim lookup", "user.FindFirstValue(\"tid\")")]
    [InlineData("claim lookup", "user.FindAll(\"tid\")")]
    [InlineData("claim lookup", "user.HasClaim(\"tid\", \"x\")")]
    [InlineData(".Claims", "foreach (var c in user.Claims) { }")]
    [InlineData("Items indexer read", "var v = context.Items[\"k\"];")]
    [InlineData("Items indexer read", "if (context.Items[\"k\"] == null) { }")]
    [InlineData("Items lookup", "context.Items.TryGetValue(\"k\", out var v);")]
    [InlineData("Items lookup", "context.Items.Values.ToList();")]
    public void Tenant_resolution_patterns_catch_known_evasions(string patternName, string sample)
    {
        ForbiddenTenantResolution.Single(p => p.Name == patternName).Pattern.IsMatch(sample)
            .ShouldBeTrue($"pattern '{patternName}' must match: {sample}");
    }

    [Theory]
    [InlineData("context.Items[\"k\"] = value;")]
    [InlineData("context.Items [ \"k\" ]=value;")]
    [InlineData("using System.Security.Claims;")]
    [InlineData("var tenant = requestContext.GetRequestTenant(context);")]
    [InlineData("var sec = requestContext.GetSecurityContext(context);")]
    public void Tenant_resolution_patterns_allow_writes_and_request_context(string sample)
    {
        ForbiddenTenantResolution.Where(p => p.Pattern.IsMatch(sample)).Select(p => p.Name).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("public sealed class Fake : IModuleRequestContext { }")]
    [InlineData("internal sealed record Fake(int A) : IModuleRequestContext;")]
    [InlineData("public record struct Fake : IModuleRequestContext { }")]
    [InlineData("public readonly struct Fake : IDisposable, IModuleRequestContext { }")]
    [InlineData("public interface IMore : IModuleRequestContext { }")]
    [InlineData("class Fake\n    : Base,\n      Autheris.Modules.Abstractions.IModuleRequestContext\n{ }")]
    public void Request_context_implementation_pattern_catches_all_type_kinds(string sample)
    {
        ModuleRequestContextImplementation.IsMatch(sample).ShouldBeTrue(sample);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Modules_do_not_resolve_tenant_from_http_items()
    {
        var violations = new List<string>();
        foreach (var (rel, content) in ModuleSources())
        {
            foreach (var (name, pattern) in ForbiddenTenantResolution)
            {
                if (pattern.IsMatch(content))
                {
                    violations.Add($"{rel} resolves tenant or principal via '{name}'");
                }
            }
        }

        violations.ShouldBeEmpty(
            "Modules must take tenant and principal from IModuleRequestContext or ICallerContextProvider only:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void No_module_type_implements_module_request_context()
    {
        var violations = ModuleSources()
            .Where(f => ModuleRequestContextImplementation.IsMatch(f.Content))
            .Select(f => f.Rel)
            .ToList();
        violations.ShouldBeEmpty("No module may implement IModuleRequestContext:\n" + string.Join("\n", violations));

        var moduleAssemblies = new[]
        {
            typeof(Autheris.Modules.Lakehouse.LakehouseModule).Assembly,
            typeof(Autheris.Modules.Dbt.DbtModule).Assembly,
            typeof(Autheris.Modules.OpenMetadata.OpenMetadataModule).Assembly,
            typeof(Autheris.Modules.Events.EventsModule).Assembly,
            typeof(Autheris.Modules.Lineage.LineageModule).Assembly
        };
        moduleAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IModuleRequestContext).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ShouldBeEmpty();

        typeof(ModuleLoader).Assembly.GetTypes()
            .Where(t => typeof(IModuleRequestContext).IsAssignableFrom(t) && t.IsClass)
            .Select(t => t.Name)
            .ShouldBe([nameof(ModuleRequestContext)]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dbt_webhook_handler_referenced_only_by_core_webhook_endpoint()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var allowedCore = new[]
        {
            "src/Autheris.Application/Catalog/Semantic/IDbtCloudWebhookHandler.cs",
            "src/Autheris.Api/Endpoints/WebhookEndpoints.cs",
            "src/Autheris.Api/Hosting/ModuleSecurityGuards.cs",
        };

        var violations = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles.Where(f => f.Text.Contains("IDbtCloudWebhookHandler", StringComparison.Ordinal)))
        {
            if (!allowedCore.Contains(file.RelativePath, StringComparer.Ordinal))
            {
                violations.Add(file.RelativePath);
            }
        }

        violations.ShouldBeEmpty("Only the core webhook route (and the module guard) may reference IDbtCloudWebhookHandler:\n" + string.Join("\n", violations));

        var module = Path.Combine(root, "modules", "Autheris.Modules.Dbt");
        var implementations = Directory.EnumerateFiles(module, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("IDbtCloudWebhookHandler", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        implementations.ShouldBe(["DbtServiceCollectionExtensions.cs", "DbtWebhookReceiver.cs"]);

        var receiver = typeof(Autheris.Modules.Dbt.DbtModule).Assembly.GetType("Autheris.Modules.Dbt.DbtWebhookReceiver");
        receiver.ShouldNotBeNull().IsPublic.ShouldBeFalse("The webhook handler implementation must be internal.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_webhook_handler_referenced_only_by_core_webhook_endpoint()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var allowedCore = new[]
        {
            "src/Autheris.Application/Catalog/OpenMetadata/IOpenMetadataWebhookHandler.cs",
            "src/Autheris.Api/Endpoints/WebhookEndpoints.cs",
            "src/Autheris.Api/Hosting/ModuleSecurityGuards.cs",
        };

        var violations = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles.Where(f => f.Text.Contains("IOpenMetadataWebhookHandler", StringComparison.Ordinal)))
        {
            if (!allowedCore.Contains(file.RelativePath, StringComparer.Ordinal))
            {
                violations.Add(file.RelativePath);
            }
        }

        violations.ShouldBeEmpty("Only the core webhook route (and the module guard) may reference IOpenMetadataWebhookHandler:\n" + string.Join("\n", violations));

        var module = Path.Combine(root, "modules", "Autheris.Modules.OpenMetadata");
        var implementations = Directory.EnumerateFiles(module, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("IOpenMetadataWebhookHandler", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        implementations.ShouldBe(["OpenMetadataModule.cs", "OpenMetadataSyncService.cs"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Signature_verifier_uses_fixed_time_compare()
    {
        var dbtVerifier = RepositoryScan.ProductionFiles
            .Single(f => f.RelativePath == "src/Autheris.Application/Catalog/Semantic/DbtCloudWebhookSignature.cs");
        dbtVerifier.Text.ShouldContain("CryptographicOperations.FixedTimeEquals");
        dbtVerifier.Text.ShouldNotContain("SequenceEqual");
        dbtVerifier.Text.ShouldNotContain("string.Equals");
        dbtVerifier.Text.ShouldNotContain("== computed");
        dbtVerifier.Text.ShouldNotContain("ILogger");

        typeof(Autheris.Application.Catalog.Semantic.DbtCloudWebhookSignature).IsAbstract.ShouldBeTrue("The verifier must be a static class.");
        typeof(Autheris.Application.Catalog.Semantic.DbtCloudWebhookSignature).IsSealed.ShouldBeTrue();

        var omVerifier = RepositoryScan.ProductionFiles
            .Single(f => f.RelativePath == "src/Autheris.Application/Catalog/OpenMetadata/OpenMetadataWebhookSignature.cs");
        omVerifier.Text.ShouldContain("CryptographicOperations.FixedTimeEquals");
        omVerifier.Text.ShouldNotContain("SequenceEqual");
        omVerifier.Text.ShouldNotContain("string.Equals");
        omVerifier.Text.ShouldNotContain("== computed");

        typeof(Autheris.Application.Catalog.OpenMetadata.OpenMetadataWebhookSignature).IsAbstract.ShouldBeTrue("The verifier must be a static class.");
        typeof(Autheris.Application.Catalog.OpenMetadata.OpenMetadataWebhookSignature).IsSealed.ShouldBeTrue();

        var webhooks = RepositoryScan.ProductionFiles.Single(f => f.RelativePath == "src/Autheris.Api/Endpoints/WebhookEndpoints.cs");
        webhooks.Text.ShouldNotContain("DBT_WEBHOOK_SECRET");
        RepositoryScan.ProductionFiles
            .Where(f => f.Text.Contains("DBT_WEBHOOK_SECRET", StringComparison.Ordinal))
            .Select(f => f.RelativePath)
            .ShouldBeEmpty("The environment secret fallback was removed; only Gateway:Dbt:WebhookSecret counts.");
    }

    [Fact]
    public void Module_error_codes_equal_api_error_codes()
    {
        var apiCodes = typeof(Autheris.Api.Errors.ApiErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .ToDictionary(f => f.Name, f => (string?)f.GetRawConstantValue(), StringComparer.Ordinal);

        var moduleCodes = typeof(ModuleErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .ToList();

        moduleCodes.ShouldNotBeEmpty();
        foreach (var field in moduleCodes)
        {
            apiCodes.ShouldContainKey(field.Name);
            field.GetRawConstantValue().ShouldBe(apiCodes[field.Name], $"ModuleErrorCodes.{field.Name} drifted from ApiErrorCodes.{field.Name}");
        }
    }

    [Fact]
    public void Module_request_context_registered_without_modules()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        Autheris.Api.Extensions.GatewayServiceCollectionExtensions.AddGatewayModules(
            services,
            config,
            new TestHostEnvironment(),
            new Autheris.Domain.Options.GatewayOptions());

        services.Where(d => d.ServiceType == typeof(IModuleRequestContext))
            .ShouldHaveSingleItem()
            .ImplementationType.ShouldBe(typeof(ModuleRequestContext));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_project_references_exclude_runtime_and_native_assets()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var modulesRootDir = Path.Combine(root, "modules");
        Directory.Exists(modulesRootDir).ShouldBeTrue("Missing modules/ directory");

        var moduleProjects = Directory.EnumerateFiles(modulesRootDir, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        moduleProjects.ShouldNotBeEmpty("Expected non-test module projects under modules/");

        var violations = new List<string>();
        foreach (var csproj in moduleProjects)
        {
            var projectName = Path.GetFileNameWithoutExtension(csproj);
            var doc = System.Xml.Linq.XDocument.Load(csproj);
            var projectRefs = doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference");
            foreach (var pr in projectRefs)
            {
                var include = pr.Attribute("Include")?.Value ?? "<unknown>";
                var isPrivateFalse = string.Equals(
                    pr.Elements().FirstOrDefault(e => e.Name.LocalName == "Private")?.Value.Trim(),
                    "false",
                    StringComparison.OrdinalIgnoreCase);
                var excludeTokens = (pr.Elements().FirstOrDefault(e => e.Name.LocalName == "ExcludeAssets")?.Value ?? string.Empty)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!isPrivateFalse || !excludeTokens.Contains("runtime") || !excludeTokens.Contains("native"))
                {
                    violations.Add($"{projectName} -> {include}");
                }
            }
        }

        violations.ShouldBeEmpty(
            "Every module ProjectReference must set <Private>false</Private> and <ExcludeAssets>runtime;native</ExcludeAssets> so host native libraries are not duplicated into module bundles.");
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";

        public string ApplicationName { get; set; } = "Autheris.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static List<string> ExtractProjectReferences(string csprojContent) =>
        ProjectReferenceRegex()
            .Matches(csprojContent)
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/')))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
