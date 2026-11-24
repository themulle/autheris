namespace Autheris.Application.Identity.Services;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

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
    private readonly Autheris.Application.Events.Interfaces.ICloudEventSubscriptionStore? _subscriptionStore;
    private readonly IGovernanceChangeNotifier? _governanceChangeNotifier;
    private readonly IGrantRevocationService _grantRevocationService;
    private readonly ILogger<IdentityDeprovisioningService>? _logger;
    private readonly TimeProvider _timeProvider;

    public IdentityDeprovisioningService(
        IIdentityStoreRepository storeRepository,
        IGrantRevocationService grantRevocationService,
        ITokenRevocationService? tokenRevocationService = null,
        IRedisLockPubSubChannel? pubSubChannel = null,
        IRoleResolverCache? roleResolverCache = null,
        IAuditWriter? auditWriter = null,
        Autheris.Application.Events.Interfaces.ICloudEventSubscriptionStore? subscriptionStore = null,
        IGovernanceChangeNotifier? governanceChangeNotifier = null,
        TimeProvider? timeProvider = null,
        ILogger<IdentityDeprovisioningService>? logger = null)
    {
        _storeRepository = storeRepository ?? throw new ArgumentNullException(nameof(storeRepository));
        _tokenRevocationService = tokenRevocationService;
        _pubSubChannel = pubSubChannel;
        _roleResolverCache = roleResolverCache;
        _auditWriter = auditWriter;
        _subscriptionStore = subscriptionStore;
        _governanceChangeNotifier = governanceChangeNotifier;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _grantRevocationService = grantRevocationService ?? throw new ArgumentNullException(nameof(grantRevocationService));
        _logger = logger;
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

        var now = _timeProvider.GetUtcNow();

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

        // 5. Invalidate role cache and broadcast identity change
        _roleResolverCache?.Invalidate(realmId, userKey);
        if (_governanceChangeNotifier != null)
        {
            await _governanceChangeNotifier.NotifyAsync(
                GovernanceChangeEvent.ForIdentity(tenantId.Value, realmId, now, subjectKey: userKey),
                ct).ConfigureAwait(false);
        }

        // 5b. GHSA-wvwx-q6jg-44wv: webhook subscription cleanup
        if (_subscriptionStore != null)
        {
            try
            {
                var subs = await _subscriptionStore.ListSubscriptionsAsync(tenantId.Value, ct).ConfigureAwait(false);
                foreach (var sub in subs)
                {
                    if (string.Equals(sub.CreatedBySid, userKey, StringComparison.OrdinalIgnoreCase))
                    {
                        await _subscriptionStore.RemoveSubscriptionAsync(tenantId.Value, sub.Id, ct).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // Non-fatal if subscription store is unavailable
            }
        }

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

        // 7. Revoke every data, unmask and catalog grant and withdraw pending requests. The lock and deactivation above stay
        // in force; an incomplete run fails the call so the deprovisioning is retried idempotently.
        var revocation = new GrantRevocationResult(0, 0, 0, 0, false);
        foreach (var form in await SubjectFormsAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false))
        {
            var partial = await _grantRevocationService.RevokeAllForSubjectAsync(tenantId, form, reason, ct).ConfigureAwait(false);
            revocation = new GrantRevocationResult(
                revocation.GrantsRevoked + partial.GrantsRevoked,
                revocation.CatalogGrantsRevoked + partial.CatalogGrantsRevoked,
                revocation.RequestsWithdrawn + partial.RequestsWithdrawn,
                revocation.Failed + partial.Failed,
                revocation.Truncated || partial.Truncated);
        }

        if (!revocation.Complete)
        {
            throw new GrantRevocationIncompleteException(revocation);
        }
    }

    /// <summary>
    /// The realm-qualified subject form always belongs to this realm. The bare form is revoked only when no other realm of the
    /// tenant holds the same user key, so a deprovisioning never revokes the grants of an unrelated user of another realm.
    /// </summary>
    private async Task<Sid[]> SubjectFormsAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct)
    {
        var qualified = new Sid($"{realmId}|{userKey}");
        if (await _storeRepository.UserKeyExistsInOtherRealmAsync(tenantId, realmId, userKey, ct).ConfigureAwait(false))
        {
            _logger?.LogWarning(
                "Bare subject form of a deprovisioned user key is ambiguous across realms of tenant {Tenant}; only the realm-qualified grants of realm {Realm} are revoked.",
                LogSanitizer.Sanitize(tenantId.Value), LogSanitizer.Sanitize(realmId));
            return [qualified];
        }

        return [new Sid(userKey), qualified];
    }
}
