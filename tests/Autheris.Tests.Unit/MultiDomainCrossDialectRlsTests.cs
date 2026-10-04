using System;
using System.Collections.Generic;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class MultiDomainCrossDialectRlsTests
{
    private readonly RowFilterSqlBuilder _builder = new();

    #region Correlated Subqueries Across All Five Dialects

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "EXISTS (SELECT 1 FROM [dbo].[customers] AS [c] WHERE [c].[id] = [i].[customer_id])")]
    [InlineData(DatabaseDialect.PostgreSql, "EXISTS (SELECT 1 FROM \"dbo\".\"customers\" AS \"c\" WHERE \"c\".\"id\" = \"i\".\"customer_id\")")]
    [InlineData(DatabaseDialect.Databricks, "EXISTS (SELECT 1 FROM `dbo`.`customers` AS `c` WHERE `c`.`id` = `i`.`customer_id`)")]
    [InlineData(DatabaseDialect.Sqlite, "EXISTS (SELECT 1 FROM \"customers\" AS \"c\" WHERE \"c\".\"id\" = \"i\".\"customer_id\")")]
    [InlineData(DatabaseDialect.Oracle, "EXISTS (SELECT 1 FROM \"dbo\".\"customers\" \"c\" WHERE \"c\".\"id\" = \"i\".\"customer_id\")")]
    public void CorrelatedSubquery_BasicExists_ProducesExactDialectSyntax(DatabaseDialect dialect, string expectedSql)
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            TargetTableAlias = "i",
            DependentTable = new TableIdentifier("crm", "dbo", "customers"),
            DependentTableAlias = "c",
            ForeignKeyColumn = "customer_id",
            PrimaryKeyColumn = "id"
        };

        var sql = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, dialect);
        sql.ShouldBe(expectedSql);
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "1")]
    [InlineData(DatabaseDialect.Oracle, "1")]
    [InlineData(DatabaseDialect.PostgreSql, "TRUE")]
    [InlineData(DatabaseDialect.Databricks, "TRUE")]
    [InlineData(DatabaseDialect.Sqlite, "TRUE")]
    public void CorrelatedSubquery_BooleanPredicates_FormatAccuratelyPerDialect(DatabaseDialect dialect, string expectedBool)
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            TargetTableAlias = "t",
            DependentTable = new TableIdentifier("finance", "dbo", "accounts"),
            DependentTableAlias = "a",
            ForeignKeyColumn = "account_id",
            PrimaryKeyColumn = "id",
            SubqueryFilterPredicateJson = "{\"a.is_active\": true}"
        };

        var sql = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, dialect);

        var colRef = dialect == DatabaseDialect.SqlServer
            ? "[a].[is_active]"
            : (dialect == DatabaseDialect.Databricks ? "`a`.`is_active`" : "\"a\".\"is_active\"");

        sql.ShouldContain($"{colRef} = {expectedBool}");
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.Databricks)]
    [InlineData(DatabaseDialect.Sqlite)]
    [InlineData(DatabaseDialect.Oracle)]
    public void CorrelatedSubquery_MultiHopTemporalInterval_GeneratesValidSyntaxForAllDialects(DatabaseDialect dialect)
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            TargetTableAlias = "inv",
            ForeignKeyColumn = "cust_id",
            TargetTemporalColumn = "inv.booking_date",
            DependentTable = new TableIdentifier("erp", "dbo", "cust_registry"),
            DependentTableAlias = "cr",
            PrimaryKeyColumn = "id",
            DependentValidFromColumn = "lease.valid_from",
            DependentValidToColumn = "lease.valid_to",
            AdditionalHops = new List<SubqueryJoinHop>
            {
                new()
                {
                    Table = new TableIdentifier("erp", "dbo", "crane_leases"),
                    TableAlias = "lease",
                    LeftJoinColumn = "cr.id",
                    RightJoinColumn = "lease.cust_id"
                }
            },
            SubqueryFilterPredicateJson = "{\"cr.tier\": \"VIP\"}"
        };

        var sql = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, dialect);

        sql.ShouldStartWith("EXISTS (SELECT 1 FROM ");
        sql.ShouldContain("INNER JOIN ");
        sql.ShouldContain("booking_date");
        sql.ShouldContain("valid_from");
        sql.ShouldContain("IS NULL OR");

        if (dialect == DatabaseDialect.Oracle)
        {
            // Oracle: no AS in table aliases
            sql.ShouldNotContain(" AS ");
        }
        else
        {
            sql.ShouldContain(" AS ");
        }
    }

    #endregion

    #region Cross-Source Virtual Set Filtering

    [Fact]
    public void CrossSourceSetFilter_OddCounts_ChunksAccurately()
    {
        // 7 items with batch limit 3 -> 3 chunks: [3, 3, 1]
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter,
            ColumnName = "customer_id",
            ValueJson = "[\"C-1\", \"C-2\", \"C-3\", \"C-4\", \"C-5\", \"C-6\", \"C-7\"]"
        };

        var sqlSqlServer = AdvancedRlsFilterGenerator.BuildCrossSourceSetFilter(filter, maxBatchSize: 3, DatabaseDialect.SqlServer);
        sqlSqlServer.ShouldBe("(([customer_id] IN ('C-1', 'C-2', 'C-3')) OR ([customer_id] IN ('C-4', 'C-5', 'C-6')) OR ([customer_id] IN ('C-7')))");

        var sqlDatabricks = AdvancedRlsFilterGenerator.BuildCrossSourceSetFilter(filter, maxBatchSize: 3, DatabaseDialect.Databricks);
        sqlDatabricks.ShouldBe("((`customer_id` IN ('C-1', 'C-2', 'C-3')) OR (`customer_id` IN ('C-4', 'C-5', 'C-6')) OR (`customer_id` IN ('C-7')))");

        var sqlPostgres = AdvancedRlsFilterGenerator.BuildCrossSourceSetFilter(filter, maxBatchSize: 3, DatabaseDialect.PostgreSql);
        sqlPostgres.ShouldBe("((\"customer_id\" IN ('C-1', 'C-2', 'C-3')) OR (\"customer_id\" IN ('C-4', 'C-5', 'C-6')) OR (\"customer_id\" IN ('C-7')))");
    }

    [Fact]
    public void CrossSourceSetFilter_EscapesSingleQuotesInValues()
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter,
            ColumnName = "client_name",
            ValueJson = "[\"O'Connor\", \"L'Aura\", \"NormalName\"]"
        };

        var sql = AdvancedRlsFilterGenerator.BuildCrossSourceSetFilter(filter, maxBatchSize: 10, DatabaseDialect.SqlServer);
        sql.ShouldBe("[client_name] IN ('O''Connor', 'L''Aura', 'NormalName')");
    }

    #endregion

    #region RowFilterSqlBuilder Combined Filters & Operators

    [Fact]
    public void BuildCombinedRowFilter_WhenAllowHasUnconstrainedConsent_ShortCircuitsAllowPredicates()
    {
        var allowWithFilter = new Consent
        {
            Effect = ConsentEffect.Allow,
            RowFilters = new[]
            {
                new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueJson = "\"EMEA\"" }
            }
        };

        var allowUnconstrained = new Consent
        {
            Effect = ConsentEffect.Allow,
            RowFilters = Array.Empty<ConsentRowFilter>() // No filters -> Unrestricted!
        };

        // When any allow consent has no filter, row filtering is unconstrained
        var combined = _builder.BuildCombinedRowFilter(
            new[] { allowWithFilter, allowUnconstrained },
            Array.Empty<Consent>(),
            DatabaseDialect.SqlServer);

        combined.ShouldBeNull();
    }

    [Fact]
    public void BuildCombinedRowFilter_MultipleAllowAndMultipleDeny_CombinesWithOrAndNot()
    {
        var allow1 = new Consent
        {
            Effect = ConsentEffect.Allow,
            RowFilters = new[]
            {
                new ConsentRowFilter { ColumnName = "department", Operator = "EQ", ValueJson = "\"Finance\"" }
            }
        };

        var allow2 = new Consent
        {
            Effect = ConsentEffect.Allow,
            RowFilters = new[]
            {
                new ConsentRowFilter { ColumnName = "department", Operator = "EQ", ValueJson = "\"Controlling\"" }
            }
        };

        var deny1 = new Consent
        {
            Effect = ConsentEffect.Deny,
            RowFilters = new[]
            {
                new ConsentRowFilter { ColumnName = "is_confidential", Operator = "EQ", ValueJson = "true" }
            }
        };

        var deny2 = new Consent
        {
            Effect = ConsentEffect.Deny,
            RowFilters = new[]
            {
                new ConsentRowFilter { ColumnName = "amount", Operator = "GT", ValueJson = "1000000" }
            }
        };

        // SQL Server Dialect
        var combinedSql = _builder.BuildCombinedRowFilter(
            new[] { allow1, allow2 },
            new[] { deny1, deny2 },
            DatabaseDialect.SqlServer);

        combinedSql.ShouldNotBeNull();
        // A consents combined with OR: ([department] = 'Finance' OR [department] = 'Controlling')
        combinedSql.ShouldContain("([department] = 'Finance' OR [department] = 'Controlling')");
        // D consents combined with AND NOT ((D1 OR D2))
        combinedSql.ShouldContain("AND NOT (([is_confidential] = 1 OR [amount] > 1000000))");
    }

    [Theory]
    [InlineData("EQ", "\"ACTIVE\"", "[status] = 'ACTIVE'")]
    [InlineData("NEQ", "\"ARCHIVED\"", "[status] <> 'ARCHIVED'")]
    [InlineData("LT", "100", "[status] < 100")]
    [InlineData("GT", "50", "[status] > 50")]
    [InlineData("LTE", "1000", "[status] <= 1000")]
    [InlineData("GTE", "10", "[status] >= 10")]
    [InlineData("LIKE", "\"%CORP%\"", "[status] LIKE '%CORP%'")]
    public void FormatCondition_AllOperators_FormatAccurately(string op, string valJson, string expected)
    {
        var filter = new ConsentRowFilter
        {
            ColumnName = "status",
            Operator = op,
            ValueJson = valJson
        };

        var result = _builder.FormatCondition(filter, DatabaseDialect.SqlServer);
        result.ShouldBe(expected);
    }

    [Fact]
    public void FormatCondition_NullValueHandling_ProducesIsNullAndIsNotNull()
    {
        var filterNullEq = new ConsentRowFilter
        {
            ColumnName = "deleted_at",
            Operator = "EQ",
            ValueJson = "null"
        };
        _builder.FormatCondition(filterNullEq, DatabaseDialect.SqlServer).ShouldBe("[deleted_at] IS NULL");

        var filterNullNeq = new ConsentRowFilter
        {
            ColumnName = "deleted_at",
            Operator = "NEQ",
            ValueJson = "null"
        };
        _builder.FormatCondition(filterNullNeq, DatabaseDialect.SqlServer).ShouldBe("[deleted_at] IS NOT NULL");
    }

    [Fact]
    public void FormatCondition_InOperator_HandlesArrayAndEmptyArray()
    {
        var filterWithValues = new ConsentRowFilter
        {
            ColumnName = "country",
            Operator = "IN",
            ValueJson = "[\"DE\", \"AT\", \"CH\"]"
        };
        _builder.FormatCondition(filterWithValues, DatabaseDialect.SqlServer)
            .ShouldBe("[country] IN ('DE', 'AT', 'CH')");

        var filterEmpty = new ConsentRowFilter
        {
            ColumnName = "country",
            Operator = "IN",
            ValueJson = "[]"
        };
        _builder.FormatCondition(filterEmpty, DatabaseDialect.SqlServer)
            .ShouldBe("1 = 0");
    }

    #endregion
}
