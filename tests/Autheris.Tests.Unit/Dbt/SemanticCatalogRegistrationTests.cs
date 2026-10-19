namespace Autheris.Tests.Unit.Dbt;

using System;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>#112 A-12 / M-6: catalog selection by <c>Semantic:PersistManifest</c> and the start-up check for the store.</summary>
public sealed class SemanticCatalogRegistrationTests
{
    private static ServiceProvider Build(bool persist, bool withStore)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new SemanticOptions { PersistManifest = persist }));
        services.AddSingleton(SemanticCatalogTestKit.Mode(TenancyMode.Multi));
        if (withStore)
        {
            services.AddSingleton<ISemanticManifestStore, FakeSemanticManifestStore>();
        }

        services.AddSemanticCatalog();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void PersistManifestTrue_UsesThePersistentCatalog() =>
        Build(persist: true, withStore: true).GetRequiredService<ISemanticCatalogService>().ShouldBeOfType<PersistentSemanticCatalog>();

    [Fact]
    public void PersistManifestFalse_UsesTheInMemoryCatalog_WithoutAStore() =>
        Build(persist: false, withStore: false).GetRequiredService<ISemanticCatalogService>().ShouldBeOfType<InMemorySemanticCatalog>();

    [Fact]
    [Trait("Category", "Security")]
    public void PersistManifestTrue_WithoutStore_FailsStartupValidation()
    {
        var sp = Build(persist: true, withStore: false);
        var validator = new SemanticOptionsValidator();

        var startup = Should.Throw<Exception>(() =>
        {
            foreach (var v in sp.GetServices<IValidateOptions<SemanticOptions>>())
            {
                var result = v.Validate(null, new SemanticOptions { PersistManifest = true });
                if (result.Failed) throw new OptionsValidationException(nameof(SemanticOptions), typeof(SemanticOptions), result.Failures);
            }
        });

        startup.ShouldBeOfType<OptionsValidationException>();
        validator.Validate(null, new SemanticOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void PersistManifestTrue_WithStore_PassesStartupValidation()
    {
        var sp = Build(persist: true, withStore: true);

        sp.GetServices<IValidateOptions<SemanticOptions>>().ShouldAllBe(v => v.Validate(null, new SemanticOptions()).Succeeded);
    }
}
