namespace Autheris.Tests.Unit.Diagnostics;

using Autheris.Application.Diagnostics.Shadowing;
using Shouldly;
using Xunit;

/// <summary>MISC-14: parsed GraphQL/SQL literal redaction, path allowlist, unparseable bodies are not shadowed.</summary>
[Trait("Category", "Security")]
public sealed class ShadowingRedactionMisc14Tests
{
    private static string Gql(string query) => PiiShadowingRedactor.RedactBody("{\"query\":" + System.Text.Json.JsonSerializer.Serialize(query) + "}")!;

    [Fact]
    public void GraphQlInlineIntArgument_Redacted() => Gql("{ patient(id: 4711) { name } }").ShouldNotContain("4711");

    [Fact]
    public void GraphQlNestedEq_Redacted()
    {
        var result = Gql("{ people(where: {insuranceNo: {eq: \"A123456789\"}}) { id } }");

        result.ShouldNotContain("A123456789");
    }

    [Fact]
    public void GraphQlEnumAndBoolean_Redacted()
    {
        var result = Gql("{ people(status: SECRET_STATUS, vip: true) { id } }");

        result.ShouldNotContain("SECRET_STATUS");
        result.ShouldNotContain("true");
    }

    [Fact]
    public void SqlBetweenBounds_Redacted()
    {
        var result = PiiShadowingRedactor.RedactBody("{\"sql\":\"SELECT id FROM t WHERE salary BETWEEN 50000 AND 90000\"}")!;

        result.ShouldNotContain("50000");
        result.ShouldNotContain("90000");
    }

    [Fact]
    public void SqlCaseThenLiteral_Redacted()
    {
        var result = PiiShadowingRedactor.RedactBody("{\"sql\":\"SELECT CASE WHEN a = 1 THEN 123456 ELSE 7 END FROM t\"}")!;

        result.ShouldNotContain("123456");
    }

    [Fact]
    public void RestPathKey_Redacted() =>
        PiiShadowingRedactor.RedactPathAndQuery("/api/employees/4711", ["api", "employees"]).ShouldBe("/api/employees/_");

    [Fact]
    public void ODataKeySegment_Redacted() =>
        PiiShadowingRedactor.RedactPathAndQuery("/odata/employees(4711)", ["odata", "employees"]).ShouldBe("/odata/employees(_)");

    [Fact]
    public void NonAllowlistedSegment_Replaced() =>
        PiiShadowingRedactor.RedactPathAndQuery("/api/customers/john-doe", ["api"]).ShouldBe("/api/_/_");

    [Fact]
    public void StaticPath_Kept() =>
        PiiShadowingRedactor.RedactPathAndQuery("/graphql").ShouldBe("/graphql");

    [Fact]
    public void UnparseableBody_NotShadowed()
    {
        PiiShadowingRedactor.TryRedactBody("{\"query\":\"{ patient(id: \"}", out var redacted).ShouldBeFalse();
        redacted.ShouldBeNull();
        PiiShadowingRedactor.TryRedactBody("this is # not a query", out _).ShouldBeFalse();
        PiiShadowingRedactor.RedactBody("{\"query\":\"{ patient(id: \"}").ShouldBe("***");
    }
}
