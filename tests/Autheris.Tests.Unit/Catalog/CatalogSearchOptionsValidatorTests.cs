namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Catalog.Search;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public sealed class CatalogSearchOptionsValidatorTests
{
    private readonly CatalogSearchOptionsValidator _validator = new();

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RefreshSeconds_1To4_Rejected(int refreshSeconds)
    {
        var options = new CatalogSearchOptions
        {
            Enabled = true,
            EmbeddingProvider = CatalogEmbeddingProvider.None,
            RefreshSeconds = refreshSeconds
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    public void RefreshSeconds_0And5_Accepted(int refreshSeconds)
    {
        var options = new CatalogSearchOptions
        {
            Enabled = true,
            EmbeddingProvider = CatalogEmbeddingProvider.None,
            RefreshSeconds = refreshSeconds
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void LocalOnnx_FailsStartup_WithExplicitMessage()
    {
        var options = new CatalogSearchOptions
        {
            Enabled = true,
            EmbeddingProvider = CatalogEmbeddingProvider.LocalOnnx,
            ModelPath = "/models/model.onnx",
            TokenizerPath = "/models/tokenizer.json",
            ModelSha256 = new string('a', 64),
            RefreshSeconds = 15
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotBeNull();
        result.FailureMessage!.ShouldContain("LocalOnnx embedding provider is not available in this build.");
    }
}
