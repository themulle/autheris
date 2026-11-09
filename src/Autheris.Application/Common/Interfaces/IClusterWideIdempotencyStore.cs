namespace Autheris.Application.Common.Interfaces;

/// <summary>
/// Marks an idempotency store whose reservations are shared by every replica. Only such a store may back
/// data-changing requests, because a per-process store lets a retry on another replica execute again.
/// </summary>
public interface IClusterWideIdempotencyStore : IIdempotencyStore;
