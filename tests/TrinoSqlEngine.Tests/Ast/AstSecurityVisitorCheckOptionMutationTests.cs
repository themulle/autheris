namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

/// <summary>
/// Mutation-gate tests (M-12) for the WITH CHECK OPTION verification of INSERT and UPDATE, the consent-filter rejection, the correlated
/// row filter rejection and the masked column guards of the options path of <c>AstSecurityVisitor</c>.
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class AstSecurityVisitorCheckOptionMutationTests
{
    private readonly FastSqlEngine _engine = new();

    private string Secure(string sql, RlsOptions options)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        var builder = new SqlAstBuilder(AstBuilderOptions.FromRlsOptions(options));
        var ast = builder.BuildStatement(tree);
        var secured = (SqlStatement)new AstSecurityVisitor(options, _engine).Visit(ast);
        return SqlDialectGeneratorFactory.GetGenerator(options.TargetDialect).GenerateSql(secured);
    }

    private string Rejected(string sql, RlsOptions options) => Assert.Throws<SecurityException>(() => Secure(sql, options)).Message;

    private static RlsOptions Check(string? expectedTenant = "acme") => new()
    {
        EnforceReadOnlyQueries = false,
        EnforceWithCheckOption = true,
        ExpectedTenantValue = expectedTenant,
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42")
    };

    // ---- UPDATE ----

    [Fact]
    public void Update_OfTheTenantColumn_IsRejectedByDefault()
    {
        Assert.Equal("Modification of tenant column 'tenant_id' is not allowed in UPDATE statement.",
            Rejected("UPDATE orders SET tenant_id = 'acme' WHERE id = 1", Check()));
        Assert.Equal("Modification of tenant column 'TENANT_ID' is not allowed in UPDATE statement.",
            Rejected("UPDATE orders SET TENANT_ID = 'acme' WHERE id = 1", Check()));
    }

    [Fact]
    public void Update_OfTheTenantColumn_NeedsTheExpectedTenantWhenModificationIsAllowed()
    {
        var o = Check(expectedTenant: null);
        o.DisallowTenantColumnModificationInUpdate = false;
        Assert.Equal("Expected tenant value must be configured when WITH CHECK OPTION is active.", Rejected("UPDATE orders SET tenant_id = 'acme' WHERE id = 1", o));
        o.ExpectedTenantValue = "";
        Assert.Equal("Expected tenant value must be configured when WITH CHECK OPTION is active.", Rejected("UPDATE orders SET tenant_id = 'acme' WHERE id = 1", o));

        o.ExpectedTenantValue = "'acme'";
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("UPDATE orders SET tenant_id = 'acme' WHERE id = 1", o), StringComparison.Ordinal);
        Assert.Equal("Tenant column 'tenant_id' UPDATE value 'evil' does not match expected tenant 'acme'.", Rejected("UPDATE orders SET tenant_id = 'evil' WHERE id = 1", o));
        Assert.Equal("Tenant column 'tenant_id' in UPDATE must be a literal value.", Rejected("UPDATE orders SET tenant_id = other WHERE id = 1", o));
        Assert.Equal("Tenant column 'tenant_id' in UPDATE must be a literal value.", Rejected("UPDATE orders SET tenant_id = NULL WHERE id = 1", o));
    }

    [Fact]
    public void Update_WithoutCheckOption_AllowsTheTenantColumn()
    {
        var o = Check();
        o.EnforceWithCheckOption = false;
        Assert.Contains("\"tenant_id\" = 'evil'", Secure("UPDATE orders SET tenant_id = 'evil' WHERE id = 1", o), StringComparison.Ordinal);
    }

    [Fact]
    public void Update_UsesThePerTableTenantColumn()
    {
        var o = Check();
        o.TableTenantColumns["orders"] = "org";
        Assert.Equal("Modification of tenant column 'org' is not allowed in UPDATE statement.", Rejected("UPDATE orders SET org = 'acme' WHERE id = 1", o));
        Assert.Contains("\"tenant_id\" = 'x'", Secure("UPDATE orders SET tenant_id = 'x' WHERE id = 1", o), StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateAndDelete_RejectACorrelatedRowFilter()
    {
        var o = Check();
        o.PolicyProvider = new DefaultRlsPolicyProvider("EXISTS (SELECT 1 FROM grants g WHERE g.id = autheris_target.id)");
        o.EnforceWithCheckOption = false;
        Assert.Equal("Correlated row filters are not supported for UPDATE/DELETE statements.", Rejected("UPDATE orders SET x = 1 WHERE id = 1", o));
        Assert.Equal("Correlated row filters are not supported for UPDATE/DELETE statements.", Rejected("DELETE FROM orders WHERE id = 1", o));
    }

    [Fact]
    public void UpdateAndDelete_OnATableWithoutAPolicy_KeepTheirWhereUnchanged()
    {
        var o = Check();
        o.EnforceWithCheckOption = false;
        o.PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42", predicate: t => t == "other");
        Assert.Equal("DELETE FROM \"orders\" WHERE \"id\" = 1", Secure("DELETE FROM orders WHERE id = 1", o));
        Assert.Equal("UPDATE \"orders\" SET \"x\" = 1 WHERE \"id\" = 1", Secure("UPDATE orders SET x = 1 WHERE id = 1", o));
        Assert.Equal("DELETE FROM \"other\" WHERE (\"id\" = 1) AND (\"tenant_id\" = 42)", Secure("DELETE FROM other WHERE id = 1", o));
    }

    [Fact]
    public void UpdateAndDelete_OnACteTarget_AreNotPolicyFiltered()
    {
        var o = Check();
        o.EnforceWithCheckOption = false;
        // a data-modifying statement cannot target a CTE name that is not in scope, so the plain statement keeps its filter
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE id = 1", o), StringComparison.Ordinal);
    }

    // ---- INSERT ----

    [Fact]
    public void Insert_IntoAConsentFilteredTable_IsRejected()
    {
        var o = Check();
        o.TablesWithConsentRowFilter.Add("orders");
        Assert.Equal("INSERT into table 'orders' with custom row-level consent filter is not permitted.",
            Rejected("INSERT INTO orders (id, tenant_id) VALUES (1, 'acme')", o));

        var qualified = Check();
        qualified.TablesWithConsentRowFilter.Add("orders");
        Assert.Equal("INSERT into table 'dbo.orders' with custom row-level consent filter is not permitted.",
            Rejected("INSERT INTO dbo.orders (id, tenant_id) VALUES (1, 'acme')", qualified));

        var byFullName = Check();
        byFullName.TablesWithConsentRowFilter.Add("dbo.orders");
        Assert.Equal("INSERT into table 'dbo.orders' with custom row-level consent filter is not permitted.",
            Rejected("INSERT INTO dbo.orders (id, tenant_id) VALUES (1, 'acme')", byFullName));

        var other = Check();
        other.TablesWithConsentRowFilter.Add("grants");
        Assert.Contains("'acme'", Secure("INSERT INTO orders (id, tenant_id) VALUES (1, 'acme')", other), StringComparison.Ordinal);

        var off = Check();
        off.TablesWithConsentRowFilter.Add("orders");
        off.RejectConsentFilteredInsert = false;
        Assert.Contains("'acme'", Secure("INSERT INTO orders (id, tenant_id) VALUES (1, 'acme')", off), StringComparison.Ordinal);

        var empty = Check();
        Assert.Contains("'acme'", Secure("INSERT INTO orders (id, tenant_id) VALUES (1, 'acme')", empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_Values_AreVerifiedAgainstTheExpectedTenant()
    {
        Assert.Equal("INSERT INTO \"orders\" (\"id\", \"tenant_id\") VALUES (1, 'acme')", Secure("INSERT INTO orders (id, tenant_id) VALUES (1, 'acme')", Check()));
        Assert.Equal("INSERT INTO \"orders\" (\"tenant_id\", \"id\") VALUES ('acme', 1), ('acme', 2)", Secure("INSERT INTO orders (tenant_id, id) VALUES ('acme', 1), ('acme', 2)", Check()));
        Assert.Equal("Tenant column 'tenant_id' INSERT value 'evil' does not match expected tenant 'acme'.", Rejected("INSERT INTO orders (id, tenant_id) VALUES (1, 'evil')", Check()));
        Assert.Equal("Tenant column 'tenant_id' INSERT value 'evil' does not match expected tenant 'acme'.",
            Rejected("INSERT INTO orders (id, tenant_id) VALUES (1, 'acme'), (2, 'evil')", Check()));
        Assert.Equal("Tenant column 'tenant_id' in INSERT must be a literal value.", Rejected("INSERT INTO orders (id, tenant_id) VALUES (1, other)", Check()));
        Assert.Equal("Tenant column 'tenant_id' in INSERT must be a literal value.", Rejected("INSERT INTO orders (id, tenant_id) VALUES (1, NULL)", Check()));
        Assert.Equal("Tenant column 'TENANT_ID' must be explicitly specified in INSERT statement.".Replace("TENANT_ID", "tenant_id"), Rejected("INSERT INTO orders (id) VALUES (1)", Check()));
        Assert.Equal("Tenant column 'tenant_id' has no value in an INSERT VALUES row.", Rejected("INSERT INTO orders (id, tenant_id) VALUES (1)", Check()));
        Assert.Equal("Tenant column 'tenant_id' has no value in an INSERT VALUES row.", Rejected("INSERT INTO orders (id, tenant_id) VALUES (1, 'acme'), (2)", Check()));
        Assert.Equal("INSERT INTO \"orders\" (\"TENANT_ID\") VALUES ('acme')", Secure("INSERT INTO orders (TENANT_ID) VALUES ('acme')", Check()));
    }

    [Fact]
    public void Insert_NeedsTheExpectedTenantValue()
    {
        Assert.Equal("Expected tenant value must be configured when WITH CHECK OPTION is active.", Rejected("INSERT INTO orders (id, tenant_id) VALUES (1, 'acme')", Check(expectedTenant: null)));
        Assert.Equal("Expected tenant value must be configured when WITH CHECK OPTION is active.", Rejected("INSERT INTO orders (id, tenant_id) VALUES (1, 'acme')", Check(expectedTenant: "")));
        Assert.Contains("'acme'", Secure("INSERT INTO orders (id, tenant_id) VALUES (1, 'acme')", Check(expectedTenant: "'acme'")), StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_WithoutTheTenantColumn_IsAllowedOnlyWhenNotRequired()
    {
        var o = Check();
        o.RequireTenantColumnInInsert = false;
        Assert.Equal("INSERT INTO \"orders\" (\"id\") VALUES (1)", Secure("INSERT INTO orders (id) VALUES (1)", o));
        Assert.Equal("Tenant column 'tenant_id' must be explicitly specified in INSERT statement.", Rejected("INSERT INTO orders VALUES (1, 'acme')", Check()));
        var off = Check();
        off.EnforceWithCheckOption = false;
        Assert.Equal("INSERT INTO \"orders\" (\"id\") VALUES (1)", Secure("INSERT INTO orders (id) VALUES (1)", off));
    }

    [Fact]
    public void Insert_Select_IsVerifiedPerProjection()
    {
        Assert.Contains("SELECT 1, 'acme'", Secure("INSERT INTO orders (id, tenant_id) SELECT 1, 'acme'", Check()), StringComparison.Ordinal);
        Assert.Equal("Tenant column 'tenant_id' INSERT SELECT value 'evil' does not match expected tenant 'acme'.", Rejected("INSERT INTO orders (id, tenant_id) SELECT 1, 'evil'", Check()));
        Assert.Equal("Tenant column 'tenant_id' in INSERT SELECT must be a literal value.", Rejected("INSERT INTO orders (id, tenant_id) SELECT 1, x FROM src", Check()));
        Assert.Equal("Tenant column 'tenant_id' in INSERT SELECT cannot be verified (wildcard projection).", Rejected("INSERT INTO orders (id, tenant_id) SELECT * FROM src", Check()));
        Assert.Equal("Tenant column 'tenant_id' in INSERT SELECT cannot be verified (wildcard projection).", Rejected("INSERT INTO orders (id, tenant_id) SELECT *, 'acme' FROM src", Check()));
        Assert.Equal("Tenant column 'tenant_id' in INSERT SELECT cannot be verified (wildcard projection).", Rejected("INSERT INTO orders (id, tenant_id) SELECT 1, * FROM src", Check()));
        Assert.Equal("Tenant column 'tenant_id' has no value in INSERT SELECT.", Rejected("INSERT INTO orders (id, tenant_id) SELECT 1", Check()));
        Assert.Equal("Tenant column 'tenant_id' has no value in INSERT SELECT.", Rejected("INSERT INTO orders (id, tenant_id) SELECT 1 AS a", Check()));
        // a wildcard behind the tenant position is not in the way
        Assert.Contains("'acme'", Secure("INSERT INTO orders (tenant_id, id) SELECT 'acme', x.* FROM src x", Check()), StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_SetOperationSources_AreVerifiedOnBothSides()
    {
        Assert.Contains("UNION ALL", Secure("INSERT INTO orders (id, tenant_id) SELECT 1, 'acme' UNION ALL SELECT 2, 'acme'", Check()), StringComparison.Ordinal);
        Assert.Equal("Tenant column 'tenant_id' INSERT SELECT value 'evil' does not match expected tenant 'acme'.",
            Rejected("INSERT INTO orders (id, tenant_id) SELECT 1, 'evil' UNION ALL SELECT 2, 'acme'", Check()));
        Assert.Equal("Tenant column 'tenant_id' INSERT SELECT value 'evil' does not match expected tenant 'acme'.",
            Rejected("INSERT INTO orders (id, tenant_id) SELECT 1, 'acme' UNION ALL SELECT 2, 'evil'", Check()));
    }
}
