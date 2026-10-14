namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Tests.Security;
using Xunit;

/// <summary>
/// Wildcard expansion with column masks on the governed compile path. (The legacy "masking without a column provider" case has no
/// typed form: a table outside the catalog is rejected, see <c>TypedInjectionTests.UnknownTable_IsRejected</c>.)
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public sealed class AstSecurityVisitorMaskingTests
{
    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "\"Email\"")]
    [InlineData(TargetSqlDialect.SqlServer, "[Email]")]
    public void Masking_WildcardExpansion_ProjectsTheMaskWithADelimitedAlias(TargetSqlDialect dialect, string quotedEmail)
    {
        var env = new ProbeEnv(dialect);
        env.MaskEmail();

        var compiled = env.Compile("SELECT * FROM orders");

        // SEC M-24: the masked column keeps its (delimited) name, the other columns are projected as cataloged columns
        Assert.Contains($"AS {quotedEmail}", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("NULL", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(dialect == TargetSqlDialect.SqlServer ? "[Status]" : "\"Status\"", compiled.Sql, StringComparison.Ordinal);
        // the unmasked source column is never projected: the name appears only as the alias of the mask
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(compiled.Sql, System.Text.RegularExpressions.Regex.Escape(quotedEmail)));
    }

    [Fact]
    public void Masking_MaskedColumnInAPredicate_IsRejected()
    {
        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        env.MaskEmail();

        Assert.ThrowsAny<System.Security.SecurityException>(() => env.Compile("SELECT id FROM orders WHERE email = 'x'"));
    }
}
