using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) for the typed DML security visitor: every rejection of INSERT, UPDATE, DELETE and MERGE names its own
/// reason (the message is part of the contract: it carries no value and no name from the request).
/// </summary>
[Trait("Category", "SqlGate")]
public class TypedDmlMessageMutationTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();
    private InMemoryTableCatalog? _catalog;
    private HashSet<string> _consent = new();

    public TypedDmlMessageMutationTests()
    {
        _rowFilters.NoPolicy.Add(Orders);
        _rowFilters.NoPolicy.Add(Entitlements);
        _rowFilters.NoPolicy.Add(Lookup);
    }

    private static PolicyPredicate RegionPredicate() => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
            BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new("EU", SqlParameterType.String) });

    private void RegionPolicy()
    {
        _rowFilters.NoPolicy.Remove(Orders);
        _rowFilters.Predicates[Orders] = RegionPredicate();
    }

    private void MaskEmail() =>
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);

    private CompileRequest Request(DmlGuardOptions? dml = null, TargetSqlDialect dialect = TargetSqlDialect.SqlServer) => new()
    {
        TargetDialect = dialect,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = _catalog ?? Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
            Dml = dml ?? DmlGuardOptions.Strict,
            TablesWithConsentRowFilter = _consent
        }
    };

    private string Reason<T>(string sql, TargetSqlDialect dialect = TargetSqlDialect.SqlServer) where T : Exception =>
        Assert.Throws<T>(() => _engine.Compile(sql.AsMemory(), Request(dialect: dialect), CancellationToken.None)).Message;

    private string Reason(string sql, TargetSqlDialect dialect = TargetSqlDialect.SqlServer) => Reason<SecurityException>(sql, dialect);

    private CompiledSql Compile(string sql, TargetSqlDialect dialect = TargetSqlDialect.SqlServer) =>
        _engine.Compile(sql.AsMemory(), Request(dialect: dialect), CancellationToken.None);

    // ---- targets and columns ----

    [Fact]
    public void AnUnknownTarget_IsRejected()
    {
        Assert.Equal("A DML target could not be resolved against the catalog.", Reason("DELETE FROM mystery WHERE id = 1"));
        Assert.Equal("A DML target could not be resolved against the catalog.", Reason("UPDATE mystery SET id = 1 WHERE id = 1"));
        Assert.Equal("A DML target could not be resolved against the catalog.", Reason("INSERT INTO mystery (id) VALUES (1)"));
    }

    [Fact]
    public void ADmlColumnThatIsNotCataloged_OrIsAmbiguous_IsRejected()
    {
        Assert.Equal("A DML column is not a cataloged column of the target table.", Reason("INSERT INTO orders (id, tenantid, nope) VALUES (1, 'acme', 2)"));
        _catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Orders, ImmutableArray.Create(
                new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Status", "nvarchar(20)"), new CatalogColumn("STATUS", "nvarchar(20)")),
                "TenantId", 1)
        }, "dbo");
        Assert.Equal("A DML column name is ambiguous in the catalog.", Reason("INSERT INTO orders (id, tenantid, status) VALUES (1, 'acme', 'x')"));
    }

    [Fact]
    public void InsertColumns_MustBeListedOnceAndNotMasked()
    {
        Assert.Equal("A governed INSERT must list its target columns.", Reason("INSERT INTO orders VALUES (1, 'acme')"));
        Assert.Equal("A column is listed more than once.", Reason("INSERT INTO orders (id, tenantid, id) VALUES (1, 'acme', 2)"));
        Assert.Equal("A column is listed more than once.", Reason("INSERT INTO orders (id, tenantid, ID) VALUES (1, 'acme', 2)"));
        MaskEmail();
        Assert.Equal("A masked column must not be written.", Reason("INSERT INTO orders (id, tenantid, email) VALUES (1, 'acme', 'x')"));
        Assert.Equal("The tenant column must be explicitly specified in an INSERT statement.", Reason("INSERT INTO orders (id, status) VALUES (1, 'x')"));
    }

    [Fact]
    public void InsertValues_MustMatchTheColumnListAndCarryTheCallersTenant()
    {
        Assert.Equal("The number of inserted values does not match the column list.", Reason("INSERT INTO orders (id, tenantid) VALUES (1)"));
        Assert.Equal("The number of inserted values does not match the column list.", Reason("INSERT INTO orders (id, tenantid) VALUES (1, 'acme', 3)"));
        Assert.Equal("The number of inserted values does not match the column list.", Reason("INSERT INTO orders (id, tenantid) VALUES (1, 'acme'), (2)"));
        Assert.Equal("The tenant column in INSERT must be a literal equal to the caller's tenant.", Reason("INSERT INTO orders (id, tenantid) VALUES (1, id)"));
        Assert.Equal("The tenant column value in INSERT does not match the caller's tenant.", Reason("INSERT INTO orders (id, tenantid) VALUES (1, 'other')"));
        Assert.Equal("The tenant column value in INSERT does not match the caller's tenant.", Reason("INSERT INTO orders (id, tenantid) VALUES (1, 'acme'), (2, 'other')"));
    }

    [Fact]
    public void InsertSelect_MustProjectExactlyTheListedColumns_AndCarryTheCallersTenant()
    {
        Assert.Equal("An INSERT SELECT must project exactly its listed columns (no wildcard).", Reason("INSERT INTO orders (id, tenantid) SELECT * FROM entitlements"));
        Assert.Equal("An INSERT SELECT must project exactly its listed columns (no wildcard).", Reason("INSERT INTO orders (id, tenantid) SELECT id FROM entitlements"));
        Assert.Equal("An INSERT SELECT must project exactly its listed columns (no wildcard).", Reason("INSERT INTO orders (id, tenantid) SELECT id, 'acme', orderid FROM entitlements"));
        Assert.Equal("An INSERT SELECT must project exactly its listed columns (no wildcard).", Reason("INSERT INTO orders (id, tenantid) SELECT id, e.* FROM entitlements e"));
        Assert.Equal("The tenant column in INSERT SELECT must be a literal equal to the caller's tenant.", Reason("INSERT INTO orders (id, tenantid) SELECT id, orderid FROM entitlements"));
        Assert.Equal("The tenant column value in INSERT SELECT does not match the caller's tenant.", Reason("INSERT INTO orders (id, tenantid) SELECT id, 'other' FROM entitlements"));
        Assert.Equal("The tenant column value in INSERT SELECT does not match the caller's tenant.",
            Reason("INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM entitlements UNION ALL SELECT id, 'other' FROM entitlements"));
        Assert.Equal("The tenant column value in INSERT SELECT does not match the caller's tenant.",
            Reason("INSERT INTO orders (id, tenantid) SELECT id, 'other' FROM entitlements UNION ALL SELECT id, 'acme' FROM entitlements"));
        var union = Compile("INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM entitlements UNION ALL SELECT id, 'acme' FROM entitlements");
        Assert.Contains(union.Parameters, p => p.Origin == ParameterOrigin.Tenant && p.SourceName == "__autheris_tenant");
    }

    // ---- row policy tables ----

    [Fact]
    public void InsertIntoAPolicyTable_NeedsValues_AndEveryPolicyColumn()
    {
        RegionPolicy();
        Assert.Equal("INSERT ... SELECT into a table with a row-level policy is not permitted; use VALUES (check option).",
            Reason("INSERT INTO orders (id, tenantid, region) SELECT id, 'acme', 'EU' FROM entitlements"));
        Assert.Equal("An INSERT into a table with a row-level policy must supply every column the policy references.",
            Reason("INSERT INTO orders (id, tenantid) VALUES (1, 'acme')"));
        Assert.True(Compile("INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')").RequiresRowCountCheck);
    }

    [Fact]
    public void InsertIntoAConsentFilteredTable_IsRejected_WithOrWithoutAnAdminPolicy()
    {
        _consent = new HashSet<string> { "Orders" };
        Assert.Equal("INSERT into a table with a consent-based row policy is not permitted.", Reason("INSERT INTO orders (id, tenantid) VALUES (1, 'acme')"));
        RegionPolicy();
        Assert.Equal("INSERT into a table with a consent-based row policy is not permitted.", Reason("INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')"));
        _consent = new HashSet<string> { "dbo.Orders" };
        Assert.Equal("INSERT into a table with a consent-based row policy is not permitted.", Reason("INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')"));
        _consent = new HashSet<string> { "Entitlements" };
        Assert.True(Compile("INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')").RequiresRowCountCheck);
    }

    [Fact]
    public void InsertCheckOption_OnAStringPredicateOtherThanEquality_IsATypedRejection()
    {
        _rowFilters.NoPolicy.Remove(Orders);
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new LikeExpression(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
                new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_region"] = new("E%", SqlParameterType.String) });
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Compile("INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')"));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
        Assert.Contains("INSERT check option on a string column supports only equality and IN (byte-exact)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorrelatedPolicy_IsRejectedForEveryDmlKind()
    {
        _rowFilters.NoPolicy.Remove(Orders);
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(RowFilterAliases.Target), new SqlIdentifier("Id", true) })),
                BinaryOperator.Equal, new PolicyParameterExpression("__pol_id", SqlParameterType.Int64)),
            new Dictionary<string, PolicyValue> { ["__pol_id"] = new(1L, SqlParameterType.Int64) });
        const string expected = "Correlated row filters are not supported for INSERT, UPDATE, DELETE and MERGE statements.";
        Assert.Equal(expected, Reason("DELETE FROM orders WHERE id = 1"));
        Assert.Equal(expected, Reason("UPDATE orders SET status = 'x' WHERE id = 1"));
        Assert.Equal(expected, Reason("INSERT INTO orders (id, tenantid) VALUES (1, 'acme')"));
    }

    // ---- UPDATE ----

    [Fact]
    public void UpdateAssignments_AreGuarded()
    {
        MaskEmail();
        Assert.Equal("A column is assigned more than once.", Reason("UPDATE orders SET status = 'a', status = 'b' WHERE id = 1"));
        Assert.Equal("A column is assigned more than once.", Reason("UPDATE orders SET status = 'a', STATUS = 'b' WHERE id = 1"));
        Assert.Equal("A masked column must not be written.", Reason("UPDATE orders SET email = 'x' WHERE id = 1"));
        Assert.Equal("The tenant column must not be modified by an UPDATE statement.", Reason("UPDATE orders SET tenantid = 'acme' WHERE id = 1"));
        RegionPolicy();
        Assert.Equal("A column referenced by the row policy must not be assigned.", Reason("UPDATE orders SET region = 'EU' WHERE id = 1"));
        Assert.Equal("A column referenced by the row policy must not be assigned.", Reason("UPDATE orders SET REGION = 'EU' WHERE id = 1"));
    }

    [Fact]
    public void MaskedReads_AreRejectedInUpdateAndDelete_AndPermittedWhenTheSwitchIsOff()
    {
        MaskEmail();
        string update = Assert.Throws<SecurityException>(() => Compile("UPDATE orders SET status = email WHERE id = 1")).Message;
        Assert.Equal("A masked column of the DML target is read by the statement.", update);
        string delete = Assert.Throws<SecurityException>(() => Compile("DELETE FROM orders WHERE email = 'x'")).Message;
        Assert.Equal("A masked column of the DML target is read by the statement.", delete);
        string where = Assert.Throws<SecurityException>(() => Compile("UPDATE orders SET status = 'a' WHERE email = 'x'")).Message;
        Assert.Equal("A masked column of the DML target is read by the statement.", where);
        // a request that relaxes the guard is rejected as a configuration error (CR-ADG-38), so the typed path never reaches the relaxed branch
        var relaxed = DmlGuardOptions.Strict with { RejectMaskedColumnsInDml = false };
        Assert.Throws<SqlCompileConfigurationException>(() => _engine.Compile("DELETE FROM orders WHERE email = 'x'".AsMemory(), Request(relaxed), CancellationToken.None));
    }

    // ---- MERGE ----

    private const string MergeHead = "MERGE INTO orders t USING entitlements s ON t.id = s.orderid ";

    [Fact]
    public void Merge_SourceAliasMustDifferFromTheTarget()
    {
        const string expected = "A MERGE source alias must differ from the target alias and the target table name.";
        Assert.Equal(expected, Reason("MERGE INTO orders t USING entitlements t ON t.id = t.orderid WHEN MATCHED THEN DELETE"));
        Assert.Equal(expected, Reason("MERGE INTO orders t USING entitlements orders ON t.id = orders.orderid WHEN MATCHED THEN DELETE"));
        Assert.Equal(expected, Reason("MERGE INTO orders USING entitlements orders ON orders.id = orders.orderid WHEN MATCHED THEN DELETE"));
    }

    [Fact]
    public void Merge_InsertIntoAPolicyOrConsentTable_IsRejected()
    {
        const string insert = "WHEN NOT MATCHED THEN INSERT (id, tenantid, region) VALUES (s.id, 'acme', 'EU')";
        Assert.Contains("INSERT", Compile(MergeHead + insert).Sql, StringComparison.Ordinal);
        RegionPolicy();
        Assert.Equal("MERGE INSERT into a table with a row-level policy is not permitted.", Reason(MergeHead + insert));
        _rowFilters.NoPolicy.Add(Orders);
        _consent = new HashSet<string> { "Orders" };
        Assert.Equal("MERGE INSERT into a table with a row-level policy is not permitted.", Reason(MergeHead + insert));
        _consent = new HashSet<string> { "dbo.Orders" };
        Assert.Equal("MERGE INSERT into a table with a row-level policy is not permitted.", Reason(MergeHead + insert));
        _consent = new HashSet<string> { "Other" };
        Assert.Contains("INSERT", Compile(MergeHead + insert).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_MaskedReads_AreRejectedInEveryPartOfTheStatement()
    {
        MaskEmail();
        Assert.Equal("A masked column of the DML target is read by the statement.", Assert.Throws<SecurityException>(() => Compile("MERGE INTO orders t USING entitlements s ON t.email = s.orderid WHEN MATCHED THEN DELETE")).Message);
        Assert.Equal("A masked column of the DML target is read by the statement.", Assert.Throws<SecurityException>(() => Compile(MergeHead + "WHEN MATCHED AND t.email = 'x' THEN DELETE")).Message);
        Assert.Equal("A masked column of the DML target is read by the statement.", Assert.Throws<SecurityException>(() => Compile(MergeHead + "WHEN MATCHED THEN UPDATE SET status = t.email")).Message);
        Assert.Equal("A masked column of the DML target is read by the statement.", Assert.Throws<SecurityException>(() =>
            Compile(MergeHead + "WHEN NOT MATCHED THEN INSERT (id, tenantid, status) VALUES (s.id, 'acme', t.email)")).Message);
        Assert.Equal("A masked column must not be written.", Reason(MergeHead + "WHEN NOT MATCHED THEN INSERT (id, tenantid, email) VALUES (s.id, 'acme', 'x')"));
    }
}
