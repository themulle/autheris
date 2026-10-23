namespace Autheris.Tests.Unit.GraphQL;

using Autheris.GraphQL.Catalog;
using Shouldly;
using Xunit;

/// <summary>#306 GAP-015 (SEC-PG-13): the aggregate missing-required-filter error uses the stable column code.</summary>
[Trait("Category", "Security")]
public sealed class AggregateFailureMapperTests
{
    [Fact]
    [Trait("SecurityCondition", "SEC-PG-13")]
    public void SEC_PG_13_RequiredFilterMissing_UsesColumnCode()
    {
        var mapped = AggregateFailureMapper.ForAggregate(new TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException("REQUIRED_FILTER_COLUMN_MISSING: Table requires filter on column 'tenant_id'."));

        mapped.ShouldNotBeNull();
        mapped!.Errors[0].Code.ShouldBe("REQUIRED_FILTER_COLUMN_MISSING");
        mapped.Errors[0].Message.ShouldBe("A required filter column is missing.");
        mapped.Errors[0].Message.ShouldNotContain("tenant_id");
    }
}
