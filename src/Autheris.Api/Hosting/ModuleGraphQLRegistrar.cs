namespace Autheris.Api.Hosting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Modules.Abstractions;
using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Guarded implementation of <see cref="IModuleGraphQLRegistrar"/> that verifies <c>[ExtendObjectType]</c>
/// and mandatory field- or type-level authorization attributes on every GraphQL type extension contributed by a module.
/// </summary>
public sealed class ModuleGraphQLRegistrar : IModuleGraphQLRegistrar
{
    private readonly string _moduleId;
    private readonly IRequestExecutorBuilder? _graphqlBuilder;
    private readonly List<Type> _registeredTypes = [];

    public ModuleGraphQLRegistrar(string moduleId, IRequestExecutorBuilder? graphqlBuilder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        _moduleId = moduleId;
        _graphqlBuilder = graphqlBuilder;
    }

    public string ModuleId => _moduleId;

    public IReadOnlyList<Type> RegisteredTypeExtensions => _registeredTypes;

    public void AddTypeExtension(Type typeExtension)
    {
        ArgumentNullException.ThrowIfNull(typeExtension);

        if (!typeExtension.IsClass)
        {
            throw new ModuleLoadException(
                _moduleId,
                $"GraphQL type extension '{typeExtension.FullName}' in module '{_moduleId}' must be a class.");
        }

        if (!HasExtendObjectTypeAttribute(typeExtension))
        {
            throw new ModuleLoadException(
                _moduleId,
                $"GraphQL type extension '{typeExtension.FullName}' in module '{_moduleId}' is missing [ExtendObjectType].");
        }

        if (HasAllowAnonymousAttribute(typeExtension))
        {
            throw new ModuleLoadException(
                _moduleId,
                $"GraphQL type extension '{typeExtension.FullName}' in module '{_moduleId}' carries [AllowAnonymous], which is forbidden.");
        }

        bool typeHasAuthorize = HasAuthorizeAttribute(typeExtension);

        var candidateMethods = typeExtension
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

        var candidateProperties = typeExtension
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        bool hasAnyMembers = false;

        foreach (var method in candidateMethods)
        {
            hasAnyMembers = true;
            if (HasAllowAnonymousAttribute(method))
            {
                throw new ModuleLoadException(
                    _moduleId,
                    $"GraphQL resolver '{typeExtension.Name}.{method.Name}' in module '{_moduleId}' carries [AllowAnonymous], which is forbidden.");
            }

            if (!typeHasAuthorize && !HasAuthorizeAttribute(method))
            {
                throw new ModuleLoadException(
                    _moduleId,
                    $"GraphQL resolver '{typeExtension.Name}.{method.Name}' in module '{_moduleId}' is missing required [Authorize] attribute.");
            }
        }

        foreach (var prop in candidateProperties)
        {
            hasAnyMembers = true;
            if (HasAllowAnonymousAttribute(prop))
            {
                throw new ModuleLoadException(
                    _moduleId,
                    $"GraphQL property '{typeExtension.Name}.{prop.Name}' in module '{_moduleId}' carries [AllowAnonymous], which is forbidden.");
            }

            if (!typeHasAuthorize && !HasAuthorizeAttribute(prop))
            {
                throw new ModuleLoadException(
                    _moduleId,
                    $"GraphQL property '{typeExtension.Name}.{prop.Name}' in module '{_moduleId}' is missing required [Authorize] attribute.");
            }
        }

        if (!hasAnyMembers && !typeHasAuthorize)
        {
            throw new ModuleLoadException(
                _moduleId,
                $"GraphQL type extension '{typeExtension.FullName}' in module '{_moduleId}' declares no authorized fields.");
        }

        _registeredTypes.Add(typeExtension);
        _graphqlBuilder?.AddTypeExtension(typeExtension);
    }

    private static bool HasExtendObjectTypeAttribute(MemberInfo member) =>
        member.GetCustomAttributes(inherit: true)
            .Any(a => string.Equals(a.GetType().Name, "ExtendObjectTypeAttribute", StringComparison.Ordinal));

    private static bool HasAuthorizeAttribute(MemberInfo member) =>
        member.GetCustomAttributes(inherit: true)
            .Any(a => string.Equals(a.GetType().Name, "AuthorizeAttribute", StringComparison.Ordinal));

    private static bool HasAllowAnonymousAttribute(MemberInfo member) =>
        member.GetCustomAttributes(inherit: true)
            .Any(a => string.Equals(a.GetType().Name, "AllowAnonymousAttribute", StringComparison.Ordinal));
}
