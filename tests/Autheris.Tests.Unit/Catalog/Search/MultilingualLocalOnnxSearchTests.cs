namespace Autheris.Tests.Unit.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Catalog.Relevance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class MultilingualLocalOnnxSearchTests
{
    [Fact]
    public async Task LocalOnnxEmbeddingGenerator_WithValidOnnxAndVocab_RunsInProcessAndEnablesHybridMode()
    {
        var dir = Directory.CreateTempSubdirectory("autheris-onnx-test-");
        try
        {
            var modelPath = Path.Combine(dir.FullName, "multilingual-test.onnx");
            var vocabPath = Path.Combine(dir.FullName, "vocab.txt");

            var vocabLines = Enumerable.Range(0, 128).Select(i => $"tok_{i}").ToArray();
            vocabLines[0] = "[PAD]";
            vocabLines[100] = "[UNK]";
            vocabLines[101] = "[CLS]";
            vocabLines[102] = "[SEP]";
            vocabLines[103] = "[MASK]";
            vocabLines[104] = "orders";
            vocabLines[105] = "customers";
            vocabLines[106] = "kran";
            vocabLines[107] = "standort";
            File.WriteAllBytes(vocabPath, Encoding.UTF8.GetBytes(string.Join('\n', vocabLines) + "\n"));
            var onnxBytes = BuildMinimal384DimEmbeddingOnnxModel(vocabSize: 128, dimensions: 384);
            File.WriteAllBytes(modelPath, onnxBytes);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(onnxBytes));

            using var generator = new LocalOnnxEmbeddingGenerator(
                modelPath,
                sha256,
                dimensions: 384,
                logger: NullLogger<LocalOnnxEmbeddingGenerator>.Instance,
                tokenizerPath: vocabPath);

            generator.IsAvailable.ShouldBeTrue();
            generator.EmbeddingDimensions.ShouldBe(384);

            var single = generator.GenerateNormalizedEmbedding("orders");
            single.Length.ShouldBe(384);
            TensorPrimitives.Norm(single.Span).ShouldBeInRange(0.99f, 1.01f);

            var batch = await generator.GenerateNormalizedEmbeddingsAsync(["orders", "kran"], CancellationToken.None);
            batch.Count.ShouldBe(2);
            batch[0].Length.ShouldBe(384);
            batch[1].Length.ShouldBe(384);

            var table = new TableMetadata
            {
                Identifier = new TableIdentifier("sales", "dbo", "orders"),
                Table = new Table
                {
                    Id = Guid.NewGuid(),
                    SourceName = "sales",
                    SchemaName = "dbo",
                    TableName = "orders",
                    DisplayName = "Orders",
                    IsActive = true
                },
                Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
            };

            var engine = new CatalogSearchEngine(
                generator,
                Options.Create(new CatalogSearchOptions { EmbeddingProvider = CatalogEmbeddingProvider.LocalOnnx }),
                NullLogger<CatalogSearchEngine>.Instance);

            await engine.RebuildIndexAsync([table], CancellationToken.None);
            var hits = await engine.SearchAsync(new CatalogSearchQuery("orders", Mode: CatalogSearchMode.Hybrid), _ => true);

            hits.ShouldHaveSingleItem();
            hits[0].EffectiveMode.ShouldBe("hybrid");
            hits[0].VectorScore.ShouldNotBeNull();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HybridSearch_WithMultilingualEmbeddings_ResolvesCrossLanguageAndRelatedTerms()
    {
        var embedder = new MultilingualSemanticTestEmbedder();
        var engine = new CatalogSearchEngine(
            embedder,
            Options.Create(new CatalogSearchOptions
            {
                EmbeddingProvider = CatalogEmbeddingProvider.LocalOnnx,
                MinVectorSimilarity = 0.25f
            }),
            NullLogger<CatalogSearchEngine>.Instance);

        await engine.RebuildIndexAsync(RelevanceFixture.Tables, CancellationToken.None);

        foreach (var testCase in RelevanceFixture.Cases.Where(c => c.Needs == RelevanceNeed.Embeddings))
        {
            var hits = await engine.SearchAsync(
                new CatalogSearchQuery(testCase.Query, Limit: RelevanceFixture.Tables.Count, Mode: CatalogSearchMode.Hybrid),
                _ => true,
                ct: CancellationToken.None);

            hits.ShouldNotBeEmpty();
            hits[0].EffectiveMode.ShouldBe("hybrid");

            var rankedIds = hits.Select(h => h.TableIdentifier.ToString()).ToList();
            foreach (var expected in testCase.Expected)
            {
                var idx = rankedIds.IndexOf(expected);
                idx.ShouldBeGreaterThanOrEqualTo(0, $"{testCase.Id}: expected '{expected}' for '{testCase.Query}'.");
                (idx + 1).ShouldBeLessThanOrEqualTo(testCase.MaxRank, $"{testCase.Id}: '{expected}' ranked {idx + 1} for '{testCase.Query}'.");
            }
        }
    }

    [Fact]
    public void BuildEmbeddingText_OmitsVerboseColumnDescriptions_ToAvoidTruncationOnWideTables()
    {
        var columns = Enumerable.Range(1, 60)
            .Select(i => new TableColumn
            {
                ColumnName = $"col_{i:00}",
                DataType = "varchar",
                DisplayName = $"Column {i}",
                Description = new string('x', 200) + $" secret_col_desc_{i}"
            })
            .ToList();

        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("fleet", "dbo", "cranes"),
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "fleet",
                SchemaName = "dbo",
                TableName = "cranes",
                DisplayName = "Kranstammdaten",
                Description = "Kranspezifikationen und Traglasten",
                IsActive = true
            },
            Columns = columns
        };

        var text = CatalogSearchDocumentBuilder.BuildEmbeddingText(table);
        text.ShouldContain("cranes");
        text.ShouldContain("Kranstammdaten");
        text.ShouldContain("Kranspezifikationen");
        text.ShouldContain("col_01");
        text.ShouldContain("col_60");
        text.ShouldNotContain("secret_col_desc_");
    }

    [Fact]
    public async Task HybridSearch_OnThreeThousandTables_AnswersWithinLatencyBudget()
    {
        var tables = new List<TableMetadata>(3_000);
        for (int i = 0; i < 3_000; i++)
        {
            tables.Add(new TableMetadata
            {
                Identifier = new TableIdentifier($"dom{i % 10}", "dbo", $"table_{i:0000}"),
                Table = new Table
                {
                    Id = Guid.NewGuid(),
                    SourceName = $"dom{i % 10}",
                    SchemaName = "dbo",
                    TableName = $"table_{i:0000}",
                    DisplayName = $"Table {i}",
                    Description = i == 1500 ? "Kranspezifikationen und GPS-Position" : $"Description for table {i}",
                    IsActive = true
                },
                Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
            });
        }

        var embedder = new MultilingualSemanticTestEmbedder();
        var engine = new CatalogSearchEngine(
            embedder,
            Options.Create(new CatalogSearchOptions { EmbeddingProvider = CatalogEmbeddingProvider.LocalOnnx }),
            NullLogger<CatalogSearchEngine>.Instance);

        await engine.RebuildIndexAsync(tables, CancellationToken.None);

        var sw = Stopwatch.StartNew();
        var hits = await engine.SearchAsync(
            new CatalogSearchQuery("Standort crane", Limit: 10, Mode: CatalogSearchMode.Hybrid),
            _ => true,
            ct: CancellationToken.None);
        sw.Stop();

        hits.ShouldNotBeEmpty();
        hits[0].EffectiveMode.ShouldBe("hybrid");
        sw.ElapsedMilliseconds.ShouldBeLessThan(250);
    }

    private sealed class MultilingualSemanticTestEmbedder : ILocalEmbeddingGenerator
    {
        private static readonly (string[] Tokens, int Dim)[] ConceptAxes =
        [
            (["crane", "kran", "kranspezifikation", "kranspezifikationen", "hoist", "boom"], 0),
            (["standort", "location", "gps", "position", "koordinaten", "coordinates", "telematik", "telematic"], 1),
            (["error", "störung", "stoerung", "fault", "fehler", "alarm", "incident"], 2),
            (["maintenance", "wartung", "service", "inspektion"], 3),
            (["customer", "kunde", "kunden", "auftrag", "order"], 4),
            (["invoice", "rechnung", "rechnungen", "rechnungskopf", "rechnungskoepfe", "billing", "faktura"], 5),
            (["supplier", "lieferant", "lieferanten", "vendor"], 6),
            (["staff", "employee", "employees", "mitarbeiter", "personal"], 7)
        ];

        public int EmbeddingDimensions => 16;
        public string ModelIdentifier => "test-multilingual-e5-small";
        public bool IsAvailable => true;

        public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text)
        {
            var vec = new float[EmbeddingDimensions];
            var lower = (text ?? string.Empty).ToLowerInvariant();
            foreach (var (tokens, dim) in ConceptAxes)
            {
                foreach (var tok in tokens)
                {
                    if (lower.Contains(tok, StringComparison.Ordinal))
                    {
                        vec[dim] += 1f;
                    }
                }
            }

            // Deterministic fallback axis so unrelated texts still have unit length
            vec[15] = 0.05f;
            float norm = TensorPrimitives.Norm(vec);
            TensorPrimitives.Divide(vec, norm, vec);
            return vec;
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var results = new ReadOnlyMemory<float>[texts.Count];
            for (int i = 0; i < texts.Count; i++)
            {
                results[i] = GenerateNormalizedEmbedding(texts[i]);
            }
            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(results);
        }
    }

    /// <summary>
    /// Builds a minimal valid ONNX model (IR v8, opset 17): Gather(weight[vocabSize, dimensions], input_ids) -> [batch, seqLen, dimensions].
    /// </summary>
    private static byte[] BuildMinimal384DimEmbeddingOnnxModel(int vocabSize, int dimensions)
    {
        var weights = new float[vocabSize * dimensions];
        for (int r = 0; r < vocabSize; r++)
        {
            for (int c = 0; c < dimensions; c++)
            {
                weights[(r * dimensions) + c] = ((r + 1) * (c + 1) % 17 + 1) * 0.01f;
            }
        }

        var weightBytes = new byte[weights.Length * sizeof(float)];
        Buffer.BlockCopy(weights, 0, weightBytes, 0, weightBytes.Length);

        var weightTensor = ProtoConcat(
            ProtoVarint(1, vocabSize),
            ProtoVarint(1, dimensions),
            ProtoVarint(2, 1), // FLOAT
            ProtoString(8, "W"),
            ProtoBytes(9, weightBytes));

        var node = ProtoConcat(
            ProtoString(1, "W"),
            ProtoString(1, "input_ids"),
            ProtoString(2, "last_hidden_state"),
            ProtoString(4, "Gather"));

        var inputIdsInfo = ValueInfo("input_ids", 7 /* INT64 */, 2);
        var attentionMaskInfo = ValueInfo("attention_mask", 7 /* INT64 */, 2);
        var outputInfo = ValueInfo("last_hidden_state", 1 /* FLOAT */, 3, lastDim: dimensions);

        var graph = ProtoConcat(
            ProtoBytes(1, node),
            ProtoString(2, "minimal_embed"),
            ProtoBytes(5, weightTensor),
            ProtoBytes(11, inputIdsInfo),
            ProtoBytes(11, attentionMaskInfo),
            ProtoBytes(12, outputInfo));

        var opset = ProtoConcat(ProtoVarint(2, 17));
        return ProtoConcat(
            ProtoVarint(1, 8), // ir_version = 8
            ProtoBytes(8, opset),
            ProtoBytes(7, graph));
    }

    private static byte[] ValueInfo(string name, int elemType, int rank, int? lastDim = null)
    {
        var dims = new List<byte[]>();
        for (int i = 0; i < rank; i++)
        {
            if (i == rank - 1 && lastDim.HasValue)
            {
                dims.Add(ProtoVarint(1, lastDim.Value));
            }
            else
            {
                dims.Add(ProtoString(2, $"d{i}"));
            }
        }

        var shape = ProtoConcat(dims.Select(d => ProtoBytes(1, d)).ToArray());
        var tensorType = ProtoConcat(ProtoVarint(1, elemType), ProtoBytes(2, shape));
        var typeProto = ProtoBytes(1, tensorType);
        return ProtoConcat(ProtoString(1, name), ProtoBytes(2, typeProto));
    }

    private static byte[] ProtoVarint(int field, long value)
    {
        using var ms = new MemoryStream();
        WriteVarint(ms, ((ulong)field << 3) | 0);
        WriteVarint(ms, (ulong)value);
        return ms.ToArray();
    }

    private static byte[] ProtoString(int field, string value) => ProtoBytes(field, Encoding.UTF8.GetBytes(value));

    private static byte[] ProtoBytes(int field, byte[] payload)
    {
        using var ms = new MemoryStream();
        WriteVarint(ms, ((ulong)field << 3) | 2);
        WriteVarint(ms, (ulong)payload.Length);
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }

    private static byte[] ProtoConcat(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts)
        {
            ms.Write(p, 0, p.Length);
        }
        return ms.ToArray();
    }

    private static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }
}
