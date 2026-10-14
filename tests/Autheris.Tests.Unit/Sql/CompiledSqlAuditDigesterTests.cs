namespace Autheris.Tests.Unit.Sql;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>M-A3 / L-A1: the keyed statement hash of the audit record and the key handling of the audit digester (SEC-ADG-23).</summary>
public sealed class CompiledSqlAuditDigesterTests
{
    private static readonly byte[] AuditKey = Encoding.UTF8.GetBytes("audit-hmac-key-0123456789abcdef-0123456789");
    private static readonly byte[] Master = Encoding.UTF8.GetBytes("master-hmac-secret-0123456789abcdef-012345");

    private static IOptions<GatewayOptions> Options(string? auditRef = "AUDIT", string? masterRef = null) => Microsoft.Extensions.Options.Options.Create(new GatewayOptions
    {
        GovernanceDb = new GovernanceDbOptions { AuditHmacKeyVaultRef = auditRef },
        DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = masterRef ?? string.Empty }
    });

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static IKeyVaultSecretProvider Secrets(byte[]? audit = null, byte[]? master = null)
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        if (audit is not null)
        {
            secrets.GetSecretBytes("AUDIT").Returns(audit);
        }

        if (master is not null)
        {
            secrets.GetSecretBytes("MASTER").Returns(master);
        }

        return secrets;
    }

    private static CompiledSqlAuditDigester Digester(byte[]? audit = null, string env = "Production") =>
        new(Options(), Secrets(audit ?? AuditKey), Env(env));

    private static string ExpectedHash(byte[] auditKey, string redacted)
    {
        var sub = HKDF.DeriveKey(HashAlgorithmName.SHA256, auditKey, 32, info: "Autheris:SqlOriginalHash:v1"u8.ToArray());
        return Convert.ToHexString(HMACSHA256.HashData(sub, Encoding.UTF8.GetBytes(redacted)));
    }

    // ---- M-A3 -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void OriginalSqlHash_IsAnHmacOverTheRedactedText_WithItsOwnSubKey()
    {
        const string Sql = "SELECT id FROM orders WHERE email = 'alice@example.com' AND id = 4711";
        var (redacted, hash) = Digester().Anonymize(Sql);

        hash.ShouldBe(ExpectedHash(AuditKey, redacted));
        // neither the unkeyed hash of the raw text (brute-forceable over values) nor the unkeyed hash of the redacted text
        hash.ShouldNotBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql))));
        hash.ShouldNotBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(redacted))));
    }

    [Fact]
    public void OriginalSqlHash_DoesNotDependOnTheLiteralValues()
    {
        var digester = Digester();

        var a = digester.Anonymize("SELECT id FROM orders WHERE email = 'alice@example.com' AND id = 1");
        var b = digester.Anonymize("SELECT id FROM orders WHERE email = 'bob@example.com' AND id = 99999");
        var c = digester.Anonymize("SELECT id FROM orders WHERE email = 'alice@example.com' AND region = 1");

        b.OriginalSqlHash.ShouldBe(a.OriginalSqlHash);   // no value is derivable from, or even distinguishable in, the hash
        c.OriginalSqlHash.ShouldNotBe(a.OriginalSqlHash);
        a.RedactedSql.ShouldNotContain("alice");
        a.OriginalSqlHash.ShouldNotContain("alice");
    }

    // ---- L-3: every literal token kind is redacted --------------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT id FROM orders WHERE ratio > 1.5E3", "SELECT id FROM orders WHERE ratio > 9.25E-7", "1.5E3", "9.25E-7")]
    [InlineData("SELECT id FROM orders WHERE ratio > .5E2", "SELECT id FROM orders WHERE ratio > 3E10", ".5E2", "3E10")]
    [InlineData("SELECT id FROM orders WHERE raw = X'DEADBEEF'", "SELECT id FROM orders WHERE raw = X'00112233'", "DEADBEEF", "00112233")]
    public void OriginalSqlHash_DoesNotDependOnDoubleAndBinaryLiterals(string first, string second, string valueOfFirst, string valueOfSecond)
    {
        var digester = Digester();

        var a = digester.Anonymize(first);
        var b = digester.Anonymize(second);

        a.RedactedSql.ShouldNotContain(valueOfFirst);
        b.RedactedSql.ShouldNotContain(valueOfSecond);
        b.OriginalSqlHash.ShouldBe(a.OriginalSqlHash);
    }

    [Theory]
    [InlineData("SELECT id FROM orders WHERE email = 'alice@example.com")]    // unterminated string: the lexer reports an error
    [InlineData("SELECT id FROM orders WHERE note = 'secret-value' AND x = `")]
    public void Anonymize_LexerError_RedactsTheWholeStatement(string sql)
    {
        var (redacted, _) = Digester().Anonymize(sql);

        redacted.ShouldNotContain("alice");
        redacted.ShouldNotContain("secret-value");
        redacted.ShouldNotContain("orders");
    }

    [Fact]
    public void OriginalSqlHash_IsDeterministicForAKey_AndDiffersBetweenKeys()
    {
        const string Sql = "SELECT id FROM orders WHERE id = 1";
        var other = Encoding.UTF8.GetBytes("another-audit-key-0123456789abcdef-0123456789");

        Digester().Anonymize(Sql).OriginalSqlHash.ShouldBe(Digester().Anonymize(Sql).OriginalSqlHash);
        Digester(other).Anonymize(Sql).OriginalSqlHash.ShouldNotBe(Digester().Anonymize(Sql).OriginalSqlHash);
    }

    [Fact]
    public void OriginalSqlHash_UsesAnotherKeyThanTheCompiledDigest()
    {
        var digester = Digester();

        CompiledSqlAuditDigester.DeriveKey(AuditKey).ShouldNotBe(HKDF.DeriveKey(HashAlgorithmName.SHA256, AuditKey, 32, info: "Autheris:SqlOriginalHash:v1"u8.ToArray()));
        digester.Anonymize("SELECT 1").OriginalSqlHash.Length.ShouldBe(64);
    }

    [Fact]
    public void Anonymize_EmptyStatement_StillKeyedAndRedactedEmpty()
    {
        var (redacted, hash) = Digester().Anonymize("  ");

        redacted.ShouldBeEmpty();
        hash.ShouldBe(ExpectedHash(AuditKey, string.Empty));
    }

    // ---- L-A1 -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Key_PrefersTheAuditKey_OverTheMasterSecret()
    {
        var secrets = Secrets(AuditKey, Master);
        var digester = new CompiledSqlAuditDigester(Options(masterRef: "MASTER"), secrets, Env("Production"));

        digester.Anonymize("SELECT 1").OriginalSqlHash.ShouldBe(ExpectedHash(AuditKey, digester.Anonymize("SELECT 1").RedactedSql));
        secrets.DidNotReceive().GetSecretBytes("MASTER");
    }

    [Fact]
    public void Key_FallsBackToTheAuditChainKeyOfTheMasterSecret()
    {
        var chainKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, Master, 32, info: "Autheris:AuditChain:v1"u8.ToArray());
        var digester = new CompiledSqlAuditDigester(Options(auditRef: null, masterRef: "MASTER"), Secrets(master: Master), Env("Production"));

        digester.Anonymize("SELECT 1").OriginalSqlHash.ShouldBe(ExpectedHash(chainKey, "SELECT @p_redacted"));
    }

    [Fact]
    public void Key_ShortKey_OutsideDevelopment_IsRefused_InDevelopmentAccepted()
    {
        var shortKey = Encoding.UTF8.GetBytes("short");

        Should.Throw<InvalidOperationException>(() => Digester(shortKey, "Production").Anonymize("SELECT 1"));
        Digester(shortKey, "Development").Anonymize("SELECT 1").OriginalSqlHash.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Key_NothingConfigured_OutsideDevelopmentAndTest_FailsClosed()
    {
        var digester = new CompiledSqlAuditDigester(Options(auditRef: null), Secrets(), Env("Production"));

        Should.Throw<InvalidOperationException>(() => digester.Anonymize("SELECT 1"));
        Should.Throw<InvalidOperationException>(() => digester.Compute(new CompiledSql("SELECT 1", ImmutableArray<BoundParameter>.Empty, TargetSqlDialect.Sqlite, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "v")));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public void Key_NothingConfigured_DevelopmentAndTest_UsesAStableProcessKey(string env)
    {
        var digester = new CompiledSqlAuditDigester(Options(auditRef: null), Secrets(), Env(env));

        digester.Anonymize("SELECT 1").OriginalSqlHash.ShouldBe(digester.Anonymize("SELECT 1").OriginalSqlHash);
    }

    [Fact]
    public void Key_VaultFailure_OutsideDevelopment_FailsClosed_NoSilentEphemeralKey()
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes("AUDIT").Returns(_ => throw new InvalidOperationException("vault down"));
        var digester = new CompiledSqlAuditDigester(Options(), secrets, Env("Production"));

        Should.Throw<InvalidOperationException>(() => digester.Anonymize("SELECT 1"));
    }

    [Fact]
    public void Key_IsReadOnce_NotPerRequest()
    {
        var secrets = Secrets(AuditKey);
        var digester = new CompiledSqlAuditDigester(Options(), secrets, Env("Production"));

        for (var i = 0; i < 5; i++)
        {
            digester.Anonymize("SELECT 1");
        }

        secrets.Received(1).GetSecretBytes("AUDIT");
    }
}
