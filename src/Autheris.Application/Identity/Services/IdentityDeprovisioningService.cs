namespace Autheris.Application.Identity.Services;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public interface IIdentityDeprovisioningService
{
    Task DeprovisionAndLockSubjectAsync(
        TenantId tenantId,
        string realmId,
        string userKey,
        string reason,
        string source,
        CancellationToken ct = default);

    Task DeprovisionAsync(
        TenantId tenantId,
        string realmId,
        string userKey,
        string reason,
        Sid initiatorSid,
        CancellationToken ct = default) =>
        DeprovisionAndLockSubjectAsync(tenantId, realmId, userKey, reason, initiatorSid.Value, ct);
}

public sealed class IdentityDeprovisioningService : IIdentityDeprovisioningService
{
    private readonly IIdentityStoreRepository _storeRepository;
    private readonly ITokenRevocationService? _tokenRevocationService;
    private readonly IRedisLockPubSubChannel? _pubSubChannel;
    private readonly IRoleResolverCache? _roleResolverCache;
    private readonly IAuditWriter? _auditWriter;

    public IdentityDeprovisioningService(
        IIdentityStoreRepository storeRepository,
        ITokenRevocationService? tokenRevocationService = null,
        IRedisLockPubSubChannel? pubSubChannel = null,
        IRoleResolverCache? roleResolverCache = null,
        IAuditWriter? auditWriter = null)
    {
        _storeRepository = storeRepository ?? throw new ArgumentNullException(nameof(storeRepository));
        _tokenRevocationService = tokenRevocationService;
        _pubSubChannel = pubSubChannel;
        _roleResolverCache = roleResolverCache;
        _auditWriter = auditWriter;
    }

    public async Task DeprovisionAndLockSubjectAsync(
        TenantId tenantId,
        string realmId,
        string userKey,
        string reason,
        string source,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        var now = DateTimeOffset.UtcNow;

        // 1. Add lock entry
        var lockEntry = new ScimLock(tenantId, realmId, userKey, reason, source, now);
        await _storeRepository.AddLockAsync(lockEntry, ct).ConfigureAwait(false);

        // 2. Deactivate user in store
        await _storeRepository.DeactivateUserAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false);

        // 3. Invalidate tokens
        if (_tokenRevocationService != null)
        {
            await _tokenRevocationService.RevokeAsync(userKey, now.AddDays(7), ct).ConfigureAwait(false);
        }

        // 4. Broadcast over Redis pub/sub
        if (_pubSubChannel != null)
        {
            var msg = new LockPubSubMessage(tenantId.Value, realmId, userKey, reason, now);
            await _pubSubChannel.PublishLockAsync(msg, ct).ConfigureAwait(false);
        }

        // 5. Invalidate role cache
        _roleResolverCache?.Invalidate(realmId, userKey);

        // 6. Emit Tier-A WORM audit event
        if (_auditWriter != null)
        {
            var details = JsonSerializer.Serialize(new
            {
                realm_id = realmId,
                user_key = userKey,
                reason = reason,
                source = source,
                locked_at = now
            });

            var entry = new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "SECURITY_DEPROVISION_LOCKED",
                ActorSid = new Sid(source),
                TargetTable = "IDENTITY_USERS",
                Decision = "ALLOW",
                DetailsJson = details
            };

            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
    }
}
