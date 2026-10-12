namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Threading.Tasks;
using Autheris.Infrastructure.AI;
using Shouldly;
using Xunit;

public sealed class LocalEmbeddingGeneratorTests
{
    [Fact]
    public void GenerateNormalizedEmbedding_ProducesUnitLengthVector()
    {
        var generator = new LocalDeterministicEmbeddingGenerator();
        var vec = generator.GenerateNormalizedEmbedding("customers invoices billing orders");

        vec.Length.ShouldBe(generator.EmbeddingDimensions);

        // Prüfe L2-Normierung: sqrt(sum(x^2)) ≈ 1.0
        float sumSquares = 0f;
        var span = vec.Span;
        for (int i = 0; i < span.Length; i++)
        {
            sumSquares += span[i] * span[i];
        }

        MathF.Sqrt(sumSquares).ShouldBeInRange(0.999f, 1.001f);
    }

    [Fact]
    public void GenerateNormalizedEmbedding_EmptyString_ProducesZeroVector()
    {
        var generator = new LocalDeterministicEmbeddingGenerator();
        var vec = generator.GenerateNormalizedEmbedding("");

        vec.Length.ShouldBe(generator.EmbeddingDimensions);
        var span = vec.Span;
        for (int i = 0; i < span.Length; i++)
        {
            span[i].ShouldBe(0f);
        }
    }

    [Fact]
    public async Task GenerateNormalizedEmbeddingsAsync_Batch_ProducesCorrectCount()
    {
        var generator = new LocalDeterministicEmbeddingGenerator();
        var list = new[] { "table1", "table2", "table3" };
        var results = await generator.GenerateNormalizedEmbeddingsAsync(list);

        results.Count.ShouldBe(3);
        results[0].Length.ShouldBe(generator.EmbeddingDimensions);
    }
}
