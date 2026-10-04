using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.GraphQL.Filtering;
using Xunit;

namespace Autheris.Tests.Unit;

public class CompositeKeySqlBuilderTests
{
    private readonly CompositeKeySqlGenerator _generator = new();

    [Fact]
    public void GenerateTupleIn_ForPostgresAndSqlite_ProducesRowValueConstructor()
    {
        var cols = new[] { "tenant_id", "order_id" };
        var keys = new[]
        {
            new CompositeKey("T1", 101),
            new CompositeKey("T2", 202)
        };

        // SQLite
        var (sqliteSql, sqliteParams) = _generator.GenerateCompositeKeyPredicate(cols, keys, DatabaseDialect.Sqlite);
        Assert.Equal("(\"tenant_id\", \"order_id\") IN ((@p1, @p2), (@p3, @p4))", sqliteSql);
        Assert.Equal(4, sqliteParams.Count);
        Assert.Equal("T1", sqliteParams["p1"]);
        Assert.Equal(101, sqliteParams["p2"]);
        Assert.Equal("T2", sqliteParams["p3"]);
        Assert.Equal(202, sqliteParams["p4"]);

        // PostgreSQL
        var (pgSql, pgParams) = _generator.GenerateCompositeKeyPredicate(cols, keys, DatabaseDialect.PostgreSql);
        Assert.Equal("(\"tenant_id\", \"order_id\") IN (($1, $2), ($3, $4))", pgSql);
        Assert.Equal(4, pgParams.Count);
        Assert.Equal("T1", pgParams["1"]);
        Assert.Equal(101, pgParams["2"]);

        // Oracle (:p1, :p2...)
        var (oraSql, oraParams) = _generator.GenerateCompositeKeyPredicate(cols, keys, DatabaseDialect.Oracle);
        Assert.Equal("(\"tenant_id\", \"order_id\") IN ((:p1, :p2), (:p3, :p4))", oraSql);
        Assert.Equal(4, oraParams.Count);
        Assert.Equal("T1", oraParams["p1"]);
        Assert.Equal(101, oraParams["p2"]);
    }

    [Fact]
    public void GenerateValuesJoin_ForSqlServer_ProducesValidValuesDerivedTable()
    {
        var cols = new[] { "company_code", "doc_no" };
        var keys = new[]
        {
            new CompositeKey("DE", "INV-1"),
            new CompositeKey("US", "INV-2")
        };

        var (sqlJoin, parameters) = _generator.GenerateValuesJoinClause(cols, keys, DatabaseDialect.SqlServer, targetTableAlias: "c");

        var expectedJoin = "INNER JOIN (VALUES (@p1, @p2), (@p3, @p4)) AS _k([company_code], [doc_no]) ON c.[company_code] = _k.[company_code] AND c.[doc_no] = _k.[doc_no]";
        Assert.Equal(expectedJoin, sqlJoin);
        Assert.Equal(4, parameters.Count);
        Assert.Equal("DE", parameters["p1"]);
        Assert.Equal("INV-1", parameters["p2"]);
        Assert.Equal("US", parameters["p3"]);
        Assert.Equal("INV-2", parameters["p4"]);
    }

    [Fact]
    public void GenerateDisjunctiveOr_ForSqlServer_ProducesChunkedOrCondition()
    {
        var cols = new[] { "dept_id", "emp_id" };
        var keys = new[]
        {
            new CompositeKey("HR", 5),
            new CompositeKey("IT", 9)
        };

        var (sqlOr, parameters) = _generator.GenerateDisjunctiveOrPredicate(cols, keys, DatabaseDialect.SqlServer);

        Assert.Equal("(([dept_id] = @p1 AND [emp_id] = @p2) OR ([dept_id] = @p3 AND [emp_id] = @p4))", sqlOr);
        Assert.Equal(4, parameters.Count);
        Assert.Equal("HR", parameters["p1"]);
        Assert.Equal(5, parameters["p2"]);
        Assert.Equal("IT", parameters["p3"]);
        Assert.Equal(9, parameters["p4"]);
    }

    [Fact]
    public void GeneratePredicate_WhenKeysEmpty_ReturnsFalseExpression()
    {
        var cols = new[] { "a", "b" };
        var keys = Array.Empty<CompositeKey>();

        var (sql, parameters) = _generator.GenerateCompositeKeyPredicate(cols, keys, DatabaseDialect.Sqlite);
        Assert.Equal("1 = 0", sql);
        Assert.Empty(parameters);
    }

    [Theory]
    [InlineData("id; DROP TABLE users;--")]
    [InlineData("col space")]
    [InlineData("col'quote")]
    [InlineData("1invalid_start")]
    public void GenerateCompositeKeyPredicate_InvalidColumnName_ThrowsArgumentException(string invalidCol)
    {
        var cols = new[] { invalidCol };
        var keys = new[] { new CompositeKey(1) };

        Assert.Throws<ArgumentException>(() => _generator.GenerateCompositeKeyPredicate(cols, keys, DatabaseDialect.SqlServer));
        Assert.Throws<ArgumentException>(() => _generator.GenerateCompositeKeyPredicate(cols, keys, DatabaseDialect.PostgreSql));
    }

    [Theory]
    [InlineData("id; DROP TABLE users;--")]
    [InlineData("alias'injection")]
    public void GenerateValuesJoinClause_InvalidIdentifier_ThrowsArgumentException(string invalidIdentifier)
    {
        var cols = new[] { "valid_col" };
        var keys = new[] { new CompositeKey(1) };

        Assert.Throws<ArgumentException>(() => _generator.GenerateValuesJoinClause(new[] { invalidIdentifier }, keys, DatabaseDialect.SqlServer, "c"));
        Assert.Throws<ArgumentException>(() => _generator.GenerateValuesJoinClause(cols, keys, DatabaseDialect.SqlServer, invalidIdentifier));
    }
}
