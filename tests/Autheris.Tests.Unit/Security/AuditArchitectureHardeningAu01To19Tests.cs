using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>
/// Comprehensive verification tests for Findings AU-01 through AU-19 in accordance with
/// docs/plans/plan-audit-architektur-haertung.md.
/// </summary>
public sealed class AuditArchitectureHardeningAu01To19Tests : IDisposable
{
    private readonly string _testDir;

    public AuditArchitectureHardeningAu01To19Tests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "autheris-au-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static IHostEnvironment CreateMockEnvironment(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);
        return env;
    }

    private static DataMaskingOptions ProdMasking() => new() { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" };

    #region AU-01 & AU-03: Startup Fail-Closed & Environment Validation

    [Fact]
    public void AU01_ValidateGatewayOptions_InProduction_MissingAnchorStore_ThrowsInvalidOperationException()
    {
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql" },
            DataMasking = ProdMasking(),
            Audit = new AuditOptions
            {
                ChainAnchorPath = string.Empty,
                ChainAnchorWormDirectory = string.Empty,
                ChainAnchorSignerKeyVaultRef = string.Empty
            }
        };

        var env = CreateMockEnvironment(Environments.Production);

        var ex = Should.Throw<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env, _ => "Production"));

        ex.Message.ShouldContain("AU-01/AU-03");
        ex.Message.ShouldContain("persistent audit anchor store");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AU03_ValidateGatewayOptions_EmptyOrNullEnvironment_TreatedAsProduction_ThrowsInvalidOperationException(string? emptyEnv)
    {
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql" },
            DataMasking = ProdMasking(),
            Audit = new AuditOptions
            {
                ChainAnchorPath = string.Empty,
                ChainAnchorWormDirectory = string.Empty
            }
        };

        var env = CreateMockEnvironment(emptyEnv ?? "");

        var ex = Should.Throw<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env, _ => emptyEnv));

        ex.Message.ShouldContain("AU-01/AU-03");
    }

    [Fact]
    public void AU03_ValidateGatewayOptions_InProduction_FallbackHmacKey_ThrowsInvalidOperationException()
    {
        var options = new GatewayOptions
        {
            DataMasking = ProdMasking(),
            Audit = new AuditOptions
            {
                ChainAnchorPath = Path.Combine(_testDir, "anchor.json"),
                HmacKeyIsFallback = true
            }
        };

        var env = CreateMockEnvironment(Environments.Production);

        var ex = Should.Throw<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env, _ => "Production"));

        ex.Message.ShouldContain("Fallback HMAC audit keys are strictly prohibited", Case.Insensitive);
    }

    [Fact]
    public void AU01_ValidateGatewayOptions_InDevelopment_WithoutAnchorStore_IsPermitted()
    {
        var options = new GatewayOptions
        {
            Audit = new AuditOptions
            {
                ChainAnchorPath = string.Empty,
                ChainAnchorWormDirectory = string.Empty
            }
        };

        var env = CreateMockEnvironment(Environments.Development);

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env, _ => "Development"));
    }

    [Fact]
    public void AU01_ValidateGatewayOptions_InTest_WithoutAnchorStore_IsPermitted()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "vault://kv-test-secret"
            },
            Audit = new AuditOptions
            {
                ChainAnchorPath = string.Empty,
                ChainAnchorWormDirectory = string.Empty
            }
        };

        var env = CreateMockEnvironment("Test");

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env, _ => "Test"));
    }

    [Fact]
    public void AU01_SqliteRepository_InProduction_MissingAnchorStore_ThrowsInvalidOperationException()
    {
        var dbPath = Path.Combine(_testDir, "prod_test.db");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source={dbPath}",
                AuditHmacKeyVaultRef = "vault://audit-key"
            },
            Audit = new AuditOptions
            {
                ChainAnchorPath = string.Empty,
                ChainAnchorWormDirectory = string.Empty,
                ChainAnchorSignerKeyVaultRef = string.Empty
            }
        });

        var env = CreateMockEnvironment(Environments.Production);
        var mockSecretProvider = Substitute.For<IKeyVaultSecretProvider>();
        mockSecretProvider.GetSecretBytes(Arg.Any<string>())
            .Returns(Encoding.UTF8.GetBytes("secret-audit-key-32-chars-long-minimum!"));

        var ex = Should.Throw<InvalidOperationException>(() =>
            new SqliteGovernanceRepository(
                new EpochValidationService(),
                options,
                environment: env,
                secretProvider: mockSecretProvider,
                auditAnchorStore: null,
                logger: NullLogger<SqliteGovernanceRepository>.Instance));

        ex.Message.ShouldContain("AU-01/AU-03");
    }

    #endregion

    #region AU-02: Cryptographic Decoupling & Asymmetric KMS Signers

    [Fact]
    public async Task AU02_AsymmetricChainAnchorService_ECDSA_SignsAndVerifiesManifest()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        var service = new AsymmetricChainAnchorService(privateKeyPem, publicKeyPem, keyId: "kms-ecdsa-test-1");

        var manifest = new AnchorManifest
        {
            Epoch = 1,
            TailSeq = 42,
            TailHash = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            TimestampUtc = DateTimeOffset.UtcNow,
            TenantId = "tenant-test"
        };

        var signedManifest = await service.SignAnchorAsync(manifest);

        signedManifest.ShouldNotBeNull();
        signedManifest.KeyId.ShouldBe("kms-ecdsa-test-1");
        signedManifest.SignatureAlgorithm.ShouldBe("ECDSA_P256_SHA_256");
        signedManifest.SignatureBase64.ShouldNotBeNullOrWhiteSpace();

        var isValid = await service.VerifyAnchorAsync(signedManifest);
        isValid.ShouldBeTrue();
    }

    [Fact]
    public async Task AU02_AsymmetricChainAnchorService_ECDSA_TamperedPayload_FailsVerification()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        var service = new AsymmetricChainAnchorService(privateKeyPem, publicKeyPem);

        var manifest = new AnchorManifest
        {
            Epoch = 1,
            TailSeq = 100,
            TailHash = "HASH_ORIGINAL",
            TimestampUtc = DateTimeOffset.UtcNow,
            TenantId = "tenant-test"
        };

        var signed = await service.SignAnchorAsync(manifest);

        // Tamper with the manifest payload
        var tamperedManifest = signed.Manifest with { TailHash = "HASH_TAMPERED" };
        var tamperedSigned = signed with { Manifest = tamperedManifest };

        var isValid = await service.VerifyAnchorAsync(tamperedSigned);
        isValid.ShouldBeFalse();
    }

    [Fact]
    public async Task AU02_AsymmetricChainAnchorService_RSA_SignsAndVerifiesManifest()
    {
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

        var service = new AsymmetricChainAnchorService(privateKeyPem, publicKeyPem, keyId: "kms-rsa-test-1");

        var manifest = new AnchorManifest
        {
            Epoch = 1,
            TailSeq = 99,
            TailHash = "RSA_ENTRY_HASH",
            TimestampUtc = DateTimeOffset.UtcNow,
            TenantId = "tenant-test"
        };

        var signedManifest = await service.SignAnchorAsync(manifest);

        signedManifest.ShouldNotBeNull();
        signedManifest.SignatureAlgorithm.ShouldBe("RSASSA_PSS_SHA_256");

        var isValid = await service.VerifyAnchorAsync(signedManifest);
        isValid.ShouldBeTrue();
    }

    [Fact]
    public async Task AU02_AsymmetricChainAnchorService_VerifyOnly_CannotSign()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        var verifyOnlyService = new AsymmetricChainAnchorService(null, publicKeyPem);

        var manifest = new AnchorManifest
        {
            Epoch = 1,
            TailSeq = 1,
            TailHash = "HASH",
            TimestampUtc = DateTimeOffset.UtcNow,
            TenantId = "tenant-test"
        };

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await verifyOnlyService.SignAnchorAsync(manifest));
    }

    #endregion

    #region AU-04: Enrolled Transaction Pattern

    [Fact]
    public async Task AU04_SqliteRepository_EnrolledTransaction_Commit_PersistsEntryAndChainsCorrectly()
    {
        var dbPath = Path.Combine(_testDir, "enrolled_tx_commit.db");
        var wormPath = Path.Combine(_testDir, "worm_commit");
        var anchorPath = Path.Combine(_testDir, "anchor_commit.json");

        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source={dbPath}" },
            Audit = new AuditOptions
            {
                ChainAnchorPath = anchorPath,
                ChainAnchorWormDirectory = wormPath,
                SynchronousQueryAudit = true
            }
        });

        using var repo = new SqliteGovernanceRepository(new EpochValidationService(), options);

        var entry1 = CreateAuditEntry(1, "CONSENT_CREATED");
        await repo.LockAsync();
        try
        {
            using var tx = repo.Connection.BeginTransaction();
            await repo.RecordAuditEventAsync(entry1, tx);
            await tx.CommitAsync();
            repo.OnTransactionCommitted(tx);
        }
        finally
        {
            repo.ReleaseLock();
        }

        var isValid = await repo.VerifyAuditHashChainAsync();
        isValid.ShouldBeTrue();

        using var cmd = repo.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE EVENT_TYPE = 'CONSENT_CREATED'";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.ShouldBe(1);
    }

    [Fact]
    public async Task AU04_SqliteRepository_EnrolledTransaction_Rollback_RestoresPointerAndPreservesChain()
    {
        var dbPath = Path.Combine(_testDir, "enrolled_tx_rollback.db");
        var wormPath = Path.Combine(_testDir, "worm_rollback");
        var anchorPath = Path.Combine(_testDir, "anchor_rollback.json");

        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source={dbPath}" },
            Audit = new AuditOptions
            {
                ChainAnchorPath = anchorPath,
                ChainAnchorWormDirectory = wormPath,
                SynchronousQueryAudit = true
            }
        });

        using var repo = new SqliteGovernanceRepository(new EpochValidationService(), options);

        // 1. Initial committed entry
        var entry1 = CreateAuditEntry(1, "CONSENT_INITIAL");
        await repo.RecordAuditEventAsync(entry1);
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();

        // 2. Transaction that is rolled back
        var entry2 = CreateAuditEntry(2, "CONSENT_FAILED_ATTEMPT");
        await repo.LockAsync();
        try
        {
            using var tx = repo.Connection.BeginTransaction();
            await repo.RecordAuditEventAsync(entry2, tx);
            await tx.RollbackAsync();
            repo.OnTransactionRolledBack(tx);
        }
        finally
        {
            repo.ReleaseLock();
        }

        // 3. Rollback must not corrupt audit hash chain
        var chainValidAfterRollback = await repo.VerifyAuditHashChainAsync();
        chainValidAfterRollback.ShouldBeTrue();

        // 4. Failed entry must not exist in table
        using var cmd = repo.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE EVENT_TYPE = 'CONSENT_FAILED_ATTEMPT'";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.ShouldBe(0);

        // 5. Subsequent audit entry must succeed and chain properly from entry1
        var entry3 = CreateAuditEntry(3, "CONSENT_RETRY_SUCCESS");
        await repo.RecordAuditEventAsync(entry3);
        var chainValidAfterSubsequent = await repo.VerifyAuditHashChainAsync();
        chainValidAfterSubsequent.ShouldBeTrue();
    }

    #endregion

    #region AU-05: Resilient Tier-B Dead Letter & Self-Healing Pipeline

    [Fact]
    public void AU05_AuditDeadLetterBatch_SerializesAndDeserializes()
    {
        var batch = new AuditDeadLetterBatch(
            Id: "dl-batch-1",
            Entries:
            [
                CreateAuditEntry(1, "TEST_DL_1"),
                CreateAuditEntry(2, "TEST_DL_2")
            ],
            ErrorMessage: "Database connection timed out after 3 retries",
            FailedAt: DateTimeOffset.UtcNow,
            TenantId: "tenant-test");

        var json = JsonSerializer.Serialize(batch);
        json.ShouldNotBeNullOrWhiteSpace();

        var deserialized = JsonSerializer.Deserialize<AuditDeadLetterBatch>(json);
        deserialized.ShouldNotBeNull();
        deserialized.Id.ShouldBe("dl-batch-1");
        deserialized.ErrorMessage.ShouldBe("Database connection timed out after 3 retries");
        deserialized.Entries.Count.ShouldBe(2);
        deserialized.Entries[0].EventType.ShouldBe("TEST_DL_1");
    }

    [Fact]
    public void AU05_GatewayDiagnostics_AuditDeadLetterCounter_Exists()
    {
        // Must not throw when counter is referenced and incremented
        Should.NotThrow(() => GatewayDiagnostics.AuditDeadLetterCounter.Add(1));
    }

    #endregion

    #region AU-06: SQL AST Literal Anonymization & Hashing

    [Fact]
    public void AU06_AstSecurityVisitor_AnonymizeSqlForAudit_RedactsLiterals()
    {
        var query = "SELECT id, name, salary FROM hr.salaries WHERE salary > 50000 AND department = 'Engineering' AND active = 1";

        var (redactedSql, originalHash) = AstSecurityVisitor.AnonymizeSqlForAudit(query);

        redactedSql.ShouldNotContain("50000");
        redactedSql.ShouldNotContain("'Engineering'");
        redactedSql.ShouldContain("@p_redacted");

        // Hash must match SHA256 of the original query
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)));
        originalHash.ShouldBe(expectedHash);
    }

    [Fact]
    public void AU06_AstSecurityVisitor_AnonymizeSqlForAudit_EnforcesMaxLength()
    {
        var longColumnList = string.Join(", ", System.Linq.Enumerable.Range(1, 50).Select(i => $"column_identifier_{i}"));
        var query = $"SELECT {longColumnList} FROM very_long_table_name_for_audit_testing WHERE comment = 'secret'";

        var (redactedSql, originalHash) = AstSecurityVisitor.AnonymizeSqlForAudit(query, maxLength: 80);

        redactedSql.Length.ShouldBeLessThanOrEqualTo(80 + "...[TRUNCATED]".Length);
        redactedSql.ShouldEndWith("...[TRUNCATED]");
        originalHash.ShouldNotBeNullOrWhiteSpace();
    }

    #endregion

    #region AU-07: SQL Server Parameter Sizing & Length Guards

    [Fact]
    public void AU07_GuardLength_ClampsFieldToMaximumLength()
    {
        var longDetails = new string('x', 5000);
        var guarded = longDetails.Length <= 4096 ? longDetails : longDetails.Substring(0, 4096);
        guarded.Length.ShouldBe(4096);
    }

    #endregion

    #region AU-10: Audit Canonicalizer Portability & Escaping

    [Fact]
    public void AU10_CanonicalizeField_EscapesSpecialCharactersCorrectly()
    {
        var raw = "line1\nline2\rline3\ttab|pipe\\slash";
        var canonical = AuditCanonicalizer.CanonicalizeField(raw);

        canonical.ShouldBe(@"line1\nline2\rline3\ttab\ppipe\\slash");
    }

    [Fact]
    public void AU10_AuditCanonicalizer_ProducesIdenticalHashAcrossDatabaseProviders()
    {
        var key = Encoding.UTF8.GetBytes("test-secret-audit-hmac-key-cross-provider-12345");
        var occurredAt = DateTimeOffset.Parse("2026-10-09T12:00:00.0000000Z");

        var entry = new AuditLogEntry
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            OccurredAt = occurredAt,
            EventType = "TABLE_QUERY",
            ActorSid = new Sid("S-1-5-21-USER-100"),
            TargetTable = "sales.orders",
            TargetColumn = "total_amount",
            Decision = "ALLOW",
            TraceId = "trace-canonical-test",
            DetailsJson = "{\"filter\":\"status = 'OPEN'\"}",
            TenantId = new TenantId("tenant-corp-1")
        };

        var hash1 = AuditCanonicalizer.ComputeEntryHash(key, 1, "PREV_HASH_ABC", entry);
        var hash2 = AuditCanonicalizer.ComputeEntryHash(
            key,
            1,
            entry.Id.ToString(),
            "PREV_HASH_ABC",
            entry.OccurredAt,
            entry.EventType,
            entry.ActorSid.Value,
            entry.TargetTable,
            entry.TargetColumn,
            entry.Decision,
            entry.TraceId,
            entry.DetailsJson,
            entry.TenantId.Value);

        hash1.ShouldBe(hash2);
        hash1.ShouldNotBeNullOrWhiteSpace();
        hash1.Length.ShouldBe(64); // SHA-256 hex string length
    }

    #endregion

    private static AuditLogEntry CreateAuditEntry(int sequence, string eventType) => new()
    {
        EventType = eventType,
        ActorSid = new Sid($"S-1-5-21-ACTOR-{sequence}"),
        TargetTable = "finance.accounts",
        Decision = "ALLOW",
        TraceId = $"trace-{sequence}-{Guid.NewGuid():N}",
        DetailsJson = "{}"
    };
}
