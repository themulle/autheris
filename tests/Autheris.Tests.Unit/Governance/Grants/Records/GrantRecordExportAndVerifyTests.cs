namespace Autheris.Tests.Unit.Governance.Grants.Records;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class GrantRecordExportAndVerifyTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task ExportForGrantAsync_ReturnsLifecycleRecords_KeyIds_ChainVerified_AndCorrelatedAuditEntries()
    {
        var clock = new FakeTimeProvider(BaseTime);
        var signer = EcdsaGrantRecordSigner.CreateEphemeral("key-v1");
        var store = new InMemoryGrantRecordStore();
        var writer = new GrantWriter(store, signer, clock);
        var verifier = new GrantChainVerifier(store, signer);
        var auditRepo = new InMemoryAuditLogRepository();
        var service = new GrantRecordExportService(store, signer, verifier, auditRepo);

        var grantAId = Guid.NewGuid();
        var grantBId = Guid.NewGuid();
        var consentA = BuildConsent(TenantA, grantAId, BaseTime, BaseTime.AddDays(30));
        var consentB = BuildConsent(TenantA, grantBId, BaseTime, BaseTime.AddDays(30));

        await using var tx = new StubDbTransaction();
        await writer.GrantAsync(tx, consentA, BuildInput(TenantA, "S-1-5-21-101", "approved", BaseTime));
        await writer.GrantAsync(tx, consentB, BuildInput(TenantA, "S-1-5-21-102", "approved-b", BaseTime.AddMinutes(1)));
        await AppendRenewedRecordAsync(store, signer, TenantA, consentA, BaseTime.AddDays(1), BaseTime.AddDays(45));
        await writer.RecordExpiryAsync(tx, consentA, BuildInput(TenantA, "system:expiry", "EXPIRED", BaseTime.AddDays(45)));
        await writer.RevokeAsync(tx, consentA, BuildInput(TenantA, "S-1-5-21-admin", "REVOKED", BaseTime.AddDays(46)));

        auditRepo.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            OccurredAt = BaseTime.AddHours(2),
            EventType = "DATA_READ",
            ActorSid = new Sid("S-1-5-21-user1"),
            TargetTable = "analytics.sales.orders",
            Decision = "ALLOW",
            TraceId = "trace-match-1",
            DetailsJson = JsonSerializer.Serialize(new
            {
                grants = new[]
                {
                    new { grantId = grantAId.ToString("D"), grantValidFrom = BaseTime, grantValidUntil = BaseTime.AddDays(30) }
                }
            })
        });
        auditRepo.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            OccurredAt = BaseTime.AddHours(3),
            EventType = "DATA_READ",
            ActorSid = new Sid("S-1-5-21-user2"),
            TargetTable = "analytics.sales.orders",
            Decision = "ALLOW",
            TraceId = "trace-other-grant",
            DetailsJson = JsonSerializer.Serialize(new
            {
                grants = new[]
                {
                    new { grantId = grantBId.ToString("D"), grantValidFrom = BaseTime, grantValidUntil = BaseTime.AddDays(30) }
                }
            })
        });
        auditRepo.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = TenantB,
            OccurredAt = BaseTime.AddHours(4),
            EventType = "DATA_READ",
            ActorSid = new Sid("S-1-5-21-user3"),
            TargetTable = "analytics.sales.orders",
            Decision = "ALLOW",
            TraceId = "trace-other-tenant",
            DetailsJson = JsonSerializer.Serialize(new
            {
                grants = new[]
                {
                    new { grantId = grantAId.ToString("D"), grantValidFrom = BaseTime, grantValidUntil = BaseTime.AddDays(30) }
                }
            })
        });

        var bundle = await service.ExportForGrantAsync(TenantA, grantAId);

        bundle.ShouldNotBeNull();
        bundle.TenantId.ShouldBe(TenantA.Value);
        bundle.GrantId.ShouldBe(grantAId);
        bundle.ChainVerified.ShouldBeTrue();
        bundle.Verification.IsValid.ShouldBeTrue();
        bundle.Verification.VerifiedCount.ShouldBe(4);
        bundle.KeyIds.ShouldBe(["key-v1"]);
        bundle.Records.Select(r => r.RecordType).ShouldBe(
        [
            GrantRecordType.Issued,
            GrantRecordType.Renewed,
            GrantRecordType.Expired,
            GrantRecordType.Revoked
        ]);
        bundle.CorrelatedAuditEntries.Count.ShouldBe(1);
        bundle.CorrelatedAuditEntries[0].TraceId.ShouldBe("trace-match-1");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VerifyAsync_SupportsRotatedPreviousPublicKeys_AndValidatesSignedBundle()
    {
        var (priv1, pub1) = GenerateEcdsaPemPair();
        var (priv2, _) = GenerateEcdsaPemPair();

        var signerKey1 = EcdsaGrantRecordSigner.FromPem(priv1, "key-old");
        var rotatedSigner = EcdsaGrantRecordSigner.FromPem(
            priv2,
            "key-new",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["key-old"] = pub1 });

        var store = new InMemoryGrantRecordStore();
        var clock = new FakeTimeProvider(BaseTime);
        var consentId = Guid.NewGuid();
        var consent = BuildConsent(TenantA, consentId, BaseTime, BaseTime.AddDays(30));

        await using var tx = new StubDbTransaction();
        var writerOld = new GrantWriter(store, signerKey1, clock);
        await writerOld.GrantAsync(tx, consent, BuildInput(TenantA, "S-1-5-21-101", "approved", BaseTime));

        var writerNew = new GrantWriter(store, rotatedSigner, clock);
        await writerNew.RevokeAsync(tx, consent, BuildInput(TenantA, "S-1-5-21-admin", "revoked", BaseTime.AddDays(5)));

        var service = new GrantRecordExportService(
            store,
            rotatedSigner,
            new GrantChainVerifier(store, rotatedSigner),
            new InMemoryAuditLogRepository());

        var bundle = await service.ExportForGrantAsync(TenantA, consentId);
        bundle.ShouldNotBeNull();
        bundle.KeyIds.ShouldBe(["key-old", "key-new"]);
        bundle.ChainVerified.ShouldBeTrue();

        var verifyResult = await service.VerifyAsync(
            TenantA,
            new GrantRecordVerifyRequest(GrantId: consentId, Records: bundle.Records));

        verifyResult.ShouldNotBeNull();
        verifyResult.IsValid.ShouldBeTrue();
        verifyResult.VerifiedCount.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VerifyAsync_FailsClosedOnTamperedPayload_Hash_Signature_UnknownKey_AndCrossTenant()
    {
        var signer = EcdsaGrantRecordSigner.CreateEphemeral("key-v1");
        var store = new InMemoryGrantRecordStore();
        var clock = new FakeTimeProvider(BaseTime);
        var consentId = Guid.NewGuid();
        var consent = BuildConsent(TenantA, consentId, BaseTime, BaseTime.AddDays(30));

        await using var tx = new StubDbTransaction();
        var writer = new GrantWriter(store, signer, clock);
        var issued = await writer.GrantAsync(tx, consent, BuildInput(TenantA, "S-1-5-21-101", "approved", BaseTime));

        var service = new GrantRecordExportService(
            store,
            signer,
            new GrantChainVerifier(store, signer),
            new InMemoryAuditLogRepository());

        var tamperedPayload = await service.VerifyAsync(
            TenantA,
            new GrantRecordVerifyRequest(GrantId: consentId, Records: [issued with { RequestedBy = "S-1-5-21-attacker" }]));
        tamperedPayload!.IsValid.ShouldBeFalse();
        tamperedPayload.FailureReason.ShouldBe("Record hash mismatch.");

        var tamperedHash = await service.VerifyAsync(
            TenantA,
            new GrantRecordVerifyRequest(GrantId: consentId, Records: [issued with { RecordHash = new string('f', 64) }]));
        tamperedHash!.IsValid.ShouldBeFalse();
        tamperedHash.FailureReason.ShouldBe("Record hash mismatch.");

        var tamperedSignature = await service.VerifyAsync(
            TenantA,
            new GrantRecordVerifyRequest(GrantId: consentId, Records: [issued with { Signature = Convert.ToBase64String(new byte[64]) }]));
        tamperedSignature!.IsValid.ShouldBeFalse();
        tamperedSignature.FailureReason.ShouldBe("Invalid grant record signature.");

        var unknownKey = await service.VerifyAsync(
            TenantA,
            new GrantRecordVerifyRequest(GrantId: consentId, Records: [issued with { KeyId = "unknown-key-id" }]));
        unknownKey!.IsValid.ShouldBeFalse();
        unknownKey.FailureReason.ShouldBe("Invalid grant record signature.");

        var crossTenantBundle = await service.VerifyAsync(
            TenantB,
            new GrantRecordVerifyRequest(GrantId: consentId, Records: [issued]));
        crossTenantBundle!.IsValid.ShouldBeFalse();
        crossTenantBundle.FailureReason.ShouldBe("Grant record tenant mismatch.");
    }

    [Theory]
    [InlineData("Auditor")]
    [InlineData("SecurityAuditor")]
    [InlineData("PrivacyAdmin")]
    [InlineData("DataProtectionOfficer")]
    [InlineData("GovernanceAdmin")]
    [InlineData("TenantAdmin")]
    [InlineData("SecurityAdmin")]
    [InlineData("ClusterAdmin")]
    [Trait("Category", "Security")]
    public async Task Endpoints_AllowAuthorizedRoles_AndReturnBundleAndVerification(string role)
    {
        var (service, consentId, _) = await SeedTwoTenantServiceAsync();
        var ctx = CreateHttpContext(TenantA, role);

        var exportResult = await ConsentWorkflowAdminEndpoints.ExportGrantRecordAsync(consentId, ctx, service);
        await exportResult.ExecuteAsync(ctx);
        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        ctx.Response.Body.Position = 0;
        var bundle = await JsonSerializer.DeserializeAsync<GrantRecordExportBundle>(
            ctx.Response.Body,
            CaseInsensitiveJson);
        bundle.ShouldNotBeNull();
        bundle.GrantId.ShouldBe(consentId);
        bundle.ChainVerified.ShouldBeTrue();

        var verifyCtx = CreateHttpContext(TenantA, role);
        var verifyResult = await ConsentWorkflowAdminEndpoints.VerifyGrantRecordsAsync(
            new GrantRecordVerifyRequest(GrantId: consentId, Records: bundle.Records),
            verifyCtx,
            service);
        await verifyResult.ExecuteAsync(verifyCtx);
        verifyCtx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Endpoints_EnforceTenantScoping_DenyUnauthorizedRoles_AndHideCrossTenantExistence()
    {
        var (service, tenantAGrantId, tenantBGrantId) = await SeedTwoTenantServiceAsync();

        // Unauthenticated caller -> 401
        var unauthCtx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        unauthCtx.Response.Body = new MemoryStream();
        await (await ConsentWorkflowAdminEndpoints.ExportGrantRecordAsync(tenantAGrantId, unauthCtx, service)).ExecuteAsync(unauthCtx);
        unauthCtx.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);

        // Unauthorized role -> 403
        var lowPrivCtx = CreateHttpContext(TenantA, "DataConsumer");
        await (await ConsentWorkflowAdminEndpoints.ExportGrantRecordAsync(tenantAGrantId, lowPrivCtx, service)).ExecuteAsync(lowPrivCtx);
        lowPrivCtx.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        var lowPrivVerifyCtx = CreateHttpContext(TenantA, "DataConsumer");
        await (await ConsentWorkflowAdminEndpoints.VerifyGrantRecordsAsync(
            new GrantRecordVerifyRequest(GrantId: tenantAGrantId), lowPrivVerifyCtx, service)).ExecuteAsync(lowPrivVerifyCtx);
        lowPrivVerifyCtx.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        // Non-ClusterAdmin requesting explicit cross-tenant tenantId -> 403 CROSS_TENANT_ACCESS_FORBIDDEN
        var crossTenantCtx = CreateHttpContext(TenantA, "TenantAdmin");
        await (await ConsentWorkflowAdminEndpoints.ExportGrantRecordAsync(
            tenantBGrantId, crossTenantCtx, service, tenantId: TenantB.Value)).ExecuteAsync(crossTenantCtx);
        crossTenantCtx.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        crossTenantCtx.Response.Body.Position = 0;
        using (var errDoc = await JsonDocument.ParseAsync(crossTenantCtx.Response.Body))
        {
            errDoc.RootElement.GetProperty("code").GetString().ShouldBe(ApiErrorCodes.CrossTenantAccessForbidden);
        }

        // Non-ClusterAdmin requesting cross-tenant grantId on own tenant -> 404 GRANT_NOT_FOUND (no existence oracle)
        var oracleCtx = CreateHttpContext(TenantA, "PrivacyAdmin");
        await (await ConsentWorkflowAdminEndpoints.ExportGrantRecordAsync(tenantBGrantId, oracleCtx, service)).ExecuteAsync(oracleCtx);
        oracleCtx.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        oracleCtx.Response.Body.Position = 0;
        using (var notFoundDoc = await JsonDocument.ParseAsync(oracleCtx.Response.Body))
        {
            notFoundDoc.RootElement.GetProperty("code").GetString().ShouldBe(ConsentDecisionErrorCodes.GrantNotFound);
        }

        // ClusterAdmin requesting explicit cross-tenant tenantId -> 200 OK
        var clusterCtx = CreateHttpContext(TenantA, "ClusterAdmin");
        await (await ConsentWorkflowAdminEndpoints.ExportGrantRecordAsync(
            tenantBGrantId, clusterCtx, service, tenantId: TenantB.Value)).ExecuteAsync(clusterCtx);
        clusterCtx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    private static async Task<(GrantRecordExportService Service, Guid TenantAGrantId, Guid TenantBGrantId)> SeedTwoTenantServiceAsync()
    {
        var signer = EcdsaGrantRecordSigner.CreateEphemeral("key-v1");
        var store = new InMemoryGrantRecordStore();
        var clock = new FakeTimeProvider(BaseTime);
        var writer = new GrantWriter(store, signer, clock);
        var grantA = Guid.NewGuid();
        var grantB = Guid.NewGuid();
        await using var tx = new StubDbTransaction();
        await writer.GrantAsync(tx, BuildConsent(TenantA, grantA, BaseTime, BaseTime.AddDays(30)), BuildInput(TenantA, "S-1-5-21-1", "approved", BaseTime));
        await writer.GrantAsync(tx, BuildConsent(TenantB, grantB, BaseTime, BaseTime.AddDays(30)), BuildInput(TenantB, "S-1-5-21-2", "approved", BaseTime));
        var service = new GrantRecordExportService(store, signer, new GrantChainVerifier(store, signer), new InMemoryAuditLogRepository());
        return (service, grantA, grantB);
    }

    private static DefaultHttpContext CreateHttpContext(TenantId tenant, string role)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenant.Value),
                new Claim("sub", "S-1-5-21-auditor"),
                new Claim(ClaimTypes.Role, role)
            ], "Test"))
        };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static Consent BuildConsent(TenantId tenantId, Guid consentId, DateTimeOffset validFrom, DateTimeOffset validTo) =>
        new()
        {
            Id = consentId,
            TenantId = tenantId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("analytics", "sales", "orders"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-101"),
            ValidFrom = validFrom,
            ValidTo = validTo,
            GrantSource = ConsentGrantSource.ConsentRequest
        };

    private static GrantRecordInput BuildInput(TenantId tenantId, string requestedBy, string decision, DateTimeOffset decidedAt) =>
        new(
            tenantId.Value,
            "{}",
            requestedBy,
            JsonSerializer.Serialize(new { decision }),
            decidedAt,
            ObjectId: "analytics.sales.orders",
            GranteeType: "User",
            GranteeRef: "S-1-5-21-101");

    private static async Task AppendRenewedRecordAsync(
        InMemoryGrantRecordStore store,
        IGrantRecordSigner signer,
        TenantId tenantId,
        Consent consent,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil)
    {
        await using var tx = new StubDbTransaction();
        var (lastSeq, lastHash) = await store.LockAndReadHeadAsync(tx, tenantId.Value);
        var seq = lastSeq + 1;
        var draft = new GrantRecord(
            tenantId.Value,
            seq,
            Guid.NewGuid(),
            GrantRecordType.Renewed,
            consent.Id,
            null,
            consent.GrantSource,
            null,
            consent.GranteeType.ToString(),
            consent.GranteeSid?.Value ?? string.Empty,
            consent.TableIdentifier.ToString(),
            "{}",
            validFrom,
            validUntil,
            "S-1-5-21-admin",
            "{\"action\":\"RENEWED\"}",
            validFrom,
            validFrom,
            lastHash,
            string.Empty,
            string.Empty,
            signer.ActiveKeyId);
        var hash = GrantRecordChain.ComputeRecordHash(draft);
        var sig = signer.Sign(System.Text.Encoding.UTF8.GetBytes(hash));
        var signed = draft with { RecordHash = hash, Signature = sig.Signature, KeyId = sig.KeyId };
        await store.AppendRecordAndAdvanceHeadAsync(tx, signed);
    }

    private static (string PrivatePem, string PublicPem) GenerateEcdsaPemPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }

    private sealed class InMemoryGrantRecordStore : IGrantRecordStore
    {
        private readonly Dictionary<string, List<GrantRecord>> _chains = new(StringComparer.Ordinal);

        public Task<(long LastSeq, string LastHash)> LockAndReadHeadAsync(DbTransaction transaction, string tenantId, CancellationToken ct = default)
        {
            if (!_chains.TryGetValue(tenantId, out var list) || list.Count == 0)
            {
                return Task.FromResult((0L, GrantRecordChain.GenesisHash));
            }

            var last = list[^1];
            return Task.FromResult((last.Seq, last.RecordHash));
        }

        public Task AppendRecordAndAdvanceHeadAsync(DbTransaction transaction, GrantRecord record, CancellationToken ct = default)
        {
            if (!_chains.TryGetValue(record.TenantId, out var list))
            {
                list = [];
                _chains[record.TenantId] = list;
            }

            list.Add(record);
            return Task.CompletedTask;
        }

        public Task UpdateConsentGrantRecordSeqAsync(DbTransaction transaction, string tenantId, Guid consentId, long seq, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InsertConsentRowAsync(DbTransaction transaction, Consent consent, long grantRecordSeq, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GrantRecord>> ReadTenantChainAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GrantRecord>>(
                _chains.TryGetValue(tenantId, out var list) ? [.. list.OrderBy(r => r.Seq)] : []);
    }

    private sealed class InMemoryAuditLogRepository : IAuditLogRepository
    {
        private readonly List<AuditLogEntry> _entries = [];

        public void Add(AuditLogEntry entry) => _entries.Add(entry);

        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default) =>
            RecordAuditEventAsync(entry, ct);

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            QueryAuditLogsAsync(limit: limit, tenantId: tenantId, ct: ct);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null,
            Sid? actorSid = null,
            DateTimeOffset? since = null,
            int limit = 1000,
            TenantId? tenantId = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(
                _entries
                    .Where(e => (!tenantId.HasValue || e.TenantId == tenantId.Value))
                    .Take(limit)
                    .ToArray());

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class StubDbTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => throw new NotSupportedException();
        public override void Commit() { }
        public override void Rollback() { }
    }
}
