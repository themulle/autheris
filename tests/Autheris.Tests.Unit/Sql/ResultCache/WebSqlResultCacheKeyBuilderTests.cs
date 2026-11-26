namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autheris.Application.Sql.ResultCache;
using Autheris.Domain.Common;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

public sealed class WebSqlResultCacheKeyBuilderTests
{
    private static readonly TableIdentifier Orders = new("main", "dbo", "Orders");

    private static BoundParameter Policy(int ordinal, object? value, SqlParameterType type = SqlParameterType.String) =>
        new("@p" + ordinal, "p" + ordinal, ordinal, value, type, ParameterOrigin.Policy, SourceName: "pol" + ordinal);

    private static BoundParameter Client(int ordinal, string name) =>
        new("@p" + ordinal, "p" + ordinal, ordinal, null, SqlParameterType.String, ParameterOrigin.ClientNamed, SourceName: name);

    private static WebSqlResultCacheKeyInput Input() => new(
        Tenant: new TenantId("tenant1"),
        UserSid: "S-1-5-21-1",
        PrincipalKey: "principal-a",
        LogicalSource: "Default",
        EffectiveDataSourceName: "default",
        ConnectionKey: "default",
        ConnectionFingerprint: "fp1",
        Dialect: TargetSqlDialect.PostgreSql,
        CompilerVersion: "ast-compiler/1.0",
        CompiledSql: "SELECT a FROM dbo.Orders WHERE t = @p0 AND c = @p1",
        Mode: WebSqlResultCacheKeyMode.Plain,
        DeliveredRowLimit: 1000,
        Format: "json",
        Transport: "websql",
        OutputColumnMasks: [false, true],
        Parameters: [Policy(0, "tenant1"), Client(1, "city")],
        ClientValues: new Dictionary<string, object?> { ["city"] = "Berlin" },
        MetricViewMinGroupSize: 0,
        MetricViewHiddenCountAlias: null,
        Purpose: null,
        Tables: [Orders]);

    private static string Hash(WebSqlResultCacheKeyInput input)
    {
        WebSqlResultCacheKeyBuilder.TryBuild(input, out var key).ShouldBeTrue();
        return key.Hash;
    }

    private static string HashWithValue(object? value) =>
        Hash(Input() with { Parameters = [Policy(0, value, SqlParameterType.String)] });

    [Fact]
    public void Same_input_gives_same_64_hex_key_and_carries_identity()
    {
        WebSqlResultCacheKeyBuilder.TryBuild(Input(), out var a).ShouldBeTrue();
        WebSqlResultCacheKeyBuilder.TryBuild(Input(), out var b).ShouldBeTrue();

        a.Hash.ShouldBe(b.Hash);
        a.Hash.Length.ShouldBe(64);
        a.Hash.ShouldBe(a.Hash.ToLowerInvariant());
        a.Tenant.Value.ShouldBe("tenant1");
        a.UserSid.ShouldBe("S-1-5-21-1");
        a.Tables.ShouldBe([Orders]);
    }

    [Fact]
    public void Key_changes_for_each_statement_shaping_field()
    {
        var baseHash = Hash(Input());
        var variants = new WebSqlResultCacheKeyInput[]
        {
            Input() with { Tenant = new TenantId("tenant2") },
            Input() with { LogicalSource = "other" },
            Input() with { ConnectionKey = "c2" },
            Input() with { ConnectionFingerprint = "fp2" },
            Input() with { Dialect = TargetSqlDialect.SqlServer },
            Input() with { CompilerVersion = "ast-compiler/2.0" },
            Input() with { CompiledSql = "SELECT a FROM dbo.Orders WHERE t = @p0 AND c = @p1 " },
            Input() with { Mode = WebSqlResultCacheKeyMode.PagedFull },
            Input() with { DeliveredRowLimit = 1001 },
            Input() with { Format = "parquet" },
            Input() with { Transport = "other" },
            Input() with { ClientValues = new Dictionary<string, object?> { ["city"] = "Paris" } },
            Input() with { ClientValues = new Dictionary<string, object?> { ["city"] = "Berlin", ["extra"] = 1 } },
            Input() with { Parameters = [Policy(0, "tenant2"), Client(1, "city")] },
        };

        foreach (var variant in variants)
        {
            Hash(variant).ShouldNotBe(baseHash);
        }
    }

    [Fact]
    public void Client_value_names_are_case_insensitive_and_order_independent()
    {
        var a = Input() with { ClientValues = new Dictionary<string, object?> { ["city"] = "Berlin", ["x"] = 1 } };
        var b = Input() with { ClientValues = new Dictionary<string, object?> { ["X"] = 1, ["CITY"] = "Berlin" } };

        Hash(a).ShouldBe(Hash(b));
    }

    [Fact]
    public void Parameter_order_in_the_list_does_not_matter_only_ordinal()
    {
        var a = Input() with { Parameters = [Policy(0, "tenant1"), Client(1, "city")] };
        var b = Input() with { Parameters = [Client(1, "city"), Policy(0, "tenant1")] };

        Hash(a).ShouldBe(Hash(b));
    }

    [Fact]
    public void Type_tags_separate_string_from_number()
    {
        HashWithValue("1").ShouldNotBe(HashWithValue(1));
        HashWithValue(1).ShouldNotBe(HashWithValue(1L));
        HashWithValue(1).ShouldNotBe(HashWithValue((short)1));
        HashWithValue(true).ShouldNotBe(HashWithValue(1));
        HashWithValue(null).ShouldNotBe(HashWithValue(""));
        HashWithValue("").ShouldNotBe(HashWithValue(0));
    }

    [Fact]
    public void List_boundaries_are_unambiguous()
    {
        ClientListValue List(params string[] items) => ClientListValue.Create(items.Cast<object>().ToList(), SqlParameterType.String);

        string With(ClientListValue list) => Hash(Input() with
        {
            Parameters = [Client(1, "ids")],
            ClientValues = new Dictionary<string, object?> { ["ids"] = list }
        });

        With(List("a,b")).ShouldNotBe(With(List("a", "b")));
        With(List("ab")).ShouldNotBe(With(List("a", "b")));
        With(List("a", "b")).ShouldBe(With(List("a", "b")));
        With(List("a", "b")).ShouldNotBe(With(List("b", "a")));
    }

    [Fact]
    public void Temporal_and_numeric_values_are_encoded_with_their_type()
    {
        var date = new DateOnly(2026, 1, 2);
        HashWithValue(date).ShouldNotBe(HashWithValue(new DateTime(2026, 1, 2)));
        HashWithValue(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)).ShouldNotBe(HashWithValue(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Local)));
        HashWithValue(1.5m).ShouldNotBe(HashWithValue(1.50m));
        HashWithValue(1.5d).ShouldNotBe(HashWithValue(1.5f));
        HashWithValue(Guid.Empty).ShouldNotBe(HashWithValue(new byte[16]));
        HashWithValue(new byte[] { 1, 2 }).ShouldNotBe(HashWithValue(new byte[] { 1, 2, 0 }));
        HashWithValue(DateTimeOffset.UnixEpoch).ShouldNotBe(HashWithValue(DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void Policy_value_is_unwrapped()
    {
        HashWithValue(new PolicyValue("x", SqlParameterType.String)).ShouldBe(HashWithValue("x"));
    }

    [Fact]
    public void Page_offset_and_positional_slots_are_unkeyable()
    {
        var offset = new BoundParameter("@p0", "p0", 0, 20, SqlParameterType.Int32, ParameterOrigin.PageOffset);
        var positional = new BoundParameter("@p0", "p0", 0, null, SqlParameterType.String, ParameterOrigin.ClientPositional);

        WebSqlResultCacheKeyBuilder.TryBuild(Input() with { Parameters = [offset] }, out _).ShouldBeFalse();
        WebSqlResultCacheKeyBuilder.TryBuild(Input() with { Parameters = [positional] }, out _).ShouldBeFalse();
    }

    [Fact]
    public void Missing_client_value_and_missing_sid_are_unkeyable()
    {
        WebSqlResultCacheKeyBuilder.TryBuild(Input() with { ClientValues = new Dictionary<string, object?>() }, out _).ShouldBeFalse();
        WebSqlResultCacheKeyBuilder.TryBuild(Input() with { UserSid = null }, out _).ShouldBeFalse();
        WebSqlResultCacheKeyBuilder.TryBuild(Input() with { PrincipalKey = "" }, out _).ShouldBeFalse();
        WebSqlResultCacheKeyBuilder.TryBuild(null!, out var key).ShouldBeFalse();
        key.ShouldBeNull();
    }

    [Fact]
    public void Client_names_that_differ_only_by_case_are_unkeyable()
    {
        var values = new Dictionary<string, object?> { ["city"] = "a", ["CITY"] = "b" };

        WebSqlResultCacheKeyBuilder.TryBuild(Input() with { ClientValues = values }, out _).ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Key_changes_for_metric_view_min_group_size_SECRC02()
    {
        var hash = Hash(Input() with { MetricViewMinGroupSize = 5, MetricViewHiddenCountAlias = "__cnt" });

        hash.ShouldNotBe(Hash(Input()));
        hash.ShouldNotBe(Hash(Input() with { MetricViewMinGroupSize = 10, MetricViewHiddenCountAlias = "__cnt" }));
        hash.ShouldNotBe(Hash(Input() with { MetricViewMinGroupSize = 5, MetricViewHiddenCountAlias = "__cnt2" }));
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Key_changes_for_output_mask_hash_SECRC02()
    {
        var baseHash = Hash(Input());

        Hash(Input() with { OutputColumnMasks = [true, true] }).ShouldNotBe(baseHash);
        Hash(Input() with { OutputColumnMasks = [false, true, false] }).ShouldNotBe(baseHash);
        Hash(Input() with { OutputColumnMasks = default }).ShouldNotBe(baseHash);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Key_changes_for_purpose_SECRC02()
    {
        var withPurpose = Hash(Input() with { Purpose = "audit" });

        withPurpose.ShouldNotBe(Hash(Input()));
        withPurpose.ShouldNotBe(Hash(Input() with { Purpose = "marketing" }));
        Hash(Input() with { Purpose = "" }).ShouldNotBe(Hash(Input()));
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Key_changes_for_sid_groups_roles_scopes_realm_SECRC02()
    {
        var baseHash = Hash(Input());

        Hash(Input() with { UserSid = "S-1-5-21-2" }).ShouldNotBe(baseHash);
        Hash(Input() with { PrincipalKey = "principal-b" }).ShouldNotBe(baseHash);
        Hash(Input() with { EffectiveDataSourceName = "other" }).ShouldNotBe(baseHash);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Unkeyable_value_returns_false_SECRC03()
    {
        WebSqlResultCacheKeyBuilder.TryBuild(Input() with { Parameters = [Policy(0, new object())] }, out var key).ShouldBeFalse();
        key.ShouldBeNull();
        WebSqlResultCacheKeyBuilder.TryBuild(Input() with { Parameters = [Policy(0, new List<int> { 1 })] }, out _).ShouldBeFalse();
        WebSqlResultCacheKeyBuilder.TryBuild(
            Input() with { Parameters = [Client(1, "city")], ClientValues = new Dictionary<string, object?> { ["city"] = new object() } },
            out _).ShouldBeFalse();
        WebSqlResultCacheKeyBuilder.TryBuild(
            Input() with { ClientValues = new Dictionary<string, object?> { ["city"] = "Berlin", ["other"] = new object() } },
            out _).ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Key_build_exception_is_miss_SECRC03()
    {
        var input = Input() with { ClientValues = new ThrowingDictionary() };

        Should.NotThrow(() => WebSqlResultCacheKeyBuilder.TryBuild(input, out _)).ShouldBeFalse();
        WebSqlResultCacheKeyBuilder.TryBuild(input with { Parameters = null! }, out _).ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Page_window_not_in_key_SECRC02()
    {
        // The key input has no page window member at all: a page is cut from the cached full result, so every window maps to one key.
        typeof(WebSqlResultCacheKeyInput).GetProperties()
            .Select(static p => p.Name)
            .ShouldNotContain(static n => n.Contains("Window", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Offset", StringComparison.OrdinalIgnoreCase)
                || n.Contains("PageSize", StringComparison.OrdinalIgnoreCase)
                || n.Contains("NextToken", StringComparison.OrdinalIgnoreCase));
        Hash(Input()).ShouldBe(Hash(Input()));
    }

    private sealed class ThrowingDictionary : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => throw new InvalidOperationException();

        public IEnumerable<string> Keys => throw new InvalidOperationException();

        public IEnumerable<object?> Values => throw new InvalidOperationException();

        public int Count => throw new InvalidOperationException();

        public bool ContainsKey(string key) => throw new InvalidOperationException();

        public bool TryGetValue(string key, out object? value) => throw new InvalidOperationException();

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new InvalidOperationException();

        IEnumerator IEnumerable.GetEnumerator() => throw new InvalidOperationException();
    }
}
