using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// #310 GAP-021 (SEC-PG-09): SUM/AVG over a non-numeric column and MIN/MAX over a boolean column are rejected with a fixed code before
/// the database sees the statement. The guard runs after the mask guards and only for a column the catalog lists, so an unknown or
/// denied column never discloses its type (the denied answer of SEC-X1-05 stays the only answer).
/// </summary>
[Trait("Category", "SqlGate")]
public class AggregateTypeGuardTests
{
    private sealed class Lookup(bool maskEmail = false) : IMaskedColumnLookup
    {
        public bool HasMask(string normalizedTableName, string columnName) =>
            maskEmail && columnName.Equals("email", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "integer",
        ["amount"] = "decimal(18,2)",
        ["ratio"] = "double precision",
        ["email"] = "varchar(200)",
        ["created"] = "timestamp",
        ["born"] = "date",
        ["active"] = "boolean",
        ["weird"] = "frobnicate"
    };

    private static AstSecurityVisitor Visitor(bool maskEmail = false, bool withTypes = true)
    {
        var options = new AstSecurityVisitorOptions
        {
            MaskedColumns = new Lookup(maskEmail),
            TableColumns = name => name.EndsWith("orders", StringComparison.OrdinalIgnoreCase) ? Types.Keys.ToArray() : null,
            ColumnDataType = withTypes
                ? (table, column) => table.EndsWith("orders", StringComparison.OrdinalIgnoreCase) && Types.TryGetValue(column, out var t) ? t : null
                : null,
            TablesWithMaskedColumns = maskEmail
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
        var typed = new TypedPolicyContext(Catalog(), new DictPolicyProvider(), new DictMaskProvider(),
            new TenantBinding("t", "a", SqlParameterType.String), DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer));
        return new AstSecurityVisitor(options, typed);
    }

    private static void Run(string sql, bool maskEmail = false, bool withTypes = true) =>
        Visitor(maskEmail, withTypes).Visit(CompilerTestHelpers.Build(sql));

    [Theory]
    [InlineData("SELECT SUM(email) FROM orders")]
    [InlineData("SELECT AVG(created) FROM orders")]
    [InlineData("SELECT AVG(born) FROM orders")]
    [InlineData("SELECT SUM(active) FROM orders")]
    [InlineData("SELECT MIN(active) FROM orders")]
    [InlineData("SELECT MAX(active) FROM orders")]
    [InlineData("SELECT SUM(o.email) FROM orders o")]
    [InlineData("SELECT sum(email) OVER (PARTITION BY id) FROM orders")]
    [InlineData("SELECT SUM(weird) FROM orders")]
    [InlineData("SELECT MIN(weird) FROM orders")]
    public void InvalidFunctionForType_IsRejected(string sql)
    {
        var ex = Assert.Throws<AggregateTypeInvalidException>(() => Run(sql));
        Assert.Equal(GovernedSqlErrorCodes.AggregationInvalidFunctionForType, ex.Code);
    }

    [Theory]
    [InlineData("SELECT SUM(amount) FROM orders")]
    [InlineData("SELECT AVG(id) FROM orders")]
    [InlineData("SELECT SUM(ratio) FROM orders")]
    [InlineData("SELECT MIN(email) FROM orders")]
    [InlineData("SELECT MAX(created) FROM orders")]
    [InlineData("SELECT MIN(born) FROM orders")]
    [InlineData("SELECT COUNT(active) FROM orders")]
    [InlineData("SELECT COUNT(email) FROM orders")]
    public void ValidFunctionForType_Passes(string sql) => Run(sql);

    [Theory]
    [InlineData("SELECT SUM(amount * 2) FROM orders")]
    [InlineData("SELECT SUM(CAST(email AS integer)) FROM orders")]
    [InlineData("SELECT SUM(CASE WHEN active THEN 1 ELSE 0 END) FROM orders")]
    public void ComputedExpression_Passes(string sql) => Run(sql);

    [Fact]
    public void WithoutTypeLookup_TheGuardDoesNotApply()
    {
        Run("SELECT SUM(email) FROM orders", withTypes: false);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_PG_09_UnknownColumn_DoesNotDiscloseAType()
    {
        // The column is not in the catalog (unknown or denied): the guard stays silent, the executor answers the denied code.
        Run("SELECT SUM(ghost) FROM orders");
        Run("SELECT SUM(orders.ghost) FROM orders");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_PG_09_MaskedColumn_KeepsTheMaskedAggregationAnswer()
    {
        Assert.Throws<MaskedAggregationException>(() => Run("SELECT SUM(email) FROM orders", maskEmail: true));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_PG_09_Message_NamesNeitherTheTypeNorTheArguments()
    {
        var ex = Assert.Throws<AggregateTypeInvalidException>(() => Run("SELECT SUM(email) FROM orders"));
        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.AggregationInvalidFunctionForType), ex.Message);
        Assert.DoesNotContain("email", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("varchar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("integer", AggregateTypeClass.Numeric)]
    [InlineData("BIGINT", AggregateTypeClass.Numeric)]
    [InlineData("decimal(18,2)", AggregateTypeClass.Numeric)]
    [InlineData("double precision", AggregateTypeClass.Numeric)]
    [InlineData("money", AggregateTypeClass.Numeric)]
    [InlineData("varchar(20)", AggregateTypeClass.Text)]
    [InlineData("nvarchar(max)", AggregateTypeClass.Text)]
    [InlineData("text", AggregateTypeClass.Text)]
    [InlineData("uuid", AggregateTypeClass.Text)]
    [InlineData("json", AggregateTypeClass.Text)]
    [InlineData("timestamp with time zone", AggregateTypeClass.Temporal)]
    [InlineData("date", AggregateTypeClass.Temporal)]
    [InlineData("datetime2(3)", AggregateTypeClass.Temporal)]
    [InlineData("boolean", AggregateTypeClass.Boolean)]
    [InlineData("bit", AggregateTypeClass.Boolean)]
    [InlineData("frobnicate", AggregateTypeClass.Unknown)]
    [InlineData("", AggregateTypeClass.Unknown)]
    [InlineData(null, AggregateTypeClass.Unknown)]
    public void Classify_MirrorsTheCatalogValueKinds(string? dataType, AggregateTypeClass expected) =>
        Assert.Equal(expected, AggregateTypeClasses.Classify(dataType));
}
