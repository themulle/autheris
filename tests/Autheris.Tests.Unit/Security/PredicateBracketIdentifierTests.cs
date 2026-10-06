using System;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>
/// Filter predicates in SQL Server notation ([schema].[table]) are validated as the double-quoted identifiers they are.
/// </summary>
public sealed class PredicateBracketIdentifierTests
{
    [Fact]
    public void GeneratedSqlServerCorrelatedFilter_PassesValidation()
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("lwetem_prod", "conf", "client"),
            DependentTableAlias = "c",
            ForeignKeyColumn = "client_id",
            PrimaryKeyColumn = "client_id",
            SubqueryFilterPredicateJson = "{\"c.country\": \"CH\"}"
        };
        var sql = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, DatabaseDialect.SqlServer);

        Should.NotThrow(() => SqlSecurityValidator.ValidatePredicateSql(sql, "PushdownFilterSql"));
    }

    [Theory]
    [InlineData("[dbo].[customers].[id] = 1", "\"dbo\".\"customers\".\"id\" = 1")]
    [InlineData("EXISTS (SELECT 1 FROM [conf].[client] AS [c] WHERE [c].[x] = 'a[b]c')", "EXISTS (SELECT 1 FROM \"conf\".\"client\" AS \"c\" WHERE \"c\".\"x\" = 'a[b]c')")]
    [InlineData("arr[1] = 2", "arr[1] = 2")]                 // subscript stays a subscript
    [InlineData("arr[idx] = 2", "arr[idx] = 2")]             // attached to an expression -> subscript
    [InlineData("'it''s [x]' = [y]", "'it''s [x]' = \"y\"")] // string literal untouched
    public void NormalizeBracketIdentifiers_OnlyRewritesStrictIdentifiers(string input, string expected)
    {
        SqlSecurityValidator.NormalizeBracketIdentifiers(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("[a b] = 1")]                 // not a strict identifier -> grammar rejects
    [InlineData("[x] = 1; DROP TABLE t")]     // statement terminator
    [InlineData("[x] = 1 -- comment")]        // comment
    [InlineData("[x]] = 1")]                  // malformed bracket
    public void UnsafeOrMalformedBracketPredicates_StayRejected(string predicate)
    {
        Should.Throw<ArgumentException>(() => SqlSecurityValidator.ValidatePredicateSql(predicate, "PushdownFilterSql"));
    }
}
