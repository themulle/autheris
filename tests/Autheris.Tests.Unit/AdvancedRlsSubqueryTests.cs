using System;
using System.Collections.Generic;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class AdvancedRlsSubqueryTests
{
    [Fact]
    public void SingleSource_CorrelatedSubquery_GeneratesExpectedExistsSql()
    {
        // Arrange
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            TargetTableAlias = "i",
            DependentTable = new TableIdentifier("erp", "dbo", "customers"),
            DependentTableAlias = "c",
            ForeignKeyColumn = "customer_id",
            PrimaryKeyColumn = "id",
            SubqueryFilterPredicateJson = "{\"c.country\": \"CH\"}"
        };

        // Act
        var sql = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, DatabaseDialect.SqlServer);

        // Assert
        sql.ShouldBe("EXISTS (SELECT 1 FROM [dbo].[customers] AS [c] WHERE [c].[id] = [i].[customer_id] AND [c].[country] = 'CH')");
    }

    [Fact]
    public void CraneOwnershipScenario_MultiHopWithTemporalFilter_GeneratesExactPredicate()
    {
        // Scenario: "Ein Benutzer darf nur Rechnungen von Kunden aus der Schweiz (CH) einsehen,
        // und zwar ausschließlich für Zeiträume, in denen der jeweilige Kunde Besitzer eines Krans war."
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            TargetTableAlias = "i",
            ForeignKeyColumn = "customer_id",
            TargetTemporalColumn = "i.invoice_date",
            DependentTable = new TableIdentifier("erp", "dbo", "customers"),
            DependentTableAlias = "c",
            PrimaryKeyColumn = "id",
            AdditionalHops = new List<SubqueryJoinHop>
            {
                new()
                {
                    Table = new TableIdentifier("erp", "dbo", "asset_ownership"),
                    TableAlias = "ao",
                    LeftJoinColumn = "c.id",
                    RightJoinColumn = "ao.customer_id"
                },
                new()
                {
                    Table = new TableIdentifier("erp", "dbo", "assets"),
                    TableAlias = "a",
                    LeftJoinColumn = "ao.asset_id",
                    RightJoinColumn = "a.id"
                }
            },
            SubqueryFilterPredicateJson = "{\"c.country\": \"CH\", \"a.asset_type\": \"CRANE\"}",
            DependentValidFromColumn = "ao.valid_from",
            DependentValidToColumn = "ao.valid_to"
        };

        // Act - SQL Server
        var sqlServer = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, DatabaseDialect.SqlServer);

        // Assert - SQL Server
        sqlServer.ShouldBe(
            "EXISTS (SELECT 1 FROM [dbo].[customers] AS [c] " +
            "INNER JOIN [dbo].[asset_ownership] AS [ao] ON [c].[id] = [ao].[customer_id] " +
            "INNER JOIN [dbo].[assets] AS [a] ON [ao].[asset_id] = [a].[id] " +
            "WHERE [c].[id] = [i].[customer_id] " +
            "AND [c].[country] = 'CH' AND [a].[asset_type] = 'CRANE' " +
            "AND [i].[invoice_date] >= [ao].[valid_from] " +
            "AND ([ao].[valid_to] IS NULL OR [i].[invoice_date] < [ao].[valid_to]))");

        // Act - PostgreSQL
        var sqlPostgres = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, DatabaseDialect.PostgreSql);

        // Assert - PostgreSQL
        sqlPostgres.ShouldBe(
            "EXISTS (SELECT 1 FROM \"dbo\".\"customers\" AS \"c\" " +
            "INNER JOIN \"dbo\".\"asset_ownership\" AS \"ao\" ON \"c\".\"id\" = \"ao\".\"customer_id\" " +
            "INNER JOIN \"dbo\".\"assets\" AS \"a\" ON \"ao\".\"asset_id\" = \"a\".\"id\" " +
            "WHERE \"c\".\"id\" = \"i\".\"customer_id\" " +
            "AND \"c\".\"country\" = 'CH' AND \"a\".\"asset_type\" = 'CRANE' " +
            "AND \"i\".\"invoice_date\" >= \"ao\".\"valid_from\" " +
            "AND (\"ao\".\"valid_to\" IS NULL OR \"i\".\"invoice_date\" < \"ao\".\"valid_to\"))");
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "[dbo].[customers]", "[id]")]
    [InlineData(DatabaseDialect.PostgreSql, "\"dbo\".\"customers\"", "\"id\"")]
    [InlineData(DatabaseDialect.Sqlite, "\"customers\"", "\"id\"")]
    [InlineData(DatabaseDialect.Databricks, "`dbo`.`customers`", "`id`")]
    [InlineData(DatabaseDialect.Oracle, "\"dbo\".\"customers\"", "\"id\"")]
    public void DialectQuoting_AppliesCorrectDelimiters(DatabaseDialect dialect, string expectedTable, string expectedCol)
    {
        var table = new TableIdentifier("crm", "dbo", "customers");
        AdvancedRlsFilterGenerator.FormatTableIdentifier(table, dialect).ShouldBe(expectedTable);
        AdvancedRlsFilterGenerator.QuoteSingleIdentifier("id", dialect).ShouldBe(expectedCol);
    }

    [Fact]
    public void Oracle_CorrelatedSubquery_OmitsAsForTableAliasesAndQuotesCorrectly()
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            TargetTableAlias = "i",
            DependentTable = new TableIdentifier("erp", "dbo", "customers"),
            DependentTableAlias = "c",
            ForeignKeyColumn = "customer_id",
            PrimaryKeyColumn = "id",
            SubqueryFilterPredicateJson = "{\"c.active\": true}"
        };

        var sql = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, DatabaseDialect.Oracle);

        // Oracle does not allow "AS" in FROM/JOIN table aliases and represents boolean true as 1
        sql.ShouldBe("EXISTS (SELECT 1 FROM \"dbo\".\"customers\" \"c\" WHERE \"c\".\"id\" = \"i\".\"customer_id\" AND \"c\".\"active\" = 1)");
    }

    [Fact]
    public void CrossSourceSetFilter_BatchingAndParameterBudget()
    {
        // Empty set -> 1 = 0
        var emptyFilter = new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter,
            ColumnName = "customer_id",
            ValueJson = "[]"
        };
        AdvancedRlsFilterGenerator.BuildCrossSourceSetFilter(emptyFilter).ShouldBe("1 = 0");

        // Normal set within budget
        var normalFilter = new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter,
            ColumnName = "customer_id",
            ValueJson = "[\"CUST-001\", \"CUST-002\"]"
        };
        AdvancedRlsFilterGenerator.BuildCrossSourceSetFilter(normalFilter).ShouldBe("[customer_id] IN ('CUST-001', 'CUST-002')");

        // Large set exceeding batch budget of 2
        var largeFilter = new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter,
            ColumnName = "customer_id",
            ValueJson = "[\"C-1\", \"C-2\", \"C-3\", \"C-4\", \"C-5\"]"
        };
        var chunked = AdvancedRlsFilterGenerator.BuildCrossSourceSetFilter(largeFilter, maxBatchSize: 2);
        chunked.ShouldBe("(([customer_id] IN ('C-1', 'C-2')) OR ([customer_id] IN ('C-3', 'C-4')) OR ([customer_id] IN ('C-5')))");
    }

    [Theory]
    [InlineData("'; DROP TABLE users; --")]
    [InlineData("invalid column!")]
    [InlineData("1=1")]
    [InlineData("c.id; select 1")]
    public void SqlInjectionProtection_ThrowsOnUnsafeIdentifiers(string maliciousIdentifier)
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            TargetTableAlias = maliciousIdentifier,
            DependentTable = new TableIdentifier("erp", "dbo", "customers"),
            DependentTableAlias = "c",
            ForeignKeyColumn = "customer_id",
            PrimaryKeyColumn = "id"
        };

        Should.Throw<InvalidOperationException>(() =>
            AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter));
    }

    [Fact]
    public void ConsentResolutionService_ResolvesSubqueryCorrelatedFilterInAccessDecision()
    {
        var service = new ConsentResolutionService();
        var userSid = new Sid("S-1-5-21-USER1");
        var table = new TableIdentifier("finance", "dbo", "invoices");

        var consent = new Consent
        {
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            RowFilters = new List<ConsentRowFilter>
            {
                new()
                {
                    FilterType = RowFilterType.SubqueryCorrelated,
                    TargetTableAlias = "i",
                    DependentTable = new TableIdentifier("finance", "dbo", "customers"),
                    DependentTableAlias = "c",
                    ForeignKeyColumn = "customer_id",
                    PrimaryKeyColumn = "id",
                    SubqueryFilterPredicateJson = "{\"c.country\": \"CH\"}"
                }
            }
        };

        var decision = service.ResolveAccess(
            userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            table,
            new[] { consent },
            DatabaseDialect.SqlServer);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldContain("EXISTS (SELECT 1 FROM [dbo].[customers] AS [c] WHERE [c].[id] = [i].[customer_id] AND [c].[country] = 'CH')");
    }

    [Fact]
    public void ParseSubqueryPredicates_MissingColumnOrValueInArray_ThrowsDescriptiveException()
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            TargetTableAlias = "i",
            DependentTable = new TableIdentifier("erp", "dbo", "customers"),
            DependentTableAlias = "c",
            ForeignKeyColumn = "customer_id",
            PrimaryKeyColumn = "id",
            SubqueryFilterPredicateJson = "[{\"op\": \"EQ\", \"value\": \"CH\"}]"
        };

        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, DatabaseDialect.SqlServer);
        });

        ex.Message.ShouldContain("column");
    }
}
