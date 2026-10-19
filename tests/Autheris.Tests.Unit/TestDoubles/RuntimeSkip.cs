using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

// Linked into Autheris.Tests.Integration as well (the xunit discoverer is resolved by assembly name, see the #if below).
namespace Autheris.Tests.Unit.Support;

/// <summary>
/// Thrown from inside a <see cref="RuntimeSkippableFactAttribute"/> test to report it as skipped with a reason instead of passing vacuously.
/// Used by database contract tests whose container is unavailable on a machine without Docker (a run that requires its databases fails at startup instead).
/// </summary>
public sealed class RuntimeSkipException(string reason) : Exception(reason)
{
}

/// <summary>A fact that is reported as skipped (not passed) when it throws <see cref="RuntimeSkipException"/>.</summary>
#if AUTHERIS_INTEGRATION_TESTS
[XunitTestCaseDiscoverer("Autheris.Tests.Unit.Support.RuntimeSkippableFactDiscoverer", "Autheris.Tests.Integration")]
#else
[XunitTestCaseDiscoverer("Autheris.Tests.Unit.Support.RuntimeSkippableFactDiscoverer", "Autheris.Tests.Unit")]
#endif
public sealed class RuntimeSkippableFactAttribute : FactAttribute
{
}

public sealed class RuntimeSkippableFactDiscoverer(IMessageSink diagnosticMessageSink) : IXunitTestCaseDiscoverer
{
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        yield return new RuntimeSkippableTestCase(diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
    }
}

public sealed class RuntimeSkippableTestCase : XunitTestCase
{
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes", error: true)]
    public RuntimeSkippableTestCase()
    {
    }

    public RuntimeSkippableTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
    {
    }

    public override async Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    {
        var bus = new SkippingMessageBus(messageBus);
        var summary = await base.RunAsync(diagnosticMessageSink, bus, constructorArguments, aggregator, cancellationTokenSource);
        if (bus.SkippedCount > 0)
        {
            summary.Failed -= bus.SkippedCount;
            summary.Skipped += bus.SkippedCount;
        }

        return summary;
    }

    private sealed class SkippingMessageBus(IMessageBus inner) : IMessageBus
    {
        public int SkippedCount { get; private set; }

        public bool QueueMessage(IMessageSinkMessage message)
        {
            if (message is ITestFailed failed && failed.ExceptionTypes.Contains(typeof(RuntimeSkipException).FullName))
            {
                SkippedCount++;
                return inner.QueueMessage(new TestSkipped(failed.Test, string.Join(Environment.NewLine, failed.Messages)));
            }

            return inner.QueueMessage(message);
        }

        public void Dispose() => inner.Dispose();
    }
}
