namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Identity.Attributes;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>PLAN-GOV-129 tests 19-22, ST-129-05, ST-129-06 (A-4, BC-129-05, BC-129-06): caller placeholders in sql Virtual Filters.</summary>
[Trait("Category", "Security")]
public sealed class SqlFilterCompilerCallerAttributeTests
{
    private static readonly TenantId Tenant = CallerAttributeTestSupport.Tenant;
    private static readonly ISqlEngine Engine = new FastSqlEngine();
    private static readonly PolicyPredicateRenderer Renderer = new();

    private static TableMetadata Catalogued(string schema, string table) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", schema, table),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = schema, TableName = table, SourceType = DatabaseDialect.PostgreSql.ToString(), IsActive = true },
        Columns = [new TableColumn { ColumnName = "id" }]
    };

    private static readonly IReadOnlyList<TableMetadata> Catalog = [Catalogued("conf", "client")];

    internal static CallerAttributeSnapshot Attributes() => new(1,
    [
        CallerAttributeTestSupport.Definition("dept", "claims", CallerAttributeType.Text, CallerAttributeCardinality.Scalar, 1, 60),
        CallerAttributeTestSupport.Definition("regions", "claims", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60)
    ], []);

    internal static VirtualFilter SqlFilter(string sql) => new() { TenantId = Tenant, Name = "caller_filter", Source = "lwetem_prod", Sql = sql };

    private static VirtualFilter Validate(string sql, CallerAttributeSnapshot? attributes = null) =>
        SqlFilterCompiler.Validate(SqlFilter(sql), Catalog, callerAttributes: attributes ?? Attributes());

    private const string Scalar = "from conf.client client where target.client_id = client.client_id and client.dept = {{caller.dept}}";
    private const string InList = "from conf.client client where target.client_id = client.client_id and client.region in ({{caller.regions}})";
    private const string NotIn = "from conf.client client where target.client_id = client.client_id and client.region not in ({{caller.regions}})";

    [Theory]
    [InlineData(Scalar)]
    [InlineData(InList)]
    [InlineData(NotIn)]
    [InlineData("from conf.client client where target.client_id = client.client_id and {{caller.dept}} = client.dept")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.level >= {{caller.dept}}")]
    [InlineData("from conf.client client where (target.client_id = client.client_id and client.dept <> {{caller.dept}})")]
    public void ValidShapes_AreAccepted_AndTheDefinitionIsStoredRaw(string sql)
    {
        var validated = Validate(sql);
        validated.Sql.ShouldBe(sql);
        validated.SqlTargetColumns.ShouldContain("client_id");
    }

    private static IReadOnlyList<TableMetadata> TypedCatalog(string deptType) =>
    [
        Catalogued("conf", "client") with { Columns = [new TableColumn { ColumnName = "client_id", DataType = "varchar" }, new TableColumn { ColumnName = "dept", DataType = deptType }] }
    ];

    [Theory]
    [InlineData("integer")]
    [InlineData("uniqueidentifier")]
    [InlineData("bit")]
    public void ST_129_05_V1294_TextAttribute_AgainstANonTextSourceColumn_IsRefusedAtValidate_WithoutValues(string columnType)
    {
        var ex = Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(Scalar), TypedCatalog(columnType), callerAttributes: Attributes()));
        ex.Message.ShouldContain("type");
        ex.Message.ShouldNotContain("{{caller");
    }

    [Fact]
    public void ST_129_05_V1294_TextAttribute_AgainstATextSourceColumn_IsAccepted() =>
        SqlFilterCompiler.Validate(SqlFilter(Scalar), TypedCatalog("nvarchar(50)"), callerAttributes: Attributes()).Sql.ShouldBe(Scalar);

    [Theory]
    [InlineData("from conf.client client where target.client_id = client.client_id and (client.dept = {{caller.dept}} or client.level = 1)", "")]
    [InlineData("from conf.client client where target.client_id = client.client_id and not (client.dept = {{caller.dept}})", "top-level")]
    [InlineData("from conf.client client where target.client_id = client.client_id and case when client.dept = {{caller.dept}} then 1 else 0 end = 1", "top-level")]
    [InlineData("from conf.client client where target.client_id = client.client_id and lower(client.dept) = lower({{caller.dept}})", "top-level")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.dept like {{caller.dept}}", "top-level")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.level between {{caller.dept}} and 5", "top-level")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.region in ({{caller.regions}}, 'x')", "top-level")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.region in ('x', {{caller.regions}})", "top-level")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.region in {{caller.regions}}", "")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.dept = '{{caller.dept}}'", "literal")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.dept = '__autheris_caller__dept'", "reserved")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.dept = {{caller.dept}} and client.x = {{caller.dept}} + 1", "top-level")]
    [InlineData("from conf.client c1 join conf.client c2 on c2.dept = {{caller.dept}} where target.client_id = c1.client_id", "top-level")]
    public void ST_129_05_ForbiddenPositionsAndSpellings_AreRefusedAtValidate(string sql, string reason)
    {
        var ex = Should.Throw<Exception>(() => Validate(sql));
        (ex is ArgumentException or TrinoSqlEngine.Ast.Builder.AstBuildException).ShouldBeTrue(ex.GetType().Name);
        if (reason.Length > 0) ex.Message.ShouldContain(reason, Case.Insensitive);
    }

    [Fact]
    public void ListAttribute_InAScalarComparison_IsRefused() =>
        Should.Throw<ArgumentException>(() => Validate("from conf.client client where target.client_id = client.client_id and client.region = {{caller.regions}}"))
            .Message.ShouldContain("list caller attribute");

    [Fact]
    public void UnregisteredAttribute_IsRefused() =>
        Should.Throw<ArgumentException>(() => Validate("from conf.client client where target.client_id = client.client_id and client.dept = {{caller.unknown}}"))
            .Message.ShouldContain("not registered");

    [Fact]
    public void AttributeOfAnotherTenant_IsRefused()
    {
        var other = new CallerAttributeSnapshot(1, [CallerAttributeTestSupport.Definition("dept", "claims", CallerAttributeType.Text, CallerAttributeCardinality.Scalar, 1, 60) with { Tenant = new TenantId("tenant_b") }], []);
        Should.Throw<ArgumentException>(() => Validate(Scalar, other));
    }

    [Fact]
    public void FeatureOff_NoSnapshot_RefusesAPlaceholder() =>
        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(SqlFilter(Scalar), Catalog)).Message.ShouldContain("not available");

    [Fact]
    public void PlaceholderOnlyAsIdentifierLookalike_ParametersRuleStillHolds() =>
        Should.Throw<ArgumentException>(() => Validate("from conf.client client where target.client_id = client.client_id and client.dept = @dept"));

    [Theory]
    [InlineData("from conf.client client where target.client_id = client.client_id and (client.dept = {{caller.dept}} or client.level = 1)")]
    [InlineData("from conf.client client where target.client_id = client.client_id and not (client.dept = {{caller.dept}})")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.dept = '__autheris_caller__dept'")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.dept = '{{caller.dept}}'")]
    [InlineData("from conf.client client where target.client_id = client.client_id and lower(client.dept) = lower({{caller.dept}})")]
    public void ST_129_05_AStoredDefinitionOfAForbiddenShape_IsRefusedAtCompileTyped_AndTheTextIsTheConstant(string sql)
    {
        var stored = SqlFilter(sql);   // bypassed Validate (a stored row, a direct DB write, a definition predating the feature)
        Should.Throw<Exception>(() => SqlFilterCompiler.CompileTyped(stored, DatabaseDialect.PostgreSql, Engine));
        SqlFilterCompiler.Compile(stored, DatabaseDialect.PostgreSql).ShouldBe(SqlFilterCompiler.CallerAttributeFilterText);
    }

    [Theory]
    [InlineData(Scalar)]
    [InlineData(InList)]
    [InlineData(NotIn)]
    public void ST_129_06_TheTextForm_IsTheDenyAllConstant_NeverASentinelOrValue(string sql)
    {
        var text = SqlFilterCompiler.Compile(Validate(sql), DatabaseDialect.PostgreSql);
        text.ShouldBe("(1 = 0)");
        text.ShouldNotContain("__autheris");
        text.ShouldNotContain("{{");
    }

    [Fact]
    public void CompileTyped_ProducesNullTypedSlots_WithTheReservedName()
    {
        var typed = SqlFilterCompiler.CompileTyped(Validate(InList), DatabaseDialect.PostgreSql, Engine);
        typed.Parameters.Keys.ShouldContain("__gql_caller_regions__0");
        typed.Parameters["__gql_caller_regions__0"].Type.ShouldBe(SqlParameterType.Null);
        typed.Parameters["__gql_caller_regions__0"].Value.ShouldBeNull();
        CallerAttributeBinder.SlotAttributes(typed).ShouldBe(["regions"]);
        Renderer.Render(typed, TargetSqlDialect.PostgreSql, RowFilterAliases.Target).Sql.ShouldNotContain("__autheris_caller__");
    }

    [Fact]
    public void ADefinitionWithoutPlaceholders_IsUnchanged()
    {
        var filter = SqlFilter("from conf.client client where target.client_id = client.client_id and client.region = 'EU'");
        var typed = SqlFilterCompiler.CompileTyped(filter, DatabaseDialect.PostgreSql, Engine);
        CallerAttributeBinder.SlotAttributes(typed).ShouldBeEmpty();
        SqlFilterCompiler.Compile(filter, DatabaseDialect.PostgreSql).ShouldStartWith("EXISTS (");
    }

    // ---- binder -------------------------------------------------------------------------------------------------------------------------

    private static CallerAttributeSet Set(string name, params string[] values) => new(new Dictionary<string, CallerAttributeResult>
    {
        [name] = CallerAttributeResult.Resolved(new CallerAttributeValue(values.Cast<object>().ToList(), SqlParameterType.String))
    });

    private static PolicyPredicate Typed(string sql) => SqlFilterCompiler.CompileTyped(Validate(sql), DatabaseDialect.PostgreSql, Engine);

    [Fact]
    public void Bind_Scalar_ReplacesTheSlotWithTheValue()
    {
        var bound = CallerAttributeBinder.Bind(Typed(Scalar), Set("dept", "D1"), CallerAttributePadding.Default);
        bound.Parameters["__gql_caller_dept__0"].Value.ShouldBe("D1");
        bound.Parameters["__gql_caller_dept__0"].Type.ShouldBe(SqlParameterType.String);
        CallerAttributeBinder.SlotAttributes(bound).ShouldBeEmpty();
    }

    [Fact]
    public void Bind_InList_ExpandsPadded()
    {
        var bound = CallerAttributeBinder.Bind(Typed(InList), Set("regions", "EU", "US", "APAC"), CallerAttributePadding.Default);
        bound.Parameters.Keys.Where(k => k.StartsWith("__gql_caller_regions__")).Count().ShouldBe(4);
        Renderer.Render(bound, TargetSqlDialect.PostgreSql, RowFilterAliases.Target).Sql.ShouldNotContain("EU");
    }

    [Fact]
    public void Bind_ScalarWithMoreThanOneValue_IsDenyAll()
    {
        var bound = CallerAttributeBinder.Bind(Typed(Scalar), Set("dept", "D1", "D2"), CallerAttributePadding.Default);
        bound.Fingerprint.ShouldBe(PolicyPredicate.DenyAll.Fingerprint);
    }

    public static IEnumerable<object[]> Unresolved() =>
    [
        [CallerAttributeResult.Missing()], [CallerAttributeResult.Failed("x")], [CallerAttributeResult.OverCardinality()],
        [CallerAttributeResult.Unknown()], [CallerAttributeResult.NoSubject()]
    ];

    [Theory]
    [MemberData(nameof(Unresolved))]
    public void Bind_AnyUnresolvedSlot_IsDenyAll_ForTheWholePredicate(CallerAttributeResult result)
    {
        var bound = CallerAttributeBinder.Bind(Typed(InList), new CallerAttributeSet(new Dictionary<string, CallerAttributeResult> { ["regions"] = result }), CallerAttributePadding.Default);
        bound.Fingerprint.ShouldBe(PolicyPredicate.DenyAll.Fingerprint);
        bound.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void ST_129_05_ABinderThatThrows_ReturnsDenyAll()
    {
        var bound = CallerAttributeBinder.Bind(Typed(InList), null!, CallerAttributePadding.Default);
        bound.Fingerprint.ShouldBe(PolicyPredicate.DenyAll.Fingerprint);
    }

    [Fact]
    public void Bind_APredicateWithoutSlots_IsReturnedAsIs()
    {
        var plain = SqlFilterCompiler.CompileTyped(SqlFilter("from conf.client client where target.client_id = client.client_id and client.region = 'EU'"), DatabaseDialect.PostgreSql, Engine);
        CallerAttributeBinder.Bind(plain, CallerAttributeSet.Empty, CallerAttributePadding.Default).ShouldBeSameAs(plain);
    }

    [Fact]
    public void ST_129_14_TheBinder_CannotReplaceTheTenantParameterOrAClientParameter()
    {
        // A predicate whose parameters carry a tenant value and a Null typed value under a non reserved name: neither is a slot.
        var typed = SqlFilterCompiler.CompileTyped(SqlFilter("from conf.client client where target.client_id = client.client_id and client.region = 'EU'"), DatabaseDialect.PostgreSql, Engine);
        var bound = CallerAttributeBinder.Bind(typed, Set("regions", "X"), CallerAttributePadding.Default);
        bound.Parameters.Values.Select(v => v.Value).ShouldBe(typed.Parameters.Values.Select(v => v.Value));
    }
}
