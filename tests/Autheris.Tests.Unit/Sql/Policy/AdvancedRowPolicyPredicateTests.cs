using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.Json;
using Autheris.Application.Governance;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>WP-X0d (Phase 3 amendment, SEC-X1-02): correlated, cross-source and Casbin producers build typed predicates.</summary>
public sealed class AdvancedRowPolicyPredicateTests
{
    private readonly PolicyPredicateRenderer _renderer = new();

    [Fact]
    public void CrossSourceSetFilter_ValuesAreParameters()
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter,
            ColumnName = "customer_id",
            ValueJson = "[\"a'b\", 7, null, 2.5]"
        };

        var predicate = AdvancedRlsFilterGenerator.BuildCrossSourceSetPredicate(filter, DatabaseDialect.PostgreSql);
        var rendered = _renderer.Render(predicate, TargetSqlDialect.PostgreSql, RowFilterAliases.Target);

        rendered.Sql.ShouldBe("\"customer_id\" IN (@__gql_rls_0, @__gql_rls_1, @__gql_rls_2)");
        rendered.Sql.ShouldNotContain("a'b");
        rendered.Parameters.Values.ShouldBe(new object?[] { "a'b", 7L, 2.5m });
    }

    [Fact]
    public void CrossSourceSetFilter_EmptySet_DeniesAll_AndOracleChunksAt1000()
    {
        var empty = new ConsentRowFilter { FilterType = RowFilterType.CrossSourceSetFilter, ColumnName = "customer_id", ValueJson = "[]" };
        _renderer.Render(AdvancedRlsFilterGenerator.BuildCrossSourceSetPredicate(empty, DatabaseDialect.SqlServer), TargetSqlDialect.SqlServer, RowFilterAliases.Target)
            .Sql.ShouldBe("1 = 0");

        var many = new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter, ColumnName = "customer_id",
            ValueJson = JsonSerializer.Serialize(Enumerable.Range(0, 1500))
        };
        var predicate = AdvancedRlsFilterGenerator.BuildCrossSourceSetPredicate(many, DatabaseDialect.Oracle);
        var rendered = _renderer.Render(predicate, TargetSqlDialect.Oracle, RowFilterAliases.Target);
        predicate.Parameters.Count.ShouldBe(1500);
        System.Text.RegularExpressions.Regex.Count(rendered.Sql, " IN \\(").ShouldBe(2);
    }

    [Fact]
    public void RlsFilterGenerator_ExposesTheTypedProducers()
    {
        var generator = RlsFilterGenerator.Instance;
        generator.BuildCrossSourceSetPredicate(
            new ConsentRowFilter { ColumnName = "k", ValueJson = "[1,2]" }, DatabaseDialect.Sqlite).Parameters.Count.ShouldBe(2);
    }

}
