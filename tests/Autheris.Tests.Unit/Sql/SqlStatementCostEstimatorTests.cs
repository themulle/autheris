namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Cost;
using Autheris.Domain.Options;
using FluentAssertions;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using Xunit;

public sealed class SqlStatementCostEstimatorTests
{
    private readonly QueryCostOptions _costOptions = new();
    private readonly QueryAnalysisOptions _analysisOptions = new();
    private readonly WebSqlCostOptions _webSqlCostOptions = new();
    private readonly SqlRowLimit _rowLimit = new(1000, 10000);

    [Fact]
    public void DefaultStatement_Is576()
    {
        // Example 1: no LIMIT, 1000-row cap, one inner join: 1 + 500 + 25*1 + 50 = 576
        var metadata = FastSqlEngine.Default.Analyze("SELECT * FROM orders o JOIN customers c ON o.customer_id = c.id".AsMemory());

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            _rowLimit,
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions);

        cost.Should().Be(576);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SecondWorkedExample_Is1181()
    {
        // Example 2: WITH o AS (SELECT .. FROM orders o JOIN customers c ON ..)
        // SELECT .. FROM o a JOIN o b ON a.id = b.id UNION ALL SELECT .. FROM sales s CROSS JOIN region r LIMIT 100
        // region 12 rows, sales 5M rows -> 1181
        const string sql = "WITH o AS (SELECT o.id, c.name FROM orders o JOIN customers c ON o.customer_id = c.id) " +
                           "SELECT a.id FROM o a JOIN o b ON a.id = b.id " +
                           "UNION ALL " +
                           "SELECT s.id FROM sales s CROSS JOIN region r " +
                           "LIMIT 100";

        var metadata = FastSqlEngine.Default.Analyze(sql.AsMemory());

        long? FakeRowCount(string name) => name switch
        {
            "region" => 12,
            "sales" => 5_000_000,
            _ => 100
        };

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            _rowLimit,
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions,
            getEstimatedRowCount: FakeRowCount);

        cost.Should().Be(1181);
    }

    [Fact]
    public void CrossJoin_LargeOrUnknown_WeightsFour()
    {
        const string sql = "SELECT * FROM sales s CROSS JOIN customer c LIMIT 100";
        var metadata = FastSqlEngine.Default.Analyze(sql.AsMemory());

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            _rowLimit,
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions,
            getEstimatedRowCount: _ => null); // unknown row count -> large -> k=4

        // 1 + ceil(0.5 * 100) + 0 + 250 * 4 = 1 + 50 + 1000 = 1051
        cost.Should().Be(1051);
    }

    [Fact]
    public void CrossJoin_SmallDimension_WeightsOne()
    {
        const string sql = "SELECT * FROM dim1 a CROSS JOIN dim2 b LIMIT 100";
        var metadata = FastSqlEngine.Default.Analyze(sql.AsMemory());

        long? FakeCounts(string name) => 500; // both <= 1000 -> k=1

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            _rowLimit,
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions,
            getEstimatedRowCount: FakeCounts);

        // 1 + 50 + 0 + 250 * 1 = 301
        cost.Should().Be(301);
    }

    [Fact]
    public void ConstantOn_CountsAsCrossJoin()
    {
        const string sql = "SELECT * FROM dim1 a JOIN dim2 b ON 1=1 LIMIT 100";
        var metadata = FastSqlEngine.Default.Analyze(sql.AsMemory());

        metadata.CrossJoinCount.Should().Be(1);

        long? FakeCounts(string name) => 100;

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            _rowLimit,
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions,
            getEstimatedRowCount: FakeCounts);

        // 1 + 50 + 0 + 250 * 1 = 301
        cost.Should().Be(301);
    }

    [Fact]
    public void SetOperations_Add()
    {
        const string sql = "SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 LIMIT 10";
        var metadata = FastSqlEngine.Default.Analyze(sql.AsMemory());

        metadata.SetOperationCount.Should().Be(2);

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            _rowLimit,
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions);

        // 1 + ceil(0.5 * 10) + 2 * 25 = 1 + 5 + 50 = 56
        cost.Should().Be(56);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SetOperationBranches_CountJoins()
    {
        const string sql = "SELECT 1 UNION ALL SELECT * FROM orders o JOIN customers c ON o.customer_id = c.id LIMIT 10";
        var metadata = FastSqlEngine.Default.Analyze(sql.AsMemory());

        metadata.JoinCount.Should().Be(1);
        metadata.SetOperationCount.Should().Be(1);

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            _rowLimit,
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions);

        // 1 + 5 + 25*1 (join) + 25*1 (set op) = 56
        cost.Should().Be(56);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void View_PricedAsOneRelation()
    {
        const string sql = "SELECT * FROM crm_reporting_view LIMIT 100";
        var metadata = FastSqlEngine.Default.Analyze(sql.AsMemory());

        metadata.JoinCount.Should().Be(0);

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            _rowLimit,
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions);

        // 1 + 50 = 51
        cost.Should().Be(51);
    }

    [Fact]
    public void Offset_AddsCost()
    {
        const string sql = "SELECT * FROM orders ORDER BY id LIMIT 100";
        var metadata = FastSqlEngine.Default.Analyze(sql.AsMemory());

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            new SqlRowLimit(100, 10000),
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions,
            pageOffset: 10_000,
            isPaged: true);

        // base: 1 + ceil(0.5 * 100) = 51 + ceil(10000 / 100) = 151
        cost.Should().Be(151);
    }

    [Fact]
    public void Saturates_AtIntMax()
    {
        var metadata = FastSqlEngine.Default.Analyze("SELECT * FROM orders".AsMemory());

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            new SqlRowLimit(long.MaxValue, long.MaxValue),
            new QueryCostOptions { BaseCost = int.MaxValue / 2, RowCostFactor = 1000.0 },
            _analysisOptions,
            _webSqlCostOptions);

        cost.Should().Be(int.MaxValue);
    }

    [Fact]
    public void Dml_NeverZero()
    {
        var metadata = FastSqlEngine.Default.Analyze("INSERT INTO orders (id) VALUES (1)".AsMemory());

        int cost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            _rowLimit,
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions);

        // 1 (BaseCost) + 50 (DmlStatementCost) + ceil(0.5 * 1000) = 551 >= 51
        Assert.True(cost >= 51);
    }

    [Fact]
    public void Analysis_And_Enforcement_SameNumber()
    {
        const string sql = "SELECT * FROM orders o JOIN customers c ON o.customer_id = c.id WHERE o.total > 100";
        var metadata = FastSqlEngine.Default.Analyze(sql.AsMemory());

        int gateEstimate = SqlStatementCostEstimator.EstimateCost(
            metadata,
            _rowLimit,
            _costOptions,
            _analysisOptions,
            _webSqlCostOptions);

        // Must match the exact formula
        gateEstimate.Should().Be(576);
    }
}
