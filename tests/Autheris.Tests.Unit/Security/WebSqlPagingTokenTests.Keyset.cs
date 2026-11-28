namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Autheris.Application.Sql.Paging;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class WebSqlPagingTokenTests
{
    // ---- Keyset (v2) tokens ----

    private const string KeysetSql = "SELECT id FROM crm.customers ORDER BY id";
    private const string ShapeHash = "0123456789abcdef";
    private const long MaxOffset = 10_000;

    private static string KeysetToken(
        WebSqlPageTokenCodec codec,
        ClaimsPrincipal? user = null,
        string tenant = "tenant-1",
        string dataSource = "crm",
        string transport = "WebSql",
        string sql = KeysetSql,
        long offset = 20_000,
        int size = 100,
        int page = 201,
        IReadOnlyList<object?>? keys = null,
        TimeSpan? lifetime = null) =>
        codec.EncodeKeysetToken(
            sql,
            null,
            user ?? CreateUser(),
            new TenantId(tenant),
            dataSource,
            transport,
            offset,
            size,
            page,
            ShapeHash,
            keys ?? [123L],
            lifetime ?? TimeSpan.FromMinutes(15))!;

    private static WebSqlDecodedPageToken DecodeKeyset(
        WebSqlPageTokenCodec codec,
        string token,
        ClaimsPrincipal? user = null,
        string tenant = "tenant-1",
        string dataSource = "crm",
        string transport = "WebSql",
        string sql = KeysetSql,
        IReadOnlyDictionary<string, object?>? parameters = null) =>
        codec.DecodeToken(token, sql, parameters, user ?? CreateUser(), new TenantId(tenant), dataSource, transport, MaxOffset);

    private static string Forge(WebSqlPageTokenCodec codec, WebSqlPageTokenPayload payload) =>
        codec.Seal(payload, KeysetSql, null, "user-1", "tenant-1", "crm", "WebSql");

    private static long FutureExp => DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_RoundTrip()
    {
        var (codec, _, _) = CreateCodec();
        var guid = Guid.NewGuid();
        string token = KeysetToken(codec, keys: [123L, "abc", guid]);

        var decoded = DecodeKeyset(codec, token);

        decoded.Offset.ShouldBe(20_000);
        decoded.Size.ShouldBe(100);
        decoded.PageNumber.ShouldBe(201);
        decoded.Keyset.ShouldNotBeNull();
        decoded.Keyset!.ShapeHash.ShouldBe(ShapeHash);
        decoded.Keyset.Values.ShouldBe([(object)123L, "abc", guid]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void V1Token_BytesUnchanged_NoKeysetFields()
    {
        var (codec, _, _) = CreateCodec();
        string token = codec.EncodeToken(KeysetSql, null, CreateUser(), new TenantId("tenant-1"), "crm", "WebSql", 100, 100, 2, TimeSpan.FromMinutes(15));
        string json = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(token.Split('.')[0]));
        json.ShouldNotContain("\"h\"");
        json.ShouldNotContain("\"k\"");
        json.ShouldStartWith("{\"v\":1,\"o\":100,\"n\":100,\"p\":2,\"exp\":");
        DecodeKeyset(codec, token).Keyset.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_TamperedKeyValue_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = KeysetToken(codec);
        var parts = token.Split('.');
        var payload = JsonSerializer.Deserialize<WebSqlPageTokenPayload>(System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]))!;
        var tampered = payload with { k = ["i8:1"] };
        string forged = System.Buffers.Text.Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(tampered)) + "." + parts[1];

        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, forged));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_TamperedShapeHash_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = KeysetToken(codec);
        var parts = token.Split('.');
        var payload = JsonSerializer.Deserialize<WebSqlPageTokenPayload>(System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]))!;
        var tampered = payload with { h = "fedcba9876543210" };
        string forged = System.Buffers.Text.Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(tampered)) + "." + parts[1];

        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, forged));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_TamperedVersion_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = KeysetToken(codec);
        var parts = token.Split('.');
        var payload = JsonSerializer.Deserialize<WebSqlPageTokenPayload>(System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]))!;
        var tampered = payload with { v = 1 };
        string forged = System.Buffers.Text.Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(tampered)) + "." + parts[1];

        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, forged));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_OtherUser_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = KeysetToken(codec);
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token, user: CreateUser("user-2")));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_OtherTenant_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = KeysetToken(codec);
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token, tenant: "tenant-2"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_OtherDataSource_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = KeysetToken(codec);
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token, dataSource: "erp"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_OtherTransport_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = KeysetToken(codec);
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token, transport: "Mcp"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_OtherStatementOrParams_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = KeysetToken(codec);
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token, sql: KeysetSql + " DESC"));
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token, parameters: new Dictionary<string, object?> { ["a"] = 1 }));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_Expired_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = KeysetToken(codec, lifetime: TimeSpan.FromSeconds(-10));
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_ErrorsAreIndistinguishable()
    {
        var (codec, _, _) = CreateCodec();
        string token = KeysetToken(codec);
        var messages = new[]
        {
            Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token, user: CreateUser("user-2"))).Message,
            Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, KeysetToken(codec, lifetime: TimeSpan.FromSeconds(-10)))).Message,
            Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, Forge(codec, new WebSqlPageTokenPayload(2, 20_000, 100, 201, FutureExp, ShapeHash, ["zz:1"])))).Message,
            Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, Forge(codec, new WebSqlPageTokenPayload(9, 20_000, 100, 201, FutureExp)))).Message
        };
        messages.Distinct().Count().ShouldBe(1);
        messages[0].ShouldBe(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.PagingTokenInvalid));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_OffsetAtOrBelowMaxOffset_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        // o == MaxOffset (page 101 * 100 -> offset 10_000) and below.
        string atMax = KeysetToken(codec, offset: 10_000, page: 101);
        string below = KeysetToken(codec, offset: 100, page: 2);
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, atMax));
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, below));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_AtOrBelowMaxOffset_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string justAbove = KeysetToken(codec, offset: 10_100, page: 102);
        DecodeKeyset(codec, justAbove).Keyset.ShouldNotBeNull();
        string atMax = KeysetToken(codec, offset: 10_000, page: 101);
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, atMax));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void V1Token_BeyondMaxOffset_StillRejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = codec.EncodeToken(KeysetSql, null, CreateUser(), new TenantId("tenant-1"), "crm", "WebSql", 20_000, 100, 201, TimeSpan.FromMinutes(15));
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void V2Token_WhereV1Expected_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        // a keyset token positioned inside the offset window is not a continuation a keyset page may issue
        string inWindow = KeysetToken(codec, offset: 100, page: 2);
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, inWindow));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void V1Token_WhereKeysetRequired_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string beyond = codec.EncodeToken(KeysetSql, null, CreateUser(), new TenantId("tenant-1"), "crm", "WebSql", 10_100, 100, 102, TimeSpan.FromMinutes(15));
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, beyond));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetPage_PageSizeOverMax_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string oversized = Forge(codec, new WebSqlPageTokenPayload(2, 20_000_200, 100_001, 201, FutureExp, ShapeHash, ["i8:1"]));
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, oversized));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void V1Token_WithKeyField_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = Forge(codec, new WebSqlPageTokenPayload(1, 100, 100, 2, FutureExp, null, ["i8:1"]));
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void V2Token_WithoutKeyField_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string noKeys = Forge(codec, new WebSqlPageTokenPayload(2, 20_000, 100, 201, FutureExp, ShapeHash, null));
        string noHash = Forge(codec, new WebSqlPageTokenPayload(2, 20_000, 100, 201, FutureExp, null, ["i8:1"]));
        string emptyKeys = Forge(codec, new WebSqlPageTokenPayload(2, 20_000, 100, 201, FutureExp, ShapeHash, []));
        string badHash = Forge(codec, new WebSqlPageTokenPayload(2, 20_000, 100, 201, FutureExp, "xyz", ["i8:1"]));
        foreach (var t in new[] { noKeys, noHash, emptyKeys, badHash })
        {
            Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, t));
        }
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void UnknownVersion_Rejected(int version)
    {
        var (codec, _, _) = CreateCodec();
        string token = Forge(codec, new WebSqlPageTokenPayload(version, 100, 100, 2, FutureExp));
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_NullKeyValue_NotIssued()
    {
        var (codec, _, _) = CreateCodec();
        codec.EncodeKeysetToken(KeysetSql, null, CreateUser(), new TenantId("tenant-1"), "crm", "WebSql", 20_000, 100, 201, ShapeHash, [null], TimeSpan.FromMinutes(1)).ShouldBeNull();
        codec.EncodeKeysetToken(KeysetSql, null, CreateUser(), new TenantId("tenant-1"), "crm", "WebSql", 20_000, 100, 201, ShapeHash, [1L, null], TimeSpan.FromMinutes(1)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_UnsupportedType_NotIssued()
    {
        var (codec, _, _) = CreateCodec();
        codec.EncodeKeysetToken(KeysetSql, null, CreateUser(), new TenantId("tenant-1"), "crm", "WebSql", 20_000, 100, 201, ShapeHash, [1.5d], TimeSpan.FromMinutes(1)).ShouldBeNull();
        codec.EncodeKeysetToken(KeysetSql, null, CreateUser(), new TenantId("tenant-1"), "crm", "WebSql", 20_000, 100, 201, ShapeHash, [new byte[] { 1 }], TimeSpan.FromMinutes(1)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_TooLong_NotIssued()
    {
        var (codec, _, _) = CreateCodec();
        // 8 entries of 200 chars exceed 1024 once Base64-encoded.
        object?[] keys = Enumerable.Range(0, 8).Select(_ => (object?)new string('x', 200)).ToArray();
        codec.EncodeKeysetToken(KeysetSql, null, CreateUser(), new TenantId("tenant-1"), "crm", "WebSql", 20_000, 100, 201, ShapeHash, keys, TimeSpan.FromMinutes(1)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_MoreThanEightValues_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        object?[] nine = Enumerable.Range(0, 9).Select(i => (object?)(long)i).ToArray();
        codec.EncodeKeysetToken(KeysetSql, null, CreateUser(), new TenantId("tenant-1"), "crm", "WebSql", 20_000, 100, 201, ShapeHash, nine, TimeSpan.FromMinutes(1)).ShouldBeNull();
        string forged = Forge(codec, new WebSqlPageTokenPayload(2, 20_000, 100, 201, FutureExp, ShapeHash, nine.Select(v => "i8:" + v).ToArray()));
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, forged));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_PageNumberOverLimit_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        // Size over the 100_000 bound and an overflowing page number.
        string bigSize = Forge(codec, new WebSqlPageTokenPayload(2, 100_001L * 1, 100_001, 2, FutureExp, ShapeHash, ["i8:1"]));
        string overflow = Forge(codec, new WebSqlPageTokenPayload(2, 20_000, 100, int.MaxValue, FutureExp, ShapeHash, ["i8:1"]));
        string negativeOffset = Forge(codec, new WebSqlPageTokenPayload(2, -100, 100, 0, FutureExp, ShapeHash, ["i8:1"]));
        foreach (var t in new[] { bigSize, overflow, negativeOffset })
        {
            Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, t));
        }

        codec.EncodeKeysetToken(KeysetSql, null, CreateUser(), new TenantId("tenant-1"), "crm", "WebSql", 100_001, 100_001, 2, ShapeHash, [1L], TimeSpan.FromMinutes(1)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Token_Over1024Chars_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        string token = new string('a', 1025);
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, token));
        WebSqlPageTokenCodec.MaxTokenLength.ShouldBe(1024);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void V1ResumeToken_AtArbitraryOffset_StillDecodes_AndV2RulesUnchanged()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser();
        var tenant = new TenantId("tenant-1");

        // A v1 resume token at an offset that is no multiple of the page size decodes, the number derived from the offset.
        string v1 = codec.EncodeToken(KeysetSql, null, user, tenant, "crm", "WebSql", 103, 100, 2, TimeSpan.FromMinutes(15));
        var decodedV1 = codec.DecodeToken(v1, KeysetSql, null, user, tenant, "crm", "WebSql", MaxOffset);
        decodedV1.Offset.ShouldBe(103);
        decodedV1.PageNumber.ShouldBe(2);

        // A v2 token follows the same derivation: an unaligned offset beyond the window decodes with the derived number ...
        var decodedV2 = DecodeKeyset(codec, KeysetToken(codec, offset: 20_037, size: 100, page: 201));
        decodedV2.Offset.ShouldBe(20_037);
        decodedV2.PageNumber.ShouldBe(201);

        // ... a number that disagrees with the offset is refused at the encoder and at the decoder, and v2 stays beyond the window only.
        codec.EncodeKeysetToken(KeysetSql, null, user, tenant, "crm", "WebSql", 20_037, 100, 202, ShapeHash, [1L], TimeSpan.FromMinutes(15)).ShouldBeNull();
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, Forge(codec, new WebSqlPageTokenPayload(2, 20_037, 100, 202, FutureExp, ShapeHash, ["l:1"]))));
        codec.EncodeKeysetToken(KeysetSql, null, user, tenant, "crm", "WebSql", 9_999, 100, 100, ShapeHash, [1L], TimeSpan.FromMinutes(15))
            .ShouldNotBeNull();
        Should.Throw<SqlPagingTokenInvalidException>(() => DecodeKeyset(codec, KeysetToken(codec, offset: 9_999, size: 100, page: 100)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeysetToken_LongMultiColumnKey_Over512Chars_DecodesBeyondTheOffsetWindow()
    {
        var (codec, _, _) = CreateCodec();
        object?[] keys = [new string('a', 120), new string('b', 120), new string('c', 120), new string('d', 120)];

        string token = KeysetToken(codec, offset: 20_037, size: 100, page: 201, keys: keys);

        token.Length.ShouldBeInRange(513, WebSqlPageTokenCodec.MaxTokenLength);
        DecodeKeyset(codec, token).Keyset!.Values.Length.ShouldBe(4);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DecodeToken_LengthGate_1024Reaches_1025IsRejected()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser();
        var tenant = new TenantId("tenant-1");
        string valid = codec.EncodeToken(KeysetSql, null, user, tenant, "crm", "WebSql", 0, 100, 1, TimeSpan.FromMinutes(15));

        // Padding a valid token to exactly 1024 characters breaks the encoding or MAC, so it fails there; one more character is refused by the gate.
        string at = valid + new string('A', WebSqlPageTokenCodec.MaxTokenLength - valid.Length);
        string over = at + "A";
        at.Length.ShouldBe(WebSqlPageTokenCodec.MaxTokenLength);
        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(at, KeysetSql, null, user, tenant, "crm", "WebSql", MaxOffset));
        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(over, KeysetSql, null, user, tenant, "crm", "WebSql", MaxOffset));
        codec.DecodeToken(valid, KeysetSql, null, user, tenant, "crm", "WebSql", MaxOffset).Offset.ShouldBe(0);
    }
}
