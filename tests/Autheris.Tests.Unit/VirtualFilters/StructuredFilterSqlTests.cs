namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 4: SQL of a structured filter for one object, per dialect, over the alias
/// <c>autheris_target</c>.
/// </summary>
public sealed class StructuredFilterSqlTests
{
    private static readonly VirtualFilter David = VirtualFilterModelTests.DavidFilter();
    private static readonly FilterBinding Binding = new() { FilterName = David.Name };

    private static TableMetadata Air1(DatabaseDialect dialect, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", "fms", "air1"),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = "fms", TableName = "air1", SourceType = dialect.ToString() },
        Columns = (columns.Length == 0 ? ["id", "client_id", "ts"] : columns).Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static string Build(DatabaseDialect dialect, VirtualFilter? filter = null, FilterBinding? binding = null) =>
        new StructuredFilterSqlBuilder(Options.Create(new GatewayOptions()))
            .Build(filter ?? David, binding ?? Binding, Air1(dialect), dialect);

    private const string DavidSubquery =
        "FROM [conf].[client] AS [client] INNER JOIN [md].[crane] AS [crane] ON [crane].[serial_number] = [client].[crane_serial_number] WHERE [crane].[is_delivered] IS NULL";

    [Fact]
    public void SqlServer_Exists()
    {
        Build(DatabaseDialect.SqlServer).ShouldBe(
            $"EXISTS (SELECT 1 {DavidSubquery} AND [client].[client_id] = [autheris_target].[client_id])");
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql, "EXISTS (SELECT 1 FROM \"conf\".\"client\" AS \"client\" INNER JOIN \"md\".\"crane\" AS \"crane\" ON \"crane\".\"serial_number\" = \"client\".\"crane_serial_number\" WHERE \"crane\".\"is_delivered\" IS NULL AND \"client\".\"client_id\" = \"autheris_target\".\"client_id\")")]
    public void OtherDialects_UseExists(DatabaseDialect dialect, string expected)
    {
        Build(dialect).ShouldBe(expected);
    }

    [Fact]
    public void SeveralKeyColumns_AlwaysUseExists()
    {
        var twoKeys = David with { KeyColumns = ["client.client_id", "crane.ts"] };

        Build(DatabaseDialect.SqlServer, twoKeys).ShouldBe(
            $"EXISTS (SELECT 1 {DavidSubquery} AND [client].[client_id] = [autheris_target].[client_id] AND [crane].[ts] = [autheris_target].[ts])");
    }

    [Fact]
    public void ValidityWindow_ComparesTheBindingsTimeColumn()
    {
        var window = David with { ValidToColumn = "crane.date_of_delivery" };
        var binding = Binding with { TimeColumn = "ts" };

        Build(DatabaseDialect.SqlServer, window, binding).ShouldBe(
            $"EXISTS (SELECT 1 {DavidSubquery} AND [client].[client_id] = [autheris_target].[client_id] AND ([crane].[date_of_delivery] IS NULL OR [autheris_target].[ts] < [crane].[date_of_delivery]))");

        var both = window with { ValidFromColumn = "crane.date_of_order" };
        Build(DatabaseDialect.SqlServer, both, binding).ShouldEndWith(
            "AND ([crane].[date_of_order] IS NULL OR [autheris_target].[ts] >= [crane].[date_of_order]) AND ([crane].[date_of_delivery] IS NULL OR [autheris_target].[ts] < [crane].[date_of_delivery]))");
    }

    [Fact]
    public void ColumnMap_CorrelatesWithTheMappedTargetColumn()
    {
        var mapped = Binding with { ColumnMap = new Dictionary<string, string> { ["client_id"] = "cid" } };

        Build(DatabaseDialect.SqlServer, binding: mapped).ShouldBe(
            $"EXISTS (SELECT 1 {DavidSubquery} AND [client].[client_id] = [autheris_target].[cid])");
    }

    [Fact]
    public void Conditions_FormatLiteralsSafely()
    {
        var conditions = David with
        {
            Structured = David.Structured! with
            {
                Where =
                [
                    new FilterCondition("crane.status", FilterConditionOperator.Eq, "O'Brien"),
                    new FilterCondition("crane.model", FilterConditionOperator.NotEq, "LTM 1300"),
                    new FilterCondition("crane.weight", FilterConditionOperator.Eq, "42"),
                    new FilterCondition("crane.sold_at", FilterConditionOperator.IsNotNull)
                ]
            }
        };

        Build(DatabaseDialect.SqlServer, conditions).ShouldContain(
            "WHERE [crane].[status] = 'O''Brien' AND [crane].[model] <> 'LTM 1300' AND [crane].[weight] = 42 AND [crane].[sold_at] IS NOT NULL");
    }

    [Fact]
    public void GeneratedSql_PassesThePredicateValidator()
    {
        var sql = Build(DatabaseDialect.SqlServer, David with { ValidToColumn = "crane.date_of_delivery" }, Binding with { TimeColumn = "ts" });

        Should.NotThrow(() => SqlSecurityValidator.ValidatePredicateSql(sql, "MandatoryRowPredicateSql"));
    }

    [Fact]
    public void MandatoryPredicates_MayExceed2000Characters_UpTo8000()
    {
        var predicate = string.Join(" AND ", Enumerable.Repeat($"({Build(DatabaseDialect.SqlServer)})", 12));
        predicate.Length.ShouldBeInRange(2001, 8000);
        var decision = TableAccessDecision.Allowed(Air1(DatabaseDialect.SqlServer).Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        Should.Throw<ArgumentException>(() => SqlSecurityValidator.ValidateRowFilter(decision with { CombinedRowFilterSql = predicate }));
        Should.NotThrow(() => SqlSecurityValidator.ValidateRowFilter(decision.WithMandatoryPredicate(predicate, ["many"])));
    }

    [Fact]
    public void StructuredFilter_StringWithLeadingZero_IsQuotedAsStringLiteral()
    {
        var filter = new VirtualFilter
        {
            Name = "code_filter",
            TenantId = new TenantId("t1"),
            Source = "lwetem_prod",
            KeyColumns = ["client.client_id"],
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("lwetem_prod", "conf", "client"),
                FromAlias = "client",
                Where = [new FilterCondition("client.status_code", FilterConditionOperator.Eq, "01")]
            }
        };

        var sql = Build(DatabaseDialect.SqlServer, filter);
        sql.ShouldContain("[client].[status_code] = '01'");
        sql.ShouldNotContain("[client].[status_code] = 1");
    }
}

