namespace Autheris.Application.Dbt.Services;

using System;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

/// <summary>Registration of the semantic catalog (#112 A-12): persistent unless <c>Semantic:PersistManifest = false</c>.</summary>
public static class SemanticCatalogServiceCollectionExtensions
{
    public static IServiceCollection AddSemanticCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISemanticCatalogService>(sp =>
            sp.GetRequiredService<IOptions<SemanticOptions>>().Value.PersistManifest
                ? ActivatorUtilities.CreateInstance<PersistentSemanticCatalog>(sp)
                : ActivatorUtilities.CreateInstance<InMemorySemanticCatalog>(sp));
        services.TryAddSingleton(sp => SemanticTenancy.FromOptions(sp.GetService<IOptions<GatewayOptions>>()));
        services.TryAddSingleton(sp => new SemanticManifestValidator(
            sp.GetService<TrinoSqlEngine.Governance.IPolicyExpressionParser>() ?? Autheris.Application.Policy.Lint.PolicyParserSetup.CreateParser(),
            sp.GetRequiredService<IOptions<SemanticOptions>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SemanticOptions>, SemanticPersistenceStartupValidator>());
        return services;
    }
}

/// <summary><c>Semantic:PersistManifest = true</c> requires a registered <see cref="ISemanticManifestStore"/>: fail at start-up, not at the first request (M-6).</summary>
internal sealed class SemanticPersistenceStartupValidator(IServiceProvider services) : IValidateOptions<SemanticOptions>
{
    public ValidateOptionsResult Validate(string? name, SemanticOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.PersistManifest && services.GetService<ISemanticManifestStore>() is null)
        {
            return ValidateOptionsResult.Fail("Semantic:PersistManifest is true but no ISemanticManifestStore is registered.");
        }

        return ValidateOptionsResult.Success;
    }
}
