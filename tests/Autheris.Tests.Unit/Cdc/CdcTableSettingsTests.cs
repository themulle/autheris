namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Shouldly;
using Xunit;

public sealed class CdcTableSettingsTests
{
    [Fact]
    public void Resolve_MergesShorthandAndOverrides_TablesWin()
    {
        var o = new MssqlChangeTrackingOptions
        {
            PollingIntervalMilliseconds = 1000,
            BatchSize = 500,
            QueryTimeoutSeconds = 30,
            OnWatermarkExpired = CdcOnWatermarkExpired.Restart,
            TrackedTables = ["fin.dbo.a", "fin.dbo.b"],
            Tables = [new CdcTrackedTableOptions { Table = "FIN.DBO.B", PollingIntervalMilliseconds = 250, BatchSize = 2000, QueryTimeoutSeconds = 10, DataSource = "erp", OnWatermarkExpired = CdcOnWatermarkExpired.Halt }]
        };

        var all = CdcTableSettings.Resolve(o);

        all.Count.ShouldBe(2);
        var a = all.Single(s => s.Table.TableName == "a");
        a.PollInterval.ShouldBe(TimeSpan.FromSeconds(1));
        a.BatchSize.ShouldBe(500);
        a.QueryTimeoutSeconds.ShouldBe(30);
        a.DataSource.ShouldBeNull();
        var b = all.Single(s => string.Equals(s.Table.TableName, "b", StringComparison.OrdinalIgnoreCase));
        b.PollInterval.ShouldBe(TimeSpan.FromMilliseconds(250));
        b.BatchSize.ShouldBe(2000);
        b.QueryTimeoutSeconds.ShouldBe(10);
        b.DataSource.ShouldBe("erp");
        b.OnWatermarkExpired.ShouldBe(CdcOnWatermarkExpired.Halt);
    }

    [Fact]
    public void Resolve_ClampsOutOfRangeValues()
    {
        var s = CdcTableSettings.Resolve(new MssqlChangeTrackingOptions { PollingIntervalMilliseconds = 1, BatchSize = 999_999, QueryTimeoutSeconds = 0, TrackedTables = ["a.dbo.t"] }).Single();

        s.PollInterval.ShouldBe(TimeSpan.FromMilliseconds(250));
        s.BatchSize.ShouldBe(5000);
        s.QueryTimeoutSeconds.ShouldBe(1);
    }

    [Fact]
    public void PartitionKey_IsLowerCase_AndLeaseTtlIsAtLeastFifteenSeconds()
    {
        var s = CdcTableSettings.Resolve(new MssqlChangeTrackingOptions { TrackedTables = ["Fin.Dbo.Orders"] }).Single();

        s.PartitionKey.ShouldBe("cdc:mssql:poll:fin.dbo.orders");
        s.LeaseTtl.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void ParseTrackedTables_HandlesVariousFormats()
    {
        var list = CdcTableSettings.ParseTrackedTables(["finance.dbo.invoices", "hr.employees", "orders", "", "   "]);

        list.Count.ShouldBe(3);
        list[0].ShouldBe(new TableIdentifier("finance", "dbo", "invoices"));
        list[1].ShouldBe(new TableIdentifier("hr", "dbo", "employees"));
        list[2].ShouldBe(new TableIdentifier("default", "dbo", "orders"));
    }

    [Fact]
    public void Find_ReturnsNullForAnUntrackedTable()
    {
        var o = new MssqlChangeTrackingOptions { TrackedTables = ["a.dbo.t"] };

        CdcTableSettings.Find(o, new TableIdentifier("a", "dbo", "t")).ShouldNotBeNull();
        CdcTableSettings.Find(o, new TableIdentifier("a", "dbo", "x")).ShouldBeNull();
    }
}
