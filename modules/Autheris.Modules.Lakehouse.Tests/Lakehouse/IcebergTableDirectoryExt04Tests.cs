namespace Autheris.Extensions.Tests.Lakehouse;

using Autheris.Extensions.Lakehouse.Services;
using Shouldly;
using Xunit;

/// <summary>EXT-04: a dotted table directory name must not widen the manifest trust anchor to its parent.</summary>
[Trait("Category", "Security")]
public sealed class IcebergTableDirectoryExt04Tests
{
    [Theory]
    [InlineData("/warehouse/sales.orders", "/warehouse/sales.orders")]
    [InlineData("/warehouse/sales.orders/", "/warehouse/sales.orders")]
    [InlineData("/warehouse/sales.orders/metadata", "/warehouse/sales.orders")]
    [InlineData("/warehouse/sales.orders/metadata/v2.metadata.json", "/warehouse/sales.orders")]
    [InlineData("/orders/metadata/v2.metadata.json", "/orders")]
    [InlineData("/orders", "/orders")]
    public void GetUriTableDirectory_OnlyStripsKnownMetadataFiles(string input, string expected)
    {
        IcebergMetadataReader.GetUriTableDirectory(input).ShouldBe(expected);
    }
}
