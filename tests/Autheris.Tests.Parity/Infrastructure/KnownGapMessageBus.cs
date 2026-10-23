using Xunit.Abstractions;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Infrastructure;

/// <summary>
/// Rewrites known-gap outcomes (D-5): a failure caused by <see cref="KnownGapObservedException"/> or <see cref="PendingCellException"/>
/// becomes a skip. With a method-level <see cref="KnownGapAttribute"/> every non-hard failure becomes a skip and a pass becomes a failure.
/// Any other failure is forwarded unchanged (SEC-PAR-05b).
/// </summary>
public sealed class KnownGapMessageBus(IMessageBus inner, KnownGapAttribute? methodGap = null) : IMessageBus
{
    private static readonly string ObservedType = typeof(KnownGapObservedException).FullName!;
    private static readonly string PendingType = typeof(PendingCellException).FullName!;

    public int FailedToSkipped { get; private set; }

    public int PassedToFailed { get; private set; }

    public bool QueueMessage(IMessageSinkMessage message)
    {
        switch (message)
        {
            case ITestFailed failed when TryGetSkipReason(failed, out var reason):
                FailedToSkipped++;
                return inner.QueueMessage(new TestSkipped(failed.Test, reason));
            case ITestPassed passed when methodGap is not null:
                PassedToFailed++;
                return inner.QueueMessage(new TestFailed(
                    passed.Test,
                    passed.ExecutionTime,
                    passed.Output,
                    [typeof(XunitException).FullName!],
                    [$"{methodGap.GapId} is fixed: remove the KnownGap"],
                    [string.Empty],
                    [-1]));
            default:
                return inner.QueueMessage(message);
        }
    }

    public void Dispose()
    {
        // The inner bus is owned by the xunit runner.
    }

    private bool TryGetSkipReason(ITestFailed failed, out string reason)
    {
        reason = string.Empty;
        var types = failed.ExceptionTypes;
        var messages = failed.Messages;
        if (types is not { Length: > 0 } || messages is not { Length: > 0 })
        {
            return false;
        }

        if (messages[0].StartsWith(ParityHardFailureException.HardPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (types[0] == ObservedType || types[0] == PendingType)
        {
            reason = messages[0];
            return true;
        }

        if (methodGap is not null)
        {
            reason = $"{methodGap.GapId}: {methodGap.Reason}";
            return true;
        }

        return false;
    }
}
