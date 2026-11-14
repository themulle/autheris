namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class LocalOnnxEmbeddingGeneratorTests
{
    [Fact]
    public void IsAvailable_ReturnsFalse_WhenRuntimeAbsent()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var bytes = "dummy-onnx-model-payload"u8.ToArray();
            File.WriteAllBytes(tempFile, bytes);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

            var generator = new LocalOnnxEmbeddingGenerator(
                tempFile,
                sha256,
                dimensions: 384,
                logger: NullLogger<LocalOnnxEmbeddingGenerator>.Instance);

            LocalOnnxEmbeddingGenerator.ValidateModelFileIntegrity(tempFile, sha256).ShouldBeTrue();
            generator.IsAvailable.ShouldBeFalse();
            generator.GenerateNormalizedEmbedding("orders").IsEmpty.ShouldBeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task EmptyVector_IsNotIndexed()
    {
        var tableId = new TableIdentifier("sales", "public", "orders");
        var index = new VectorSearchIndex(
        [
            (tableId, ReadOnlyMemory<float>.Empty),
            (new TableIdentifier("sales", "public", "customers"), new float[] { 1f, 0f, 0f })
        ]);

        index.Count.ShouldBe(1);

        var table = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "public",
                TableName = "orders",
                DisplayName = "Orders",
                IsActive = true
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var generator = new LocalOnnxEmbeddingGenerator(
            modelPath: null,
            expectedSha256: null,
            dimensions: 3,
            logger: NullLogger<LocalOnnxEmbeddingGenerator>.Instance);

        var engine = new CatalogSearchEngine(
            generator,
            Options.Create(new CatalogSearchOptions
            {
                Enabled = true,
                EmbeddingProvider = CatalogEmbeddingProvider.LocalOnnx
            }),
            NullLogger<CatalogSearchEngine>.Instance);

        await engine.RebuildIndexAsync([table], CancellationToken.None);

        var hits = await engine.SearchAsync(
            new CatalogSearchQuery("orders", Mode: CatalogSearchMode.Hybrid),
            _ => true,
            CancellationToken.None);

        hits.Count.ShouldBe(1);
        hits[0].EffectiveMode.ShouldBe("keyword");
        hits[0].VectorScore.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TokenizerPath_WithTraversal_FailsValidationAndLogsError()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var bytes = "dummy-onnx-model-payload"u8.ToArray();
            File.WriteAllBytes(tempFile, bytes);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var logger = new CapturingLogger<LocalOnnxEmbeddingGenerator>();

            var generator = new LocalOnnxEmbeddingGenerator(
                tempFile,
                sha256,
                dimensions: 384,
                logger: logger,
                tokenizerPath: "../escape/tokenizer.json");

            generator.IsValidConfiguration.ShouldBeFalse();
            generator.IsAvailable.ShouldBeFalse();
            logger.Entries.ShouldContain(e =>
                e.Level == LogLevel.Error
                && e.Message.Contains("tokenizer", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
