using Autheris.Application.Catalog.Semantic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Catalog;

public sealed class NoDbtHealthDataTests
{
    [Fact]
    public async Task Reports_every_table_healthy_and_records_nothing()
    {
        var sut = new NoDbtHealthData();
        var table = new TableIdentifier("c", "s", "t");

        var failure = new DbtTestFailure("not_null", "m", "col", "error", "failed", 1);
        await sut.SetTableHealthAsync(table, DbtModelHealthStatus.Quarantined, [failure]);

        (await sut.GetTableHealthAsync(table)).Status.ShouldBe(DbtModelHealthStatus.Healthy);
        (await sut.GetAllHealthStatesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Semantic_catalog_registration_exposes_the_import_contract()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISemanticCatalogService>(new InMemorySemanticCatalog());
        services.AddSemanticCatalog();
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISemanticMetricImport>().ShouldBeSameAs(provider.GetRequiredService<ISemanticCatalogService>());
    }
}
