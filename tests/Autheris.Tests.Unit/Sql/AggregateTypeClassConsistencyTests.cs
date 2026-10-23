namespace Autheris.Tests.Unit.Sql;

using Autheris.Application.Analytics;
using Autheris.GraphQL.Catalog;
using Shouldly;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

/// <summary>
/// #310 (SEC-PG-09): the WebSQL aggregate type guard (<see cref="AggregateTypeClasses"/>) and the GraphQL aggregation matrix
/// (<c>AggregateResultMapper.KindFor</c> over <see cref="CatalogSchemaModel.MapDataType"/>) agree on every recognised type name, and the
/// guard is never more permissive than GraphQL for them (an unrecognised name is rejected by the guard, a String by the catalog default).
/// The guard additionally knows dialect numeric spellings (Oracle NUMBER, ...) that the catalog kinds do not.
/// </summary>
public sealed class AggregateTypeClassConsistencyTests
{
    private static readonly string[] Recognised =
    [
        "int", "integer", "bigint", "smallint", "tinyint", "int2", "int4", "int8", "long", "decimal(18,2)", "numeric", "money", "smallmoney",
        "float", "double precision", "real", "float4", "float8",
        "varchar(20)", "nvarchar(max)", "char(3)", "nchar(3)", "text", "ntext", "string", "citext", "uuid", "json", "jsonb", "xml",
        "date", "datetime", "datetime2(3)", "timestamp", "timestamp with time zone", "time", "smalldatetime",
        "boolean", "bool", "bit"
    ];

    private static readonly string[] Unrecognised = ["frobnicate", "bytea", "varbinary(10)", "hierarchyid", ""];

    private static readonly (AggregateFunction Function, string Name)[] Functions =
    [
        (AggregateFunction.Sum, "sum"), (AggregateFunction.Avg, "avg"), (AggregateFunction.Min, "min"), (AggregateFunction.Max, "max")
    ];

    [Fact]
    public void RecognisedTypes_AgreeWithTheGraphQlMatrix()
    {
        foreach (var type in Recognised)
        {
            var field = CatalogSchemaModel.MapDataType(type);
            foreach (var (function, name) in Functions)
            {
                var graphQlValid = AggregateResultMapper.KindFor(function, field) is not null;
                var guardValid = AggregateTypeClasses.IsValid(name, AggregateTypeClasses.Classify(type));
                guardValid.ShouldBe(graphQlValid, $"{name} over {type}");
            }
        }
    }

    [Fact]
    public void TheGuard_IsNeverMorePermissiveThanGraphQl()
    {
        foreach (var type in Recognised.Concat(Unrecognised))
        {
            var field = CatalogSchemaModel.MapDataType(type);
            foreach (var (function, name) in Functions)
            {
                var graphQlValid = AggregateResultMapper.KindFor(function, field) is not null;
                var guardValid = AggregateTypeClasses.IsValid(name, AggregateTypeClasses.Classify(type));
                (guardValid && !graphQlValid).ShouldBeFalse($"{name} over {type}");
            }
        }
    }

    [Theory]
    [InlineData("NUMBER(10)")]
    [InlineData("NUMBER")]
    [InlineData("BINARY_DOUBLE")]
    [InlineData("hugeint")]
    public void DialectNumericSpellings_UnknownToTheCatalogKinds_AreNumericForTheGuard(string type)
    {
        // The catalog value kinds fall back to String for these (a GraphQL gap); the guard must not reject a valid Oracle or DuckDB sum.
        AggregateTypeClasses.IsValid("sum", AggregateTypeClasses.Classify(type)).ShouldBeTrue();
    }

    [Fact]
    public void UnrecognisedTypes_AreRejectedForEveryFunction()
    {
        foreach (var type in Unrecognised)
        {
            foreach (var (_, name) in Functions)
            {
                AggregateTypeClasses.IsValid(name, AggregateTypeClasses.Classify(type)).ShouldBeFalse($"{name} over '{type}'");
            }
        }
    }
}
