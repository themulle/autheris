using Autheris.Domain.Exceptions;

namespace Autheris.Application.Sql.Tree;

public sealed partial class GovernedTreeQueryService
{
    /// <summary>
    /// Bounds concurrent reads per user and root table. Root fields of one operation share the slots of that operation;
    /// other requests wait a bounded time first in, first out.
    /// </summary>
    private async ValueTask<IDisposable?> AcquireReadSlotAsync(OperationMemo memo, ResolvedTableAccess rootAccess, CancellationToken ct)
    {
        var max = _options.DataSources?.MaxConcurrentReadsPerUserAndTable ?? 0;
        if (_concurrencyGate == null || max <= 0)
        {
            return null;
        }

        var key = $"{rootAccess.Tenant.Value}|{rootAccess.UserSid.Value}|{rootAccess.Metadata.Identifier.ToQualifiedName()}".ToLowerInvariant();
        var slots = memo.ReadSlots.GetOrAdd(key, static (k, state) => new OperationReadSlots(state.Gate, k, state.Max), (Gate: _concurrencyGate, Max: max));
        var wait = TimeSpan.FromMilliseconds(_options.DataSources?.TableReadWaitTimeoutMs ?? 0);
        try
        {
            return await slots.AcquireAsync(wait, ct).ConfigureAwait(false);
        }
        catch (GatewayThrottledException)
        {
            RecordTreeUsage(rootAccess, Autheris.Application.Usage.UsageOutcome.DenyRateLimit, 0, false, default);
            throw;
        }
    }
}
