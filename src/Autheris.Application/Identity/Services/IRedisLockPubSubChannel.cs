namespace Autheris.Application.Identity.Services;

using System;
using System.Threading;
using System.Threading.Tasks;

public sealed record LockPubSubMessage(
    string TenantId,
    string RealmId,
    string UserKey,
    string Reason,
    DateTimeOffset LockedAt
);

public interface IRedisLockPubSubChannel
{
    Task PublishLockAsync(LockPubSubMessage message, CancellationToken ct = default);
    IDisposable Subscribe(Action<LockPubSubMessage> handler);
}
