using System.Text;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// Round 4 (FIX-O): core-side parts of E-05 / EX-02 (marker-based consent queries and system revocation in the
/// SQLite repository) and EX-16 (secret provider exception messages without the secret reference).
/// </summary>
public sealed class Round4OpenMetadataCoreTests : IDisposable
{
    private static readonly Guid SyncMarker = new("0e3d5c1a-7b2f-4c8e-9a61-5f0d2b7c4e19");
    private static readonly TableIdentifier SeededTable = new("finance", "dbo", "finance_table_1");

    private readonly SqliteGovernanceRepository _repository = new(new EpochValidationService());

    public void Dispose() => _repository.Dispose();

    private async Task<Consent> CreateConsentAsync(Guid? consentRequestId, string granteeSid, DateTimeOffset? validTo = null)
    {
        var meta = await _repository.GetTableMetadataAsync(SeededTable);
        meta.ShouldNotBeNull();
        return await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = SeededTable,
            ConsentRequestId = consentRequestId,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(granteeSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = validTo ?? DateTimeOffset.UtcNow.AddDays(300)
        });
    }

    [Fact]
    public async Task E05_GetActiveConsentsByConsentRequestId_ReturnsAllMarkerConsents_WithoutSubjectFilter()
    {
        var syncA = await CreateConsentAsync(SyncMarker, "S-1-5-21-R4-DELETED-USER");
        var syncB = await CreateConsentAsync(SyncMarker, "S-1-5-21-R4-OTHER-USER");
        var manual = await CreateConsentAsync(null, "S-1-5-21-R4-MANUAL");
        var expired = await CreateConsentAsync(SyncMarker, "S-1-5-21-R4-EXPIRED", DateTimeOffset.UtcNow.AddMinutes(1));

        var result = await _repository.GetActiveConsentsByConsentRequestIdAsync(SyncMarker, DateTimeOffset.UtcNow.AddMinutes(2));

        result.Select(c => c.Id).ShouldContain(syncA.Id);
        result.Select(c => c.Id).ShouldContain(syncB.Id);
        result.Select(c => c.Id).ShouldNotContain(manual.Id);
        result.Select(c => c.Id).ShouldNotContain(expired.Id);
        result.ShouldAllBe(c => c.ConsentRequestId == SyncMarker);
        result.Single(c => c.Id == syncA.Id).TableIdentifier.ShouldBe(SeededTable);
    }

    [Fact]
    public async Task E05_RevokeSystemConsent_RevokesMarkerConsent_WithoutDataOwnerRole()
    {
        var sync = await CreateConsentAsync(SyncMarker, "S-1-5-21-R4-SYNC-GRANTEE");

        var revoked = await _repository.RevokeSystemConsentAsync(sync.Id, SyncMarker, new Sid("system:openmetadata-sync"), "test");

        revoked.ShouldBeTrue();
        (await _repository.GetConsentByIdAsync(sync.Id))!.IsRevoked.ShouldBeTrue();
        (await _repository.GetActiveConsentsByConsentRequestIdAsync(SyncMarker, DateTimeOffset.UtcNow)).Select(c => c.Id).ShouldNotContain(sync.Id);
    }

    [Fact]
    public async Task E05_RevokeSystemConsent_CannotRevokeManualConsent()
    {
        var manual = await CreateConsentAsync(null, "S-1-5-21-R4-MANUAL-2");

        var revoked = await _repository.RevokeSystemConsentAsync(manual.Id, SyncMarker, new Sid("system:openmetadata-sync"), "attack");

        revoked.ShouldBeFalse();
        (await _repository.GetConsentByIdAsync(manual.Id))!.IsRevoked.ShouldBeFalse();
    }

    private static DefaultEnvironmentSecretProvider CreateSecretProvider(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);
        return new DefaultEnvironmentSecretProvider(new ConfigurationBuilder().Build(), env);
    }

    [Fact]
    public void EX16_UnresolvedSecret_ExceptionMessage_DoesNotContainReferenceOrRawToken()
    {
        // A raw token mistakenly configured instead of a reference must not end up in exception messages (and logs).
        const string rawToken = "eyJhbGciOiJIUzI1NiJ9.r4-raw-token-value";
        var provider = CreateSecretProvider(Environments.Production);

        var ex = Should.Throw<InvalidOperationException>(() => provider.GetSecretBytes(rawToken));

        ex.Message.ShouldContain("Sicherheitsfehler");
        ex.Message.ShouldNotContain(rawToken);
        ex.Message.ShouldNotContain("r4-raw-token-value");
        ex.Message.ShouldContain($"Länge {rawToken.Length}");
    }

    [Fact]
    public void EX16_UnresolvedInstanceSecret_ExceptionMessage_DoesNotContainReference()
    {
        const string reference = "itsm:webhook-secret:r4-instance-xyz";
        var provider = CreateSecretProvider(Environments.Development);

        var ex = Should.Throw<InvalidOperationException>(() => provider.GetSecretBytes(reference));

        ex.Message.ShouldNotContain("r4-instance-xyz");
    }

    [Fact]
    public void EX16_ResolvedSecret_StillWorks()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("r4:om-token", "resolved")])
            .Build();
        var provider = new DefaultEnvironmentSecretProvider(configuration, env);

        Encoding.UTF8.GetString(provider.GetSecretBytes("r4:om-token")).ShouldBe("resolved");
    }
}
