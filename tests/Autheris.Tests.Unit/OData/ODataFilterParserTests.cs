namespace Autheris.Tests.Unit.OData;

using System;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Extensions.OData;
using Shouldly;
using Xunit;

public class ODataFilterParserTests
{
    [Fact]
    public void Parse_SimpleEquality_GeneratesParameterizedSql()
    {
        var clause = ODataFilterParser.Parse("status eq 'active'", DatabaseDialect.PostgreSql);

        clause.ReferencedColumns.ShouldBe(new[] { "status" });
        clause.Parameters.Count.ShouldBe(1);
        clause.Parameters["@p_od_0"].ShouldBe("active");
        clause.SqlPredicate.ShouldBe("(\"status\" = @p_od_0)");

        // SqlServer dialect test via DialectSqlFactory
        var sqlServerPredicate = clause.GetSqlPredicate(DatabaseDialect.SqlServer);
        sqlServerPredicate.ShouldBe("([status] = @p_od_0)");
    }

    [Fact]
    public void Parse_NullComparison_GeneratesIsNullAndIsNotNull()
    {
        var eqNull = ODataFilterParser.Parse("deleted_at eq null", DatabaseDialect.PostgreSql);
        eqNull.SqlPredicate.ShouldBe("(\"deleted_at\" IS NULL)");
        eqNull.Parameters.Count.ShouldBe(0);

        var neNull = ODataFilterParser.Parse("deleted_at ne null", DatabaseDialect.PostgreSql);
        neNull.SqlPredicate.ShouldBe("(\"deleted_at\" IS NOT NULL)");
        neNull.Parameters.Count.ShouldBe(0);
    }

    [Fact]
    public void Parse_ComparisonOperators_Supported()
    {
        var gtClause = ODataFilterParser.Parse("amount gt 100", DatabaseDialect.PostgreSql);
        gtClause.SqlPredicate.ShouldBe("(\"amount\" > @p_od_0)");
        gtClause.Parameters["@p_od_0"].ShouldBe(100);

        var geClause = ODataFilterParser.Parse("amount ge 50.5", DatabaseDialect.PostgreSql);
        geClause.SqlPredicate.ShouldBe("(\"amount\" >= @p_od_0)");
        geClause.Parameters["@p_od_0"].ShouldBe(50.5m);

        var ltClause = ODataFilterParser.Parse("count lt 10", DatabaseDialect.PostgreSql);
        ltClause.SqlPredicate.ShouldBe("(\"count\" < @p_od_0)");

        var leClause = ODataFilterParser.Parse("count le 20", DatabaseDialect.PostgreSql);
        leClause.SqlPredicate.ShouldBe("(\"count\" <= @p_od_0)");

        var neClause = ODataFilterParser.Parse("status ne 'archived'", DatabaseDialect.PostgreSql);
        neClause.SqlPredicate.ShouldBe("(\"status\" <> @p_od_0)");
    }

    [Fact]
    public void Parse_LogicalOperatorsAndPrecedence_Respected()
    {
        // 'and' has higher precedence than 'or'
        // a eq 1 or b eq 2 and c eq 3 -> (a = 1 OR (b = 2 AND c = 3))
        var clause = ODataFilterParser.Parse("a eq 1 or b eq 2 and c eq 3", DatabaseDialect.PostgreSql);

        clause.ReferencedColumns.ShouldBe(new[] { "a", "b", "c" });
        clause.SqlPredicate.ShouldBe("((\"a\" = @p_od_0) OR ((\"b\" = @p_od_1) AND (\"c\" = @p_od_2)))");

        // Parentheses override precedence
        var parenClause = ODataFilterParser.Parse("(a eq 1 or b eq 2) and c eq 3", DatabaseDialect.PostgreSql);
        parenClause.SqlPredicate.ShouldBe("((((\"a\" = @p_od_0) OR (\"b\" = @p_od_1))) AND (\"c\" = @p_od_2))");
    }

    [Fact]
    public void Parse_NotOperator_GeneratesNotExpression()
    {
        var clause = ODataFilterParser.Parse("not (status eq 'inactive')", DatabaseDialect.PostgreSql);
        clause.SqlPredicate.ShouldBe("NOT (((\"status\" = @p_od_0)))");
    }

    [Fact]
    public void Parse_StringFunctions_Contains_StartsWith_EndsWith()
    {
        var containsClause = ODataFilterParser.Parse("contains(company_name, 'GmbH')", DatabaseDialect.PostgreSql);
        containsClause.SqlPredicate.ShouldBe("(\"company_name\" LIKE @p_od_0)");
        containsClause.Parameters["@p_od_0"].ShouldBe("%GmbH%");

        var startsClause = ODataFilterParser.Parse("startswith(sku, 'PRD-')", DatabaseDialect.PostgreSql);
        startsClause.SqlPredicate.ShouldBe("(\"sku\" LIKE @p_od_0)");
        startsClause.Parameters["@p_od_0"].ShouldBe("PRD-%");

        var endsClause = ODataFilterParser.Parse("endswith(email, '@corp.com')", DatabaseDialect.PostgreSql);
        endsClause.SqlPredicate.ShouldBe("(\"email\" LIKE @p_od_0)");
        endsClause.Parameters["@p_od_0"].ShouldBe("%@corp.com");
    }

    [Fact]
    public void Parse_StringFunctions_ToLower_ToUpper()
    {
        var lowerClause = ODataFilterParser.Parse("tolower(city) eq 'berlin'", DatabaseDialect.PostgreSql);
        lowerClause.SqlPredicate.ShouldBe("(LOWER(\"city\") = @p_od_0)");
        lowerClause.Parameters["@p_od_0"].ShouldBe("berlin");

        var upperClause = ODataFilterParser.Parse("toupper(code) eq 'XYZ'", DatabaseDialect.PostgreSql);
        upperClause.SqlPredicate.ShouldBe("(UPPER(\"code\") = @p_od_0)");
        upperClause.Parameters["@p_od_0"].ShouldBe("XYZ");
    }

    [Fact]
    public void Parse_EscapedSingleQuotes_HandlesSafely()
    {
        var clause = ODataFilterParser.Parse("name eq 'O''Reilly'", DatabaseDialect.PostgreSql);
        clause.Parameters["@p_od_0"].ShouldBe("O'Reilly");
    }

    [Fact]
    public void Parse_SqlInjectionAttempt_InsideString_IsSafelyParameterized()
    {
        var malicious = "name eq 'admin''; DROP TABLE users; --'";
        var clause = ODataFilterParser.Parse(malicious, DatabaseDialect.PostgreSql);

        clause.ReferencedColumns.ShouldBe(new[] { "name" });
        clause.SqlPredicate.ShouldBe("(\"name\" = @p_od_0)");
        clause.Parameters["@p_od_0"].ShouldBe("admin'; DROP TABLE users; --");
    }

    [Fact]
    public void Parse_SqlInjectionAttempt_OutsideString_IsRejected()
    {
        var malicious = "name eq 'admin'; DROP TABLE users; --";
        Should.Throw<GatewayInvalidQueryException>(() => ODataFilterParser.Parse(malicious, DatabaseDialect.PostgreSql));
    }

    [Fact]
    public void Parse_DateTimeLiterals_ParsedCorrectly()
    {
        var clause = ODataFilterParser.Parse("created_at ge 2026-10-01T12:00:00Z", DatabaseDialect.PostgreSql);
        clause.ReferencedColumns.ShouldBe(new[] { "created_at" });
        clause.SqlPredicate.ShouldBe("(\"created_at\" >= @p_od_0)");
        clause.Parameters["@p_od_0"].ShouldBe(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Parse_InvalidSyntax_ThrowsGatewayInvalidQueryException()
    {
        Should.Throw<GatewayInvalidQueryException>(() => ODataFilterParser.Parse("status eq"));
        Should.Throw<GatewayInvalidQueryException>(() => ODataFilterParser.Parse("status eq 'unclosed"));
        Should.Throw<GatewayInvalidQueryException>(() => ODataFilterParser.Parse("unknown_fn(col)"));
        Should.Throw<GatewayInvalidQueryException>(() => ODataFilterParser.Parse("contains(col)")); // missing arg
    }
}
