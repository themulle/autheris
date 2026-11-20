namespace Autheris.Api.Hosting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Plugins;
using Autheris.Application.Plugins.Auth;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.State;
using Autheris.Domain.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// Fail-closed boundary rules for external modules: protected host services and extension points, and the configuration
/// section a module may claim.
/// </summary>
internal static class ModuleSecurityGuards
{
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
        typeof(IColumnMaskingProvider),
        typeof(IAuthPluginHost),
        typeof(IPluginRoleProvider),
        typeof(IPluginCallerAttributeProvider),
        typeof(IPluginAuthorizationHook),
        typeof(IPluginManager),
        typeof(ICallerContextProvider),
        typeof(ITenantRegistry),
        typeof(ISqlConnectionFactory),
        typeof(IStartupFilter),
        typeof(IHostedService),
        typeof(IServer),
        typeof(IApplicationBuilderFactory),
        typeof(IClientIpResolver),
        typeof(IHttpContextAccessor),
        typeof(TimeProvider),
        typeof(IDistributedClusterStateProvider),
        typeof(Autheris.Modules.Abstractions.IModuleRequestContext),
    ];

    private static readonly string[] ProtectedNamespaces =
    [
        "Microsoft.AspNetCore.Authorization",
        "Microsoft.AspNetCore.Authentication",
        "Microsoft.AspNetCore.DataProtection",
        "Autheris.Application.Audit",
        "Autheris.Application.Plugins.Auth",
        "Autheris.Application.Policy",
        "Autheris.Application.Identity",
    ];

    // Options types outside the protected namespaces that still steer host security behavior.
    private static readonly Type[] ProtectedOptionsTypes =
    [
        typeof(Microsoft.AspNetCore.Builder.ForwardedHeadersOptions),
    ];

    private static readonly Type[] OptionsServiceDefinitions =
    [
        typeof(IConfigureOptions<>),
        typeof(IConfigureNamedOptions<>),
        typeof(IPostConfigureOptions<>),
        typeof(IValidateOptions<>),
        typeof(IOptionsFactory<>),
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

    /// <summary>
    /// A module may claim only <c>Gateway:&lt;PascalModuleId&gt;</c> and its children. A module whose own section equals a host section
    /// (known to <see cref="Autheris.Api.Configuration.GatewayConfigurationSchema"/> or protected) is rejected.
    /// </summary>
    internal static void ValidateConfigClaim(string moduleId, string sectionPath)
    {
        var ownSection = OwnConfigSection(moduleId);
        if (!string.Equals(sectionPath, ownSection, StringComparison.OrdinalIgnoreCase) &&
            !sectionPath.StartsWith(ownSection + ":", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModuleLoadException(
                moduleId,
                $"Module '{moduleId}' cannot claim configuration section '{sectionPath}'; only '{ownSection}' and its children are allowed.");
        }
    }

    internal static string OwnConfigSection(string moduleId) =>
        "Gateway:" + string.Concat(moduleId.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    internal static bool IsHostConfigSection(string section)
    {
        // A section is a host section when it, or any parent below the root, is one.
        var segments = section.Split(':');
        for (var count = 2; count <= segments.Length; count++)
        {
            if (IsHostConfigSectionExact(string.Join(':', segments.Take(count))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHostConfigSectionExact(string section)
    {
        foreach (var protectedPrefix in ProtectedConfigPrefixes)
        {
            if (string.Equals(section, protectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                section.StartsWith(protectedPrefix + ":", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Sections that the schema reserves for external modules (for example Gateway:Lakehouse) are exactly what a module may own.
        if (Autheris.Api.Configuration.GatewayConfigurationSchema.KnownModuleSections.ContainsKey(section))
        {
            return false;
        }

        var name = section["Gateway:".Length..];
        if (!name.Contains(':', StringComparison.Ordinal) && typeof(Autheris.Domain.Options.GatewayOptions).GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null)
        {
            return true;
        }

        foreach (var (path, _) in Autheris.Api.Configuration.GatewayConfigurationSchema.SideBound)
        {
            if (string.Equals(section, path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static List<ServiceDescriptor> CaptureProtectedServicesSnapshot(IServiceCollection services) =>
        services.Where(d => IsProtectedServiceType(d.ServiceType)).ToList();

    internal static void VerifyProtectedServicesUnchanged(
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

    /// <summary>Module-owned core port mapped to the only module id and assembly allowed to register it.</summary>
    internal sealed record ModuleOwnedPort(Type ServiceType, string ModuleId, string AssemblyName);

    internal static readonly IReadOnlyList<ModuleOwnedPort> ModuleOwnedPorts =
    [
        new(typeof(IDbtCloudWebhookHandler), "dbt", "Autheris.Modules.Dbt"),
    ];

    internal static List<ServiceDescriptor> CaptureModuleOwnedPortDescriptors(IServiceCollection services)
    {
        var ownedTypes = ModuleOwnedPorts.Select(p => p.ServiceType).ToHashSet();
        return services.Where(d => ownedTypes.Contains(d.ServiceType)).ToList();
    }

    internal static List<ServiceDescriptor> CaptureDbtCloudWebhookHandlers(IServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(IDbtCloudWebhookHandler)).ToList();

    internal static void VerifyDbtCloudWebhookHandler(
        string moduleId,
        IServiceCollection services,
        IReadOnlyCollection<ServiceDescriptor>? handlersBefore = null) =>
        VerifyModuleOwnedPorts(moduleId, services, handlersBefore);

    internal static void VerifyFinalDbtCloudWebhookHandler(IServiceCollection services, IReadOnlyCollection<string> loadedModuleIds) =>
        VerifyFinalModuleOwnedPorts(services, loadedModuleIds);

    /// <summary>
    /// Enforces exclusive module ownership of core ports: at most one descriptor, non-null <c>ImplementationType</c>
    /// declared in the owning assembly (factory and instance descriptors fail), and only the owning module may add, remove,
    /// or replace it.
    /// </summary>
    internal static void VerifyModuleOwnedPorts(
        string moduleId,
        IServiceCollection services,
        IReadOnlyCollection<ServiceDescriptor>? beforeSnapshot = null)
    {
        foreach (var port in ModuleOwnedPorts)
        {
            var current = services.Where(d => d.ServiceType == port.ServiceType).ToList();
            var previous = beforeSnapshot?.Where(d => d.ServiceType == port.ServiceType).ToList() ?? [];

            if (!string.Equals(moduleId, port.ModuleId, StringComparison.Ordinal))
            {
                if (beforeSnapshot is null)
                {
                    if (current.Count > 0)
                    {
                        throw new ModuleLoadException(
                            moduleId,
                            $"Module '{moduleId}' registered '{port.ServiceType.FullName}', which only module '{port.ModuleId}' may do.");
                    }
                }
                else if (current.Count != previous.Count ||
                         current.Where((d, idx) => !ReferenceEquals(d, previous[idx])).Any())
                {
                    throw new ModuleLoadException(
                        moduleId,
                        $"Module '{moduleId}' modified '{port.ServiceType.FullName}', which only module '{port.ModuleId}' may do.");
                }

                continue;
            }

            if (current.Count > 1)
            {
                throw new ModuleLoadException(
                    moduleId,
                    $"Module '{moduleId}' registered more than one '{port.ServiceType.FullName}'.");
            }

            if (current.Count == 1)
            {
                var implAssembly = current[0].ImplementationType?.Assembly.GetName().Name;
                if (!string.Equals(implAssembly, port.AssemblyName, StringComparison.Ordinal))
                {
                    throw new ModuleLoadException(
                        moduleId,
                        $"Module '{moduleId}' registered '{port.ServiceType.FullName}' with an implementation outside '{port.AssemblyName}'.");
                }
            }
        }
    }

    /// <summary>
    /// Final whole-collection check after the module loop: catches late modifications and ensures any registered port
    /// belongs to a module that actually loaded.
    /// </summary>
    internal static void VerifyFinalModuleOwnedPorts(IServiceCollection services, IReadOnlyCollection<string> loadedModuleIds)
    {
        foreach (var port in ModuleOwnedPorts)
        {
            var current = services.Where(d => d.ServiceType == port.ServiceType).ToList();
            if (current.Count == 0)
            {
                continue;
            }

            if (!loadedModuleIds.Contains(port.ModuleId, StringComparer.Ordinal))
            {
                throw new ModuleLoadException(
                    "host",
                    $"'{port.ServiceType.FullName}' is registered but module '{port.ModuleId}' did not load.");
            }

            if (current.Count > 1)
            {
                throw new ModuleLoadException(
                    port.ModuleId,
                    $"Module '{port.ModuleId}' registered more than one '{port.ServiceType.FullName}'.");
            }

            var implAssembly = current[0].ImplementationType?.Assembly.GetName().Name;
            if (!string.Equals(implAssembly, port.AssemblyName, StringComparison.Ordinal))
            {
                throw new ModuleLoadException(
                    port.ModuleId,
                    $"Module '{port.ModuleId}' registered '{port.ServiceType.FullName}' with an implementation outside '{port.AssemblyName}'.");
            }
        }
    }

    private static bool IsProtectedNamespace(string? ns)
    {
        if (string.IsNullOrEmpty(ns))
        {
            return false;
        }

        foreach (var protectedNamespace in ProtectedNamespaces)
        {
            if (ns.StartsWith(protectedNamespace, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProtectedServiceType(Type serviceType)
    {
        if (ProtectedServiceTypes.Contains(serviceType))
        {
            return true;
        }

        if (IsProtectedNamespace(serviceType.Namespace))
        {
            return true;
        }

        // Options configurators are judged by the options type they configure (authentication, authorization, data protection, ...).
        // Open generic definitions have no concrete argument and are skipped.
        if (serviceType.IsConstructedGenericType &&
            OptionsServiceDefinitions.Contains(serviceType.GetGenericTypeDefinition()) &&
            (IsProtectedNamespace(serviceType.GetGenericArguments()[0].Namespace) ||
             ProtectedOptionsTypes.Contains(serviceType.GetGenericArguments()[0])))
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
}
