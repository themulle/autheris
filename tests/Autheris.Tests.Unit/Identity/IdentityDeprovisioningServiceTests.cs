namespace Autheris.Tests.Unit.Identity;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class IdentityDeprovisioningServiceTests
{
    private readonly IIdentityStoreRepository _storeRepo = Substitute.For<IIdentityStoreRepository>();
    private readonly ITokenRevocationService _tokenRevocation = Substitute.For<ITokenRevocationService>();
    private readonly IRedisLockPubSubChannel _pubSub = Substitute.For<IRedisLockPubSubChannel>();
    private readonly IRoleResolverCache _roleCache = Substitute.For<IRoleResolverCache>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();

    [Fact]
    public void Constructor_NullStoreRepository_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new IdentityDeprovisioningService(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeprovisionAndLockSubjectAsync_InvalidRealmId_ThrowsArgumentException(string? realmId)
    {
        var sut = new IdentityDeprovisioningService(_storeRepo);
        await Should.ThrowAsync<ArgumentException>(() =>
            sut.DeprovisionAndLockSubjectAsync(new TenantId("t1"), realmId!, "user-1", "reason", "admin"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeprovisionAndLockSubjectAsync_InvalidUserKey_ThrowsArgumentException(string? userKey)
    {
        var sut = new IdentityDeprovisioningService(_storeRepo);
        await Should.ThrowAsync<ArgumentException>(() =>
            sut.DeprovisionAndLockSubjectAsync(new TenantId("t1"), "realm-1", userKey!, "reason", "admin"));
    }

    [Fact]
    public async Task DeprovisionAndLockSubjectAsync_AllDependenciesConfigured_ExecutesFullDeprovisioningPipeline()
    {
        var sut = new IdentityDeprovisioningService(
            _storeRepo,
            _tokenRevocation,
            _pubSub,
            _roleCache,
            _auditWriter);

        var tenantId = new TenantId("tenant-corp");
        var realmId = "workforce-entra";
        var userKey = "user-alice-123";
        var reason = "Employee departed";
        var source = "SCIM_DELETE";

        ScimLock? capturedLock = null;
        await _storeRepo.AddLockAsync(Arg.Do<ScimLock>(l => capturedLock = l), Arg.Any<CancellationToken>());

        LockPubSubMessage? capturedPubSub = null;
        await _pubSub.PublishLockAsync(Arg.Do<LockPubSubMessage>(m => capturedPubSub = m), Arg.Any<CancellationToken>());

        AuditLogEntry? capturedAudit = null;
        await _auditWriter.RecordRequiredAsync(Arg.Do<AuditLogEntry>(a => capturedAudit = a), Arg.Any<CancellationToken>());

        await sut.DeprovisionAndLockSubjectAsync(tenantId, realmId, userKey, reason, source);

        // 1. Lock in store
        capturedLock.ShouldNotBeNull();
        capturedLock.TenantId.ShouldBe(tenantId);
        capturedLock.RealmId.ShouldBe(realmId);
        capturedLock.UserKey.ShouldBe(userKey);
        capturedLock.Reason.ShouldBe(reason);
        capturedLock.Source.ShouldBe(source);

        // 2. Deactivate in store
        await _storeRepo.Received(1).DeactivateUserAsync(tenantId, realmId, userKey, Arg.Any<CancellationToken>());

        // 3. Token revocation (7 day sliding window)
        await _tokenRevocation.Received(1).RevokeAsync(
            userKey,
            Arg.Is<DateTimeOffset>(d => d > DateTimeOffset.UtcNow.AddDays(6) && d <= DateTimeOffset.UtcNow.AddDays(8)),
            Arg.Any<CancellationToken>());

        // 4. Redis pub/sub broadcast
        capturedPubSub.ShouldNotBeNull();
        capturedPubSub.TenantId.ShouldBe(tenantId.Value);
        capturedPubSub.RealmId.ShouldBe(realmId);
        capturedPubSub.UserKey.ShouldBe(userKey);
        capturedPubSub.Reason.ShouldBe(reason);

        // 5. Invalidate role cache
        _roleCache.Received(1).Invalidate(realmId, userKey);

        // 6. Audit entry
        capturedAudit.ShouldNotBeNull();
        capturedAudit.TenantId.ShouldBe(tenantId);
        capturedAudit.EventType.ShouldBe("SECURITY_DEPROVISION_LOCKED");
        capturedAudit.ActorSid.Value.ShouldBe(source);
        capturedAudit.TargetTable.ShouldBe("IDENTITY_USERS");
        capturedAudit.Decision.ShouldBe("ALLOW");
        capturedAudit.DetailsJson.ShouldContain(realmId);
        capturedAudit.DetailsJson.ShouldContain(userKey);
        capturedAudit.DetailsJson.ShouldContain(reason);
    }

    [Fact]
    public async Task DeprovisionAndLockSubjectAsync_NullOptionalDependencies_SucceedsWithoutError()
    {
        var sut = new IdentityDeprovisioningService(_storeRepo);
        var tenantId = new TenantId("tenant-corp");

        await sut.DeprovisionAndLockSubjectAsync(tenantId, "realm-1", "user-1", "reason", "admin");

        await _storeRepo.Received(1).AddLockAsync(Arg.Any<ScimLock>(), Arg.Any<CancellationToken>());
        await _storeRepo.Received(1).DeactivateUserAsync(tenantId, "realm-1", "user-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeprovisionAsync_DefaultInterfaceMethod_ForwardsToDeprovisionAndLockSubject()
    {
        var sut = (IIdentityDeprovisioningService)new IdentityDeprovisioningService(_storeRepo);
        var tenantId = new TenantId("tenant-corp");
        var initiatorSid = new Sid("admin@domain.local");

        ScimLock? capturedLock = null;
        await _storeRepo.AddLockAsync(Arg.Do<ScimLock>(l => capturedLock = l), Arg.Any<CancellationToken>());

        await sut.DeprovisionAsync(tenantId, "realm-1", "user-1", "Reason-Terminated", initiatorSid);

        capturedLock.ShouldNotBeNull();
        capturedLock.Source.ShouldBe(initiatorSid.Value);
        capturedLock.Reason.ShouldBe("Reason-Terminated");
    }
}
