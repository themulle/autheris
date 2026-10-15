using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Application.Audit;
using Autheris.Application.Streaming;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Cdc;

/// <summary>REQ-CDC-99 I1-0: fixed contracts (ids, codes, options validation, audit policy).</summary>
public sealed class CdcStreamContractsTests
{
    private static CdcStreamOptionsValidator Validator(string env = "Production")
    {
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(env);
        return new CdcStreamOptionsValidator(host);
    }

    private static bool Fails(GatewayOptions o, string env = "Production") => Validator(env).Validate(null, o).Failed;

    [Fact]
    public void StreamId_New_IsValid32LowerHex()
    {
        var id = CdcStreamId.New();
        id.Value.Length.ShouldBe(32);
        CdcStreamId.IsValid(id.Value).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("abcdef0123456789abcdef012345678")]
    [InlineData("abcdef0123456789abcdef01234567zz")]
    public void StreamId_RejectsInvalid(string? value)
    {
        CdcStreamId.TryParse(value, out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new CdcStreamId(value!));
    }

    [Fact]
    public void ActivationCodes_AreUniqueStableStrings()
    {
        CdcActivationCodes.All.Distinct().Count().ShouldBe(CdcActivationCodes.All.Count);
        CdcActivationCodes.All.ShouldAllBe(c => c.StartsWith("CDC_"));
        CdcActivationCodes.PkMismatch.ShouldBe("CDC_PK_MISMATCH");
    }

    [Fact]
    public void Defaults_AreValid() => Fails(new GatewayOptions()).ShouldBeFalse();

    [Fact]
    public void KeyUnavailableException_CarriesOnlyTheCode() =>
        new CdcStreamKeyUnavailableException().Message.ShouldBe(CdcActivationCodes.KeyUnavailable);

    [Theory]
    [InlineData(249, true)]
    [InlineData(250, false)]
    public void PollingInterval_Min250(int ms, bool fails) =>
        Fails(new GatewayOptions { MssqlChangeTracking = new MssqlChangeTrackingOptions { PollingIntervalMilliseconds = ms } }).ShouldBe(fails);

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(5000, false)]
    [InlineData(5001, true)]
    public void BatchSize_Range(int size, bool fails) =>
        Fails(new GatewayOptions { MssqlChangeTracking = new MssqlChangeTrackingOptions { BatchSize = size } }).ShouldBe(fails);

    [Theory]
    [InlineData(0, true)]
    [InlineData(300, false)]
    [InlineData(301, true)]
    public void QueryTimeout_Range(int s, bool fails) =>
        Fails(new GatewayOptions { MssqlChangeTracking = new MssqlChangeTrackingOptions { QueryTimeoutSeconds = s } }).ShouldBe(fails);

    [Fact]
    public void UnknownWatermarkPolicy_IsRejected() =>
        Fails(new GatewayOptions { MssqlChangeTracking = new MssqlChangeTrackingOptions { OnWatermarkExpired = (CdcOnWatermarkExpired)99 } }).ShouldBeTrue();

    [Fact]
    public void DuplicateTrackedTables_AreRejected()
    {
        Fails(new GatewayOptions { MssqlChangeTracking = new MssqlChangeTrackingOptions { TrackedTables = ["a.dbo.t", "A.DBO.T"] } }).ShouldBeTrue();
        Fails(new GatewayOptions { MssqlChangeTracking = new MssqlChangeTrackingOptions { Tables = [new() { Table = "a.dbo.t" }, new() { Table = "a.dbo.t" }] } }).ShouldBeTrue();
    }

    [Fact]
    public void TableInBothLists_TablesWins_NoFailure() =>
        Fails(new GatewayOptions { MssqlChangeTracking = new MssqlChangeTrackingOptions { TrackedTables = ["a.dbo.t"], Tables = [new() { Table = "a.dbo.t", BatchSize = 10 }] } }).ShouldBeFalse();

    [Fact]
    public void UnqualifiedTable_IsRejected() =>
        Fails(new GatewayOptions { MssqlChangeTracking = new MssqlChangeTrackingOptions { TrackedTables = ["dbo.t"] } }).ShouldBeTrue();

    [Fact]
    public void ActiveKekId_MustBeListed()
    {
        var enc = new CdcStreamEncryptionOptions { ActiveKekId = "k2", Keks = [new() { Id = "k1", SecretRef = "cdc-stream-kek-k1" }] };
        Fails(new GatewayOptions { CdcStream = new CdcStreamOptions { Encryption = enc } }).ShouldBeTrue();
        var ok = new CdcStreamEncryptionOptions { ActiveKekId = "k1", Keks = [new() { Id = "k1", SecretRef = "cdc-stream-kek-k1" }] };
        Fails(new GatewayOptions { CdcStream = new CdcStreamOptions { Encryption = ok } }).ShouldBeFalse();
    }

    /// <summary>SEC-CDC99-14 (ST-09): the stream KEK must not reuse the webhook KEK secret.</summary>
    [Fact]
    public void Kek_ReusingWebhookSecret_IsRejected()
    {
        var enc = new CdcStreamEncryptionOptions { ActiveKekId = "k1", Keks = [new() { Id = "k1", SecretRef = "CDC-WEBHOOK-KEK" }] };
        Fails(new GatewayOptions { CdcStream = new CdcStreamOptions { Encryption = enc } }).ShouldBeTrue();
    }

    /// <summary>SEC-CDC99-07 (ST-05): IdentityMaxAgeDays = 0 only in Development.</summary>
    [Fact]
    public void IdentityMaxAgeDays_ZeroOnlyInDevelopment()
    {
        var o = new GatewayOptions { CdcStream = new CdcStreamOptions { Consumers = new CdcStreamConsumersOptions { IdentityMaxAgeDays = 0 } } };
        Fails(o, "Production").ShouldBeTrue();
        Fails(o, "Development").ShouldBeFalse();
        Fails(new GatewayOptions { CdcStream = new CdcStreamOptions { Consumers = new CdcStreamConsumersOptions { IdentityMaxAgeDays = 366 } } }, "Development").ShouldBeTrue();
    }

    [Fact]
    public void FailureMessages_DoNotEchoSecretRefs()
    {
        var enc = new CdcStreamEncryptionOptions { ActiveKekId = "k1", Keks = [new() { Id = "k1", SecretRef = "cdc-webhook-kek" }, new() { Id = "k1", SecretRef = "sentinel-secret" }] };
        var result = Validator().Validate(null, new GatewayOptions { CdcStream = new CdcStreamOptions { Encryption = enc } });
        (result.FailureMessage ?? "").ShouldNotContain("sentinel-secret");
    }

    [Fact]
    public void CdcAuditEvents_HaveExplicitPolicies()
    {
        AuditPolicyTable.For(AuditEventTypes.CdcDeliveryBatch).ShouldBe(AuditFailurePolicy.BestEffort);
        AuditPolicyTable.For(AuditEventTypes.CdcStreamResync).ShouldBe(AuditFailurePolicy.FailClosed);
        AuditEventTypes.CdcPluginReceiverRejected.ShouldBe("CDC_PLUGIN_RECEIVER_REJECTED");
    }
}

/// <summary>REQ-CDC-99 I1-0: the I1 stream tables exist in every engine's shape snapshot with the plan's keys.</summary>
public sealed class CdcStreamSchemaSnapshotTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Snapshot_ContainsI1StreamTables(string engine)
    {
        var shape = Autheris.Infrastructure.Persistence.Migrations.Shape.GovernanceSchemaShapeLoader.LoadEmbedded(engine);
        var byName = shape.Tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        byName["autheris_cdc_streams"].PrimaryKey.ShouldBe(["stream_id"]);
        byName["autheris_cdc_stream_events"].PrimaryKey.ShouldBe(["stream_id", "event_offset"]);
        byName["autheris_cdc_row_images"].PrimaryKey.ShouldBe(["stream_id", "key_fingerprint"]);
        byName["autheris_cdc_stream_keys"].PrimaryKey.ShouldBe(["stream_id", "dek_id"]);
        byName["autheris_cdc_stream_events"].Columns.Select(c => c.Name).ShouldContain("prior_tenant_id"); // SEC-CDC99-01
        byName["autheris_cdc_stream_events"].Columns.Single(c => c.Name == "payload").Family.ShouldBe("binary");
        byName["autheris_cdc_streams"].Indexes.ShouldContain(i => i.Name == "ux_cdc_streams_table" && i.Unique);
    }
}
