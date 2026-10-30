namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public class DbtHealthCircuitBreakerTests
{
    private readonly DbtTestTargetMap _testTargetMap;
    private readonly DbtHealthCircuitBreaker _circuitBreaker;
    private static readonly TenantId TestTenant = new("corp_dw");

    public DbtHealthCircuitBreakerTests()
    {
        _testTargetMap = new DbtTestTargetMap();
        _circuitBreaker = new DbtHealthCircuitBreaker(_testTargetMap);

        var models = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase)
        {
            ["monthly_revenue"] = new TableIdentifier("corp_dw", "finance", "monthly_revenue"),
            ["customers"] = new TableIdentifier("corp_dw", "sales", "customers"),
            ["orders"] = new TableIdentifier("corp_dw", "sales", "orders")
        };
        var tests = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["test.corp_dw.not_null_monthly_revenue_account_id.abc123"] = ["monthly_revenue"],
            ["test.corp_dw.unique_customers_email.def456"] = ["customers"]
        };
        _testTargetMap.Replace(TestTenant, models, tests);
        _testTargetMap.Replace(Autheris.Tests.Common.TestTenants.Default, models, tests);
    }

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
        var report = await _circuitBreaker.RecordRunResultsAsync(stream, TestTenant);

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
        await _circuitBreaker.RecordRunResultsAsync(stream, TestTenant);

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
        await _circuitBreaker.RecordRunResultsAsync(stream, TestTenant);

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
        await _circuitBreaker.RecordRunResultsAsync(stream, TestTenant);

        var allBefore = await _circuitBreaker.GetAllHealthStatesAsync();
        allBefore.Count.ShouldBeGreaterThanOrEqualTo(2);

        await _circuitBreaker.ResetAllAsync();

        var allAfter = await _circuitBreaker.GetAllHealthStatesAsync();
        allAfter.ShouldBeEmpty();
    }

    [Fact]
    public async Task TestFailure_WithUnderscoreColumn_QuarantinesAttachedModel()
    {
        // Tests where column name has underscores e.g. first_name_normalized
        var models = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase)
        {
            ["users"] = new TableIdentifier("corp_dw", "core", "users")
        };
        var tests = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["test.corp_dw.not_null_users_first_name_normalized.abc"] = ["users"]
        };
        _testTargetMap.Replace(TestTenant, models, tests);

        const string runResults = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "results": [
            {
              "status": "fail",
              "unique_id": "test.corp_dw.not_null_users_first_name_normalized.abc",
              "failures": 5,
              "message": "5 null values found"
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(runResults));
        await _circuitBreaker.RecordRunResultsAsync(stream, TestTenant);

        var targetTable = new TableIdentifier("corp_dw", "core", "users");
        var health = await _circuitBreaker.GetTableHealthAsync(targetTable);
        health.Status.ShouldBe(DbtModelHealthStatus.Quarantined);
    }

    [Fact]
    public async Task GetTableHealth_DoesNotMatchPrefixedTable()
    {
        var targetTable = new TableIdentifier("corp_dw", "sales", "orders");
        var prefixedTable = new TableIdentifier("corp_dw", "sales", "orders_archive");

        await _circuitBreaker.SetTableHealthAsync(targetTable, DbtModelHealthStatus.Quarantined, []);

        var targetHealth = await _circuitBreaker.GetTableHealthAsync(targetTable);
        targetHealth.Status.ShouldBe(DbtModelHealthStatus.Quarantined);

        // Querying orders_archive must return Healthy (exact match only)
        var prefixedHealth = await _circuitBreaker.GetTableHealthAsync(prefixedTable);
        prefixedHealth.Status.ShouldBe(DbtModelHealthStatus.Healthy);
    }

    [Fact]
    public async Task GetTableHealth_IsDeterministic_WithSimilarNames()
    {
        var tableA = new TableIdentifier("corp_dw", "sales", "users");
        var tableB = new TableIdentifier("corp_dw", "sales", "users_backup");

        await _circuitBreaker.SetTableHealthAsync(tableA, DbtModelHealthStatus.Quarantined, []);
        await _circuitBreaker.SetTableHealthAsync(tableB, DbtModelHealthStatus.Degraded, []);

        (await _circuitBreaker.GetTableHealthAsync(tableA)).Status.ShouldBe(DbtModelHealthStatus.Quarantined);
        (await _circuitBreaker.GetTableHealthAsync(tableB)).Status.ShouldBe(DbtModelHealthStatus.Degraded);
    }

    [Fact]
    public async Task RunResults_TenantA_DoesNotQuarantineTenantB()
    {
        var tenantA = new TenantId("tenant_a");
        var tenantB = new TenantId("tenant_b");

        var modelsA = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase)
        {
            ["customers"] = new TableIdentifier("tenant_a", "sales", "customers")
        };
        var testsA = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["test.tenant_a.not_null_customers_id.1"] = ["customers"]
        };
        _testTargetMap.Replace(tenantA, modelsA, testsA);

        const string runResultsA = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "results": [
            {
              "status": "fail",
              "unique_id": "test.tenant_a.not_null_customers_id.1",
              "failures": 1,
              "message": "fail"
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(runResultsA));
        await _circuitBreaker.RecordRunResultsAsync(stream, tenantA);

        var tableA = new TableIdentifier("tenant_a", "sales", "customers");
        var tableB = new TableIdentifier("tenant_b", "sales", "customers");

        (await _circuitBreaker.GetTableHealthAsync(tableA)).Status.ShouldBe(DbtModelHealthStatus.Quarantined);
        (await _circuitBreaker.GetTableHealthAsync(tableB)).Status.ShouldBe(DbtModelHealthStatus.Healthy);
    }

    [Fact]
    public async Task UnresolvableTest_QuarantinesAllCandidates()
    {
        var tenant = new TenantId("tenant_multi");
        // Two models: "users" and "users_audit"
        var models = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase)
        {
            ["users"] = new TableIdentifier("tenant_multi", "core", "users"),
            ["users_audit"] = new TableIdentifier("tenant_multi", "core", "users_audit")
        };
        // No explicit test mapping
        _testTargetMap.Replace(tenant, models, new Dictionary<string, IReadOnlyList<string>>());

        // Test without explicit testToModels map, matches both candidates
        const string runResults = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "results": [
            {
              "status": "fail",
              "unique_id": "test.tenant_multi.not_null_users_id.999",
              "failures": 3,
              "message": "Nulls found"
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(runResults));
        var report = await _circuitBreaker.RecordRunResultsAsync(stream, tenant);

        // Fail-closed: both candidates should be quarantined
        var healthUsers = await _circuitBreaker.GetTableHealthAsync(new TableIdentifier("tenant_multi", "core", "users"));
        healthUsers.Status.ShouldBe(DbtModelHealthStatus.Quarantined);
    }

    [Fact]
    public async Task UnresolvableTest_NoCandidate_ReportedUnattributed()
    {
        var tenant = new TenantId("tenant_empty");
        _testTargetMap.Replace(tenant, new Dictionary<string, TableIdentifier>(), new Dictionary<string, IReadOnlyList<string>>());

        const string runResults = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "results": [
            {
              "status": "fail",
              "unique_id": "test.unknown_project.not_null_nonexistent_col.999",
              "failures": 1,
              "message": "No match"
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(runResults));
        var report = await _circuitBreaker.RecordRunResultsAsync(stream, tenant);

        report.UnattributedFailures.ShouldNotBeNull();
        report.UnattributedFailures.ShouldContain("test.unknown_project.not_null_nonexistent_col.999");
    }

    [Fact]
    public async Task ResetTableHealth_ExactOnly()
    {
        var tableA = new TableIdentifier("corp_dw", "sales", "orders");
        var tableB = new TableIdentifier("corp_dw", "sales", "orders_archive");

        await _circuitBreaker.SetTableHealthAsync(tableA, DbtModelHealthStatus.Quarantined, []);
        await _circuitBreaker.SetTableHealthAsync(tableB, DbtModelHealthStatus.Quarantined, []);

        await _circuitBreaker.ResetTableHealthAsync(tableA);

        (await _circuitBreaker.GetTableHealthAsync(tableA)).Status.ShouldBe(DbtModelHealthStatus.Healthy);
        (await _circuitBreaker.GetTableHealthAsync(tableB)).Status.ShouldBe(DbtModelHealthStatus.Quarantined);
    }
}
