namespace Autheris.Modules.Abstractions;

using System;

/// <summary>
/// Guarded registrar for module-contributed GraphQL type extensions.
/// </summary>
public interface IModuleGraphQLRegistrar
{
    /// <summary>
    /// Registers a HotChocolate type extension class after verifying it carries <c>[ExtendObjectType]</c>
    /// and mandatory field- or type-level authorization metadata.
    /// </summary>
    void AddTypeExtension(Type typeExtension);
}
