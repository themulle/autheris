using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;
using static TrinoSqlEngine.Ast.Nodes.BinaryOperator;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation gate (M-12) tests for the DML part of <see cref="SecurityCoverageVerifier"/> (INV-2, INV-10, SEC-ADG-06/08): the verifier
/// proves on the final tree that every write is confined to the tenant and the policy, and rejects with the typed reason otherwise.
/// </summary>
[Trait("Category", "SqlGate")]
public class SecurityCoverageVerifierDmlMutationTests
{
    private const string TenantName = "__tenant";
    private static readonly SecurityPredicateId Root = new("dbo.t", 0);
    private static readonly SecurityPredicateId Policy = new("dbo.t", 1);
    private static readonly SecurityPredicateId UTenant = new("dbo.u", 0);
    private static readonly SecurityPredicateId UPolicySubquery = new("dbo.u", 5);

    private sealed record UnknownClause(Expression? Condition) : MergeClause(Condition);

    private static SqlIdentifier Q(string name) => new(name, true);

    private static ColumnReference Col(string name) => new(new SqlQualifiedName(new[] { Q(name) }));

    private static ColumnReference Col(string table, string name) => new(new SqlQualifiedName(new[] { Q(table), Q(name) }));

    private static LiteralExpression Int(long v) => new(v, LiteralType.Integer);

    private static LiteralExpression Str(string v) => new(v, LiteralType.String);

    private static Expression Tenant() => new PolicyParameterExpression(TenantName, SqlParameterType.String, ParameterOrigin.Tenant);

    private static SqlQualifiedName Canonical(string table = "t") => new(new[] { Q("dbo"), Q(table) });

    private static NamedTableSource Target(string? alias = null, string table = "t") => new(Canonical(table), alias is null ? null : Q(alias));

    private static Expression Pred(SecurityPredicateId id, SecurityScope scope) =>
        new SecurityPredicateExpression(new BinaryExpression(Col("tenant"), Equal, Tenant()), id, scope);

    private static readonly ImmutableHashSet<string> Columns = ImmutableHashSet.Create("id", "name", "secret", "tenant", "amount");

    private static TableCoverageRequirement TRequirement(bool withPolicy = false, ImmutableHashSet<string>? masked = null) => new(
        "dbo.t", "dbo", "t",
        withPolicy ? ImmutableArray.Create(Root, Policy) : ImmutableArray.Create(Root),
        ImmutableArray.Create(Root),
        MaskedColumns: masked ?? ImmutableHashSet.Create("secret"),
        CatalogColumns: Columns,
        TenantColumn: "tenant",
        PolicyColumns: ImmutableHashSet.Create("amount"),
        ColumnTypes: ImmutableDictionary<string, string>.Empty.Add("id", "int").Add("name", "nvarchar(20)").Add("tenant", "nvarchar(64)"));

    private static SecurityCoverageVerifier Verifier(
        bool withPolicy = false, DmlGuardOptions? dml = null, int maxSecured = 256, bool noMasks = false, TargetSqlDialect dialect = TargetSqlDialect.SqlServer) => new(name =>
    {
        if (name.Parts.Count == 0) return null;
        return name.SimpleName switch
        {
            "t" => TRequirement(withPolicy, noMasks ? ImmutableHashSet<string>.Empty : null),
            "u" => new TableCoverageRequirement("dbo.u", "dbo", "u", ImmutableArray.Create(UTenant), ImmutableArray.Create(UPolicySubquery), CatalogColumns: ImmutableHashSet.Create("id")),
            _ => null
        };
    }, dialect, maxSecured, dml: dml, tenantParameterName: TenantName);

    private static void AssertRejected(SqlStatement s, string message, bool withPolicy = false, DmlGuardOptions? dml = null) =>
        Assert.Equal(message, Assert.Throws<SecurityCoverageException>(() => Verifier(withPolicy, dml).Verify(s, CancellationToken.None)).Message);

    private static void Verify(SqlStatement s, bool withPolicy = false, DmlGuardOptions? dml = null) =>
        Verifier(withPolicy, dml).Verify(s, CancellationToken.None);

    private static DmlGuardOptions Strict => DmlGuardOptions.Strict;

    // ---- DELETE / UPDATE ----

    private static DeleteStatement Delete(Expression? where) => new(Target(), where);

    private static UpdateStatement Update(IReadOnlyList<UpdateAssignment> assignments, Expression? where = null) =>
        new(Target(), assignments, where ?? Pred(Root, SecurityScope.DmlTarget));

    private static UpdateAssignment Set(string column, Expression value) => new(Q(column), value);

    [Fact]
    public void Delete_WithItsPredicate_Passes_AndReportsTheAppliedId()
    {
        Assert.Equal(new[] { Root }, Verifier().Verify(Delete(Pred(Root, SecurityScope.DmlTarget)), CancellationToken.None));
    }

    [Fact]
    public void Delete_WithoutAWhere_IsRejected_WhenUnfilteredDmlIsRejectedOrThePolicyRequiresOne()
    {
        const string message = "An UPDATE or DELETE without a WHERE clause is not permitted.";
        AssertRejected(Delete(null), message);                                                    // strict
        AssertRejected(Delete(null), message, dml: Strict with { RejectUnfilteredDml = false });  // the injected predicate is still required
    }

    [Fact]
    public void Delete_WithoutAWhere_NoPredicatesRequired_IsOnlyRejectedByTheUnfilteredOption()
    {
        var verifier = new SecurityCoverageVerifier(
            _ => new TableCoverageRequirement("dbo.t", "dbo", "t", ImmutableArray<SecurityPredicateId>.Empty, ImmutableArray<SecurityPredicateId>.Empty, CatalogColumns: Columns),
            dml: Strict with { RejectUnfilteredDml = false }, tenantParameterName: TenantName);

        verifier.Verify(Delete(null), CancellationToken.None);

        var strict = new SecurityCoverageVerifier(
            _ => new TableCoverageRequirement("dbo.t", "dbo", "t", ImmutableArray<SecurityPredicateId>.Empty, ImmutableArray<SecurityPredicateId>.Empty, CatalogColumns: Columns),
            tenantParameterName: TenantName);
        Assert.Equal("An UPDATE or DELETE without a WHERE clause is not permitted.", Assert.Throws<SecurityCoverageException>(() => strict.Verify(Delete(null), CancellationToken.None)).Message);
    }

    [Fact]
    public void Delete_WithTheWrongOrMissingPredicate_IsRejected_WithTheTypedReason()
    {
        const string message = "A DML target is missing a required security predicate.";
        AssertRejected(Delete(new BinaryExpression(Col("id"), Equal, Int(1))), message);
        AssertRejected(Delete(Pred(Root, SecurityScope.Root)), message);
        AssertRejected(Delete(Pred(new SecurityPredicateId("dbo.t", 9), SecurityScope.DmlTarget)), message);
    }

    [Fact]
    public void Delete_PredicateInParenthesesOrAndChain_IsAccepted()
    {
        Verify(Delete(new ParenthesizedExpression(Pred(Root, SecurityScope.DmlTarget))));
        Verify(Delete(new BinaryExpression(Col("id"), And, new ParenthesizedExpression(Pred(Root, SecurityScope.DmlTarget)))));
        Assert.Throws<SecurityCoverageException>(() => Verify(Delete(new BinaryExpression(Pred(Root, SecurityScope.DmlTarget), Or, Col("id")))));
    }

    [Fact]
    public void UnresolvedDmlTarget_IsRejected()
    {
        var stmt = new DeleteStatement(Target(table: "ghost"), Pred(Root, SecurityScope.DmlTarget));
        AssertRejected(stmt, "A DML target could not be resolved against the catalog.");
    }

    [Fact]
    public void SecuredReferenceLimit_AppliesToDmlTargets()
    {
        var ex = Assert.Throws<SqlLimitExceededException>(() =>
            Verifier(maxSecured: 0).Verify(Delete(Pred(Root, SecurityScope.DmlTarget)), CancellationToken.None));
        Assert.Equal(SqlLimitKind.SecuredTableReferences, ex.Kind);
        Assert.Equal(1, ex.Requested);
        Assert.Equal(0, ex.Maximum);
    }

    [Fact]
    public void Update_WithoutAssignments_IsRejected()
    {
        AssertRejected(Update(Array.Empty<UpdateAssignment>()), "An UPDATE without assignments cannot be verified.");
    }

    [Fact]
    public void Update_AssigningAColumnTwice_IsRejected()
    {
        AssertRejected(Update(new[] { Set("name", Str("a")), Set("name", Str("b")) }), "A column is assigned more than once.");
    }

    [Fact]
    public void Update_WritingAMaskedColumn_IsRejected_UnlessMaskedWritesAreAllowed()
    {
        AssertRejected(Update(new[] { Set("secret", Str("a")) }), "A masked column is written by the statement.");
        Verify(Update(new[] { Set("secret", Str("a")) }), dml: Strict with { RejectMaskedColumnsInDml = false, RejectMaskedColumnsInPredicates = false });
    }

    [Fact]
    public void Update_AssigningAnUncatalogedOrUndelimitedColumn_IsRejected()
    {
        const string message = "A DML column is not a cataloged column emitted as a delimited identifier.";
        AssertRejected(Update(new[] { Set("ghost", Str("a")) }), message);
        AssertRejected(Update(new[] { new UpdateAssignment(new SqlIdentifier("name", false), Str("a")) }), message);
    }

    [Fact]
    public void Update_AssigningThePolicyColumn_IsRejected_UnlessAllowed()
    {
        AssertRejected(Update(new[] { Set("amount", Int(1)) }), "A column referenced by the row policy is assigned by the statement.");
        Verify(Update(new[] { Set("amount", Int(1)) }), dml: Strict with { RejectPolicyColumnAssignment = false });
    }

    [Theory]
    [InlineData(true, true, false)]    // modification disallowed: even the bound tenant is rejected
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]    // allowed, and the value is the bound tenant
    [InlineData(false, false, false)]  // allowed, but the value is not the bound tenant
    public void Update_TenantColumnAssignment_OnlyTheBoundTenantAndOnlyWhenAllowed(bool disallow, bool boundTenant, bool accepted)
    {
        var dml = Strict with { DisallowTenantColumnModificationInUpdate = disallow };
        var stmt = Update(new[] { Set("tenant", boundTenant ? Tenant() : Str("other")) });

        if (accepted)
        {
            Verify(stmt, dml: dml);
        }
        else
        {
            AssertRejected(stmt, "The tenant column is assigned by the statement.", dml: dml);
        }
    }

    [Fact]
    public void Update_TenantParameterOfAnotherNameOrOrigin_IsNotTheBoundTenant()
    {
        var dml = Strict with { DisallowTenantColumnModificationInUpdate = false };
        var otherName = new PolicyParameterExpression("__other", SqlParameterType.String, ParameterOrigin.Tenant);
        var otherOrigin = new PolicyParameterExpression(TenantName, SqlParameterType.String);
        AssertRejected(Update(new[] { Set("tenant", otherName) }), "The tenant column is assigned by the statement.", dml: dml);
        AssertRejected(Update(new[] { Set("tenant", otherOrigin) }), "The tenant column is assigned by the statement.", dml: dml);
    }

    [Fact]
    public void Update_ReadingAMaskedColumn_InAnAssignmentOrTheWhere_IsRejected()
    {
        AssertRejected(Update(new[] { Set("name", Col("t", "secret")) }), "A masked column of the DML target is read by the statement.");
        var where = new BinaryExpression(Pred(Root, SecurityScope.DmlTarget), And, new BinaryExpression(Col("secret"), Equal, Int(1)));
        AssertRejected(Update(new[] { Set("name", Str("a")) }, where), "A masked column of the DML target is read by the statement.");
        Verify(Update(new[] { Set("name", Col("t", "id")) }));
    }

    [Fact]
    public void Update_UnsecuredSubqueryInAnAssignmentOrTheWhere_IsRejected()
    {
        Expression Bad() => new ExistsExpression(new SelectStatement(null, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
            new NamedTableSource(Canonical("u"), null), null, null, null), null, null));

        Assert.Throws<SecurityCoverageException>(() => Verify(Update(new[] { Set("name", Bad()) })));
        Assert.Throws<SecurityCoverageException>(() => Verify(Update(new[] { Set("name", Str("a")) },
            new BinaryExpression(Pred(Root, SecurityScope.DmlTarget), And, Bad()))));
        Assert.Throws<SecurityCoverageException>(() => Verify(Delete(new BinaryExpression(Pred(Root, SecurityScope.DmlTarget), And, Bad()))));
    }

    // ---- INSERT ----

    private static InsertStatement InsertValues(params Expression[][] rows) => new(
        Target(), new[] { Q("id"), Q("tenant") }, new ValuesQueryBody(rows.Select(r => new RowValueExpression(r)).ToList()));

    [Fact]
    public void Insert_WithTheBoundTenant_Passes()
    {
        Verify(InsertValues(new Expression[] { Int(1), Tenant() }, new Expression[] { Int(2), Tenant() }));
    }

    [Fact]
    public void Insert_WithATargetAlias_IsRejected()
    {
        var stmt = InsertValues(new Expression[] { Int(1), Tenant() }) with { TargetTable = Target("a") };
        AssertRejected(stmt, "An INSERT target must not have an alias.");
    }

    [Fact]
    public void Insert_WithoutAColumnList_IsRejected()
    {
        var stmt = InsertValues(new Expression[] { Int(1), Tenant() });
        AssertRejected(stmt with { Columns = null }, "A governed INSERT must list its target columns.");
        AssertRejected(stmt with { Columns = Array.Empty<SqlIdentifier>() }, "A governed INSERT must list its target columns.");
    }

    [Fact]
    public void Insert_ColumnListProblems_AreRejected_WithTheTypedReason()
    {
        var row = new Expression[] { Int(1), Tenant() };
        var stmt = InsertValues(row);
        AssertRejected(stmt with { Columns = new[] { Q("id"), Q("id") } }, "A column is listed more than once.");
        AssertRejected(stmt with { Columns = new[] { Q("secret"), Q("tenant") } }, "A masked column is written by the statement.");
        AssertRejected(stmt with { Columns = new[] { Q("id"), Q("name") } }, "An INSERT does not carry the bound tenant.");
        AssertRejected(stmt with { Columns = new[] { Q("id"), Q("ghost") } }, "A DML column is not a cataloged column emitted as a delimited identifier.");
    }

    [Fact]
    public void Insert_IntoATableWithAConsentPolicy_NeedsTheCheckOptionShape_ElseIsRejectedByTheOption()
    {
        // with the policy predicate (ordinal 1) a plain VALUES insert has no check-option shape
        var stmt = InsertValues(new Expression[] { Int(1), Tenant() });
        AssertRejected(stmt, "An INSERT into a table with a row policy does not have the check-option shape.", withPolicy: true);
    }

    [Fact]
    public void Insert_ValuesRows_MustMatchTheColumnCount_AndCarryTheBoundTenant_AtAnyPosition()
    {
        const string message = "An INSERT row does not carry the bound tenant.";
        AssertRejected(InsertValues(new Expression[] { Int(1), Str("other") }), message);
        AssertRejected(InsertValues(new Expression[] { Int(1) }), message);
        AssertRejected(InsertValues(new Expression[] { Int(1), Tenant(), Int(3) }), message);
        AssertRejected(InsertValues(new Expression[] { Int(1), Tenant() }, new Expression[] { Int(2), Str("other") }), message);

        // the tenant column listed first (index 0) is checked as well
        var tenantFirst = new InsertStatement(Target(), new[] { Q("tenant"), Q("id") },
            new ValuesQueryBody(new[] { new RowValueExpression(new Expression[] { Str("other"), Int(1) }) }));
        AssertRejected(tenantFirst, message);
        Verify(new InsertStatement(Target(), new[] { Q("tenant"), Q("id") },
            new ValuesQueryBody(new[] { new RowValueExpression(new Expression[] { Tenant(), Int(1) }) })));
    }

    [Fact]
    public void Insert_WithoutRows_IsRejected()
    {
        AssertRejected(new InsertStatement(Target(), new[] { Q("id"), Q("tenant") }, new ValuesQueryBody(Array.Empty<RowValueExpression>())),
            "An INSERT VALUES clause without rows cannot be verified.");
    }

    private static QuerySpecification SelectRow(params Expression[] values) =>
        new(false, values.Select(v => (SelectItem)new ColumnSelectItem(v, null)).ToList(), null, null, null, null);

    [Fact]
    public void Insert_Select_MustProjectTheColumns_AndTheBoundTenant_AtAnyPosition()
    {
        InsertStatement With(QueryBody body, params string[] columns) => new(Target(), columns.Select(Q).ToList(), body);

        Verify(With(SelectRow(Int(1), Tenant()), "id", "tenant"));
        Verify(With(SelectRow(Tenant(), Int(1)), "tenant", "id"));
        AssertRejected(With(SelectRow(Int(1), Str("other")), "id", "tenant"), "An INSERT SELECT does not carry the bound tenant.");
        AssertRejected(With(SelectRow(Str("other"), Int(1)), "tenant", "id"), "An INSERT SELECT does not carry the bound tenant.");
        AssertRejected(With(SelectRow(Int(1)), "id", "tenant"), "An INSERT SELECT projection cannot be verified (wildcard or column count).");
        AssertRejected(With(new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(Int(1), null), new WildcardSelectItem(null) }, null, null, null, null), "id", "tenant"),
            "An INSERT SELECT projection cannot be verified (wildcard or column count).");
    }

    [Fact]
    public void Insert_SetOperationSource_VerifiesBothBranches()
    {
        InsertStatement With(QueryBody body) => new(Target(), new[] { Q("id"), Q("tenant") }, body);
        var good = SelectRow(Int(1), Tenant());
        var bad = SelectRow(Int(1), Str("other"));

        Verify(With(new SetOperationQuery(good, SetOperator.Union, false, good)));
        Assert.Throws<SecurityCoverageException>(() => Verify(With(new SetOperationQuery(bad, SetOperator.Union, false, good))));
        Assert.Throws<SecurityCoverageException>(() => Verify(With(new SetOperationQuery(good, SetOperator.Union, false, bad))));
    }

    // ---- INSERT with the check option (CR-ADG-35, CR-ADG-42) ----

    private static string Native(string catalogType)
    {
        Assert.True(CatalogTypeMap.TryResolve(TargetSqlDialect.SqlServer, catalogType, out string native, out _));
        return native;
    }

    private static Expression Cast(Expression value, string catalogType) => new CastExpression(value, Native(catalogType), IsNativeType: true);

    private static QuerySpecification CheckRow(Expression? id = null, Expression? name = null, Expression? tenant = null) =>
        SelectRow(id ?? Cast(Int(1), "int"), name ?? Cast(Str("a"), "nvarchar(20)"), tenant ?? Tenant());

    private static Expression CheckPredicate(Expression? inner = null, SecurityScope scope = SecurityScope.InsertCheck, SecurityPredicateId? id = null) =>
        new SecurityPredicateExpression(inner ?? new BinaryExpression(Int(1), Equal, Int(1)), id ?? Policy, scope);

    private static InsertStatement CheckInsert(
        QueryBody? rows = null, Expression? where = null, IReadOnlyList<SelectItem>? projections = null, SqlIdentifier? alias = null,
        IReadOnlyList<SqlIdentifier>? columns = null)
    {
        alias ??= AstSecurityVisitor.InsertCheckAlias;
        columns ??= new[] { Q("id"), Q("name"), Q("tenant") };
        projections ??= columns.Select(c => (SelectItem)new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { alias, c })), null)).ToList();
        var derived = new SubqueryTableSource(new SelectStatement(null, rows ?? CheckRow(), null, null), alias);
        return new InsertStatement(Target(), columns, new QuerySpecification(false, projections, derived, where ?? CheckPredicate(), null, null));
    }

    private static void AssertCheckRejected(InsertStatement stmt, string message) => AssertRejected(stmt, message, withPolicy: true);

    [Fact]
    public void CheckOptionInsert_WithTheExpectedShape_Passes_AndReportsBothPredicateIds()
    {
        var applied = Verifier(withPolicy: true).Verify(CheckInsert(), CancellationToken.None);
        Assert.Contains(Policy, applied);
        Verifier(withPolicy: true).Verify(CheckInsert(rows: new SetOperationQuery(CheckRow(), SetOperator.Union, false, CheckRow())), CancellationToken.None);
    }

    [Fact]
    public void CheckOptionInsert_OfAnotherShape_IsRejected()
    {
        const string message = "An INSERT into a table with a row policy does not have the check-option shape.";
        AssertCheckRejected(CheckInsert(alias: Q("other")), message);
        var derived = (SubqueryTableSource)((QuerySpecification)CheckInsert().Source).From!;
        var spec = (QuerySpecification)CheckInsert().Source;
        AssertCheckRejected(CheckInsert() with { Source = spec with { Distinct = true } }, message);
        AssertCheckRejected(CheckInsert() with { Source = spec with { Having = Int(1) } }, message);
        AssertCheckRejected(CheckInsert() with { Source = spec with { From = derived with { ColumnAliases = new[] { Q("x") } } } }, message);
        AssertCheckRejected(CheckInsert() with { Source = spec with { From = derived with { Subquery = derived.Subquery with { OrderBy = new OrderByClause(new[] { new OrderByElement(Int(1)) }) } } } }, message);
    }

    [Fact]
    public void CheckOptionInsert_ProjectionMustMatchTheColumnList()
    {
        const string message = "An INSERT check-option projection does not match the column list.";
        var alias = AstSecurityVisitor.InsertCheckAlias;
        ColumnSelectItem Item(string table, string column) => new(new ColumnReference(new SqlQualifiedName(new[] { Q(table), Q(column) })), null);

        AssertCheckRejected(CheckInsert(projections: new SelectItem[] { Item(alias.Value, "id"), Item(alias.Value, "name") }), message);
        AssertCheckRejected(CheckInsert(projections: new SelectItem[] { Item(alias.Value, "id"), Item(alias.Value, "tenant"), Item(alias.Value, "name") }), message);
        AssertCheckRejected(CheckInsert(projections: new SelectItem[] { Item(alias.Value, "id"), Item("other", "name"), Item(alias.Value, "tenant") }), message);
        AssertCheckRejected(CheckInsert(projections: new SelectItem[] { Item(alias.Value, "id"), new ColumnSelectItem(Col("name"), null), Item(alias.Value, "tenant") }), message);
    }

    [Fact]
    public void CheckOptionInsert_WithoutItsCheckPredicate_IsRejected()
    {
        const string message = "An INSERT into a table with a row policy is missing its check predicate.";
        AssertCheckRejected(CheckInsert(where: CheckPredicate(scope: SecurityScope.Root)), message);
        AssertCheckRejected(CheckInsert(where: CheckPredicate(id: Root)), message);
        AssertCheckRejected(CheckInsert(where: new BinaryExpression(Col("id"), Equal, Int(1))), message);
    }

    [Fact]
    public void CheckOptionInsert_StringComparisonOtherThanByteExact_IsRejected()
    {
        var bare = new BinaryExpression(new ColumnReference(new SqlQualifiedName(new[] { AstSecurityVisitor.InsertCheckAlias, Q("name") })), Equal, new PolicyParameterExpression("p", SqlParameterType.String));
        AssertCheckRejected(CheckInsert(where: CheckPredicate(bare)), "An INSERT check compares a string column otherwise than byte-exact.");
    }

    [Fact]
    public void CheckOptionInsert_ColumnWithoutACatalogType_IsRejected()
    {
        var requirementWithoutTypes = TRequirement(withPolicy: true) with { ColumnTypes = ImmutableDictionary<string, string>.Empty.Add("id", "int") };
        var verifier = new SecurityCoverageVerifier(_ => requirementWithoutTypes, tenantParameterName: TenantName);
        Assert.Equal("An INSERT check-option column has no known catalog type to cast to.",
            Assert.Throws<SecurityCoverageException>(() => verifier.Verify(CheckInsert(), CancellationToken.None)).Message);

        var unknownType = TRequirement(withPolicy: true) with { ColumnTypes = TRequirement().ColumnTypes!.SetItem("name", "geometry-ish") };
        var verifier2 = new SecurityCoverageVerifier(_ => unknownType, tenantParameterName: TenantName);
        Assert.Throws<SecurityCoverageException>(() => verifier2.Verify(CheckInsert(), CancellationToken.None));
    }

    [Fact]
    public void CheckOptionInsert_RowProblems_AreRejected_WithTheTypedReason()
    {
        AssertCheckRejected(CheckInsert(rows: SelectRow(Cast(Int(1), "int"), Tenant())), "An INSERT check-option row does not match the column list.");
        AssertCheckRejected(CheckInsert(rows: CheckRow(tenant: Str("other"))), "An INSERT row does not carry the bound tenant.");
        AssertCheckRejected(CheckInsert(rows: CheckRow(id: Int(1))), "An INSERT check-option value is not cast to the catalog type of its column.");
        AssertCheckRejected(CheckInsert(rows: CheckRow(name: new CastExpression(Str("a"), Native("nvarchar(20)"), IsTryCast: true, IsNativeType: true))),
            "An INSERT check-option value is not cast to the catalog type of its column.");
        AssertCheckRejected(CheckInsert(rows: CheckRow(name: new CastExpression(Str("a"), Native("nvarchar(20)"), IsNativeType: false))),
            "An INSERT check-option value is not cast to the catalog type of its column.");
        AssertCheckRejected(CheckInsert(rows: CheckRow(name: Cast(Str("a"), "int"))), "An INSERT check-option value is not cast to the catalog type of its column.");
        AssertCheckRejected(CheckInsert(rows: new SetOperationQuery(CheckRow(), SetOperator.Union, true, CheckRow())), "An INSERT check-option source shape cannot be verified.");
    }

    // ---- MERGE ----

    private static SubqueryTableSource SecuredU(SqlIdentifier alias, Expression? where = null) =>
        new(new SelectStatement(null,
                new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(Col("id"), Q("id")) },
                    new NamedTableSource(Canonical("u"), null), where ?? Pred(UTenant, SecurityScope.MergeSource), null, null), null, null), alias);

    private static MergeStatement MergeWith(string? alias, SqlIdentifier sourceAlias, params MergeClause[] clauses) =>
        new(Target(alias), SecuredU(sourceAlias), Pred(Root, SecurityScope.MergeOn), clauses);

    private static MergeStatement MergeOf(params MergeClause[] clauses) => MergeWith("tgt", Q("src"), clauses);

    [Fact]
    public void Merge_WithTheExpectedShape_Passes()
    {
        Verify(MergeOf(
            new MergeUpdateClause(null, new[] { Set("name", Col("src", "id")) }),
            new MergeDeleteClause(Col("src", "id")),
            new MergeInsertClause(null, new[] { Q("id"), Q("tenant") }, new Expression[] { Col("src", "id"), Tenant() })));
    }

    [Fact]
    public void Merge_TargetWithoutAnAlias_IsRejected()
    {
        AssertRejected(MergeWith(null, Q("src"), new MergeDeleteClause(null)), "A MERGE target must have an alias so that its predicates can be qualified.");
    }

    [Theory]
    [InlineData("tgt")]
    [InlineData("TGT")]
    [InlineData("t")]
    public void Merge_SourceQualifierEqualToTheTargetNames_IsRejected(string sourceAlias)
    {
        AssertRejected(MergeWith("tgt", Q(sourceAlias), new MergeDeleteClause(null)), "A MERGE source alias equals the target alias or table name.");
    }

    [Fact]
    public void Merge_WithoutClauses_IsRejected()
    {
        AssertRejected(MergeOf(), "A MERGE without WHEN clauses cannot be verified.");
    }

    [Fact]
    public void Merge_OnWithoutThePredicate_IsRejected()
    {
        var stmt = MergeOf(new MergeDeleteClause(null)) with { On = new BinaryExpression(Col("id"), Equal, Int(1)) };
        AssertRejected(stmt, "A DML target is missing a required security predicate.");
    }

    [Fact]
    public void Merge_ReadingAMaskedColumn_InTheOnOrAClauseCondition_IsRejected()
    {
        const string message = "A masked column of the DML target is read by the statement.";
        var secret = new BinaryExpression(Col("tgt", "secret"), Equal, Int(1));
        AssertRejected(MergeOf(new MergeDeleteClause(secret)), message);
        AssertRejected(MergeOf(new MergeDeleteClause(null)) with { On = new BinaryExpression(Pred(Root, SecurityScope.MergeOn), And, secret) }, message);
        AssertRejected(MergeOf(new MergeUpdateClause(null, new[] { Set("name", Col("tgt", "secret")) })), message);
        AssertRejected(MergeOf(new MergeInsertClause(null, new[] { Q("id"), Q("tenant") }, new Expression[] { Col("tgt", "secret"), Tenant() })), message);
    }

    [Fact]
    public void Merge_InsertClause_MustCarryTheBoundTenant_AtAnyPosition_AndMatchTheColumnCount()
    {
        const string message = "A MERGE INSERT clause does not carry the bound tenant.";
        AssertRejected(MergeOf(new MergeInsertClause(null, new[] { Q("id"), Q("tenant") }, new Expression[] { Int(1), Str("other") })), message);
        AssertRejected(MergeOf(new MergeInsertClause(null, new[] { Q("tenant"), Q("id") }, new Expression[] { Str("other"), Int(1) })), message);
        AssertRejected(MergeOf(new MergeInsertClause(null, new[] { Q("id"), Q("tenant") }, new Expression[] { Int(1) })), message);
        Verify(MergeOf(new MergeInsertClause(null, new[] { Q("tenant"), Q("id") }, new Expression[] { Tenant(), Int(1) })));
    }

    [Fact]
    public void Merge_ClauseConditionsAndValues_AreVerifiedAsUserExpressions_NotAsPolicySubqueries()
    {
        // table u requires its root predicate (UTenant) in a user expression and its policy-subquery predicate only inside a policy
        Expression UserSubquery(Expression? where) => new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) }, SecuredU(Q("x"), where), null, null, null), null, null));

        Verify(MergeOf(new MergeDeleteClause(UserSubquery(null))));
        Verify(MergeOf(new MergeInsertClause(null, new[] { Q("id"), Q("tenant") }, new Expression[] { UserSubquery(null), Tenant() })));
        Verify(MergeOf(new MergeUpdateClause(null, new[] { Set("name", UserSubquery(null)) })));
        Assert.Throws<SecurityCoverageException>(() => Verify(MergeOf(new MergeDeleteClause(UserSubquery(Int(1))))));
    }

    [Fact]
    public void Merge_UnsecuredSubqueryInAClauseConditionOrValue_IsRejected()
    {
        Expression Bad() => new ExistsExpression(new SelectStatement(null, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
            new NamedTableSource(Canonical("u"), null), null, null, null), null, null));

        Assert.Throws<SecurityCoverageException>(() => Verify(MergeOf(new MergeDeleteClause(Bad()))));
        Assert.Throws<SecurityCoverageException>(() => Verify(MergeOf(new MergeInsertClause(null, new[] { Q("id"), Q("tenant") }, new Expression[] { Bad(), Tenant() }))));
        Assert.Throws<SecurityCoverageException>(() => Verify(MergeOf(new MergeUpdateClause(null, new[] { Set("name", Bad()) }))));
        Assert.Throws<SecurityCoverageException>(() => Verify(MergeOf(new MergeDeleteClause(null)) with { On = new BinaryExpression(Pred(Root, SecurityScope.MergeOn), And, Bad()) }));
    }

    [Fact]
    public void Merge_UnknownClause_IsRejected_WithItsTypeName()
    {
        AssertRejected(MergeOf(new UnknownClause(null)), "MERGE clause 'UnknownClause' is not covered.");
    }

    [Fact]
    public void Merge_UpdateClause_WritingAMaskedColumn_IsRejected()
    {
        AssertRejected(MergeOf(new MergeUpdateClause(null, new[] { Set("secret", Str("a")) })), "A masked column is written by the statement.");
    }

    [Fact]
    public void Merge_InsertClause_IntoATableWithAConsentPolicy_IsRejected_UnlessTheOptionAllowsIt()
    {
        var on = new BinaryExpression(Pred(Root, SecurityScope.MergeOn), And, Pred(Policy, SecurityScope.MergeOn));
        var stmt = MergeOf(new MergeInsertClause(null, new[] { Q("id"), Q("tenant") }, new Expression[] { Int(1), Tenant() })) with { On = on };

        AssertRejected(stmt, "An INSERT into a table with a row policy cannot be verified against the policy.", withPolicy: true);
        Verify(stmt, withPolicy: true, dml: Strict with { RejectConsentFilteredInsert = false });
    }
}
