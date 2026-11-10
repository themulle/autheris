namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using TrinoSqlEngine.Tests.Security;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public sealed class AstSecurityVisitorWritePermitTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    private CompileRequest Request(
        DmlWritePermit? write = null,
        IReadOnlySet<string>? consentRowFilterTables = null,
        StatementPermissions statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge)
    {
        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.SqlServer,
            AllowExperimentalDialect = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Statements = statements,
            Policy = new GovernancePolicy
            {
                RowFilters = _rowFilters,
                Masks = _masks,
                Catalog = DmlCompileTests.CatalogFor(TargetSqlDialect.SqlServer),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
                Dml = DmlGuardOptions.Strict,
                Write = write,
                TablesWithConsentRowFilter = consentRowFilterTables ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            }
        };
    }

    private CompiledSql Compile(string sql, DmlWritePermit? write = null, IReadOnlySet<string>? consentRowFilterTables = null) =>
        _engine.Compile(sql.AsMemory(), Request(write, consentRowFilterTables), CancellationToken.None);

    [Fact]
    public void Dml_WithoutPermit_IsRejected()
    {
        Assert.Throws<SecurityException>(() => Compile("DELETE FROM orders WHERE id = 1", write: null));
        Assert.Throws<SecurityException>(() => Compile("UPDATE orders SET status = 'closed' WHERE id = 1", write: null));
        Assert.Throws<SecurityException>(() => Compile("INSERT INTO orders (id, tenantid, status) VALUES (1, 'acme', 'open')", write: null));
        Assert.Throws<SecurityException>(() => Compile("MERGE INTO orders t USING entitlements s ON t.id = s.id WHEN MATCHED THEN DELETE", write: null));
    }

    [Fact]
    public void Insert_ColumnOutsidePermit_IsRejected()
    {
        var permit = new DmlWritePermit(
            DmlWriteOperation.Insert,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "tenantid" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var ex = Assert.Throws<SecurityException>(() =>
            Compile("INSERT INTO orders (id, tenantid, status) VALUES (1, 'acme', 'open')", write: permit));
        Assert.Contains("A column is not covered by a write grant.", ex.Message);
    }

    [Fact]
    public void Update_ColumnOutsidePermit_IsRejected()
    {
        var permit = new DmlWritePermit(
            DmlWriteOperation.Update,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var ex = Assert.Throws<SecurityException>(() =>
            Compile("UPDATE orders SET status = 'closed' WHERE id = 1", write: permit));
        Assert.Contains("A column is not covered by a write grant.", ex.Message);
    }

    [Fact]
    public void MaskedColumn_WritableOnlyWithPermit()
    {
        _masks.Masks[(Orders, "email")] = new MaskSpec(
            MaskKind.Nullify,
            new MaskArguments(),
            FrozenDictionary<string, PolicyValue>.Empty);

        var permitWithout = new DmlWritePermit(
            DmlWriteOperation.Update,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "status" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Throws<SecurityException>(() =>
            Compile("UPDATE orders SET email = 'test@example.com' WHERE id = 1", write: permitWithout));

        var permitWith = new DmlWritePermit(
            DmlWriteOperation.Update,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "email" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var compiled = Compile("UPDATE orders SET email = 'test@example.com' WHERE id = 1", write: permitWith);
        Assert.Contains("Email", compiled.Sql);
    }

    [Fact]
    public void MaskedColumn_InWhere_StillRejectedWithPermit()
    {
        _masks.Masks[(Orders, "email")] = new MaskSpec(
            MaskKind.Nullify,
            new MaskArguments(),
            FrozenDictionary<string, PolicyValue>.Empty);

        var permit = new DmlWritePermit(
            DmlWriteOperation.Update,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "email", "status" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var ex = Assert.Throws<SecurityException>(() =>
            Compile("UPDATE orders SET status = 'open' WHERE email = 'test@example.com'", write: permit));
        Assert.Contains("masked column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteProtectedColumn_RejectedEvenWithPermit()
    {
        var permit = new DmlWritePermit(
            DmlWriteOperation.Update,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "status", "amount" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "amount" });

        var ex = Assert.Throws<SecurityException>(() =>
            Compile("UPDATE orders SET amount = 100 WHERE id = 1", write: permit));
        Assert.Contains("A write-protected column must not be written.", ex.Message);
    }

    [Fact]
    public void PrimaryKeyColumn_UpdateRejected_InsertAllowed()
    {
        var updatePermit = new DmlWritePermit(
            DmlWriteOperation.Update,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "status" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" });

        var updateEx = Assert.Throws<SecurityException>(() =>
            Compile("UPDATE orders SET id = 2 WHERE id = 1", write: updatePermit));
        Assert.Contains("A write-protected column must not be written.", updateEx.Message);

        var insertPermit = new DmlWritePermit(
            DmlWriteOperation.Insert,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "tenantid", "status" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var insertCompiled = Compile("INSERT INTO orders (id, tenantid, status) VALUES (1, 'acme', 'open')", write: insertPermit);
        Assert.Equal(SqlStatementClass.Insert, insertCompiled.StatementClass);
    }

    [Fact]
    public void Insert_ConsentInListFilter_UsesCheckOption()
    {
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new InListExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Status", true) })),
                new Expression[]
                {
                    new PolicyParameterExpression("__pol_s1", SqlParameterType.String),
                    new PolicyParameterExpression("__pol_s2", SqlParameterType.String)
                },
                IsNotIn: false),
            new Dictionary<string, PolicyValue>
            {
                ["__pol_s1"] = new("open", SqlParameterType.String),
                ["__pol_s2"] = new("pending", SqlParameterType.String)
            });

        var permit = new DmlWritePermit(
            DmlWriteOperation.Insert,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "tenantid", "status" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var compiled = Compile(
            "INSERT INTO orders (id, tenantid, status) VALUES (1, 'acme', 'open')",
            write: permit,
            consentRowFilterTables: new HashSet<string>());

        Assert.Equal(SqlStatementClass.Insert, compiled.StatementClass);
        Assert.Contains("autheris_ins", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Insert_ConsentSubqueryFilter_StaysRejected()
    {
        var permit = new DmlWritePermit(
            DmlWriteOperation.Insert,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "tenantid", "status" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var ex = Assert.Throws<SecurityException>(() =>
            Compile(
                "INSERT INTO orders (id, tenantid, status) VALUES (1, 'acme', 'open')",
                write: permit,
                consentRowFilterTables: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.orders" }));

        Assert.Contains("INSERT into a table with a consent-based row policy is not permitted.", ex.Message);
    }

    [Fact]
    public void Update_PolicyColumnAssignment_StillRejected()
    {
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_r", SqlParameterType.String)),
            new Dictionary<string, PolicyValue>
            {
                ["__pol_r"] = new("EU", SqlParameterType.String)
            });

        var permit = new DmlWritePermit(
            DmlWriteOperation.Update,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "region", "status" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var ex = Assert.Throws<SecurityException>(() =>
            Compile("UPDATE orders SET region = 'US' WHERE id = 1", write: permit));
        Assert.Contains("A column referenced by the row policy must not be assigned.", ex.Message);
    }

    [Fact]
    public void TenantColumn_Update_StillRejected()
    {
        var permit = new DmlWritePermit(
            DmlWriteOperation.Update,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tenantid", "status" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var ex = Assert.Throws<SecurityException>(() =>
            Compile("UPDATE orders SET tenantid = 'acme' WHERE id = 1", write: permit));
        Assert.Contains("The tenant column must not be modified by an UPDATE statement.", ex.Message);
    }

    [Fact]
    public void Merge_IsRejected()
    {
        var permit = new DmlWritePermit(
            DmlWriteOperation.Update,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "status" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Throws<SecurityException>(() =>
            Compile("MERGE INTO orders t USING entitlements s ON t.id = s.id WHEN MATCHED THEN DELETE", write: permit));
    }
}
