namespace Autheris.Tests.Unit.GraphQL;

using System.Linq;
using Autheris.GraphQL.Interceptors;
using HotChocolate.Language;
using Shouldly;
using Xunit;

/// <summary>Issue #447 SEC-ATR-06: every root field of the selected operation is collected, through aliases, fragments and inline fragments.</summary>
[Trait("Category", "Security")]
public sealed class TransportGateMiddlewareTests
{
    private static string[] Roots(string query, string? operationName = null) =>
        TransportGateMiddleware.RootFieldNames(Utf8GraphQLParser.Parse(query), operationName).OrderBy(n => n, System.StringComparer.Ordinal).ToArray();

    [Fact]
    public void RootFieldNames_CollectsAliasesFragmentsAndInlineFragments()
    {
        Roots("""
            query Q {
              a: sItems { id }
              ...F
              ... on Query { tItems { id } }
            }
            fragment F on Query { sItemsConnection { edges { node { id } } } }
            """).ShouldBe(["sItems", "sItemsConnection", "tItems"]);
    }

    [Fact]
    public void RootFieldNames_WithoutOperationName_CoversEveryQueryOperation_FailClosed()
    {
        const string doc = "query A { sItems { id } } query B { tItems { id } } mutation M { doIt { id } }";
        Roots(doc).ShouldBe(["sItems", "tItems"]);
        Roots(doc, "B").ShouldBe(["tItems"]);
    }

    [Fact]
    public void RootFieldNames_RecursiveFragments_Terminate()
    {
        Roots("query Q { ...A } fragment A on Query { x ...B } fragment B on Query { y ...A }").ShouldBe(["x", "y"]);
    }
}
