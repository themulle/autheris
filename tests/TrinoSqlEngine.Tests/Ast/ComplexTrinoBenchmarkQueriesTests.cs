namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;
using Xunit;

/// <summary>
/// Emission of complex Trino statements per dialect. The tenant predicate of the tables (CTEs, joins, EXISTS, set operations) is
/// asserted by the governed compile tests (<c>TypedInjectionTests</c>, <c>CteShadowingTests</c>, <c>TenantPredicateStructureTests</c>).
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class ComplexTrinoBenchmarkQueriesTests
{
    [Fact]
    public void Statement1_ChainedCtes_WithAggregationJoinAndPaging()
    {
        string sql = @"
            WITH dept_summary AS (
                SELECT department, COUNT(*) AS invoice_count, AVG(amount) AS avg_amount
                FROM invoices
                GROUP BY department
                HAVING COUNT(*) > 5
            ),
            high_value_invoices AS (
                SELECT i.id, i.department, i.vendor, i.amount
                FROM invoices AS i
                JOIN dept_summary AS d ON i.department = d.department
                WHERE i.amount > d.avg_amount
            )
            SELECT h.department, h.vendor, h.amount
            FROM high_value_invoices AS h
            ORDER BY h.amount DESC
            OFFSET 0 LIMIT 10";

        // 1. PostgreSQL
        string pgSql = AstEmit.Inline(sql, TargetSqlDialect.PostgreSql);

        Assert.Contains("WITH \"dept_summary\" AS (", pgSql);
        Assert.Contains("\"high_value_invoices\" AS (", pgSql);
        Assert.Contains("\"invoices\"", pgSql);
        Assert.Contains("GROUP BY \"department\"", pgSql);
        Assert.Contains("HAVING COUNT(*) > 5", pgSql);
        Assert.Contains("LIMIT 10 OFFSET 0", pgSql);

        // 2. SQL Server (T-SQL)
        string msSql = AstEmit.Inline(sql, TargetSqlDialect.SqlServer);

        Assert.Contains("WITH [dept_summary] AS (", msSql);
        Assert.Contains("[high_value_invoices] AS (", msSql);
        Assert.Contains("[invoices]", msSql);
        Assert.Contains("GROUP BY [department]", msSql);
        Assert.Contains("HAVING COUNT(*) > 5", msSql);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY", msSql);
    }

    [Fact]
    public void Statement2_CorrelatedScalarSubquery_AndInSubqueryWithHaving()
    {
        string sql = @"
            SELECT c.id, c.name, c.company,
                   (
                       SELECT SUM(o.total_amount) 
                       FROM orders AS o 
                       WHERE o.customer_id = c.id
                   ) AS total_revenue
            FROM customers AS c
            WHERE c.id IN (
                SELECT o.customer_id
                FROM orders AS o
                GROUP BY o.customer_id
                HAVING COUNT(o.id) >= 3
            )
            ORDER BY total_revenue DESC";

        // 1. PostgreSQL
        string pgSql = AstEmit.Inline(sql, TargetSqlDialect.PostgreSql);

        Assert.Contains("\"customers\"", pgSql);
        Assert.Contains("\"orders\"", pgSql);
        Assert.Contains("SUM(\"o\".\"total_amount\")", pgSql);
        Assert.Contains("\"o\".\"customer_id\" = \"c\".\"id\"", pgSql);
        Assert.Contains("GROUP BY \"o\".\"customer_id\"", pgSql);
        Assert.Contains("HAVING COUNT(\"o\".\"id\") >= 3", pgSql);

        // 2. SQL Server (T-SQL)
        string msSql = AstEmit.Inline(sql, TargetSqlDialect.SqlServer);

        Assert.Contains("[customers]", msSql);
        Assert.Contains("[orders]", msSql);
        Assert.Contains("SUM([o].[total_amount])", msSql);
        Assert.Contains("[o].[customer_id] = [c].[id]", msSql);
        Assert.Contains("GROUP BY [o].[customer_id]", msSql);
        Assert.Contains("HAVING COUNT([o].[id]) >= 3", msSql);
    }

    [Fact]
    public void Statement3_SearchedCase_WithNestedExistsAndNotExists()
    {
        string sql = @"
            SELECT o.id, o.total_amount,
                   CASE 
                       WHEN o.total_amount >= 1000 THEN 'PLATINUM'
                       WHEN EXISTS (
                           SELECT 1 
                           FROM finance_items AS f 
                           WHERE f.parent_id = o.id AND f.price > 250
                       ) THEN 'GOLD'
                       ELSE 'STANDARD'
                   END AS priority_tier
            FROM orders AS o
            JOIN customers AS c ON o.customer_id = c.id
            WHERE o.status = 'COMPLETED'
              AND NOT EXISTS (
                  SELECT 1 
                  FROM shipments AS s 
                  WHERE s.order_id = o.id AND s.status = 'RETURNED'
              )";

        // 1. PostgreSQL
        string pgSql = AstEmit.Inline(sql, TargetSqlDialect.PostgreSql);

        Assert.Contains("CASE", pgSql);
        Assert.Contains("WHEN \"o\".\"total_amount\" >= 1000 THEN 'PLATINUM'", pgSql);
        Assert.Contains("EXISTS (", pgSql);
        Assert.Contains("\"finance_items\"", pgSql);
        Assert.Contains("\"shipments\"", pgSql);
        Assert.Contains("NOT (EXISTS (", pgSql);

        // 2. SQL Server (T-SQL)
        string msSql = AstEmit.Inline(sql, TargetSqlDialect.SqlServer);

        Assert.Contains("CASE", msSql);
        Assert.Contains("N'PLATINUM'", msSql);
        Assert.Contains("EXISTS (", msSql);
        Assert.Contains("[finance_items]", msSql);
        Assert.Contains("[shipments]", msSql);
        Assert.Contains("NOT (EXISTS (", msSql);
    }

    [Fact]
    public void Statement4_MultiWayJoinPipeline_MixedInnerAndLeftJoins()
    {
        string sql = @"
            SELECT o.id AS order_id, 
                   c.name AS customer_name, 
                   s.status AS shipment_status, 
                   f.product_name, 
                   f.price
            FROM orders AS o
            JOIN customers AS c ON o.customer_id = c.id
            LEFT JOIN shipments AS s ON s.order_id = o.id
            LEFT JOIN finance_items AS f ON f.parent_id = o.id
            WHERE o.total_amount > 100
              AND (s.status IS NULL OR s.status <> 'CANCELLED')
            ORDER BY o.id ASC, f.price DESC";

        // 1. PostgreSQL
        string pgSql = AstEmit.Inline(sql, TargetSqlDialect.PostgreSql);

        Assert.Contains("INNER JOIN", pgSql);
        Assert.Contains("LEFT OUTER JOIN", pgSql);
        Assert.Contains("\"orders\"", pgSql);
        Assert.Contains("\"customers\"", pgSql);
        Assert.Contains("\"shipments\"", pgSql);
        Assert.Contains("\"finance_items\"", pgSql);
        Assert.Contains("\"s\".\"status\" IS NULL OR \"s\".\"status\" <> 'CANCELLED'", pgSql);

        // 2. SQL Server (T-SQL)
        string msSql = AstEmit.Inline(sql, TargetSqlDialect.SqlServer);

        Assert.Contains("INNER JOIN", msSql);
        Assert.Contains("LEFT OUTER JOIN", msSql);
        Assert.Contains("[orders]", msSql);
        Assert.Contains("[customers]", msSql);
        Assert.Contains("[shipments]", msSql);
        Assert.Contains("[finance_items]", msSql);
        Assert.Contains("[s].[status] IS NULL OR [s].[status] <> N'CANCELLED'", msSql);
    }

    [Fact]
    public void Statement5_SetOperations_UnionAllWithAggregation()
    {
        string sql = @"
            SELECT c.id, c.name, 'HIGH_VOLUME' AS segment
            FROM customers AS c
            JOIN orders AS o ON c.id = o.customer_id
            GROUP BY c.id, c.name
            HAVING SUM(o.total_amount) > 10000
            UNION ALL
            SELECT c.id, c.name, 'REPEAT_BUYER' AS segment
            FROM customers AS c
            JOIN orders AS o ON c.id = o.customer_id
            GROUP BY c.id, c.name
            HAVING COUNT(o.id) >= 5";

        // 1. PostgreSQL
        string pgSql = AstEmit.Inline(sql, TargetSqlDialect.PostgreSql);

        Assert.Contains("UNION ALL", pgSql);
        Assert.Contains("\"customers\"", pgSql);
        Assert.Contains("\"orders\"", pgSql);
        Assert.Contains("GROUP BY \"c\".\"id\", \"c\".\"name\"", pgSql);
        Assert.Contains("HAVING SUM(\"o\".\"total_amount\") > 10000", pgSql);
        Assert.Contains("HAVING COUNT(\"o\".\"id\") >= 5", pgSql);

        // 2. SQL Server (T-SQL)
        string msSql = AstEmit.Inline(sql, TargetSqlDialect.SqlServer);

        Assert.Contains("UNION ALL", msSql);
        Assert.Contains("[customers]", msSql);
        Assert.Contains("[orders]", msSql);
        Assert.Contains("GROUP BY [c].[id], [c].[name]", msSql);
        Assert.Contains("HAVING SUM([o].[total_amount]) > 10000", msSql);
        Assert.Contains("HAVING COUNT([o].[id]) >= 5", msSql);
    }
}
