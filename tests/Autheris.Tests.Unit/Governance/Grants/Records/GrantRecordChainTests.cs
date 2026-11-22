using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Autheris.Api.Configuration;
using Autheris.Api.Errors;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Middleware;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Autheris.Tests.Unit.Governance.Grants.Records;

public sealed class GrantRecordChainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 7, 30, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Security")]
    public void Canonical_Json_IsStable()
    {
        var r1 = CreateSampleRecord(1, GrantRecordChain.GenesisHash, "{\"b\":2,\"a\":1}", "{\"z\":\"ok\",\"a\":10}");
        var r2 = r1 with
        {
            ScopeJson = "{  \"a\": 1,  \"b\": 2 }",
            DecisionJson = "{\"a\":10,\"z\":\"ok\"}",
            RecordType = GrantRecordType.RequestExpired
        };
        var r1Expired = r1 with { RecordType = GrantRecordType.RequestExpired };

        GrantRecordCanonicalizer.Canonicalize(r1Expired).ShouldBe(GrantRecordCanonicalizer.Canonicalize(r2));
        GrantRecordChain.ComputeRecordHash(r1Expired).ShouldBe(GrantRecordChain.ComputeRecordHash(r2));
        GrantRecordCanonicalizer.ToSqlString(GrantRecordType.Rejected).ShouldBe("REJECTED");
        GrantRecordCanonicalizer.ToSqlString(GrantRecordType.RequestExpired).ShouldBe("REQUEST_EXPIRED");
        GrantRecordCanonicalizer.ParseSqlString("REJECTED").ShouldBe(GrantRecordType.Rejected);
        GrantRecordCanonicalizer.ParseSqlString("REQUEST_EXPIRED").ShouldBe(GrantRecordType.RequestExpired);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Append_LinksPreviousHash()
    {
        using var signer = EcdsaGrantRecordSigner.CreateEphemeral("key-v1");
        var store = new InMemoryGrantRecordStore();
        var writer = new GrantWriter(store, signer, new FixedTimeProvider(FixedNow));
        var verifier = new GrantChainVerifier(store, signer);
        using var tx = new FakeDbTransaction();

        var consent = CreateSampleConsent();
        var input = new GrantRecordInput("tenant-a", "{\"columns\":[\"id\"]}", "alice", "{\"mode\":\"auto\"}", FixedNow);

        var r1 = await writer.GrantAsync(tx, consent, input);
        var r2 = await writer.RevokeAsync(tx, consent, input);
        var r3 = await writer.RecordRejectionAsync(tx, input with { ObjectId = "sales.orders", GranteeType = "USER", GranteeRef = "bob" });
        var r4 = await writer.RecordExpiryAsync(tx, null, input with { ObjectId = "sales.orders", GranteeType = "USER", GranteeRef = "bob" });

        r1.Seq.ShouldBe(1);
        r1.PrevHash.ShouldBe(GrantRecordChain.GenesisHash);
        r2.Seq.ShouldBe(2);
        r2.PrevHash.ShouldBe(r1.RecordHash);
        r3.Seq.ShouldBe(3);
        r3.RecordType.ShouldBe(GrantRecordType.Rejected);
        r3.PrevHash.ShouldBe(r2.RecordHash);
        r4.Seq.ShouldBe(4);
        r4.RecordType.ShouldBe(GrantRecordType.RequestExpired);
        r4.PrevHash.ShouldBe(r3.RecordHash);

        store.ConsentGrantSeqs[consent.Id].ShouldBe(1);
        var verification = await verifier.VerifyTenantChainAsync("tenant-a");
        verification.IsValid.ShouldBeTrue();
        verification.VerifiedCount.ShouldBe(4);
        verification.LastSeq.ShouldBe(4);
        verification.LastHash.ShouldBe(r4.RecordHash);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Tampered_Field_FailsVerification()
    {
        using var signer = EcdsaGrantRecordSigner.CreateEphemeral("key-v1");
        var store = new InMemoryGrantRecordStore();
        var writer = new GrantWriter(store, signer, new FixedTimeProvider(FixedNow));
        using var tx = new FakeDbTransaction();

        var consent = CreateSampleConsent();
        var input = new GrantRecordInput("tenant-a", "{}", "alice", "{}", FixedNow);
        await writer.GrantAsync(tx, consent, input);
        await writer.RevokeAsync(tx, consent, input);

        store.Records[1] = store.Records[1] with { GranteeRef = "attacker" };

        var result = await new GrantChainVerifier(store, signer).VerifyTenantChainAsync("tenant-a");
        result.IsValid.ShouldBeFalse();
        result.BrokenAtSeq.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Removed_Record_BreaksChain()
    {
        using var signer = EcdsaGrantRecordSigner.CreateEphemeral("key-v1");
        var store = new InMemoryGrantRecordStore();
        var writer = new GrantWriter(store, signer, new FixedTimeProvider(FixedNow));
        using var tx = new FakeDbTransaction();

        var consent = CreateSampleConsent();
        var input = new GrantRecordInput("tenant-a", "{}", "alice", "{}", FixedNow);
        await writer.GrantAsync(tx, consent, input);
        await writer.GrantAsync(tx, consent, input);
        await writer.RevokeAsync(tx, consent, input);

        store.Records.RemoveAt(1);

        var result = await new GrantChainVerifier(store, signer).VerifyTenantChainAsync("tenant-a");
        result.IsValid.ShouldBeFalse();
        result.BrokenAtSeq.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Signature_WithOtherKey_Fails()
    {
        using var signerA = EcdsaGrantRecordSigner.CreateEphemeral("shared-id");
        using var signerB = EcdsaGrantRecordSigner.CreateEphemeral("shared-id");
        var store = new InMemoryGrantRecordStore();
        var writer = new GrantWriter(store, signerA, new FixedTimeProvider(FixedNow));
        using var tx = new FakeDbTransaction();

        await writer.GrantAsync(tx, CreateSampleConsent(), new GrantRecordInput("tenant-a", "{}", "alice", "{}", FixedNow));

        var result = await new GrantChainVerifier(store, signerB).VerifyTenantChainAsync("tenant-a");
        result.IsValid.ShouldBeFalse();
        result.BrokenAtSeq.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task No_consent_change_without_signed_grant_record()
    {
        using var unavailableSigner = EcdsaGrantRecordSigner.Unavailable("HSM unavailable");
        var store = new InMemoryGrantRecordStore();
        var writer = new GrantWriter(store, unavailableSigner, new FixedTimeProvider(FixedNow));
        using var tx = new FakeDbTransaction();

        await Should.ThrowAsync<GrantRecordUnavailableException>(() =>
            writer.GrantAsync(tx, CreateSampleConsent(), new GrantRecordInput("tenant-a", "{}", "alice", "{}", FixedNow)));

        store.Records.ShouldBeEmpty();
        store.ConsentGrantSeqs.ShouldBeEmpty();

        var failingWriter = new GrantWriter(store, new ThrowingGrantSigner(), new FixedTimeProvider(FixedNow));
        await Should.ThrowAsync<GrantRecordUnavailableException>(() =>
            failingWriter.GrantAsync(tx, CreateSampleConsent(), new GrantRecordInput("tenant-a", "{}", "alice", "{}", FixedNow)));

        store.Records.ShouldBeEmpty();
        store.ConsentGrantSeqs.ShouldBeEmpty();
        GatewayExceptionStatusMapper.MapStatus(new GrantRecordUnavailableException()).ShouldBe(StatusCodes.Status503ServiceUnavailable);
        ApiErrorCodes.IsGoverned(ApiErrorCodes.GrantRecordUnavailable).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Signing_key_separate_from_audit_key()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ecdsa.ExportPkcs8PrivateKeyPem();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Governance:GrantRecords:SigningKey"] = pem,
                ["Gateway:GovernanceDb:AuditHmacKey"] = pem
            })
            .Build();

        Should.Throw<InvalidOperationException>(() =>
            GrantRecordKeyStartupCheck.CreateSigner(config, null, new TestHostEnvironment("Production")));

        var emptyProdConfig = new ConfigurationBuilder().Build();
        using var prodMissingSigner = GrantRecordKeyStartupCheck.CreateSigner(emptyProdConfig, null, new TestHostEnvironment("Production"));
        var startupCheck = new GrantRecordKeyStartupCheck(prodMissingSigner, new TestHostEnvironment("Production"));
        Should.Throw<GrantRecordUnavailableException>(() => startupCheck.StartAsync(CancellationToken.None));

        var vaultConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Governance:GrantRecords:SigningKeyVaultRef"] = "grant-signing-key",
                ["Gateway:GovernanceDb:AuditHmacKeyVaultRef"] = "audit-hmac-key"
            })
            .Build();
        var secrets = new FakeSecretProvider(new Dictionary<string, byte[]>
        {
            ["grant-signing-key"] = Encoding.UTF8.GetBytes(pem),
            ["audit-hmac-key"] = Encoding.UTF8.GetBytes(pem)
        });

        Should.Throw<InvalidOperationException>(() =>
            GrantRecordKeyStartupCheck.CreateSigner(vaultConfig, secrets, new TestHostEnvironment("Production")));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Rotated_KeyId_Historical_Record_Remains_Verifiable()
    {
        using var keyV1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var keyV2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v1PublicPem = keyV1.ExportSubjectPublicKeyInfoPem();

        var store = new InMemoryGrantRecordStore();
        using var tx = new FakeDbTransaction();
        var consent = CreateSampleConsent();
        var input = new GrantRecordInput("tenant-a", "{}", "alice", "{}", FixedNow);

        using (var signerV1 = EcdsaGrantRecordSigner.FromPem(keyV1.ExportPkcs8PrivateKeyPem(), "key-v1"))
        {
            var writerV1 = new GrantWriter(store, signerV1, new FixedTimeProvider(FixedNow));
            await writerV1.GrantAsync(tx, consent, input);
        }

        using var signerV2 = EcdsaGrantRecordSigner.FromPem(
            keyV2.ExportPkcs8PrivateKeyPem(),
            "key-v2",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["key-v1"] = v1PublicPem });

        var writerV2 = new GrantWriter(store, signerV2, new FixedTimeProvider(FixedNow));
        var r2 = await writerV2.RevokeAsync(tx, consent, input);

        store.Records[0].KeyId.ShouldBe("key-v1");
        r2.KeyId.ShouldBe("key-v2");

        var result = await new GrantChainVerifier(store, signerV2).VerifyTenantChainAsync("tenant-a");
        result.IsValid.ShouldBeTrue();
        result.VerifiedCount.ShouldBe(2);

        var rotatedConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Governance:GrantRecords:SigningKey"] = keyV2.ExportPkcs8PrivateKeyPem(),
                ["Gateway:Governance:GrantRecords:KeyId"] = "key-v2",
                ["Gateway:Governance:GrantRecords:PreviousPublicKeys:0:KeyId"] = "key-v1",
                ["Gateway:Governance:GrantRecords:PreviousPublicKeys:0:PublicKeyPem"] = v1PublicPem
            })
            .Build();
        UnknownGatewayKeyGuard.FindUnknownKeys(rotatedConfig).ShouldBeEmpty();
        using var configSigner = GrantRecordKeyStartupCheck.CreateSigner(rotatedConfig, null, new TestHostEnvironment("Production"));
        (await new GrantChainVerifier(store, configSigner).VerifyTenantChainAsync("tenant-a")).IsValid.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Empty_ledger_after_reset_verifies()
    {
        using var signer = EcdsaGrantRecordSigner.CreateEphemeral("key-v1");
        var store = new InMemoryGrantRecordStore();
        var verifier = new GrantChainVerifier(store, signer);

        var result = await verifier.VerifyTenantChainAsync("tenant-a");
        result.IsValid.ShouldBeTrue();
        result.VerifiedCount.ShouldBe(0);
        result.LastSeq.ShouldBe(0);
        result.LastHash.ShouldBe(GrantRecordChain.GenesisHash);
    }

    private static Consent CreateSampleConsent() =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId("tenant-a"),
            TableId = Guid.NewGuid(),
            TableIdentifier = TableIdentifier.Parse("sales.customers"),
            GranteeType = GranteeType.User,
            GranteeSid = "alice",
            CreatedBySid = "admin",
            ValidFrom = FixedNow,
            ValidTo = FixedNow.AddDays(30),
            GrantSource = ConsentGrantSource.ConsentRequest
        };

    private static GrantRecord CreateSampleRecord(long seq, string prevHash, string scopeJson, string decisionJson) =>
        new("tenant-a", seq, Guid.NewGuid(), GrantRecordType.Issued, Guid.NewGuid(), null, ConsentGrantSource.ConsentRequest,
            null, "USER", "alice", "sales.customers", scopeJson, FixedNow, FixedNow.AddDays(30), "admin", decisionJson, FixedNow, FixedNow, prevHash);

    private sealed class InMemoryGrantRecordStore : IGrantRecordStore
    {
        public List<GrantRecord> Records { get; } = [];
        public Dictionary<Guid, long> ConsentGrantSeqs { get; } = [];
        private readonly Dictionary<string, (long LastSeq, string LastHash)> _heads = new(StringComparer.Ordinal);

        public Task<(long LastSeq, string LastHash)> LockAndReadHeadAsync(DbTransaction transaction, string tenantId, CancellationToken ct = default) =>
            Task.FromResult(_heads.TryGetValue(tenantId, out var head) ? head : (0L, GrantRecordChain.GenesisHash));

        public Task AppendRecordAndAdvanceHeadAsync(DbTransaction transaction, GrantRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            _heads[record.TenantId] = (record.Seq, record.RecordHash);
            return Task.CompletedTask;
        }

        public Task UpdateConsentGrantRecordSeqAsync(DbTransaction transaction, string tenantId, Guid consentId, long seq, CancellationToken ct = default)
        {
            ConsentGrantSeqs[consentId] = seq;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GrantRecord>> ReadTenantChainAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GrantRecord>>(Records.Where(r => r.TenantId == tenantId).OrderBy(r => r.Seq).ToList());
    }

    private sealed class ThrowingGrantSigner : IGrantRecordSigner
    {
        public string ActiveKeyId => "throwing";
        public void EnsureSigningKeyAvailable() { }
        public GrantSignatureResult Sign(ReadOnlySpan<byte> data) => throw new CryptographicException("HSM signing timeout.");
        public bool Verify(ReadOnlySpan<byte> data, string signatureBase64, string keyId) => false;
    }

    private sealed class FakeSecretProvider(IReadOnlyDictionary<string, byte[]> secrets) : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) =>
            secrets.TryGetValue(secretRef, out var val) ? val : Array.Empty<byte>();
    }

    private sealed class FakeDbTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Api";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
