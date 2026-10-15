using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Cache;

/// <summary>
/// Stable log event ids of the consent cache. The 411x range belongs to the consent cache L2 tier
/// (4101 is taken by the ReBAC table gate).
/// </summary>
internal static class ConsentCacheLogEvents
{
    public static readonly EventId L2ReadFailed = new(4111, "ConsentCacheL2ReadFailed");
    public static readonly EventId L2DeleteFailed = new(4112, "ConsentCacheL2DeleteFailed");
    public static readonly EventId L2WriteFailed = new(4113, "ConsentCacheL2WriteFailed");
}
