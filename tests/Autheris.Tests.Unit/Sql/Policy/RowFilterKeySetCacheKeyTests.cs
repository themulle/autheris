namespace Autheris.Tests.Unit.Sql.Policy;

using System.Collections.Immutable;
using System.Globalization;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>Issue #107 SEC-RF107-03: complete, unambiguous, culture-invariant cache key; SEC-RF107-18: no text budget in the key; D-WPC-8: the row limit bind is hashed.</summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetCacheKeyTests
{
    private static readonly TableIdentifier Target = new("d", "s", "target");
    private static readonly TableIdentifier Dependent = new("d", "s", "dep");

    private static BoundParameter Bind(string name, object? value, SqlParameterType type = SqlParameterType.String, ParameterOrigin origin = ParameterOrigin.Policy)
        => new($":{name}", name, 0, value, type, origin);

    private static CompiledKeyQuery Query(string sql = "SELECT DISTINCT k FROM dep WHERE t = :t", params BoundParameter[] binds)
        => new(sql, binds.Length == 0 ? [Bind("t", "tenant-a")] : [.. binds], "int", "int", "fk", "v1");

    private static RowFilterKeySetCacheKeyInput Input(
        string provider = "SqlServer",
        string connectionString = "Server=a;Database=b",
        TargetSqlDialect dialect = TargetSqlDialect.SqlServer,
        string tenant = "tenant-a",
        string dataSource = "ds",
        CompiledKeyQuery? query = null,
        int maxKeys = 2000,
        IReadOnlyDictionary<TableIdentifier, long>? epochs = null)
        => new(provider, connectionString, dialect, tenant, dataSource, query ?? Query(), maxKeys,
            epochs ?? new Dictionary<TableIdentifier, long> { [Target] = 3, [Dependent] = 7 });

    private static string Key(RowFilterKeySetCacheKeyInput input) => RowFilterKeySetCacheKey.Compute(input);

    [Fact]
    public void EveryComponent_ChangesKey_SEC_RF107_03()
    {
        var baseKey = Key(Input());
        var variants = new Dictionary<string, RowFilterKeySetCacheKeyInput>
        {
            ["provider"] = Input(provider: "PostgreSql"),
            ["connection string"] = Input(connectionString: "Server=a;Database=c"),
            ["dialect"] = Input(dialect: TargetSqlDialect.PostgreSql),
            ["tenant"] = Input(tenant: "tenant-b"),
            ["data source"] = Input(dataSource: "ds2"),
            ["key sql"] = Input(query: Query("SELECT DISTINCT k FROM dep WHERE t = :t AND x = 1")),
            ["bind value"] = Input(query: Query(binds: Bind("t", "tenant-b"))),
            ["bind name"] = Input(query: Query(binds: Bind("u", "tenant-a"))),
            ["bind type"] = Input(query: Query(binds: Bind("t", "tenant-a", SqlParameterType.Binary))),
            ["bind origin"] = Input(query: Query(binds: Bind("t", "tenant-a", origin: ParameterOrigin.Tenant))),
            ["extra bind"] = Input(query: Query(binds: [Bind("t", "tenant-a"), Bind("u", 1L, SqlParameterType.Int64)])),
            ["key column type"] = Input(query: Query() with { KeyColumnType = "bigint" }),
            ["fk column type"] = Input(query: Query() with { ForeignKeyColumnType = "bigint" }),
            ["fk column"] = Input(query: Query() with { ForeignKeyColumn = "fk2" }),
            ["compiler version"] = Input(query: Query() with { CompilerVersion = "v2" }),
            ["max keys"] = Input(maxKeys: 1000),
            ["target epoch"] = Input(epochs: new Dictionary<TableIdentifier, long> { [Target] = 4, [Dependent] = 7 }),
            ["dependent epoch"] = Input(epochs: new Dictionary<TableIdentifier, long> { [Target] = 3, [Dependent] = 8 }),
            ["extra epoch identifier"] = Input(epochs: new Dictionary<TableIdentifier, long> { [Target] = 3, [Dependent] = 7, [new("d", "s", "other")] = 1 })
        };

        foreach (var (name, input) in variants)
        {
            Key(input).ShouldNotBe(baseKey, $"changing the {name} must change the key");
        }

        variants.Values.Select(Key).Distinct().Count().ShouldBe(variants.Count, "all single-component variants differ from each other");
    }

    [Fact]
    public void DelimiterAmbiguity_ProducesDifferentKeys_SEC_RF107_03()
    {
        Key(Input(tenant: "a:b", dataSource: "c")).ShouldNotBe(Key(Input(tenant: "a", dataSource: "b:c")));
        Key(Input(tenant: "a", dataSource: "bc")).ShouldNotBe(Key(Input(tenant: "ab", dataSource: "c")));
        Key(Input(tenant: "1:a", dataSource: "")).ShouldNotBe(Key(Input(tenant: "", dataSource: "1:a")));
        Key(Input(query: Query(binds: [Bind("a", "x"), Bind("b", "y")])))
            .ShouldNotBe(Key(Input(query: Query(binds: [Bind("a", "xs:1:b"), Bind("y", "")]))));
    }

    [Fact]
    public void IsCultureInvariant_SEC_RF107_03()
    {
        var input = Input(query: Query(binds:
        [
            Bind("a", 1234567.891m, SqlParameterType.Decimal),
            Bind("b", 12345L, SqlParameterType.Int64),
            Bind("c", DateTimeOffset.Parse("2026-10-17T10:00:00+02:00", CultureInfo.InvariantCulture), SqlParameterType.TimestampTz),
            Bind("d", "I"),
            Bind("e", 1.5d, SqlParameterType.Double)
        ]));
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = Key(input);
            foreach (var name in new[] { "tr-TR", "de-DE", "ar-SA", "fr-FR" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Key(input).ShouldBe(invariant, $"culture {name}");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void IsStable_AndIndependentOfEpochDictionaryOrder()
    {
        var a = new Dictionary<TableIdentifier, long> { [Target] = 3, [Dependent] = 7 };
        var b = new Dictionary<TableIdentifier, long> { [Dependent] = 7, [Target] = 3 };

        Key(Input(epochs: a)).ShouldBe(Key(Input(epochs: b)));
        Key(Input()).ShouldBe(Key(Input()));
    }

    [Fact]
    public void UsesFullSha256_SEC_RF107_03()
    {
        var key = Key(Input());

        key.Length.ShouldBe(64);
        key.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void RowLimitBind_IsHashed_D_WPC_8()
    {
        var limit1 = Bind("p_limit", 2001L, SqlParameterType.Int64, ParameterOrigin.QueryLiteral);
        var limit2 = Bind("p_limit", 1001L, SqlParameterType.Int64, ParameterOrigin.QueryLiteral);

        Key(Input(query: Query(binds: [Bind("t", "tenant-a"), limit1])))
            .ShouldNotBe(Key(Input(query: Query(binds: [Bind("t", "tenant-a"), limit2]))));
    }

    [Fact]
    public void NullStringEmptyAndOtherTypes_AreDistinguishedByTypeTag()
    {
        var keys = new object?[] { null, "", "null", 0L, "0", 0m, false, new byte[] { 0 }, Guid.Empty }
            .Select(v => Key(Input(query: Query(binds: Bind("t", v)))))
            .ToList();

        keys.Distinct().Count().ShouldBe(keys.Count);
    }

    [Fact]
    public void BindValues_AreEncodedInvariantAndCanonical()
    {
        string K(object? v) => Key(Input(query: Query(binds: Bind("t", v))));

        K(5).ShouldBe(K(5L));   // int and long are one canonical decimal integer
        K(new byte[] { 1, 2 }).ShouldBe(K(new byte[] { 1, 2 }));
        K(new byte[] { 1, 2 }).ShouldNotBe(K(new byte[] { 1, 3 }));
        K("a\ud800b").ShouldNotBe(K("a\ud801b"));   // unpaired surrogates must not collapse to one replacement character
    }

    [Fact]
    public void TheTextBudget_IsNotPartOfTheInput_SEC_RF107_18()
        => typeof(RowFilterKeySetCacheKeyInput).GetProperties()
            .Select(p => p.Name)
            .ShouldNotContain(n => n.Contains("Budget", StringComparison.OrdinalIgnoreCase) || n.Contains("UserSql", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Expansion", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Diagnostic_IsTheFirst12HexDigits_SEC_RF107_09_b()
    {
        var key = Key(Input());

        RowFilterKeySetCacheKey.DiagnosticId(key).ShouldBe(key[..12]);
    }
}
