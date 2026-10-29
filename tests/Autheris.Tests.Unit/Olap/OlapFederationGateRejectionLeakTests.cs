namespace Autheris.Tests.Unit.Olap;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

/// <summary>ARCH-MNT-08-FU-01: rejection text and inner exception text never reach the response, the audit record or the log.</summary>
public sealed partial class OlapFederationGateTests
{
    private const string Canary = "canary_col_7731";

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_OLAP_SC_01_T_03_Rejection_ResponseBody_EqualsFixedMessage()
    {
        var h = Build(TwoSources, Open("Olap"));

        var outcome = await Run(h, $"SELECT '{Canary}\\' FROM orders");

        outcome.StatusCode.ShouldBe(400);
        outcome.ErrorCode.ShouldBe(OlapSqlRejectedException.Invalid);
        outcome.Error.ShouldBe(new OlapSqlRejectedException(OlapSqlRejectionReason.EscapeStringsProhibited).Message);
        outcome.Error!.ShouldNotContain(Canary);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_OLAP_SC_01_T_04_T_05_T_06_ColumnPolicyRejection_InnerMessage_NotInResponseAuditOrLog()
    {
        var columns = new List<TableColumn>
        {
            new() { ColumnName = "id", DataType = "int" },
            new() { ColumnName = Canary, DataType = "string", IsSensitive = true }
        };
        TableDef Pii(string domain, string table, string connector) => new(
            domain, table, ConnectorId: connector, Columns: columns,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, [Canary] = ColumnAccessLevel.Mask },
            Rules: new Dictionary<string, MaskingRule> { [Canary] = new MaskingRule { RuleType = "REDACT" } });
        var logs = new List<(LogLevel Level, string Message)>();
        var h = Build([Pii("sales", "orders", "sql-a"), Pii("crm", "customers", "sql-b")], Open("Olap"), logger: new ListLogger<OlapStagingService>(logs));

        var outcome = await h.Sut.ExecuteAsync(
            User(), Caller(), new OlapQueryCommand($"SELECT a.id FROM orders a JOIN customers b ON a.{Canary} = b.{Canary}", ["sales.public.orders", "crm.public.customers"]),
            isDevelopment: true, "trace-1", System.Threading.CancellationToken.None);

        outcome.StatusCode.ShouldBe(403);
        outcome.ErrorCode.ShouldBe(OlapSqlRejectedException.ColumnPolicy);
        outcome.Error.ShouldBe(new OlapSqlRejectedException(OlapSqlRejectionReason.ColumnPolicy).Message);
        outcome.Details.ShouldBeNull();
        System.Text.Json.JsonSerializer.Serialize(outcome).ShouldNotContain(Canary);
        h.Records.ShouldAllBe(r => !(r.DetailsJson ?? string.Empty).Contains(Canary));
        logs.ShouldAllBe(l => !l.Message.Contains(Canary));
        logs.ShouldContain(l => l.Message.Contains(OlapSqlRejectedException.ColumnPolicy));
    }
}
