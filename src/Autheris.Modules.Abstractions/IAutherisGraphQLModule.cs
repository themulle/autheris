namespace Autheris.Modules.Abstractions;

/// <summary>
/// Optional contract implemented by an <see cref="IAutherisModule"/> that contributes GraphQL type extensions.
/// </summary>
public interface IAutherisGraphQLModule
{
    /// <summary>
    /// Registers GraphQL type extensions through the guarded GraphQL registrar.
    /// </summary>
    void ConfigureGraphQL(IModuleGraphQLRegistrar graphql);
}
