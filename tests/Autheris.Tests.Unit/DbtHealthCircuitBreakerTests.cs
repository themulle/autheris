namespace Autheris.Tests.Unit;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public class DbtHealthCircuitBreakerTests
{
    private readonly DbtHealthCircuitBreaker _circuitBreaker = new();

    private const string PassingRunResults = """
    {
      "metadata": {
        "dbt_schema_version": "https://schemas.getdbt.com/dbt/run-results/v5.json",
        "dbt_version": "1.8.0",
        "generated_at": "2026-09-29T04:30:00Z",
        "elapsed_time": 4.5
      },
      "results": [
        {
          "status": "success",
          "execution_time": 1.2,
          "unique_id": "model.corp_dw.monthly_revenue",
          "failures": 0,
          "message": "OK"
        },
        {
          "status": "pass",
          "execution_time": 0.3,
          "unique_id": "test.corp_dw.not_null_monthly_revenue_account_id.abc123",
          "failures": 0,
          "message": null
        }
      ]
    }
    """;

    private const string FailingRunResults = """
    {
      "metadata": {
        "dbt_schema_version": "https://schemas.getdbt.com/dbt/run-results/v5.json",
        "dbt_version": "1.8.0",
        "generated_at": "2026-09-29T05:00:00Z",
        "elapsed_time": 2.1
      },
      "results": [
        {
          "status": "fail",
          "execution_time": 0.8,
          "unique_id": "test.corp_dw.not_null_monthly_revenue_account_id.abc123",
          "failures": 14,
          "message": "Got 14 results, configured to fail if != 0"
        },
        {
          "status": "warn",
          "execution_time": 0.4,
          "unique_id": "test.corp_dw.unique_customers_email.def456",
          "failures": 1,
          "message": "Got 1 duplicate"
        }
      ]
    }
    """;

    [Fact]
    public async Task GetTableHealthAsync_ReturnsHealthyByDefault()
    {
        var table = new TableIdentifier("corp_dw", "finance", "orders");
        var health = await _circuitBreaker.GetTableHealthAsync(table);

        health.Status.ShouldBe(DbtModelHealthStatus.Healthy);
        health.ActiveFailures.ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordRunResultsAsync_WithPassingResults_MaintainsHealthyStatus()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PassingRunResults));
        var report = await _circuitBreaker.RecordRunResultsAsync(stream);

        report.DbtVersion.ShouldBe("1.8.0");
        report.Results.Count.ShouldBe(2);

        var table = new TableIdentifier("corp_dw", "finance", "monthly_revenue");
        var health = await _circuitBreaker.GetTableHealthAsync(table);
        health.Status.ShouldBe(DbtModelHealthStatus.Healthy);
    }

    [Fact]
    public async Task RecordRunResultsAsync_WithTestFailures_QuarantinesTargetModel()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(FailingRunResults));
        await _circuitBreaker.RecordRunResultsAsync(stream);

        // Model with failing test should be Quarantined
        var tableRevenue = new TableIdentifier("corp_dw", "finance", "monthly_revenue");
        var healthRevenue = await _circuitBreaker.GetTableHealthAsync(tableRevenue);

        healthRevenue.Status.ShouldBe(DbtModelHealthStatus.Quarantined);
        healthRevenue.ActiveFailures.ShouldNotBeEmpty();
        healthRevenue.ActiveFailures[0].TestName.ShouldBe("not_null");
        healthRevenue.ActiveFailures[0].FailedRowsCount.ShouldBe(14);

        // Model with warning test should be Degraded
        var tableCustomers = new TableIdentifier("corp_dw", "sales", "customers");
        var healthCustomers = await _circuitBreaker.GetTableHealthAsync(tableCustomers);

        healthCustomers.Status.ShouldBe(DbtModelHealthStatus.Degraded);
        healthCustomers.ActiveFailures.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ResetTableHealthAsync_RestoresHealthyState()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(FailingRunResults));
        await _circuitBreaker.RecordRunResultsAsync(stream);

        var tableRevenue = new TableIdentifier("corp_dw", "finance", "monthly_revenue");
        var healthBefore = await _circuitBreaker.GetTableHealthAsync(tableRevenue);
        healthBefore.Status.ShouldBe(DbtModelHealthStatus.Quarantined);

        await _circuitBreaker.ResetTableHealthAsync(tableRevenue);

        var healthAfter = await _circuitBreaker.GetTableHealthAsync(tableRevenue);
        healthAfter.Status.ShouldBe(DbtModelHealthStatus.Healthy);
    }

    [Fact]
    public async Task ResetAllAsync_ClearsAllQuarantines()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(FailingRunResults));
        await _circuitBreaker.RecordRunResultsAsync(stream);

        var allBefore = await _circuitBreaker.GetAllHealthStatesAsync();
        allBefore.Count.ShouldBeGreaterThanOrEqualTo(2);

        await _circuitBreaker.ResetAllAsync();

        var allAfter = await _circuitBreaker.GetAllHealthStatesAsync();
        allAfter.ShouldBeEmpty();
    }
}
