namespace Autheris.Api.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security;
using System.Security.Cryptography;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Plugins;
using Autheris.Application.Plugins.Auth;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Domain.Interfaces;
using Autheris.Infrastructure.Modules.Trust;
using Autheris.Modules.Abstractions;
using HotChocolate.Execution.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Represents a verified and activated module loaded by <see cref="ModuleLoader"/>.
/// </summary>
public sealed record LoadedModuleDescriptor(
    string Id,
    ModuleManifest Manifest,
    IAutherisModule Instance,
    ModuleAssemblyLoadContext LoadContext);

/// <summary>
/// Result of running <see cref="ModuleLoader"/> at startup.
/// </summary>
public sealed class ModuleLoadResult
{
    public ModuleLoadResult(IReadOnlyList<LoadedModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Modules = modules;
    }

    public IReadOnlyList<LoadedModuleDescriptor> Modules { get; }

    public IReadOnlyList<string> LoadedModuleIds =>
        Modules.Select(m => m.Id).ToArray();

    public IReadOnlyList<string> ClaimedConfigurationSections =>
        Modules.SelectMany(m => m.Instance.ConfigurationSections).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Maps HTTP endpoints for all loaded modules through <see cref="ModuleEndpointRegistrar"/> and runs post-mapping verification.
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        foreach (var module in Modules)
        {
            var registrar = new ModuleEndpointRegistrar(endpoints, module.Id);
            module.Instance.MapEndpoints(registrar);
            ModuleEndpointRegistrar.VerifyModuleEndpoints(endpoints, module.Id);
        }
    }

    /// <summary>
    /// Registers GraphQL type extensions for all loaded modules implementing <see cref="IAutherisGraphQLModule"/>.
    /// </summary>
    public void ConfigureGraphQL(IRequestExecutorBuilder? graphqlBuilder = null)
    {
        foreach (var module in Modules)
        {
            if (module.Instance is IAutherisGraphQLModule gqlModule)
            {
                var registrar = new ModuleGraphQLRegistrar(module.Id, graphqlBuilder);
                gqlModule.ConfigureGraphQL(registrar);
            }
        }
    }
}

/// <summary>
/// Loads, verifies, and initializes out-of-core signed modules from <c>Gateway:Modules:Path</c>
/// according to the ordered allowlist in <c>Gateway:Modules:Enabled</c>.
/// </summary>
public sealed class ModuleLoader
{
    public const string DefaultModulesPath = "/app/modules";
    public const string DefaultHostVersion = "1.2.0";
    public const string DefaultAbstractionsVersion = "1.0.0";
    public const string EmbeddedPublicKeyResourceName = "Autheris.Api.Hosting.Keys.module-release-pub.pem";

    public const string EnabledConfigKey = "Gateway:Modules:Enabled";
    public const string PathConfigKey = "Gateway:Modules:Path";
    public const string AllowUnsignedConfigKey = "Gateway:Modules:AllowUnsignedInDevelopment";
    public const string WorkspacePathConfigKey = "Gateway:Modules:WorkspacePath";

    private static readonly Type[] ProtectedServiceTypes =
    [
        typeof(IAuditWriter),
        typeof(IAuditLogRepository),
        typeof(IAuthorizationHandler),
        typeof(IAuthorizationPolicyProvider),
        typeof(IAuthorizationService),
        typeof(IAuthorizationEvaluator),
        typeof(IClaimsTransformation),
        typeof(IAuthenticationSchemeProvider),
        typeof(IAuthenticationHandlerProvider),
        typeof(IAuthenticationService),
        typeof(IPolicyEnforcementService),
        typeof(IColumnMaskingProvider),
        typeof(IAuthPluginHost),
        typeof(IPluginRoleProvider),
        typeof(IPluginCallerAttributeProvider),
        typeof(IPluginAuthorizationHook),
        typeof(IPluginManager),
        typeof(ICallerContextProvider),
        typeof(ITenantRegistry),
        typeof(ISqlConnectionFactory),
    ];

    private static readonly string[] ProtectedConfigPrefixes =
    [
        "Gateway:Authentication",
        "Gateway:Authorization",
        "Gateway:Audit",
        "Gateway:Modules",
        "Gateway:Plugins",
        "Gateway:GovernanceDb",
        "Gateway:DataSources",
    ];

    private readonly ECDsa? _publicKeyOverride;
    private readonly string _hostVersion;
    private readonly string _abstractionsVersion;
    private readonly Func<string, bool>? _isDirectoryWritableOverride;
    private readonly ILogger<ModuleLoader> _logger;

    public ModuleLoader(
        ECDsa? publicKeyOverride = null,
        string hostVersion = DefaultHostVersion,
        string abstractionsVersion = DefaultAbstractionsVersion,
        Func<string, bool>? isDirectoryWritableOverride = null,
        ILogger<ModuleLoader>? logger = null)
    {
        _publicKeyOverride = publicKeyOverride;
        _hostVersion = string.IsNullOrWhiteSpace(hostVersion) ? DefaultHostVersion : hostVersion;
        _abstractionsVersion = string.IsNullOrWhiteSpace(abstractionsVersion) ? DefaultAbstractionsVersion : abstractionsVersion;
        _isDirectoryWritableOverride = isDirectoryWritableOverride;
        _logger = logger ?? NullLogger<ModuleLoader>.Instance;
    }

    /// <summary>
    /// Loads the embedded release ECDSA P-256 public key compiled into <c>Autheris.Api</c>.
    /// </summary>
    public static ECDsa LoadEmbeddedReleasePublicKey()
    {
        var assembly = typeof(ModuleLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedPublicKeyResourceName)
            ?? throw new InvalidOperationException($"Embedded module release public key '{EmbeddedPublicKeyResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        return ecdsa;
    }

    /// <summary>
    /// Static convenience entry point for discovering, verifying, and configuring enabled modules.
    /// </summary>
    public static ModuleLoadResult LoadModules(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        IGovernanceConnectionFactory? connectionFactory = null,
        ILogger<ModuleLoader>? logger = null)
    {
        var loader = new ModuleLoader(logger: logger);
        var hostContext = connectionFactory is not null
            ? new ModuleHostContext(TimeProvider.System, DefaultHostVersion, connectionFactory)
            : null;
        return loader.LoadAndConfigureServices(services, configuration, environment, hostContext);
    }

    /// <summary>
    /// Discovers, verifies, and calls <see cref="IAutherisModule.ConfigureServices"/> on all enabled modules.
    /// Fails closed with <see cref="ModuleLoadException"/> on any trust, version, permission, or security boundary violation.
    /// </summary>
    public ModuleLoadResult LoadAndConfigureServices(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        IModuleHostContext? hostContext = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        bool isDevelopment = environment.IsDevelopment();
        bool isProduction = environment.IsProduction() || !isDevelopment;
        bool allowUnsigned = ReadAllowUnsignedFlag(configuration);

        if (allowUnsigned && !isDevelopment)
        {
            throw new ModuleLoadException(
                "host",
                $"Configuration key '{AllowUnsignedConfigKey}' is true outside the Development environment ({environment.EnvironmentName}).");
        }

        string? configuredModulesRoot = configuration[PathConfigKey];
        string modulesRoot = string.IsNullOrWhiteSpace(configuredModulesRoot)
            ? DefaultModulesPath
            : configuredModulesRoot;

        var enabledIds = ReadEnabledModuleIds(configuration);

        // Enforce separate trust domains between customer plugin directories and module root directory
        VerifyPluginAndModuleDirectoriesDisjoint(configuration, modulesRoot, enabledIds.Count > 0);

        if (enabledIds.Count == 0)
        {
            return new ModuleLoadResult([]);
        }

        var effectiveHostContext = hostContext ?? new ModuleHostContext(
            TimeProvider.System,
            _hostVersion,
            new GovernanceConnectionFactory(new Autheris.Domain.Options.GovernanceDbOptions(), statePaths: null, environment));

        string? workspacePath = configuration[WorkspacePathConfigKey];
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            if (!isDevelopment)
            {
                throw new ModuleLoadException(
                    "host",
                    $"Configuration key '{WorkspacePathConfigKey}' is not allowed outside the Development environment ({environment.EnvironmentName}).");
            }

            if (!allowUnsigned)
            {
                throw new ModuleLoadException(
                    "host",
                    $"Configuration key '{WorkspacePathConfigKey}' requires '{AllowUnsignedConfigKey}' to be true.");
            }

            if (TryLoadWorkspaceModules(enabledIds, services, configuration, effectiveHostContext, isDevelopment, allowUnsigned, workspacePath, out var workspaceResult))
            {
                return workspaceResult;
            }

            throw new ModuleLoadException(enabledIds[0], $"Failed to load workspace modules from '{workspacePath}'.");
        }

        string fullModulesRoot = Path.GetFullPath(modulesRoot);
        if (!Directory.Exists(fullModulesRoot))
        {
            throw new ModuleLoadException(enabledIds[0], $"Module root directory '{fullModulesRoot}' does not exist.");
        }

        ModuleDirectoryPermissionCheck.VerifyDirectory(
            fullModulesRoot,
            isProduction,
            moduleId: enabledIds[0],
            isWritableOverride: _isDirectoryWritableOverride);

        using var ownedPublicKey = _publicKeyOverride is null ? LoadEmbeddedReleasePublicKey() : null;
        ECDsa effectivePublicKey = _publicKeyOverride ?? ownedPublicKey!;

        var loadedModules = new List<LoadedModuleDescriptor>(enabledIds.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var moduleId in enabledIds)
        {
            ValidateModuleIdFormat(moduleId);

            if (!seenIds.Add(moduleId))
            {
                throw new ModuleLoadException(moduleId, $"Duplicate module id '{moduleId}' in '{EnabledConfigKey}'.");
            }

            var moduleDir = Path.Combine(fullModulesRoot, moduleId);
            var fullModuleDir = Path.GetFullPath(moduleDir);
            if (!fullModuleDir.StartsWith(fullModulesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new ModuleLoadException(moduleId, $"Module directory '{fullModuleDir}' escapes module root '{fullModulesRoot}'.");
            }

            if (!Directory.Exists(fullModuleDir))
            {
                throw new ModuleLoadException(moduleId, $"Module directory '{fullModuleDir}' does not exist.");
            }

            ModuleDirectoryPermissionCheck.VerifyDirectory(
                fullModuleDir,
                isProduction,
                moduleId: moduleId,
                isWritableOverride: _isDirectoryWritableOverride);

            var manifestPath = Path.Combine(fullModuleDir, "module.json");
            if (!File.Exists(manifestPath))
            {
                throw new ModuleLoadException(moduleId, $"Missing module.json in '{fullModuleDir}'.");
            }

            ModuleManifest manifest;
            try
            {
                byte[] manifestBytes = File.ReadAllBytes(manifestPath);
                manifest = ModuleManifest.Parse(manifestBytes);
            }
            catch (Exception ex) when (ex is not ModuleLoadException)
            {
                throw new ModuleLoadException(moduleId, $"Invalid module.json: {ex.Message}", ex);
            }

            if (!string.Equals(manifest.Id, moduleId, StringComparison.Ordinal))
            {
                throw new ModuleLoadException(
                    moduleId,
                    $"Manifest id '{manifest.Id}' does not match enabled module directory '{moduleId}'.");
            }

            VerifyVersionCompatibility(moduleId, "hostVersion", manifest.HostVersion, _hostVersion);
            VerifyVersionCompatibility(moduleId, "abstractionsVersion", manifest.AbstractionsVersion, _abstractionsVersion);

            bool isUnsignedDev = allowUnsigned && isDevelopment && string.IsNullOrWhiteSpace(manifest.Signature);
            if (isUnsignedDev)
            {
                _logger.LogWarning("Loading unsigned module '{ModuleId}' v{Version} because {Flag}=true in Development.", moduleId, manifest.Version, AllowUnsignedConfigKey);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(manifest.Signature))
                {
                    throw new ModuleLoadException(moduleId, "Module manifest is unsigned and unsigned modules are not permitted.");
                }

                if (!ManifestSignatureVerifier.VerifyManifest(manifest, effectivePublicKey, out var sigFailure))
                {
                    throw new ModuleLoadException(moduleId, sigFailure ?? "Manifest signature verification failed.");
                }
            }

            VerifiedModuleImage verifiedImage;
            try
            {
                verifiedImage = FileHashVerifier.VerifyAndLoad(
                    fullModuleDir,
                    manifest,
                    verifyHashes: !isUnsignedDev || manifest.Files.Count > 0);
            }
            catch (Exception ex) when (ex is SecurityException or IOException or InvalidOperationException)
            {
                throw new ModuleLoadException(moduleId, ex.Message, ex);
            }

            var alc = new ModuleAssemblyLoadContext(verifiedImage);
            alc.EnsureNoHostAssemblyCollisions(AssemblyLoadContext.Default.Assemblies);

            Assembly entryAssembly;
            try
            {
                entryAssembly = alc.LoadEntryAssembly();
            }
            catch (Exception ex) when (ex is not ModuleLoadException)
            {
                throw new ModuleLoadException(moduleId, $"Failed to load entry assembly '{manifest.EntryAssembly}': {ex.Message}", ex);
            }

            Type? entryType = entryAssembly.GetType(manifest.EntryType, throwOnError: false, ignoreCase: false);
            if (entryType is null)
            {
                throw new ModuleLoadException(moduleId, $"Entry type '{manifest.EntryType}' was not found in '{manifest.EntryAssembly}'.");
            }

            if (entryType.IsAbstract || entryType.IsInterface || !typeof(IAutherisModule).IsAssignableFrom(entryType))
            {
                throw new ModuleLoadException(moduleId, $"Entry type '{manifest.EntryType}' does not implement '{nameof(IAutherisModule)}'.");
            }

            IAutherisModule moduleInstance;
            try
            {
                moduleInstance = (IAutherisModule)(Activator.CreateInstance(entryType)
                    ?? throw new ModuleLoadException(moduleId, $"Activator returned null for '{manifest.EntryType}'."));
            }
            catch (Exception ex) when (ex is not ModuleLoadException)
            {
                throw new ModuleLoadException(moduleId, $"Failed to instantiate module '{manifest.EntryType}': {ex.Message}", ex);
            }

            if (!string.Equals(moduleInstance.Id, moduleId, StringComparison.Ordinal))
            {
                throw new ModuleLoadException(moduleId, $"Module instance Id '{moduleInstance.Id}' does not match manifest id '{moduleId}'.");
            }

            var scopedConfig = BuildScopedModuleConfiguration(moduleId, moduleInstance.ConfigurationSections, configuration);
            var protectedSnapshot = CaptureProtectedServicesSnapshot(services);

            try
            {
                moduleInstance.ConfigureServices(services, scopedConfig, effectiveHostContext);
            }
            catch (Exception ex) when (ex is not ModuleLoadException)
            {
                throw new ModuleLoadException(moduleId, $"ConfigureServices failed: {ex.Message}", ex);
            }

            VerifyProtectedServicesUnchanged(moduleId, services, protectedSnapshot);

            _logger.LogInformation("Loaded signed module '{ModuleId}' v{Version} (keyId: {KeyId}).", moduleId, manifest.Version, manifest.KeyId);
            _logger.LogInformation("ModuleLoaded: Id '{ModuleId}', Version '{Version}', KeyId '{KeyId}', Signed '{Signed}'", manifest.Id, manifest.Version, manifest.KeyId, true);
            loadedModules.Add(new LoadedModuleDescriptor(moduleId, manifest, moduleInstance, alc));
        }

        services.AddSingleton(new ModuleLoadResult(loadedModules));
        return new ModuleLoadResult(loadedModules);
    }

    private static bool ReadAllowUnsignedFlag(IConfiguration configuration)
    {
        var raw = configuration[AllowUnsignedConfigKey];
        return bool.TryParse(raw, out var parsed) && parsed;
    }

    private static List<string> ReadEnabledModuleIds(IConfiguration configuration)
    {
        var list = new List<string>();
        var section = configuration.GetSection(EnabledConfigKey);

        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            foreach (var part in section.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                list.Add(part);
            }
        }

        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                list.Add(child.Value.Trim());
            }
        }

        return list;
    }

    private static void VerifyPluginAndModuleDirectoriesDisjoint(
        IConfiguration configuration,
        string modulesRoot,
        bool hasEnabledModules)
    {
        var pluginDirs = new[]
        {
            configuration["Gateway:Plugins:Directory"],
        };

        var fullModulesRoot = Path.GetFullPath(modulesRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var pluginDir in pluginDirs)
        {
            if (string.IsNullOrWhiteSpace(pluginDir))
            {
                continue;
            }

            var fullPluginDir = Path.GetFullPath(pluginDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullPluginDir, fullModulesRoot, StringComparison.Ordinal) ||
                fullPluginDir.StartsWith(fullModulesRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                fullModulesRoot.StartsWith(fullPluginDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new ModuleLoadException(
                    hasEnabledModules ? "host" : "plugins",
                    $"Customer plugin directory '{fullPluginDir}' and module root directory '{fullModulesRoot}' must not be equal or nested.");
            }
        }
    }

    private static void ValidateModuleIdFormat(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId) ||
            moduleId.Contains("..", StringComparison.Ordinal) ||
            moduleId.Contains('/', StringComparison.Ordinal) ||
            moduleId.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(moduleId))
        {
            throw new ModuleLoadException(
                string.IsNullOrWhiteSpace(moduleId) ? "unknown" : moduleId,
                $"Invalid module id '{moduleId}'.");
        }
    }

    private static void VerifyVersionCompatibility(
        string moduleId,
        string fieldName,
        string manifestVersion,
        string hostExpectedVersion)
    {
        if (!TryParseMajorMinor(manifestVersion, out int reqMajor, out int reqMinor) ||
            !TryParseMajorMinor(hostExpectedVersion, out int hostMajor, out int hostMinor))
        {
            throw new ModuleLoadException(
                moduleId,
                $"Invalid semantic version in '{fieldName}': manifest='{manifestVersion}', host='{hostExpectedVersion}'.");
        }

        if (reqMajor != hostMajor || reqMinor != hostMinor)
        {
            throw new ModuleLoadException(
                moduleId,
                $"Incompatible {fieldName} '{manifestVersion}'; host requires '{hostExpectedVersion}' (major.minor match).");
        }
    }

    private static bool TryParseMajorMinor(string versionText, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return false;
        }

        var core = versionText.Split('-', '+')[0];
        if (Version.TryParse(core, out var parsed))
        {
            major = parsed.Major;
            minor = parsed.Minor;
            return true;
        }

        return false;
    }

    private static IConfiguration BuildScopedModuleConfiguration(
        string moduleId,
        IReadOnlyList<string> declaredSections,
        IConfiguration rootConfiguration)
    {
        var entries = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (declaredSections is not null)
        {
            foreach (var sectionPath in declaredSections)
            {
                if (string.IsNullOrWhiteSpace(sectionPath))
                {
                    continue;
                }

                foreach (var protectedPrefix in ProtectedConfigPrefixes)
                {
                    if (string.Equals(sectionPath, protectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                        sectionPath.StartsWith(protectedPrefix + ":", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ModuleLoadException(
                            moduleId,
                            $"Module '{moduleId}' cannot claim protected host configuration section '{sectionPath}'.");
                    }
                }

                var section = rootConfiguration.GetSection(sectionPath);
                foreach (var kv in section.AsEnumerable(makePathsRelative: false))
                {
                    if (kv.Value is not null)
                    {
                        entries[kv.Key] = kv.Value;
                    }
                }
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(entries)
            .Build();
    }

    private static List<ServiceDescriptor> CaptureProtectedServicesSnapshot(IServiceCollection services) =>
        services.Where(d => IsProtectedServiceType(d.ServiceType)).ToList();

    private static void VerifyProtectedServicesUnchanged(
        string moduleId,
        IServiceCollection services,
        List<ServiceDescriptor> beforeSnapshot)
    {
        var afterSnapshot = services.Where(d => IsProtectedServiceType(d.ServiceType)).ToList();
        if (beforeSnapshot.Count != afterSnapshot.Count)
        {
            var diffDescriptor = afterSnapshot.FirstOrDefault(d => !beforeSnapshot.Contains(d))
                ?? beforeSnapshot.FirstOrDefault(d => !afterSnapshot.Contains(d));
            var diffType = diffDescriptor?.ServiceType
                ?? afterSnapshot.FirstOrDefault()?.ServiceType
                ?? beforeSnapshot.FirstOrDefault()?.ServiceType;

            throw new ModuleLoadException(
                moduleId,
                $"Module '{moduleId}' attempted to add or remove protected host security service '{diffType?.FullName ?? "unknown"}'.");
        }

        for (int i = 0; i < beforeSnapshot.Count; i++)
        {
            if (!ReferenceEquals(beforeSnapshot[i], afterSnapshot[i]))
            {
                throw new ModuleLoadException(
                    moduleId,
                    $"Module '{moduleId}' attempted to replace protected host security service '{afterSnapshot[i].ServiceType.FullName}'.");
            }
        }
    }

    private static bool IsProtectedServiceType(Type serviceType)
    {
        if (ProtectedServiceTypes.Contains(serviceType))
        {
            return true;
        }

        var ns = serviceType.Namespace ?? string.Empty;
        if (ns.StartsWith("Microsoft.AspNetCore.Authorization", StringComparison.Ordinal) ||
            ns.StartsWith("Microsoft.AspNetCore.Authentication", StringComparison.Ordinal) ||
            ns.StartsWith("Autheris.Application.Audit", StringComparison.Ordinal) ||
            ns.StartsWith("Autheris.Application.Plugins.Auth", StringComparison.Ordinal))
        {
            return true;
        }

        var name = serviceType.Name;
        if (name.Contains("ColumnMask", StringComparison.Ordinal) ||
            name.Contains("Masking", StringComparison.Ordinal) ||
            name.Contains("AccessDecision", StringComparison.Ordinal) ||
            name.Contains("AuditWriter", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private bool TryLoadWorkspaceModules(
        IReadOnlyList<string> enabledIds,
        IServiceCollection services,
        IConfiguration configuration,
        IModuleHostContext effectiveHostContext,
        bool isDevelopment,
        bool allowUnsigned,
        string workspacePath,
        out ModuleLoadResult result)
    {
        result = new ModuleLoadResult([]);
        if (!isDevelopment || !allowUnsigned)
        {
            throw new ModuleLoadException("host", "Workspace modules require both Development environment and AllowUnsignedInDevelopment=true.");
        }

        var loadedModules = new List<LoadedModuleDescriptor>(enabledIds.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workspaceRoot = Path.GetFullPath(workspacePath);

        foreach (var moduleId in enabledIds)
        {
            ValidateModuleIdFormat(moduleId);
            if (!seenIds.Add(moduleId))
            {
                throw new ModuleLoadException(moduleId, $"Duplicate module id '{moduleId}' in '{EnabledConfigKey}'.");
            }

            var pascal = string.Concat(moduleId.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
            var assemblyFileName = $"Autheris.Modules.{pascal}.dll";
            var moduleDllPath = FindWorkspaceModuleDll(workspaceRoot, pascal, assemblyFileName, out var moduleBinDir);
            if (moduleDllPath is null)
            {
                return false;
            }

            try
            {
                FileHashVerifier.EnsureNoSymlinkEscape(workspaceRoot, moduleDllPath);
            }
            catch (Exception ex)
            {
                throw new ModuleLoadException(moduleId, $"Symlink check failed: {ex.Message}", ex);
            }

            byte[] dllBytes = File.ReadAllBytes(moduleDllPath);
            var filesDict = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [assemblyFileName] = dllBytes
            };

            if (moduleBinDir is not null && Directory.Exists(moduleBinDir))
            {
                var hostAssemblyNames = AssemblyLoadContext.Default.Assemblies
                    .Select(a => a.GetName().Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var peerDll in Directory.EnumerateFiles(moduleBinDir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    var peerName = Path.GetFileName(peerDll);
                    var peerSimple = Path.GetFileNameWithoutExtension(peerDll);
                    if (!string.Equals(peerName, assemblyFileName, StringComparison.OrdinalIgnoreCase)
                        && !hostAssemblyNames.Contains(peerSimple))
                    {
                        filesDict[peerName] = File.ReadAllBytes(peerDll);
                    }
                }
            }

            var manifest = new ModuleManifest(
                Schema: 1,
                Id: moduleId,
                Version: _hostVersion,
                HostVersion: _hostVersion,
                AbstractionsVersion: _abstractionsVersion,
                EntryAssembly: assemblyFileName,
                EntryType: $"Autheris.Modules.{pascal}.{pascal}Module",
                Files: [new ModuleManifestFile(assemblyFileName, IntegrityHashHelper.ComputeSha256HexLower(dllBytes))],
                KeyId: "workspace",
                Signature: string.Empty);

            var verifiedImage = new VerifiedModuleImage(
                moduleId,
                moduleBinDir ?? Path.GetDirectoryName(moduleDllPath)!,
                manifest,
                filesDict);

            var alc = new ModuleAssemblyLoadContext(verifiedImage);
            var hostAssembliesWithoutSelf = AssemblyLoadContext.Default.Assemblies
                .Where(a => !string.Equals(a.GetName().Name, $"Autheris.Modules.{pascal}", StringComparison.OrdinalIgnoreCase));
            alc.EnsureNoHostAssemblyCollisions(hostAssembliesWithoutSelf);

            Assembly entryAssembly = alc.LoadEntryAssembly();

            var entryType = entryAssembly.GetType(manifest.EntryType, throwOnError: false, ignoreCase: false);
            if (entryType is null)
            {
                Type?[] candidateTypes;
                try
                {
                    candidateTypes = entryAssembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    candidateTypes = ex.Types;
                }

                entryType = candidateTypes.FirstOrDefault(t => t != null && !t.IsAbstract && !t.IsInterface && typeof(IAutherisModule).IsAssignableFrom(t));
            }

            if (entryType is null)
            {
                throw new ModuleLoadException(moduleId, $"Entry type '{manifest.EntryType}' was not found in '{assemblyFileName}'.");
            }

            var moduleInstance = (IAutherisModule)(Activator.CreateInstance(entryType)
                ?? throw new ModuleLoadException(moduleId, $"Activator returned null for '{entryType.FullName}'."));

            if (!string.Equals(moduleInstance.Id, moduleId, StringComparison.Ordinal))
            {
                throw new ModuleLoadException(moduleId, $"Module instance Id '{moduleInstance.Id}' does not match manifest id '{moduleId}'.");
            }

            var scopedConfig = BuildScopedModuleConfiguration(moduleId, moduleInstance.ConfigurationSections, configuration);
            var protectedSnapshot = CaptureProtectedServicesSnapshot(services);

            try
            {
                moduleInstance.ConfigureServices(services, scopedConfig, effectiveHostContext);
            }
            catch (Exception ex) when (ex is not ModuleLoadException)
            {
                throw new ModuleLoadException(moduleId, $"ConfigureServices failed: {ex.Message}", ex);
            }

            VerifyProtectedServicesUnchanged(moduleId, services, protectedSnapshot);

            _logger.LogWarning("Module '{ModuleId}' loaded from workspace path (unsigned)", moduleId);
            _logger.LogInformation("ModuleLoaded: Id '{ModuleId}', Version '{Version}', KeyId '{KeyId}', Signed '{Signed}'", manifest.Id, manifest.Version, manifest.KeyId, false);

            loadedModules.Add(new LoadedModuleDescriptor(moduleId, manifest, moduleInstance, alc));
        }

        result = new ModuleLoadResult(loadedModules);
        services.AddSingleton(result);
        return true;
    }

    private static string? FindWorkspaceModuleDll(string workspaceRoot, string pascal, string assemblyFileName, out string? moduleBinDir)
    {
        moduleBinDir = null;
        if (!Directory.Exists(workspaceRoot))
        {
            return null;
        }

        var found = Directory.EnumerateFiles(workspaceRoot, assemblyFileName, SearchOption.AllDirectories)
            .FirstOrDefault();
        if (found is not null)
        {
            moduleBinDir = Path.GetDirectoryName(found);
            return found;
        }

        return null;
    }
}
