namespace TrinoSqlEngine.Tests;

using System;
using TrinoSqlEngine;
using Xunit;

public class SecurityReviewMediumSqlEngineTests
{
    private const string ConsentFilter =
        "tenant_id = 'T1' AND EXISTS (SELECT 1 FROM {0} AS d WHERE d.\"subject\"='u1' AND d.\"row_id\" = autheris_target.\"id\")";

    private static RlsOptions ConsentOptions(string consentTableRef) => new()
    {
        TargetDialect = TargetSqlDialect.Sqlite,
        PolicyFiltersAreTargetDialectSql = true,
        PolicyProvider = new DefaultRlsPolicyProvider(
            predicate: t => t.Equals("secret", StringComparison.OrdinalIgnoreCase),
            filterFunc: _ => string.Format(System.Globalization.CultureInfo.InvariantCulture, ConsentFilter, consentTableRef))
    };

    private static string Rewrite(string engineName, string sql, RlsOptions options)
    {
        options.RewriterEngine = engineName;
        return new FastSqlEngine().RewriteRls(sql.AsMemory(), options);
    }

    [Theory]
    [InlineData("LegacyTokenStream", "\"consents\"", "WITH consents(subject,row_id) AS (VALUES ('u1',1),('u1',2)) SELECT * FROM secret")]
    [InlineData("AstCompiler", "\"consents\"", "WITH consents(subject,row_id) AS (VALUES ('u1',1),('u1',2)) SELECT * FROM secret")]
    [InlineData("LegacyTokenStream", "consents", "WITH CONSENTS(subject,row_id) AS (VALUES ('u1',1)) SELECT * FROM secret")]
    [InlineData("AstCompiler", "consents", "WITH CONSENTS(subject,row_id) AS (VALUES ('u1',1)) SELECT * FROM secret")]
    [InlineData("LegacyTokenStream", "\"consents\"", "WITH \"Consents\" AS (SELECT 1 AS subject, 1 AS row_id) SELECT * FROM secret")]
    [InlineData("AstCompiler", "\"consents\"", "WITH \"Consents\" AS (SELECT 1 AS subject, 1 AS row_id) SELECT * FROM secret")]
    // CTE declared after the table reference inside an earlier CTE body (order independence).
    [InlineData("LegacyTokenStream", "consents", "WITH a AS (SELECT * FROM secret), consents AS (SELECT 1 AS subject, 1 AS row_id) SELECT * FROM a")]
    [InlineData("AstCompiler", "consents", "WITH a AS (SELECT * FROM secret), consents AS (SELECT 1 AS subject, 1 AS row_id) SELECT * FROM a")]
    public void SR_M_25_UserCteShadowingPolicyPredicateTable_IsRejected(string engine, string consentRef, string sql)
    {
        Assert.ThrowsAny<Exception>(() => Rewrite(engine, sql, ConsentOptions(consentRef)));
    }

    [Theory]
    [InlineData("LegacyTokenStream")]
    [InlineData("AstCompiler")]
    public void SR_M_25_SchemaQualifiedPredicateTable_WithUnrelatedCte_IsAccepted(string engine)
    {
        string sql = "WITH other(subject,row_id) AS (VALUES ('u1',1)) SELECT * FROM secret";
        string result = Rewrite(engine, sql, ConsentOptions("\"gw\".\"consents\""));
        Assert.Contains("\"gw\".\"consents\"", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LegacyTokenStream")]
    [InlineData("AstCompiler")]
    public void SR_M_25_QualifiedPredicateTable_WithSameNamedCte_IsAccepted(string engine)
    {
        // Qualified names are not shadowable: a CTE cannot shadow "gw"."consents".
        string sql = "WITH consents(subject,row_id) AS (VALUES ('u1',1)) SELECT * FROM secret";
        string result = Rewrite(engine, sql, ConsentOptions("\"gw\".\"consents\""));
        Assert.Contains("\"gw\".\"consents\"", result, StringComparison.Ordinal);
    }

    private static RlsOptions InsertOptions() => new()
    {
        TargetDialect = TargetSqlDialect.Sqlite,
        EnforceWithCheckOption = true,
        ExpectedTenantValue = "'T1'",
        RequireTenantColumnInInsert = true,
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 'T1'")
    };

    [Theory]
    [InlineData("LegacyTokenStream", "INSERT INTO orders (id, tenant_id, TENANT_ID) VALUES (1, 'T1', 'T2')")]
    [InlineData("AstCompiler", "INSERT INTO orders (id, tenant_id, TENANT_ID) VALUES (1, 'T1', 'T2')")]
    [InlineData("LegacyTokenStream", "INSERT INTO orders (id, \"tenant_id\", tenant_id) VALUES (1, 'T1', 'T2')")]
    [InlineData("AstCompiler", "INSERT INTO orders (id, \"tenant_id\", tenant_id) VALUES (1, 'T1', 'T2')")]
    [InlineData("LegacyTokenStream", "INSERT INTO orders (id, tenant_id, id) VALUES (1, 'T1', 2)")]
    [InlineData("AstCompiler", "INSERT INTO orders (id, tenant_id, id) VALUES (1, 'T1', 2)")]
    public void SR_M_26_DuplicateInsertColumn_IsRejected(string engine, string sql)
    {
        Assert.ThrowsAny<Exception>(() => Rewrite(engine, sql, InsertOptions()));
    }

    [Theory]
    [InlineData("LegacyTokenStream")]
    [InlineData("AstCompiler")]
    public void SR_M_26_DistinctInsertColumns_AreAccepted(string engine)
    {
        string result = Rewrite(engine, "INSERT INTO orders (id, tenant_id) VALUES (1, 'T1')", InsertOptions());
        Assert.Contains("INSERT", result, StringComparison.OrdinalIgnoreCase);
    }
}
