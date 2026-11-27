using System.Text.Json;
using Autheris.Api.Middleware;
using Autheris.Domain.Interfaces;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Execution;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.DisclosureFixture;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// #1188: precise validation errors (UNKNOWN_FIELD, ARGUMENT_INVALID, VARIABLE_TYPE_MISMATCH) only when the caller can read every
/// catalog table the document touches with every column; every other case is the shielded validation body, byte-identical.
/// </summary>
[Collection("CatalogRelation")]
public sealed class GraphQlValidationErrorDisclosureTests
{
    private const string Customers1 = "sales_dbo_customers";

    private static async Task<(string Json, OperationResult Result)> RunAsync(DisclosureFixture fx, string env, string query, string? vars = null)
    {
        var executor = await fx.CreateAsync(env);
        return await DisclosureFixture.RunAsync(executor, query, vars);
    }

    private static JsonElement FirstError(string json) => JsonDocument.Parse(json).RootElement.GetProperty("errors")[0];

    private static string CodeOf(string json) => FirstError(json).GetProperty("extensions").GetProperty("code").GetString()!;

    private static void ShouldBeShielded(string json, OperationResult result, string? because = null)
    {
        json.ShouldBe(ValidationBody, because);
        result.ContextData.ContainsKey(ExecutionContextData.ValidationErrors).ShouldBeTrue(because);
    }

    private static string Col(string query, string token) => (query.IndexOf(token, StringComparison.Ordinal) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- Engine pins: which node HotChocolate reports per rule -------------------------------------------------------------

    private static async Task<IReadOnlyList<IError>> RawErrorsAsync(string query, string? vars = null)
    {
        var fx = new DisclosureFixture { WithShield = false, WithErrorFilter = false };
        var (_, result) = await RunAsync(fx, "Production", query, vars);
        return result.Errors ?? [];
    }

    private static void Pin(IReadOnlyList<IError> errors, string anchor, string query, string token, bool lastOccurrence = false)
    {
        errors.Count.ShouldBe(1, string.Join(";", errors.Select(e => e.Message)));
        errors[0].Extensions!["specifiedBy"].ShouldBeOfType<string>().ShouldEndWith("#" + anchor);
        var index = lastOccurrence ? query.LastIndexOf(token, StringComparison.Ordinal) : query.IndexOf(token, StringComparison.Ordinal);
        var location = errors[0].Locations.ShouldHaveSingleItem();
        location.Line.ShouldBe(1);
        location.Column.ShouldBe(index + 1);
    }

    [Fact]
    public async Task EnginePin_FieldSelections_ReportsLocationOfFieldNode()
    {
        const string q = "{ sales_dbo_customers(first: 5) { id al: unknown_col } }";
        Pin(await RawErrorsAsync(q), "sec-Field-Selections", q, "al:");
    }

    [Fact]
    public async Task EnginePin_InputObjectFieldNames_ReportsLocationOfObjectField()
    {
        const string q = "{ sales_dbo_customers(first: 5, where: { unk: { eq: 1 } }) { id } }";
        Pin(await RawErrorsAsync(q), "sec-Input-Object-Field-Names", q, "unk");
    }

    [Fact]
    public async Task EnginePin_ValuesOfCorrectType_ReportsLocationOfValueNode()
    {
        const string q = "{ sales_dbo_customers(first: \"x\") { id } }";
        Pin(await RawErrorsAsync(q), "sec-Values-of-Correct-Type", q, "\"x\"");
    }

    [Fact]
    public async Task EnginePin_ValuesOfCorrectType_InObjectField_ReportsLocationOfValueNode()
    {
        const string q = "{ sales_dbo_customers(first: 5, orderBy: [{ id: SIDEWAYS }]) { id } }";
        Pin(await RawErrorsAsync(q), "sec-Values-of-Correct-Type", q, "SIDEWAYS");
    }

    [Fact]
    public async Task EnginePin_ArgumentNames_ReportsLocationOfArgumentNode()
    {
        const string q = "{ sales_dbo_customers(frist: 5) { id } }";
        Pin(await RawErrorsAsync(q), "sec-Argument-Names", q, "frist");
    }

    [Fact]
    public async Task EnginePin_RequiredArguments_ReportsLocationOfFieldNode()
    {
        var field = AggregateTypeNames.For(Customers1).Field;
        var q = "{ " + field + "(groupBy: [id]) { suppressed } }";
        Pin(await RawErrorsAsync(q), "sec-Required-Arguments", q, field);
    }

    [Fact]
    public async Task EnginePin_VariableUsage_ReportsLocationOfVariableNode()
    {
        const string q = "query($v: String) { sales_dbo_customers(first: $v) { id } }";
        Pin(await RawErrorsAsync(q), "sec-All-Variable-Usages-Are-Allowed", q, "$v", lastOccurrence: true);
    }

    // ---- Precise answers --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Precise_AllEnvironments_FullyVisibleTable_ReturnsPrecise(string env)
    {
        const string q = "{ sales_dbo_customers(first: 5) { id unknown_col } }";
        var (json, result) = await RunAsync(new DisclosureFixture(), env, q);

        json.ShouldBe("{\"errors\":[{\"message\":\"The field \\u0027unknown_col\\u0027 does not exist on \\u0027sales_dbo_customers\\u0027.\",\"locations\":[{\"line\":1,\"column\":" + Col(q, "unknown_col") + "}],\"extensions\":{\"code\":\"UNKNOWN_FIELD\",\"table\":\"sales_dbo_customers\",\"field\":\"unknown_col\"}}]}");
        result.ContextData.ContainsKey(ExecutionContextData.ValidationErrors).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Precise_MaskedColumnTable_ReturnsUnknownField(string env)
    {
        var (json, _) = await RunAsync(new DisclosureFixture(), env, "{ sales_dbo_masked { id nope } }");

        CodeOf(json).ShouldBe("UNKNOWN_FIELD");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Precise_UnknownWhereColumn_ReturnsUnknownFieldWithArgument(string env)
    {
        var (json, _) = await RunAsync(new DisclosureFixture(), env, "{ sales_dbo_customers(first: 5, where: { unk: { eq: 1 } }) { id } }");

        var ext = FirstError(json).GetProperty("extensions");
        ext.GetProperty("code").GetString().ShouldBe("UNKNOWN_FIELD");
        ext.GetProperty("field").GetString().ShouldBe("unk");
        ext.GetProperty("argument").GetString().ShouldBe("where");
        ext.GetProperty("table").GetString().ShouldBe(Customers1);
    }

    [Fact]
    public async Task Precise_UnknownOrderByColumn_ReturnsUnknownField()
    {
        var (json, _) = await RunAsync(new DisclosureFixture(), "Production", "{ sales_dbo_customers(first: 5, orderBy: [{ unk: ASC }]) { id } }");

        var ext = FirstError(json).GetProperty("extensions");
        ext.GetProperty("code").GetString().ShouldBe("UNKNOWN_FIELD");
        ext.GetProperty("argument").GetString().ShouldBe("orderBy");
    }

    [Fact]
    public async Task Precise_UnknownGroupByColumn_ReturnsUnknownField()
    {
        var field = AggregateTypeNames.For(Customers1).Field;
        var (json, _) = await RunAsync(new DisclosureFixture(), "Production", "{ " + field + "(groupBy: [bogus], aggregates: []) { suppressed } }");

        var ext = FirstError(json).GetProperty("extensions");
        ext.GetProperty("code").GetString().ShouldBe("UNKNOWN_FIELD");
        ext.GetProperty("field").GetString().ShouldBe("bogus");
        ext.GetProperty("argument").GetString().ShouldBe("groupBy");
    }

    [Fact]
    public async Task Precise_WrongArgumentName_ReturnsArgumentInvalid()
    {
        var (json, _) = await RunAsync(new DisclosureFixture(), "Production", "{ sales_dbo_customers(frist: 5) { id } }");

        var error = FirstError(json);
        error.GetProperty("message").GetString().ShouldBe("The argument 'frist' of 'sales_dbo_customers' is invalid.");
        error.GetProperty("extensions").GetProperty("code").GetString().ShouldBe("ARGUMENT_INVALID");
    }

    [Theory]
    [InlineData("{ sales_dbo_customers(first: \"x\") { id } }", "first")]
    [InlineData("{ sales_dbo_customers(first: 5, orderBy: [{ id: SIDEWAYS }]) { id } }", "orderBy")]
    [InlineData("{ sales_dbo_customers(first: 5, where: { id: { bad: 1 } }) { id } }", "where")]
    public async Task Precise_WrongLiteralOrOperator_ReturnsArgumentInvalid(string query, string argument)
    {
        var (json, _) = await RunAsync(new DisclosureFixture(), "Production", query);

        var ext = FirstError(json).GetProperty("extensions");
        ext.GetProperty("code").GetString().ShouldBe("ARGUMENT_INVALID");
        ext.GetProperty("argument").GetString().ShouldBe(argument);
        ext.GetProperty("field").GetString().ShouldBe(Customers1);
    }

    [Fact]
    public async Task Precise_MissingRequiredArgument_ReturnsArgumentInvalid()
    {
        var field = AggregateTypeNames.For(Customers1).Field;
        var (json, _) = await RunAsync(new DisclosureFixture(), "Production", "{ " + field + "(groupBy: [id]) { suppressed } }");

        var ext = FirstError(json).GetProperty("extensions");
        ext.GetProperty("code").GetString().ShouldBe("ARGUMENT_INVALID");
        ext.GetProperty("argument").GetString().ShouldBe("aggregates");
    }

    [Fact]
    public async Task Precise_IncompatibleVariable_ReturnsVariableTypeMismatch()
    {
        var (json, _) = await RunAsync(new DisclosureFixture(), "Production", "query($v: String) { sales_dbo_customers(first: $v) { id } }");

        var error = FirstError(json);
        error.GetProperty("message").GetString().ShouldBe("The variable '$v' does not match the expected type 'Int'.");
        var ext = error.GetProperty("extensions");
        ext.GetProperty("code").GetString().ShouldBe("VARIABLE_TYPE_MISMATCH");
        ext.GetProperty("variable").GetString().ShouldBe("v");
        ext.GetProperty("expectedType").GetString().ShouldBe("Int");
    }

    [Fact]
    public async Task Precise_RelationFieldOnVisibleTables_IsPreciseWhenTargetIsVisible()
    {
        var fx = new DisclosureFixture();
        fx.DeniedTables.Clear();
        var (json, _) = await RunAsync(fx, "Production", "{ sales_dbo_orders { id secret { id zzz } } }");

        var ext = FirstError(json).GetProperty("extensions");
        ext.GetProperty("code").GetString().ShouldBe("UNKNOWN_FIELD");
        ext.GetProperty("table").GetString().ShouldBe("sales_dbo_secrets");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Precise_BodyContainsOnlyAstNamesAndFixedTemplates()
    {
        var longName = new string('x', 128);
        var (json, _) = await RunAsync(new DisclosureFixture(), "Production", "{ sales_dbo_customers(first: 5) { id " + longName + " } }");

        FirstError(json).GetProperty("message").GetString().ShouldBe($"The field '{longName}' does not exist on 'sales_dbo_customers'.");
        foreach (var forbidden in new[] { "specifiedBy", "HC0", "Did you mean", "does not exist on the type", "spec.graphql.org", "path" })
        {
            json.ShouldNotContain(forbidden);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Shield_AliasOnUnknownField_EchoesFieldNotAlias()
    {
        var (json, _) = await RunAsync(new DisclosureFixture(), "Production", "{ sales_dbo_customers { id aliasname: nope } }");

        FirstError(json).GetProperty("message").GetString()!.ShouldContain("'nope'");
        json.ShouldNotContain("aliasname");
    }

    // ---- Shield: byte-identical to today's body -----------------------------------------------------------------------------

    [Theory]
    [InlineData("Production", "{ unknown_table { id } }")]
    [InlineData("Development", "{ unknown_table { id } }")]
    [InlineData("Production", "{ sales_dbo_secrets { id } }")]
    [InlineData("Production", "{ sales_dbo_secrets { id nope } }")]
    [InlineData("Development", "{ sales_dbo_secrets { id nope } }")]
    [InlineData("Production", "{ sales_dbo_partial { id hidden } }")]
    [InlineData("Production", "{ sales_dbo_partial { id nope } }")]
    [InlineData("Development", "{ sales_dbo_partial { id nope } }")]
    [Trait("Category", "Security")]
    public async Task Shield_UnknownTable_InvisibleTable_DeniedColumn_PartlyDeniedUnknownName_AreByteIdentical(string env, string query)
    {
        var (json, result) = await RunAsync(new DisclosureFixture(), env, query);

        ShouldBeShielded(json, result, query);
    }

    // Execution-phase denial is shielded outside Development only (unchanged behavior), so this comparison runs in Production.
    [Fact]
    [Trait("Category", "Security")]
    public async Task Shield_DeniedColumnTable_SelectingDeniedColumn_EqualsUnknownColumn()
    {
        const string env = "Production";
        var (denied, deniedResult) = await RunAsync(new DisclosureFixture(), env, "{ sales_dbo_partial { id hidden } }");
        var (unknown, unknownResult) = await RunAsync(new DisclosureFixture(), env, "{ sales_dbo_partial { id nope } }");

        denied.ShouldBe(unknown);
        deniedResult.ContextData.Keys.OrderBy(k => k).ShouldBe(unknownResult.ContextData.Keys.OrderBy(k => k));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Shield_RelationTargetInvisible_UnknownFieldElsewhere_EqualsPlainUnknown(string env)
    {
        var (viaRelationOwner, r1) = await RunAsync(new DisclosureFixture(), env, "{ sales_dbo_orders { id nope } }");
        var (plainUnknownRoot, r2) = await RunAsync(new DisclosureFixture(), env, "{ unknown_table { nope } }");

        ShouldBeShielded(viaRelationOwner, r1);
        viaRelationOwner.ShouldBe(plainUnknownRoot);
        r2.ContextData.ContainsKey(ExecutionContextData.ValidationErrors).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Production", "{ sales_dbo_customers { id nope } sales_dbo_secrets { id } }")]
    [InlineData("Development", "{ sales_dbo_customers { id nope } sales_dbo_secrets { id } }")]
    [InlineData("Production", "{ sales_dbo_customers(frist: 1) { id nope } sales_dbo_partial { id nope } }")]
    [InlineData("Development", "{ sales_dbo_customers(frist: 1) { id nope } sales_dbo_partial { id nope } }")]
    [InlineData("Production", "{ sales_dbo_customers { id nope } __schema { nope } }")]
    [InlineData("Production", "{ sales_dbo_customers { id nope name { x } } }")]
    [InlineData("Production", "{ sales_dbo_customers(first: 5000) { id nope } }")]
    [InlineData("Development", "{ sales_dbo_customers(first: 5000) { id nope } }")]
    [InlineData("Production", "{ sales_dbo_customers { id nope } } fragment F on sales_dbo_secrets { id nope }")]
    [InlineData("Production", "{ sales_dbo_customers { id nope } } fragment F on Nope { id }")]
    [InlineData("Production", "{ sales_dbo_customers { id nope } } fragment F on sales_dbo_customers { id }")]
    [InlineData("Production", "{ sales_dbo_customers { id ...A nope } } fragment A on sales_dbo_customers { ...B } fragment B on sales_dbo_customers { ...A }")]
    [InlineData("Production", "{ sales_dbo_customers { id @include(if: 5) } }")]
    [InlineData("Production", "{ sales_dbo_customers { id @nope } }")]
    [InlineData("Production", "{ __schema { nope } }")]
    [InlineData("Production", "mutation { x }")]
    [Trait("Category", "Security")]
    public async Task Shield_MixedOrUnsupportedDocuments_ShieldWholeResult(string env, string query)
    {
        var (json, result) = await RunAsync(new DisclosureFixture(), env, query);

        ShouldBeShielded(json, result, query);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Shield_ResolverMissing_Shields(string env)
    {
        var (json, result) = await RunAsync(new DisclosureFixture { IncludeResolver = false }, env, "{ sales_dbo_customers { id nope } }");

        ShouldBeShielded(json, result);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Shield_ResolverThrows_Shields(string env)
    {
        var (json, result) = await RunAsync(new DisclosureFixture { ThrowFor = _ => true }, env, "{ sales_dbo_customers { id nope } }");

        ShouldBeShielded(json, result);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Shield_UnauthenticatedCaller_Shields(string env)
    {
        var (json, result) = await RunAsync(new DisclosureFixture { Authenticated = false }, env, "{ sales_dbo_customers { id nope } }");

        ShouldBeShielded(json, result);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Shield_ResolverReturnsDenied_Shields(string env)
    {
        var fx = new DisclosureFixture();
        fx.DeniedTables.Add(Customers);
        var (json, result) = await RunAsync(fx, env, "{ sales_dbo_customers { id nope } }");

        ShouldBeShielded(json, result);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Shield_SchemaColumnMissingInMetadata_Shields(string env)
    {
        var fx = new DisclosureFixture();
        fx.MetadataColumnNames[Customers] = ["id"];
        var (json, result) = await RunAsync(fx, env, "{ sales_dbo_customers { id nope } }");

        ShouldBeShielded(json, result);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Shield_MetadataColumnDenied_Shields(string env)
    {
        var fx = new DisclosureFixture();
        fx.Levels[Customers] = new(StringComparer.OrdinalIgnoreCase) { ["name"] = ColumnAccessLevel.Deny };
        var (json, result) = await RunAsync(fx, env, "{ sales_dbo_customers { id nope } }");

        ShouldBeShielded(json, result);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Shield_MoreThan32Tables_Shields(string env)
    {
        var fx = new DisclosureFixture { ExtraTables = 34, MaxRootFields = 100 };
        var roots = string.Join(' ', Enumerable.Range(0, 33).Select(i => $"sales_dbo_extra{i:00} {{ id }}"));
        var (json, result) = await RunAsync(fx, env, "{ " + roots + " sales_dbo_customers { nope } }");

        ShouldBeShielded(json, result);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Shield_NameLongerThan128_Shields(string env)
    {
        var (json, result) = await RunAsync(new DisclosureFixture(), env, "{ sales_dbo_customers { id " + new string('x', 129) + " } }");

        ShouldBeShielded(json, result);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Precise_ManyUnknownFields_AnswerNeverMoreThanTheErrorCap()
    {
        // The engine stops collecting validation errors early; whatever number reaches the answer stays within the cap.
        var fields = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"nope{i}"));
        var (json, _) = await RunAsync(new DisclosureFixture(), "Production", "{ sales_dbo_customers { id " + fields + " } }");

        JsonDocument.Parse(json).RootElement.GetProperty("errors").GetArrayLength().ShouldBeLessThanOrEqualTo(GraphQlValidationErrorDisclosure.MaxErrors);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task Shield_WalkDepthOverCap_Shields(string env)
    {
        var open = string.Concat(Enumerable.Repeat("... on sales_dbo_customers { ", 70));
        var close = string.Concat(Enumerable.Repeat(" }", 70));
        var (json, result) = await RunAsync(new DisclosureFixture(), env, "{ sales_dbo_customers { " + open + "nope" + close + " } }");

        ShouldBeShielded(json, result);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Shield_DocumentWithoutErrors_PassesUnchanged()
    {
        var (json, result) = await RunAsync(new DisclosureFixture(), "Production", "{ __typename }");

        json.ShouldBe("{\"data\":{\"__typename\":\"Query\"}}");
        (result.Errors ?? []).ShouldBeEmpty();
    }

    // ---- Variable coercion (no location, no names in the engine error) ------------------------------------------------------

    private const string CoercionQuery = "query($v: Int) { sales_dbo_customers(first: $v) { id } }";
    private const string BadValue = "{\"v\":2147483648}";

    [Theory]
    [InlineData("Production", "sales_dbo_secrets")]
    [InlineData("Development", "sales_dbo_secrets")]
    [InlineData("Production", "sales_dbo_partial")]
    [InlineData("Development", "sales_dbo_partial")]
    [Trait("Category", "Security")]
    public async Task VariableCoercion_InvisibleTable_ReturnsShieldedBodyByteIdentical(string env, string table)
    {
        var (json, result) = await RunAsync(new DisclosureFixture(), env, $"query($v: Int) {{ {table}(first: $v) {{ id }} }}", BadValue);

        ShouldBeShielded(json, result);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task VariableCoercion_UnknownRoot_And_InvisibleTable_AreIndistinguishable(string env)
    {
        // Out-of-range and wrong JSON kinds (string, boolean, list) for the Int variable all take a coercion path.
        foreach (var value in new[] { BadValue, "{\"v\":\"abc\"}", "{\"v\":true}", "{\"v\":[1]}" })
        {
            var (invisible, r1) = await RunAsync(new DisclosureFixture(), env, "query($v: Int) { sales_dbo_secrets(first: $v) { id } }", value);
            var (unknown, r2) = await RunAsync(new DisclosureFixture(), env, "query($v: Int) { unknown_table(first: $v) { id } }", value);

            invisible.ShouldBe(unknown, value);
            r1.ContextData.Keys.OrderBy(k => k).ShouldBe(r2.ContextData.Keys.OrderBy(k => k), value);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task VariableCoercion_FullyVisibleTable_KeepsSanitizedVariableError(string env)
    {
        var (json, result) = await RunAsync(new DisclosureFixture(), env, CoercionQuery, BadValue);

        json.ShouldBe("{\"errors\":[{\"message\":\"A variable value is invalid.\",\"extensions\":{\"code\":\"GRAPHQL_VARIABLE_INVALID\"}}]}");
        result.ContextData.ContainsKey(ExecutionContextData.ValidationErrors).ShouldBeFalse();
    }

    // These documents pass validation and fail while the variables are coerced, so they take the coercion path, not the validation path.
    [Theory]
    [InlineData("query($v: Int = \"x\") { sales_dbo_customers(first: $v) { id } }")]
    [InlineData("query($b: Int) { sales_dbo_customers { id @include(if: $b) } }")]
    [Trait("Category", "Security")]
    public async Task VariableCoercion_DefaultOrDirectiveUsage_OnVisibleTable_KeepsSanitizedVariableError(string query)
    {
        var (json, _) = await RunAsync(new DisclosureFixture(), "Production", query);

        CodeOf(json).ShouldBe("GRAPHQL_VARIABLE_INVALID");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task VariableCoercion_GateUnavailable_ReturnsShieldedBody(string env)
    {
        var (missing, r1) = await RunAsync(new DisclosureFixture { IncludeResolver = false }, env, CoercionQuery, BadValue);
        var (throwing, r2) = await RunAsync(new DisclosureFixture { ThrowFor = _ => true }, env, CoercionQuery, BadValue);

        ShouldBeShielded(missing, r1);
        ShouldBeShielded(throwing, r2);
    }
}
