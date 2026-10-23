using System.Text.Json;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Seed;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Autheris.Tests.Parity.Meta;

[Trait("Category", "Parity")]
public sealed class ParitySeedAndResultsTests : ParityTestBase
{
    [Fact]
    public void ParitySeed_ExpectedValuesMatchRows()
    {
        var rows = ParitySeed.Sales;
        var amounts = rows.Where(r => r.Amount is not null).Select(r => r.Amount!.Value).ToList();

        rows.Count.ShouldBe(ParityExpected.TotalRows);
        rows.Select(r => r.Id).Distinct().Count().ShouldBe(rows.Count);
        amounts.Count.ShouldBe(ParityExpected.NonNullAmountRows);
        amounts.Sum().ShouldBe(ParityExpected.SumAmount);
        amounts.Min().ShouldBe(ParityExpected.MinAmount);
        amounts.Max().ShouldBe(ParityExpected.MaxAmount);
        Math.Abs(amounts.Average() - ParityExpected.AvgAmount).ShouldBeLessThan(1e-9m);
        rows.Sum(r => r.Qty ?? 0).ShouldBe(ParityExpected.SumQty);
        rows.Select(r => r.Product).Distinct().Count().ShouldBe(ParityExpected.DistinctProducts);

        rows.Where(r => r.Region is not null).GroupBy(r => r.Region!).ToDictionary(g => g.Key, g => g.Count()).ShouldBe(ParityExpected.RowsByRegion, ignoreOrder: true);
        rows.Where(r => r.Region is not null && r.Amount is not null).GroupBy(r => r.Region!).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount!.Value))
            .ShouldBe(ParityExpected.SumAmountByRegion, ignoreOrder: true);
        rows.Count(r => r.Region is null).ShouldBe(ParityExpected.RowsWithNullRegion);
        rows.Where(r => r.Region is null).Sum(r => r.Amount ?? 0m).ShouldBe(ParityExpected.SumAmountNullRegion);
        rows.GroupBy(r => r.Product).ToDictionary(g => g.Key, g => g.Count()).ShouldBe(ParityExpected.RowsByProduct, ignoreOrder: true);
        rows.GroupBy(r => r.Product).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount ?? 0m)).ShouldBe(ParityExpected.SumAmountByProduct, ignoreOrder: true);
        rows.Where(r => r.Region == "Empty").ShouldAllBe(r => r.Amount == null && r.Qty == null);
    }

    [Fact]
    public void ParitySeed_SqliteFileMatchesRows()
    {
        using var dir = new Autheris.Tests.Common.TempDirectory();
        var path = Path.Combine(dir.Path, "seed.db");

        ParitySeed.Create(path);

        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), SUM(amount) FROM parity_sales";
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetInt32(0).ShouldBe(ParityExpected.TotalRows);
        ((decimal)reader.GetDouble(1)).ShouldBe(ParityExpected.SumAmount);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void ParityResults_WritesOneLinePerCell()
    {
        using var dir = new Autheris.Tests.Common.TempDirectory();
        var writer = new ParityResultsWriter(dir.Path);

        writer.Record(new ParityResult("agg.WebSql.count.basic", "agg", "WebSql", "count", "sqlite", ParityOutcome.Pass, null, "20261023"));
        writer.Record(new ParityResult("agg.GraphQl.count.basic", "agg", "GraphQl", "count", "sqlite", ParityOutcome.Gap, "PARITY-GAP-001", "20261023"));

        var lines = File.ReadAllLines(writer.FilePath);
        lines.Length.ShouldBe(2);
        using var second = JsonDocument.Parse(lines[1]);
        second.RootElement.GetProperty("cellId").GetString().ShouldBe("agg.GraphQl.count.basic");
        second.RootElement.GetProperty("outcome").GetString().ShouldBe("gap");
        second.RootElement.GetProperty("gapId").GetString().ShouldBe("PARITY-GAP-001");
        second.RootElement.GetProperty("seed").GetString().ShouldBe("20261023");
        lines[0].ShouldNotContain("X-Test");
    }

    [Fact]
    public void ParityResults_RejectsInvalidCellId()
    {
        using var dir = new Autheris.Tests.Common.TempDirectory();
        var writer = new ParityResultsWriter(dir.Path);

        Should.Throw<ArgumentException>(() => writer.Record(new ParityResult("bad id|x", "b", "c", "d", "sqlite", ParityOutcome.Pass, null, "1")));
        File.Exists(writer.FilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task ParityCell_RecordsPassGapPendingAndFail()
    {
        using var dir = new Autheris.Tests.Common.TempDirectory();
        var writer = new ParityResultsWriter(dir.Path);

        await ParityCell.RunAsync(new ParityCellInfo("b", "WebSql", "cap", "pass"), () => Task.CompletedTask, writer);
        await Should.ThrowAsync<KnownGapObservedException>(() =>
            ParityCell.RunAsync(new ParityCellInfo("b", "WebSql", "cap", "gap", GapId: "PARITY-GAP-001"), () => throw new InvalidOperationException("x"), writer));
        await Should.ThrowAsync<PendingCellException>(() =>
            ParityCell.RunAsync(new ParityCellInfo("b", "Mcp", "cap", "pending"), () => throw new PendingCellException("Mcp", "cap"), writer));
        await Should.ThrowAsync<InvalidOperationException>(() =>
            ParityCell.RunAsync(new ParityCellInfo("b", "WebSql", "cap", "fail"), () => throw new InvalidOperationException("x"), writer));

        File.ReadAllLines(writer.FilePath)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("outcome").GetString())
            .ShouldBe(["pass", "gap", "pending", "fail"]);
    }
}
