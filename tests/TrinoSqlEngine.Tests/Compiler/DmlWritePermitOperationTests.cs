using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the write permit checks of the typed DML security visitor: a permit is bound to exactly one operation
/// (a DELETE permit never authorizes an UPDATE or INSERT and vice versa), a write-protected column is never written, and every refusal
/// carries its own fixed message. The permit is the only authority for a write, so a statement run under the wrong operation's permit
/// must be refused before any rewriting happens.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class DmlWritePermitOperationTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();

    public DmlWritePermitOperationTests()
    {
        _rowFilters.NoPolicy.Add(Orders);
        _rowFilters.NoPolicy.Add(Entitlements);
        _rowFilters.NoPolicy.Add(Lookup);
    }

    private static readonly string[] AllColumns = ["Id", "TenantId", "Region", "Status", "Amount", "Email"];

    private static DmlWritePermit Permit(DmlWriteOperation operation, params string[] protectedColumns) => new(
        operation,
        new HashSet<string>(AllColumns, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(protectedColumns, StringComparer.OrdinalIgnoreCase));

    private CompiledSql Compile(string sql, DmlWritePermit? permit) => _engine.Compile(sql.AsMemory(), new CompileRequest
    {
        TargetDialect = TargetSqlDialect.SqlServer,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = new DictMaskProvider(),
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
            Write = permit
        }
    }, CancellationToken.None);

    private const string Delete = "DELETE FROM orders WHERE id = 5";
    private const string Update = "UPDATE orders SET status = 'x' WHERE id = 5";
    private const string Insert = "INSERT INTO orders (id, tenantid, region, status) VALUES (1, 'acme', 'EU', 'x')";

    [Theory]
    [InlineData(Delete, DmlWriteOperation.Update, "DELETE requires a delete write permit.")]
    [InlineData(Delete, DmlWriteOperation.Insert, "DELETE requires a delete write permit.")]
    [InlineData(Update, DmlWriteOperation.Delete, "UPDATE requires an update write permit.")]
    [InlineData(Update, DmlWriteOperation.Insert, "UPDATE requires an update write permit.")]
    [InlineData(Insert, DmlWriteOperation.Delete, "INSERT requires an insert write permit.")]
    [InlineData(Insert, DmlWriteOperation.Update, "INSERT requires an insert write permit.")]
    public void PermitOfAnotherOperation_IsRefused(string sql, DmlWriteOperation operation, string message)
    {
        var ex = Assert.Throws<SecurityException>(() => Compile(sql, Permit(operation)));
        Assert.Equal(message, ex.Message);
    }

    [Theory]
    [InlineData(Delete, DmlWriteOperation.Delete)]
    [InlineData(Update, DmlWriteOperation.Update)]
    [InlineData(Insert, DmlWriteOperation.Insert)]
    public void PermitOfTheSameOperation_IsAccepted(string sql, DmlWriteOperation operation)
    {
        var compiled = Compile(sql, Permit(operation));
        Assert.StartsWith(sql[..6], compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TenantId", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(Delete)]
    [InlineData(Update)]
    [InlineData(Insert)]
    public void MissingPermit_IsRefused(string sql)
    {
        var ex = Assert.Throws<SecurityException>(() => Compile(sql, null));
        Assert.Equal("A write permit is required for DML operations.", ex.Message);
    }

    [Fact]
    public void WriteProtectedColumn_IsNeverWritten()
    {
        var update = Assert.Throws<SecurityException>(() => Compile(Update, Permit(DmlWriteOperation.Update, "Status")));
        Assert.Equal("A write-protected column must not be written.", update.Message);

        var insert = Assert.Throws<SecurityException>(() => Compile(Insert, Permit(DmlWriteOperation.Insert, "region")));
        Assert.Equal("A write-protected column must not be written.", insert.Message);

        // a protected column that is not written does not matter
        Assert.NotNull(Compile(Update, Permit(DmlWriteOperation.Update, "Amount")));
        Assert.NotNull(Compile(Insert, Permit(DmlWriteOperation.Insert, "Amount", "Email")));
    }

    [Fact]
    public void ColumnOutsideTheWritableSet_IsRefused()
    {
        var narrow = new DmlWritePermit(DmlWriteOperation.Update, new HashSet<string>(["Amount"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var ex = Assert.Throws<SecurityException>(() => Compile(Update, narrow));
        Assert.Equal("A column is not covered by a write grant.", ex.Message);

        var insertNarrow = new DmlWritePermit(DmlWriteOperation.Insert, new HashSet<string>(["Id", "Region"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("A column is not covered by a write grant.", Assert.Throws<SecurityException>(() => Compile(Insert, insertNarrow)).Message);
    }

    [Fact]
    public void InsertWithoutColumnList_IsRefused()
    {
        var ex = Assert.Throws<SecurityException>(() => Compile("INSERT INTO orders VALUES (1, 'a', 'EU', 'x', 1, 'e')", Permit(DmlWriteOperation.Insert)));
        Assert.Equal("A governed INSERT must list its target columns.", ex.Message);
    }

    [Fact]
    public void DuplicateInsertColumn_IsRefused()
    {
        var ex = Assert.Throws<SecurityException>(() => Compile("INSERT INTO orders (id, id) VALUES (1, 2)", Permit(DmlWriteOperation.Insert)));
        Assert.Equal("A column is listed more than once.", ex.Message);
    }
}
