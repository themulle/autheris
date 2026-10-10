namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class VectorSearchIndexTests
{
    [Fact]
    public void Search_EmptyIndex_ReturnsEmptyList()
    {
        var index = new VectorSearchIndex([]);
        var results = index.Search(new float[384]);
        results.ShouldBeEmpty();
    }

    [Fact]
    public void Search_RanksByDotProduct_Accurately()
    {
        var table1 = new TableIdentifier("sales", "public", "revenue");
        var table2 = new TableIdentifier("crm", "public", "customers");

        // Erstelle zwei normierte Vektoren
        var vec1 = new float[4] { 1f, 0f, 0f, 0f };
        var vec2 = new float[4] { 0f, 1f, 0f, 0f };

        var index = new VectorSearchIndex(new List<(TableIdentifier, ReadOnlyMemory<float>)>
        {
            (table1, vec1),
            (table2, vec2)
        });

        // Query genau in Richtung von vec1
        var query = new float[4] { 0.9f, 0.1f, 0f, 0f };
        var results = index.Search(query, minScore: 0.1f, topK: 5);

        results.Count.ShouldBe(2);
        results[0].TableId.ShouldBe(table1);
        results[0].Score.ShouldBeGreaterThan(0.85);
        results[1].TableId.ShouldBe(table2);
        results[1].Score.ShouldBeLessThan(0.2);
    }

    [Fact]
    public void Search_FiltersOut_BelowMinScore()
    {
        var table1 = new TableIdentifier("sales", "public", "orders");
        var vec1 = new float[4] { 0.2f, 0f, 0f, 0f };

        var index = new VectorSearchIndex([(table1, vec1)]);

        // Query orthogonal (Kosinus = 0.0, unter minScore 0.5)
        var query = new float[4] { 0f, 1f, 0f, 0f };
        var results = index.Search(query, minScore: 0.5f, topK: 5);

        results.ShouldBeEmpty();
    }
}
