using System.Collections.Immutable;
using System.Text.RegularExpressions;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Paging;

public class KeysetSeekCompileTests
{
    private const string Sql = "SELECT id, name FROM crm.customers ORDER BY id";

    private static KeysetTestEnv Env(TargetSqlDialect dialect = TargetSqlDialect.PostgreSql) =>
        new KeysetTestEnv(dialect).Table("customers", [], "id:int", "name:varchar", "region:varchar");

    private static string Hash(KeysetTestEnv env, string sql, params string[] key) =>
        env.Compile(sql, KeysetTestEnv.Key(key)).KeysetKey!.ShapeHash;

    private static CompiledSql Seek(KeysetTestEnv env, string sql, string[] key, params object[] values) =>
        env.Compile(sql, KeysetTestEnv.Key(key), KeysetTestEnv.Seek(Hash(env, sql, key), values));

    [Fact]
    public void KeyInfo_IsReported_WithShapeHash()
    {
        var env = Env();
        var compiled = env.Compile(Sql, KeysetTestEnv.Key("id"));
        Assert.Null(compiled.KeysetUnavailable);
        var key = compiled.KeysetKey!;
        Assert.Equal(["id"], key.Columns.ToArray());
        Assert.Equal([false], key.Descending.ToArray());
        Assert.Equal([0], key.OutputOrdinals.ToArray());
        Assert.Equal(16, key.ShapeHash.Length);
        Assert.Equal(key.ShapeHash, key.ShapeHash.ToLowerInvariant());
        Assert.DoesNotContain(compiled.Parameters, p => p.Origin == ParameterOrigin.KeysetSeek);
        Assert.Null(env.Compile(Sql, null).KeysetKey);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.Databricks)]
    public void Seek_GoldenPerDialect(TargetSqlDialect dialect)
    {
        var env = new KeysetTestEnv(dialect).Table("items", [], "tenant_id:int", "sku:int", "label:int");
        var compiled = Seek(env, "SELECT tenant_id, sku FROM crm.items ORDER BY tenant_id DESC, sku", ["tenant_id", "sku"], 10, 20);

        var slots = compiled.Parameters.Where(p => p.Origin == ParameterOrigin.KeysetSeek).ToArray();
        Assert.Equal(2, slots.Length);
        Assert.Equal([10, 20], slots.OrderBy(s => s.SourceName).Select(s => s.Value).ToArray());
        Assert.Equal(["keyset:0", "keyset:1"], slots.Select(s => s.SourceName!).OrderBy(n => n).ToArray());
        // (tenant_id < @0) OR (tenant_id = @0 AND sku > @1): DESC seeks with <, ASC with >, the earlier keys by equality.
        var text = Regex.Replace(compiled.Sql, "[\"`\\[\\]]", string.Empty);
        Assert.True(Regex.IsMatch(text, @"\(items\.tenant_id < \S+\) OR \(items\.tenant_id = \S+ AND items\.sku > \S+\)", RegexOptions.IgnoreCase), text);
        Assert.Contains("ORDER BY", compiled.Sql);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetSeek_ValuesOnlyInBoundSlots_NeverInSqlText()
    {
        var env = new KeysetTestEnv(TargetSqlDialect.PostgreSql).Table("people", [], "code:varchar", "n:int");
        string hash = Hash(env, "SELECT code FROM crm.people ORDER BY code", "code");
        var compiled = env.Compile("SELECT code FROM crm.people ORDER BY code", KeysetTestEnv.Key("code"), KeysetTestEnv.Seek(hash, "zz'; DROP TABLE x;--"));

        Assert.DoesNotContain("zz", compiled.Sql);
        Assert.DoesNotContain("DROP", compiled.Sql);
        var slot = Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.KeysetSeek);
        Assert.Equal("zz'; DROP TABLE x;--", slot.Value);
        Assert.Equal("varchar", slot.ColumnType);

        var numeric = Seek(Env(), Sql, ["id"], 424242);
        Assert.DoesNotContain("424242", numeric.Sql);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Seek_WithoutCandidate_Rejected()
    {
        var env = Env();
        string hash = Hash(env, Sql, "id");
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(Sql, null, KeysetTestEnv.Seek(hash, 1)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetSeek_WithPageOffset_Rejected()
    {
        var env = Env();
        string hash = Hash(env, Sql, "id");
        var request = env.Request(KeysetTestEnv.Key("id"), KeysetTestEnv.Seek(hash, 1)) with { BindPageOffset = true, PageOffset = 0 };
        Assert.Throws<KeysetSeekRejectedException>(() => env.Engine.Compile(Sql.AsMemory(), request, CancellationToken.None));
        var offsetOnly = env.Request(KeysetTestEnv.Key("id"), KeysetTestEnv.Seek(hash, 1)) with { PageOffset = 3 };
        Assert.Throws<KeysetSeekRejectedException>(() => env.Engine.Compile(Sql.AsMemory(), offsetOnly, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetSeek_OnDml_Rejected()
    {
        var env = Env();
        string hash = Hash(env, Sql, "id");
        var request = env.Request(KeysetTestEnv.Key("id"), KeysetTestEnv.Seek(hash, 1)) with { Statements = StatementPermissions.Delete };
        Assert.Throws<KeysetSeekRejectedException>(() =>
            env.Engine.Compile("DELETE FROM crm.customers WHERE id = 5".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetSeek_ShapeHashMismatch_FailsClosed()
    {
        var env = Env();
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(Sql, KeysetTestEnv.Key("id"), KeysetTestEnv.Seek("0123456789abcdef", 1)));
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(Sql, KeysetTestEnv.Key("id"), KeysetTestEnv.Seek(string.Empty, 1)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetSeek_CountOrTypeMismatch_FailsClosed()
    {
        var env = Env();
        string hash = Hash(env, Sql, "id");
        var key = KeysetTestEnv.Key("id");
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(Sql, key, KeysetTestEnv.Seek(hash, 1, 2)));
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(Sql, key, new KeysetSeek(hash, ImmutableArray<object>.Empty)));
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(Sql, key, new KeysetSeek(hash, [null!])));
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(Sql, key, KeysetTestEnv.Seek(hash, 1.5)));
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(Sql, key, KeysetTestEnv.Seek(hash, new object())));
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(Sql, key, KeysetTestEnv.Seek(hash, true)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Keyset_PkChangedBetweenPages_Rejected()
    {
        var env = Env();
        string hash = Hash(env, Sql, "id");

        var widened = new KeysetTestEnv(TargetSqlDialect.PostgreSql).Table("customers", [], "id:bigint", "name:varchar");
        Assert.Throws<KeysetSeekRejectedException>(() => widened.Compile(Sql, KeysetTestEnv.Key("id"), KeysetTestEnv.Seek(hash, 1)));

        var composite = "SELECT id, name FROM crm.customers ORDER BY id, name";
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(composite, KeysetTestEnv.Key("id", "name"), KeysetTestEnv.Seek(hash, 1, "a")));
        Assert.Throws<KeysetSeekRejectedException>(() => env.Compile(Sql, KeysetTestEnv.Key("name"), KeysetTestEnv.Seek(hash, 1)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Keyset_OrderByChangedShape_Rejected()
    {
        var env = Env();
        string hash = Hash(env, Sql, "id");
        var key = KeysetTestEnv.Key("id");
        Assert.Throws<KeysetSeekRejectedException>(() =>
            env.Compile("SELECT id, name FROM crm.customers ORDER BY id DESC", key, KeysetTestEnv.Seek(hash, 1)));
        Assert.Throws<KeysetSeekRejectedException>(() =>
            env.Compile("SELECT id, name FROM crm.customers ORDER BY name", key, KeysetTestEnv.Seek(hash, 1)));
        Assert.Throws<KeysetSeekRejectedException>(() =>
            env.Compile("SELECT id FROM crm.customers JOIN crm.customers c2 ON c2.id = customers.id ORDER BY id", key, KeysetTestEnv.Seek(hash, 1)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetPredicate_UserWhereWithOr_StillAndedWithSeek()
    {
        var env = Env();
        var compiled = Seek(env, "SELECT id, name FROM crm.customers WHERE id < 5 OR name = 'x' ORDER BY id", ["id"], 3);
        var text = Regex.Replace(compiled.Sql, "[\"`\\[\\]]", string.Empty);
        Assert.Matches(@"WHERE \(\(?customers\.id < .*OR.*name = .*\)\) AND \(\(customers\.id > \S+\)\)|WHERE \(.*\bOR\b.*\) AND \(.*customers\.id > \S+.*\)", text);
        int andAt = text.LastIndexOf(" AND ", StringComparison.Ordinal);
        Assert.True(text.IndexOf(" OR ", StringComparison.Ordinal) < andAt, text);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetPredicate_RowFilterStillApplied()
    {
        var env = Env().Policy("customers", PolicyPredicate.Create(
            new BinaryExpression(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("region", true) })), BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_region"] = new("EU", SqlParameterType.String) }));

        var compiled = Seek(env, Sql, ["id"], 7);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.KeysetSeek && Equals(p.Value, 7));
        var policy = Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Policy);
        Assert.Equal("EU", policy.Value);
        Assert.Contains(compiled.AppliedPredicates, _ => true);
        var text = Regex.Replace(compiled.Sql, "[\"`\\[\\]]", string.Empty);
        Assert.Contains("region", text);
        Assert.Contains("id >", text);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetPredicate_DoesNotSatisfyRequiredFilterRule()
    {
        var env = new KeysetTestEnv(TargetSqlDialect.PostgreSql).Table("customers", ["id"], "id:int", "name:varchar");
        string hash = Hash(env, "SELECT id, name FROM crm.customers WHERE id = 1 ORDER BY id", "id");

        // the seek compares the key column, but it is the page continuation, not a filter the caller chose
        Assert.Throws<RequiredFilterColumnMissingException>(() => env.Compile(Sql, KeysetTestEnv.Key("id")));
        Assert.Throws<RequiredFilterColumnMissingException>(() => env.Compile(Sql, KeysetTestEnv.Key("id"), KeysetTestEnv.Seek(hash, 1)));
        Assert.Contains(env.Compile("SELECT id, name FROM crm.customers WHERE id = 1 ORDER BY id", KeysetTestEnv.Key("id"), KeysetTestEnv.Seek(hash, 0)).Parameters,
            p => p.Origin == ParameterOrigin.KeysetSeek);
    }

    [Fact]
    public void Seek_TemplateCacheReusedAcrossValues()
    {
        var env = Env();
        string hash = Hash(env, Sql, "id");
        var key = KeysetTestEnv.Key("id");
        var first = env.Compile(Sql, key, KeysetTestEnv.Seek(hash, 1));
        long hits = env.Engine.CompileCache.Stats.Hits;
        var second = env.Compile(Sql, key, KeysetTestEnv.Seek(hash, 2));

        Assert.Equal(hits + 1, env.Engine.CompileCache.Stats.Hits);
        Assert.Equal(first.Sql, second.Sql);
        Assert.Equal(1, first.Parameters.Single(p => p.Origin == ParameterOrigin.KeysetSeek).Value);
        Assert.Equal(2, second.Parameters.Single(p => p.Origin == ParameterOrigin.KeysetSeek).Value);
        Assert.Equal(first.KeysetKey, second.KeysetKey);

        // a seek of another value type is another template, a plain compile never reuses a seek template
        long before = env.Engine.CompileCache.Stats.Hits;
        env.Compile(Sql, key, KeysetTestEnv.Seek(hash, 3L));
        Assert.Equal(before, env.Engine.CompileCache.Stats.Hits);
        Assert.DoesNotContain(env.Compile(Sql, key).Parameters, p => p.Origin == ParameterOrigin.KeysetSeek);
    }

    [Fact]
    public void KeyOrdinals_SelectStar_CatalogOrder()
    {
        var env = new KeysetTestEnv(TargetSqlDialect.PostgreSql).Table("items", [], "name:varchar", "id:int", "total:int");
        Assert.Equal([1], env.Compile("SELECT * FROM crm.items ORDER BY id", KeysetTestEnv.Key("id")).KeysetKey!.OutputOrdinals.ToArray());
        Assert.Equal([3], env.Compile("SELECT total, name, items.* FROM crm.items ORDER BY id", KeysetTestEnv.Key("id")).KeysetKey!.OutputOrdinals.ToArray());
        Assert.Equal([2, 0], env.Compile("SELECT id, total, name FROM crm.items ORDER BY name, id", KeysetTestEnv.Key("id", "name")).KeysetKey!.OutputOrdinals.ToArray());
    }

    [Fact]
    public void KeyOrdinals_KeyNotProjected_Unavailable()
    {
        var env = Env();
        var compiled = env.Compile("SELECT name FROM crm.customers ORDER BY id", KeysetTestEnv.Key("id"));
        Assert.Null(compiled.KeysetKey);
        Assert.Equal(KeysetUnavailableReasons.KeyNotProjected, compiled.KeysetUnavailable);
        var transformed = env.Compile("SELECT id + 1 AS id2, name FROM crm.customers ORDER BY id", KeysetTestEnv.Key("id"));
        Assert.Equal(KeysetUnavailableReasons.KeyNotProjected, transformed.KeysetUnavailable);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Keyset_KeyNotProjected_NotEligible()
    {
        var env = Env();
        string hash = Hash(env, Sql, "id");
        Assert.Throws<KeysetSeekRejectedException>(() =>
            env.Compile("SELECT name FROM crm.customers ORDER BY id", KeysetTestEnv.Key("id"), KeysetTestEnv.Seek(hash, 1)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Keyset_HiddenKeyColumn_NotEligible()
    {
        // the declared key names a column the catalog does not expose to the caller
        var compiled = Env().Compile(Sql, KeysetTestEnv.Key("hidden_id"));
        Assert.Null(compiled.KeysetKey);
        Assert.Equal(KeysetUnavailableReasons.NotPrimaryKey, compiled.KeysetUnavailable);

        var env = new KeysetTestEnv(TargetSqlDialect.PostgreSql).Table("customers", [], "name:varchar");
        var hidden = env.Compile("SELECT name FROM crm.customers ORDER BY name", KeysetTestEnv.Key("name", "id"));
        Assert.Equal(KeysetUnavailableReasons.NotPrimaryKey, hidden.KeysetUnavailable);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Keyset_MaskedKeyColumn_NotEligible()
    {
        var env = Env().Mask("customers", "id");
        var outcome = Record.Exception(() => env.Compile(Sql, KeysetTestEnv.Key("id")));
        if (outcome is null)
        {
            var compiled = env.Compile(Sql, KeysetTestEnv.Key("id"));
            Assert.Null(compiled.KeysetKey);
            Assert.Equal(KeysetUnavailableReasons.KeyMasked, compiled.KeysetUnavailable);
        }
        else
        {
            Assert.IsType<MaskedColumnPredicateException>(outcome);   // the statement itself orders by a masked column
        }
    }

    [Fact]
    public void KeyOrdinals_MaskedKey_Unavailable()
    {
        // masked key that is not part of ORDER BY cannot be a key: the key must be in ORDER BY, so a masked key never yields a KeysetKey
        var env = Env().Mask("customers", "name");
        var compiled = env.Compile("SELECT id, name FROM crm.customers ORDER BY id", KeysetTestEnv.Key("id", "name"));
        Assert.Null(compiled.KeysetKey);
        Assert.NotNull(compiled.KeysetUnavailable);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Keyset_KeyMaskedAtContinuation_Rejected()
    {
        var plain = Env();
        string hash = Hash(plain, Sql, "id");
        var masked = Env().Mask("customers", "id");
        Assert.Throws<KeysetSeekRejectedException>(() => masked.Compile(Sql, KeysetTestEnv.Key("id"), KeysetTestEnv.Seek(hash, 1)));
    }

    [Fact]
    public void Oracle_TextKey_NotEligible_ButNumericKeyIs()
    {
        var env = new KeysetTestEnv(TargetSqlDialect.Oracle).Table("people", [], "code:varchar2(20)", "n:number");
        Assert.Equal(KeysetUnavailableReasons.Shape, env.Compile("SELECT code FROM crm.people ORDER BY code", KeysetTestEnv.Key("code")).KeysetUnavailable);
        Assert.NotNull(env.Compile("SELECT n FROM crm.people ORDER BY n", KeysetTestEnv.Key("n")).KeysetKey);
    }

    [Fact]
    public void Ineligible_WithoutSeek_CompilesAsBefore()
    {
        var env = Env();
        var compiled = env.Compile("SELECT id FROM crm.customers ORDER BY name", KeysetTestEnv.Key("id"));
        Assert.Equal(KeysetUnavailableReasons.NotPrimaryKey, compiled.KeysetUnavailable);
        Assert.Equal(env.Compile("SELECT id FROM crm.customers ORDER BY name", null).Sql, compiled.Sql);
    }
}
