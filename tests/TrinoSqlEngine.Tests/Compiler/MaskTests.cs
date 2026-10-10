using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

public class MaskTests
{
    private static ColumnReference Col(string name) => new(new SqlQualifiedName(new[] { new SqlIdentifier(name, true) }));

    private static PolicyParameterExpression P(string name, SqlParameterType type) => new(name, type, ParameterOrigin.Mask);

    private static CompiledSql GenerateMask(MaskExpression mask, Dictionary<string, PolicyValue>? values = null)
    {
        var stmt = new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(mask, new SqlIdentifier(mask.Column.Name.SimpleName, true)) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("t", true) }), null),
                null, null, null), null, null);
        return CompilerTestHelpers.GenerateSqlServer(stmt, values);
    }

    [Fact]
    public void Nullify_IsTypedNull_FromTheCatalogType()
    {
        var c = GenerateMask(new MaskExpression(MaskKind.Nullify, Col("Email"), new MaskArguments(), "nvarchar(200)"));
        Assert.Contains("CAST(NULL AS nvarchar(200)) AS [Email]", c.Sql);
        Assert.Empty(c.Parameters);
    }

    [Fact]
    public void Redact_Text_BindsTheReplacement_NoInlineString()
    {
        var c = GenerateMask(
            new MaskExpression(MaskKind.Redact, Col("Email"), new MaskArguments(Constant: P("m_redact", SqlParameterType.String)), "nvarchar(200)"),
            new() { ["m_redact"] = new("[REDACTED]", SqlParameterType.String) });

        Assert.DoesNotContain('\'', c.Sql);
        Assert.DoesNotContain("REDACTED", c.Sql);
        var p = Assert.Single(c.Parameters);
        Assert.Equal("[REDACTED]", p.Value);
        Assert.Equal(ParameterOrigin.Mask, p.Origin);
    }

    [Theory]
    [InlineData("decimal(18,2)")]
    [InlineData("int")]
    [InlineData("datetime2")]
    [InlineData("date")]
    [InlineData("bit")]
    [InlineData("uniqueidentifier")]
    public void Redact_NumericOrTemporal_IsTypedNull(string dataType)
    {
        var c = GenerateMask(new MaskExpression(MaskKind.Redact, Col("X"), new MaskArguments(), dataType));
        Assert.Contains($"CAST(NULL AS {dataType}) AS [X]", c.Sql);
        Assert.Empty(c.Parameters);
    }

    [Fact]
    public void PartialMask_AllArgumentsAreBound()
    {
        var c = GenerateMask(
            new MaskExpression(MaskKind.PartialMask, Col("Email"), new MaskArguments(
                KeepPrefix: P("m_p", SqlParameterType.Int32), KeepSuffix: P("m_s", SqlParameterType.Int32), MaskChar: P("m_c", SqlParameterType.String)),
                "nvarchar(200)"),
            new()
            {
                ["m_p"] = new(2, SqlParameterType.Int32), ["m_s"] = new(4, SqlParameterType.Int32), ["m_c"] = new("*", SqlParameterType.String)
            });

        Assert.DoesNotContain('\'', c.Sql);
        Assert.Contains("LEFT([Email], @p0)", c.Sql);
        Assert.Contains("RIGHT([Email], @p1)", c.Sql);
        Assert.Contains("REPLICATE(@p2", c.Sql);
        Assert.Equal(3, c.Parameters.Length);
        Assert.All(c.Parameters, p => Assert.Equal(ParameterOrigin.Mask, p.Origin));
    }

    [Fact]
    public void Hmac_UsesBoundKeyPads_AndOnlyTheReviewedConstantFragments()
    {
        var c = GenerateMask(
            new MaskExpression(MaskKind.Hmac, Col("Email"), new MaskArguments(
                HmacKey: new("m_i", SqlParameterType.Binary, ParameterOrigin.Mask),
                HmacKeyOuter: new("m_o", SqlParameterType.Binary, ParameterOrigin.Mask)), "nvarchar(200)"),
            new()
            {
                ["m_i"] = new(new byte[] { 1, 2, 3 }, SqlParameterType.Binary), ["m_o"] = new(new byte[] { 4, 5, 6 }, SqlParameterType.Binary)
            });

        Assert.Contains("HASHBYTES('SHA2_256', @p", c.Sql);
        Assert.Equal(2, c.Parameters.Length);
        Assert.All(c.Parameters, p => Assert.Equal(SqlParameterType.Binary, p.Type));
        Assert.DoesNotContain("0x", c.Sql);
    }

    [Fact]
    public void GeoJitter_RoundsWithAStructuralScale_Clamped()
    {
        var c = GenerateMask(new MaskExpression(MaskKind.GeoJitter, Col("Lat"), new MaskArguments(Decimals: 9), "float"));
        Assert.Contains("ROUND([Lat], 6)", c.Sql);
        Assert.Empty(c.Parameters);
    }

    [Fact]
    public void Constant_IsBoundAndCastToTheColumnType()
    {
        var c = GenerateMask(
            new MaskExpression(MaskKind.Constant, Col("Status"), new MaskArguments(Constant: P("m_k", SqlParameterType.String)), "nvarchar(20)"),
            new() { ["m_k"] = new("hidden", SqlParameterType.String) });
        Assert.Contains("CAST(@p0 AS nvarchar(20)) AS [Status]", c.Sql);
        Assert.Equal("hidden", c.Parameters[0].Value);
    }

    [Fact]
    public void MaskArgument_WithoutValue_FailsClosed()
    {
        Assert.Throws<SecurityException>(() => GenerateMask(
            new MaskExpression(MaskKind.Constant, Col("Status"), new MaskArguments(Constant: P("missing", SqlParameterType.String)), "nvarchar(20)")));
    }

    [Theory]
    [InlineData("int); DROP TABLE x; --")]
    [InlineData("nvarchar(200")]
    [InlineData("sysname")]
    [InlineData("unknown_type")]
    [InlineData("")]
    public void HostileOrUnknownDataType_FailsClosed(string dataType)
    {
        Assert.ThrowsAny<Exception>(() => GenerateMask(new MaskExpression(MaskKind.Nullify, Col("X"), new MaskArguments(), dataType)));
    }

    [Fact]
    public void MissingDataType_FailsClosed_ForTypedMasks()
    {
        Assert.ThrowsAny<SecurityException>(() => GenerateMask(new MaskExpression(MaskKind.Nullify, Col("X"), new MaskArguments(), null)));
    }

    [Fact]
    public void ToString_NeverPrintsBoundValues()
    {
        var secret = "s3cr3t-key-material";
        var p = new BoundParameter("@p0", "@p0", 0, secret, SqlParameterType.String, ParameterOrigin.Mask, "m");
        var v = new PolicyValue(secret, SqlParameterType.String);
        var t = new TenantBinding("__t", secret, SqlParameterType.String);
        Assert.DoesNotContain(secret, p.ToString());
        Assert.DoesNotContain(secret, v.ToString());
        Assert.DoesNotContain(secret, t.ToString());
        var spec = new MaskSpec(MaskKind.Hmac, new MaskArguments(), new Dictionary<string, PolicyValue> { ["k"] = v }.ToFrozenDictionary());
        Assert.DoesNotContain(secret, spec.ToString());
        var compiled = new CompiledSql("SELECT 1", ImmutableArray.Create(p), TargetSqlDialect.SqlServer, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "v");
        Assert.DoesNotContain(secret, compiled.ToString());
    }

    // ---- verifier ----

    private static SecurityCoverageVerifier VerifierWithMask(params string[] masked) => new(_ =>
        new TableCoverageRequirement("dbo.t", "dbo", "t", ImmutableArray<SecurityPredicateId>.Empty, ImmutableArray<SecurityPredicateId>.Empty,
            masked.ToImmutableHashSet(StringComparer.Ordinal)));

    private static SelectStatement Wrapped(params SelectItem[] projections) => new(null,
        new QuerySpecification(false, new SelectItem[]
            {
                new WildcardSelectItem(null)
            }, new SubqueryTableSource(new SelectStatement(null,
                new QuerySpecification(false, projections,
                    new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("t", true) }), null),
                    null, null, null), null, null), new SqlIdentifier("t", true)),
            null, null, null), null, null);

    [Fact]
    public void Verifier_MaskedColumn_MustBeAMaskExpression()
    {
        var plain = new ColumnSelectItem(Col("Email"), new SqlIdentifier("Email", true));
        var masked = new ColumnSelectItem(new MaskExpression(MaskKind.Nullify, Col("Email"), new MaskArguments(), "nvarchar(10)"), new SqlIdentifier("Email", true));

        Assert.Throws<SecurityCoverageException>(() => VerifierWithMask("Email").Verify(Wrapped(plain), CancellationToken.None));
        VerifierWithMask("Email").Verify(Wrapped(masked), CancellationToken.None);
        VerifierWithMask().Verify(Wrapped(plain), CancellationToken.None);
    }

    [Fact]
    public void Verifier_WildcardProjection_IsRejected_WhenTheTableHasMasks()
    {
        Assert.Throws<SecurityCoverageException>(() => VerifierWithMask("Email").Verify(Wrapped(new WildcardSelectItem(null)), CancellationToken.None));
    }

    [Fact]
    public void Verifier_MaskExpressionOverAnotherColumn_IsRejected()
    {
        var swapped = new ColumnSelectItem(new MaskExpression(MaskKind.Nullify, Col("Other"), new MaskArguments(), "int"), new SqlIdentifier("Email", true));
        Assert.Throws<SecurityCoverageException>(() => VerifierWithMask("Email").Verify(Wrapped(swapped), CancellationToken.None));
    }

    // ---- typed injection ----

    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    private TypedPolicyContext Context(DialectCapabilities? caps = null) => new(
        PolicyFixtures.Catalog(), _rowFilters, _masks,
        new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
        caps ?? DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer));

    private static MaskSpec RedactSpec() => new(MaskKind.Redact,
        new MaskArguments(Constant: P("__mask_email", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__mask_email"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());

    private SqlStatement Secure(string sql, TypedPolicyContext typed) =>
        (SqlStatement)new AstSecurityVisitor(new RlsOptions(), new FastSqlEngine(), typed).Visit(CompilerTestHelpers.Build(sql));

    [Fact]
    public void TypedInjection_ProjectsMaskExpression_ForMaskedColumns_AndCollectsValues()
    {
        _rowFilters.Predicates[Orders] = PolicyPredicate.DenyAll;
        _masks.Masks[(Orders, "email")] = RedactSpec();
        var typed = Context();

        var secured = (SelectStatement)Secure("SELECT id, email FROM orders", typed);

        var wrapper = (SubqueryTableSource)((QuerySpecification)secured.Body).From!;
        var items = ((QuerySpecification)wrapper.Subquery.Body).Projections.Cast<ColumnSelectItem>().ToList();
        var email = items.Single(i => i.Alias!.Value == "Email");
        var mask = Assert.IsType<MaskExpression>(email.Expression);
        Assert.Equal("nvarchar(200)", mask.DataType);
        Assert.All(items.Where(i => i.Alias!.Value != "Email"), i => Assert.IsType<ColumnReference>(i.Expression));
        Assert.Equal("[REDACTED]", typed.PolicyValues["__mask_email"].Value);
        Assert.NotEqual("-", typed.Tables.Single().MaskFingerprint);
        typed.CreateVerifier().Verify(secured, CancellationToken.None);
    }

    [Fact]
    public void Hmac_DegradesToRedact_WhenInDbHmacFalse()
    {
        _rowFilters.Predicates[Orders] = PolicyPredicate.DenyAll;
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Hmac,
            new MaskArguments(HmacKey: new("k_i", SqlParameterType.Binary, ParameterOrigin.Mask), HmacKeyOuter: new("k_o", SqlParameterType.Binary, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue>
            {
                ["k_i"] = new(new byte[] { 1 }, SqlParameterType.Binary), ["k_o"] = new(new byte[] { 2 }, SqlParameterType.Binary)
            }.ToFrozenDictionary());
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer) with { InDbHmac = false };
        var typed = Context(caps);

        var secured = (SelectStatement)Secure("SELECT email FROM orders", typed);

        var mask = Collect<MaskExpression>(secured).Single();
        Assert.Equal(MaskKind.Redact, mask.Kind);
        Assert.DoesNotContain("k_i", typed.PolicyValues.Keys);   // the key never reaches the statement
        Assert.DoesNotContain("k_o", typed.PolicyValues.Keys);
    }

    [Fact]
    public void Hmac_WithInDbHmac_KeepsTheHmacMask()
    {
        _rowFilters.Predicates[Orders] = PolicyPredicate.DenyAll;
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Hmac,
            new MaskArguments(HmacKey: new("k_i", SqlParameterType.Binary, ParameterOrigin.Mask), HmacKeyOuter: new("k_o", SqlParameterType.Binary, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue>
            {
                ["k_i"] = new(new byte[] { 1 }, SqlParameterType.Binary), ["k_o"] = new(new byte[] { 2 }, SqlParameterType.Binary)
            }.ToFrozenDictionary());
        var secured = (SelectStatement)Secure("SELECT email FROM orders", Context());
        Assert.Equal(MaskKind.Hmac, Collect<MaskExpression>(secured).Single().Kind);
    }

    private static List<T> Collect<T>(SqlNode node) where T : class
    {
        var list = new List<T>();
        void Walk(object? n)
        {
            if (n is null || n is string) return;
            if (n is T t) list.Add(t);
            if (n is System.Collections.IEnumerable e) { foreach (var i in e) Walk(i); return; }
            if (n is SqlNode)
            {
                foreach (var p in n.GetType().GetProperties())
                {
                    if (p.GetIndexParameters().Length > 0 || p.PropertyType.IsPrimitive || p.PropertyType.IsEnum) continue;
                    Walk(p.GetValue(n));
                }
            }
        }

        Walk(node);
        return list;
    }

    [Fact]
    public void MaskExpression_IsOpaque_ToRewriters()
    {
        var mask = new MaskExpression(MaskKind.Nullify, Col("X"), new MaskArguments(), "int");
        Assert.Same(mask, new AstSimplificationVisitor().Visit(mask));
        Assert.Same(mask, new AstValidationVisitor().Visit(mask));
    }
}
