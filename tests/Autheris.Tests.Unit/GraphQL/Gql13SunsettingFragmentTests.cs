namespace Autheris.Tests.Unit.GraphQL;

using Autheris.GraphQL.Interceptors;
using HotChocolate.Language;
using Shouldly;
using Xunit;

/// <summary>
/// GQL-13: fields selected through named fragments are evaluated for schema sunsetting.
/// </summary>
public sealed class Gql13SunsettingFragmentTests
{
    [Fact]
    public void GQL_13_FieldsInNamedFragments_AreReferenced()
    {
        var doc = Utf8GraphQLParser.Parse("query { orders { ...F } } fragment F on orders { legacy_ssn }");

        SchemaSunsettingExecutionMiddleware.ExtractReferencedFields(doc).ShouldContain(("orders", "legacy_ssn"));
    }

    [Fact]
    public void GQL_13_CyclicFragments_Terminate()
    {
        var doc = Utf8GraphQLParser.Parse("query { orders { ...A } } fragment A on orders { id ...B } fragment B on orders { name ...A }");

        SchemaSunsettingExecutionMiddleware.ExtractReferencedFields(doc).Count.ShouldBeGreaterThan(0);
    }
}
