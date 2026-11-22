namespace Autheris.Tests.Unit.Jobs;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Runtime.Jobs.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

public sealed class AsyncQueryJobCsvTests
{
    [Fact]
    public async Task WriteCsvResultAsync_FormatsNumbersAndDatesWithInvariantCulture_UnderCommaDecimalCulture()
    {
        using var tempDir = new TempDirectory();
        var filePath = Path.Combine(tempDir.Path, "result.csv");

        var result = new GovernedSqlResult(
            OriginalSql: "SELECT amount, ratio, created_on, formula FROM t",
            Columns: ["amount", "ratio", "created_on", "formula"],
            Rows:
            [
                new Dictionary<string, object?>
                {
                    ["amount"] = -12.34m,
                    ["ratio"] = 3.5d,
                    ["created_on"] = new DateOnly(2026, 11, 21),
                    ["formula"] = "=SUM(1,2)"
                }
            ],
            RowCount: 1,
            ElapsedMilliseconds: 5);

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var german = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;

            await AsyncQueryJobBackgroundWorker.WriteCsvResultAsync(filePath, result, CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        var lines = await File.ReadAllLinesAsync(filePath);
        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("amount,ratio,created_on,formula");
        lines[1].ShouldBe("-12.34,3.5,11/21/2026,\"'=SUM(1,2)\"");
    }
}
