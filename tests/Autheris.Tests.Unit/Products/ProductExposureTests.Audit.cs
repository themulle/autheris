using System.Text.Json;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

public sealed partial class ProductExposureTests
{
    [Fact]
    public async Task Procedure_product_read_audit_carries_product_id_and_hash()
    {
        var service = await CreateAsync(ClearGrant());
        var product = (await _store.GetProductAsync(Tenant, "sales", "get_orders"))!;

        await service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant);

        var entry = _entries.Last(e => e.EventType == "PROCEDURE_EXECUTE");
        using var details = JsonDocument.Parse(entry.DetailsJson);
        details.RootElement.GetProperty("productId").GetGuid().ShouldBe(product.Id);
        details.RootElement.GetProperty("definitionHash").GetString().ShouldBe(product.DefinitionHash);
    }
}
