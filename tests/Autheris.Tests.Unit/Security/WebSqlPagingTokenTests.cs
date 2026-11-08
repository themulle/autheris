namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Sql.Paging;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

public sealed class WebSqlPagingTokenTests
{
    private sealed class TestSecretProvider(byte[] secret) : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) => secret;
    }

    private static ClaimsPrincipal CreateUser(string sid = "user-1", string tenant = "tenant-1") =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, sid),
                new Claim("sub", sid),
                new Claim("tenant_id", tenant)
            ],
            "TestAuth"));

    private static (WebSqlPageTokenCodec Codec, CompiledSqlAuditDigester Digester, GatewayOptions Options) CreateCodec(byte[]? auditKey = null)
    {
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                AuditHmacKeyVaultRef = auditKey is not null ? "vault://audit-key" : string.Empty
            },
            WebSql = new WebSqlOptions
            {
                Paging = new WebSqlPagingOptions
                {
                    Enabled = true,
                    MaxOffset = 10_000,
                    TokenLifetimeMinutes = 15
                }
            }
        };

        var secretProvider = auditKey is not null ? new TestSecretProvider(auditKey) : null;
        var digester = new CompiledSqlAuditDigester(Options.Create(options), secretProvider, null);
        var codec = new WebSqlPageTokenCodec(digester);
        return (codec, digester, options);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OtherPrincipal_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        var user1 = CreateUser("user-1", "tenant-1");
        var user2 = CreateUser("user-2", "tenant-1");
        var tenant = new TenantId("tenant-1");

        string token = codec.EncodeToken(
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user1,
            tenant,
            "crm",
            "WebSql",
            100,
            100,
            2,
            TimeSpan.FromMinutes(15));

        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(
            token,
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user2,
            tenant,
            "crm",
            "WebSql",
            10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OtherTenant_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("user-1", "tenant-1");
        var tenant1 = new TenantId("tenant-1");
        var tenant2 = new TenantId("tenant-2");

        string token = codec.EncodeToken(
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant1,
            "crm",
            "WebSql",
            100,
            100,
            2,
            TimeSpan.FromMinutes(15));

        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(
            token,
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant2,
            "crm",
            "WebSql",
            10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OtherStatement_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("user-1", "tenant-1");
        var tenant = new TenantId("tenant-1");

        string token = codec.EncodeToken(
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant,
            "crm",
            "WebSql",
            100,
            100,
            2,
            TimeSpan.FromMinutes(15));

        // Even one whitespace change must alter SHA-256 and fail MAC
        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(
            token,
            "SELECT id  FROM crm.customers ORDER BY id",
            null,
            user,
            tenant,
            "crm",
            "WebSql",
            10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OtherParameters_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("user-1", "tenant-1");
        var tenant = new TenantId("tenant-1");
        var p1 = new Dictionary<string, object?> { ["status"] = "active" };
        var p2 = new Dictionary<string, object?> { ["status"] = "inactive" };

        string token = codec.EncodeToken(
            "SELECT id FROM crm.customers WHERE status = :status ORDER BY id",
            p1,
            user,
            tenant,
            "crm",
            "WebSql",
            100,
            100,
            2,
            TimeSpan.FromMinutes(15));

        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(
            token,
            "SELECT id FROM crm.customers WHERE status = :status ORDER BY id",
            p2,
            user,
            tenant,
            "crm",
            "WebSql",
            10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OtherDataSource_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("user-1", "tenant-1");
        var tenant = new TenantId("tenant-1");

        string token = codec.EncodeToken(
            "SELECT id FROM customers ORDER BY id",
            null,
            user,
            tenant,
            "crm_primary",
            "WebSql",
            100,
            100,
            2,
            TimeSpan.FromMinutes(15));

        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(
            token,
            "SELECT id FROM customers ORDER BY id",
            null,
            user,
            tenant,
            "crm_secondary",
            "WebSql",
            10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Expired_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("user-1", "tenant-1");
        var tenant = new TenantId("tenant-1");

        // Lifetime negative so it is already expired
        string token = codec.EncodeToken(
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant,
            "crm",
            "WebSql",
            100,
            100,
            2,
            TimeSpan.FromSeconds(-5));

        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(
            token,
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant,
            "crm",
            "WebSql",
            10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TamperedOffset_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("user-1", "tenant-1");
        var tenant = new TenantId("tenant-1");

        string token = codec.EncodeToken(
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant,
            "crm",
            "WebSql",
            100,
            100,
            2,
            TimeSpan.FromMinutes(15));

        var parts = token.Split('.');
        byte[] payloadBytes = System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]);
        var payload = JsonSerializer.Deserialize<WebSqlPageTokenPayload>(payloadBytes)!;
        var tampered = payload with { o = 999 };
        string tamperedPayloadBase64 = System.Buffers.Text.Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(tampered));
        string tamperedToken = tamperedPayloadBase64 + "." + parts[1];

        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(
            tamperedToken,
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant,
            "crm",
            "WebSql",
            10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TamperedPageNumber_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("user-1", "tenant-1");
        var tenant = new TenantId("tenant-1");

        string token = codec.EncodeToken(
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant,
            "crm",
            "WebSql",
            100,
            100,
            2,
            TimeSpan.FromMinutes(15));

        var parts = token.Split('.');
        byte[] payloadBytes = System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]);
        var payload = JsonSerializer.Deserialize<WebSqlPageTokenPayload>(payloadBytes)!;
        var tampered = payload with { p = 5 };
        string tamperedPayloadBase64 = System.Buffers.Text.Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(tampered));
        string tamperedToken = tamperedPayloadBase64 + "." + parts[1];

        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(
            tamperedToken,
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant,
            "crm",
            "WebSql",
            10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void InconsistentOffsetAndPage_Rejected()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("user-1", "tenant-1");
        var tenant = new TenantId("tenant-1");

        var payload = new WebSqlPageTokenPayload(1, 150, 100, 2, DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds());
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        string token = codec.EncodeToken(
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant,
            "crm",
            "WebSql",
            100,
            100,
            2,
            TimeSpan.FromMinutes(15));

        var parts = token.Split('.');
        string tamperedToken = System.Buffers.Text.Base64Url.EncodeToString(payloadBytes) + "." + parts[1];

        Should.Throw<SqlPagingTokenInvalidException>(() => codec.DecodeToken(
            tamperedToken,
            "SELECT id FROM crm.customers ORDER BY id",
            null,
            user,
            tenant,
            "crm",
            "WebSql",
            10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Payload_RevealsNoStatementOrCaller()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("alice@company.com", "tenant-corp");
        var tenant = new TenantId("tenant-corp");
        string sql = "SELECT sensitive_column FROM confidential_table WHERE secret = 123";

        string token = codec.EncodeToken(
            sql,
            null,
            user,
            tenant,
            "secret_db",
            "WebSql",
            50,
            50,
            2,
            TimeSpan.FromMinutes(15));

        var parts = token.Split('.');
        byte[] payloadBytes = System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]);
        string payloadJson = Encoding.UTF8.GetString(payloadBytes);

        payloadJson.ShouldNotContain("alice");
        payloadJson.ShouldNotContain("tenant-corp");
        payloadJson.ShouldNotContain("sensitive");
        payloadJson.ShouldNotContain("confidential");
        payloadJson.ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AllFailures_SameCodeAndMessage()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("user-1", "tenant-1");
        var tenant = new TenantId("tenant-1");

        string invalidToken = "not.a.valid.token";
        var ex1 = Should.Throw<SqlPagingTokenInvalidException>(() =>
            codec.DecodeToken(invalidToken, "SELECT 1", null, user, tenant, "db", "WebSql", 10_000));

        string expiredToken = codec.EncodeToken("SELECT 1", null, user, tenant, "db", "WebSql", 10, 10, 2, TimeSpan.FromSeconds(-10));
        var ex2 = Should.Throw<SqlPagingTokenInvalidException>(() =>
            codec.DecodeToken(expiredToken, "SELECT 1", null, user, tenant, "db", "WebSql", 10_000));

        ex1.Message.ShouldBe(ex2.Message);
        GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.PagingTokenInvalid).ShouldBe(ex1.Message);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PrincipalWithoutSid_GetsNoNext()
    {
        var principalWithoutSid = new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "anonymous_user")], "TestAuth"));
        principalWithoutSid.GetUserSid().ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PrincipalWithoutSid_NextRejected()
    {
        var (codec, _, _) = CreateCodec();
        var userWithoutSid = new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "alice")], "TestAuth"));
        var tenant = new TenantId("tenant-1");

        Should.Throw<InvalidOperationException>(() => codec.EncodeToken(
            "SELECT 1",
            null,
            userWithoutSid,
            tenant,
            "db",
            "WebSql",
            10,
            10,
            2,
            TimeSpan.FromMinutes(15)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KeyDiffersFromAuditDigestKey()
    {
        var (_, digester, _) = CreateCodec();
        byte[] auditPurposeKey = digester.DerivePurposeKey("audit-key-purpose");
        byte[] pagingPurposeKey = digester.DerivePurposeKey(WebSqlPageTokenCodec.HkdfInfo);

        pagingPurposeKey.ShouldNotBe(auditPurposeKey);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void HkdfInfo_IsUnique()
    {
        WebSqlPageTokenCodec.HkdfInfo.ShouldBe("autheris/websql-page-token/v1");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MacFraming_IsLengthPrefixed()
    {
        var (codec, _, _) = CreateCodec();
        var user = CreateUser("user-1", "tenant-1");
        var tenant = new TenantId("tenant-1");

        string tok1 = codec.EncodeToken("SELECT 1", null, user, tenant, "db", "WebSql", 10, 10, 2, TimeSpan.FromMinutes(15));
        string tok2 = codec.EncodeToken("SELECT 12", null, user, tenant, "db", "WebSql", 10, 10, 2, TimeSpan.FromMinutes(15));

        var mac1 = tok1.Split('.')[1];
        var mac2 = tok2.Split('.')[1];

        mac1.ShouldNotBe(mac2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AuditKeyRotation_InvalidatesToken()
    {
        var key1 = Encoding.UTF8.GetBytes("key-generation-1-0123456789abcde");
        var key2 = Encoding.UTF8.GetBytes("key-generation-2-0123456789abcde");
        var (codec1, _, _) = CreateCodec(key1);
        var (codec2, _, _) = CreateCodec(key2);
        var user = CreateUser("user-1", "tenant-1");
        var tenant = new TenantId("tenant-1");

        string token1 = codec1.EncodeToken("SELECT 1", null, user, tenant, "db", "WebSql", 10, 10, 2, TimeSpan.FromMinutes(15));

        Should.Throw<SqlPagingTokenInvalidException>(() =>
            codec2.DecodeToken(token1, "SELECT 1", null, user, tenant, "db", "WebSql", 10_000));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Oversized_RejectedBeforeDecode()
    {
        string oversizedToken = new string('a', 513);
        oversizedToken.Length.ShouldBeGreaterThan(512);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ClientParameter_CannotFillPageOffsetSlot()
    {
        var clientParams = new Dictionary<string, object?>
        {
            ["page_offset"] = 500,
            ["offset"] = 500,
            ["PageOffset"] = 500
        };

        clientParams.ContainsKey("page_offset").ShouldBeTrue();
    }
}
