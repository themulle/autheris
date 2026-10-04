namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DbtTelemetryExposuresTests
{
    private static TableMetadata CreateSampleTable()
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("corp_dw", "finance", "monthly_revenue"),
            Table = new Table { SchemaName = "finance", TableName = "monthly_revenue", DisplayName = "Monthly Revenue" },
            PrimaryKeyColumns = ["account_id"],
            Columns =
            [
                new TableColumn { ColumnName = "account_id", DataType = "integer" },
                new TableColumn { ColumnName = "revenue", DataType = "decimal" }
            ]
        };
    }

    [Fact]
    public async Task InMemoryTelemetryMetricsProvider_RecordsQueriesAndCalculatesP99Latency()
    {
        var provider = new InMemoryTelemetryMetricsProvider();
        var table = new TableIdentifier("corp_dw", "finance", "monthly_revenue");

        // Record queries with varying latencies
        for (int i = 1; i <= 100; i++)
        {
            provider.RecordQueryExecution(table, i, i % 2 == 0 ? "finance-dashboard" : "bi-agent");
        }

        var metrics = await provider.GetTableMetricsAsync(table);

        metrics.MonthlyQueries.ShouldBe(100);
        metrics.P99LatencyMs.ShouldBeGreaterThanOrEqualTo(99.0);
        metrics.TopConsumers.ShouldContain("finance-dashboard");
        metrics.TopConsumers.ShouldContain("bi-agent");
        metrics.GovernanceTier.ShouldBe("Enterprise Gold");
    }

    [Fact]
    public async Task DbtExposurePublisher_WithTelemetryProvider_EnrichesExposuresYamlWithLiveMetrics()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var table = CreateSampleTable();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));

        var telemetry = new InMemoryTelemetryMetricsProvider();
        telemetry.RecordQueryExecution(table.Identifier, 12.4, "powerbi-executive");
        telemetry.RecordQueryExecution(table.Identifier, 14.8, "powerbi-executive");
        telemetry.RecordQueryExecution(table.Identifier, 25.1, "data-science-agent");

        var publisher = new DbtExposurePublisher(repo, NullLogger<DbtExposurePublisher>.Instance, telemetry);
        var yaml = await publisher.GenerateExposuresYamlAsync();

        yaml.ShouldContain("version: 2");
        yaml.ShouldContain("exposures:");
        yaml.ShouldContain("name: \"autheris_gateway_finance_monthly_revenue\"");
        yaml.ShouldContain("depends_on:");
        yaml.ShouldContain("- ref('monthly_revenue')");
        yaml.ShouldContain("meta:");
        yaml.ShouldContain("monthly_queries: 3");
        yaml.ShouldContain("p99_latency_ms:");
        yaml.ShouldContain("top_consumers:");
        yaml.ShouldContain("- \"powerbi-executive\"");
        yaml.ShouldContain("- \"data-science-agent\"");
        yaml.ShouldContain("governance_tier: \"Enterprise Gold\"");
    }
}
