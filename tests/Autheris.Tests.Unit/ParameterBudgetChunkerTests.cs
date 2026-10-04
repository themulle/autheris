using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Xunit;

namespace Autheris.Tests.Unit;

public class ParameterBudgetChunkerTests
{
    private readonly ChunkedQueryExecutor _executor = new(defaultChunkSize: 500);

    [Fact]
    public void CalculateEffectiveChunkSize_SqliteSingleKey_RespectsDefaultAndSafetyBuffer()
    {
        // SQLite: max 999 params. Single key (1 col), 0 context params, safety buffer 50
        // Available: 999 - 50 = 949. Min(500 default, 949) = 500
        int chunkSize = _executor.CalculateEffectiveChunkSize(
            keyColumnCount: 1,
            contextParameterCount: 0,
            dialect: DatabaseDialect.Sqlite);

        Assert.Equal(500, chunkSize);
    }

    [Fact]
    public void CalculateEffectiveChunkSize_SqliteCompositeKey_PreventsParameterOverflow()
    {
        // Sonderfall: Composite Key mit 3 Spalten in SQLite (Limit 999) mit 19 RLS-/Kontextparametern
        // Available: 999 - 19 - 50 (safety buffer) = 930
        // Max keys pro Chunk: 930 / 3 = 310 Schlüssel (310 * 3 = 930 Parameter)
        // 930 + 19 = 949 Gesamtparameter <= 999!
        int chunkSize = _executor.CalculateEffectiveChunkSize(
            keyColumnCount: 3,
            contextParameterCount: 19,
            dialect: DatabaseDialect.Sqlite);

        Assert.Equal(310, chunkSize);
        Assert.True(chunkSize * 3 + 19 <= 999);
    }

    [Fact]
    public void CalculateEffectiveChunkSize_SqlServerCompositeKey_UsesHigherParameterBudget()
    {
        // SQL Server: max 2,100 params. Composite Key mit 2 Spalten, 20 RLS-Parametern, SafetyBuffer 50
        // Available: 2100 - 20 - 50 = 2030
        // Max keys: 2030 / 2 = 1015 Schlüssel
        // Bei defaultChunkSize = 500 greift der kleinere Wert 500
        int chunkSize = _executor.CalculateEffectiveChunkSize(
            keyColumnCount: 2,
            contextParameterCount: 20,
            dialect: DatabaseDialect.SqlServer);

        Assert.Equal(500, chunkSize);

        // Bei höherer Default-Größe (z.B. 1500) greift das Budget-Limit:
        var largeExecutor = new ChunkedQueryExecutor(defaultChunkSize: 1500);
        int constrainedSize = largeExecutor.CalculateEffectiveChunkSize(
            keyColumnCount: 2,
            contextParameterCount: 20,
            dialect: DatabaseDialect.SqlServer);

        Assert.Equal(1015, constrainedSize);
        Assert.True(constrainedSize * 2 + 20 <= 2100);
    }

    [Fact]
    public async Task ExecuteGroupedChunkedQuery_WithCompositeKeysAndParameterBudget_ChunksCorrectly()
    {
        // 1.000 Composite Keys à 3 Spalten für SQLite mit 20 RLS-Parametern
        // Chunk-Größe = (999 - 20 - 50) / 3 = 309
        // 1.000 / 309 = 4 Queries (309 + 309 + 309 + 73)
        var budget = new ParameterBudget(
            KeyColumnCount: 3,
            ContextParameterCount: 20,
            Dialect: DatabaseDialect.Sqlite);

        var keys = Enumerable.Range(1, 1000)
            .Select(i => new CompositeKey("TENANT_A", 2026, $"DOC_{i}"))
            .ToList();

        var capturedChunkSizes = new List<int>();
        var lockObj = new object();

        var (result, queriesDispatched) = await _executor.ExecuteGroupedWithBudgetAsync(
            keys,
            (chunk, _) =>
            {
                lock (lockObj)
                {
                    capturedChunkSizes.Add(chunk.Count);
                }
                var dict = chunk.ToDictionary(k => k, k => new List<string> { $"{k}-Child" });
                return Task.FromResult<IReadOnlyDictionary<CompositeKey, List<string>>>(dict);
            },
            budget);

        Assert.Equal(4, queriesDispatched);
        Assert.Equal(1000, result.Count);
        Assert.All(capturedChunkSizes, size => Assert.True(size <= 310));
        // Verify no parameter overflow occurred in any chunk
        Assert.All(capturedChunkSizes, size => Assert.True(size * budget.KeyColumnCount + budget.ContextParameterCount <= 999));
    }

    [Fact]
    public void CalculateEffectiveChunkSize_OracleCompositeKey_Enforces1000ParameterLimit()
    {
        // Oracle: max 1000 params (IN clause and parameter limits).
        // Composite Key with 2 columns, 10 context params, safety buffer 50.
        // Available: 1000 - 10 - 50 = 940.
        // Max keys: 940 / 2 = 470 keys.
        int chunkSize = _executor.CalculateEffectiveChunkSize(
            keyColumnCount: 2,
            contextParameterCount: 10,
            dialect: DatabaseDialect.Oracle);

        Assert.Equal(470, chunkSize);
        Assert.True(chunkSize * 2 + 10 <= 1000);
    }
}
