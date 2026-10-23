using System.ComponentModel;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Infrastructure;

internal static class ParityRunner
{
    public static KnownGapAttribute? FindMethodGap(ITestMethod method)
    {
        var attribute = method.Method.GetCustomAttributes(typeof(KnownGapAttribute)).FirstOrDefault();
        if (attribute is null)
        {
            return null;
        }

        var args = attribute.GetConstructorArguments().ToArray();
        var gap = new KnownGapAttribute((string)args[0], (string)args[1]);
        if (!KnownGap.IsValidId(gap.GapId))
        {
            throw new InvalidOperationException($"Invalid gap id '{gap.GapId}'; expected PARITY-GAP-NNN.");
        }

        // Traits on the method and on the declaring class both count (SEC-PAR-05d).
        var security = method.Method.GetCustomAttributes(typeof(TraitAttribute))
            .Concat(method.TestClass.Class.GetCustomAttributes(typeof(TraitAttribute)))
            .Select(a => a.GetConstructorArguments().ToArray())
            .Any(a => a.Length == 2 && (string)a[0] == "Category" && (string)a[1] == "Security");
        if (security)
        {
            throw new InvalidOperationException($"{gap.GapId}: KnownGap is not allowed on a Security test (SEC-PAR-05d).");
        }

        return gap;
    }

    public static async Task<RunSummary> RunAsync(ITestMethod method, IMessageBus messageBus, Func<IMessageBus, Task<RunSummary>> run)
    {
        using var bus = new KnownGapMessageBus(messageBus, FindMethodGap(method));
        var summary = await run(bus);
        Reconcile(summary, bus.PassedToFailed, bus.FailedToSkipped);
        return summary;
    }

    /// <summary>
    /// Aligns the summary with the rewritten messages: a pass flipped to failed raises Failed (passed = Total - Failed - Skipped drops),
    /// a failure rewritten to a skip moves from Failed to Skipped. Counts stay within [0, Total].
    /// </summary>
    internal static void Reconcile(RunSummary summary, int passedToFailed, int failedToSkipped)
    {
        summary.Failed = Math.Max(0, summary.Failed + passedToFailed - failedToSkipped);
        summary.Skipped += failedToSkipped;
        if (summary.Failed + summary.Skipped > summary.Total)
        {
            summary.Failed = Math.Max(0, summary.Total - summary.Skipped);
        }
    }
}

/// <summary>Test case that rewrites known-gap and pending outcomes (D-5).</summary>
public sealed class ParityTestCase : XunitTestCase
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer", true)]
    public ParityTestCase()
    {
    }

    public ParityTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod, object[]? testMethodArguments = null)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod, testMethodArguments)
    {
    }

    public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource) =>
        ParityRunner.RunAsync(TestMethod, messageBus, bus => base.RunAsync(diagnosticMessageSink, bus, constructorArguments, aggregator, cancellationTokenSource));
}

/// <summary>Theory variant of <see cref="ParityTestCase"/>.</summary>
public sealed class ParityTheoryTestCase : XunitTheoryTestCase
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer", true)]
    public ParityTheoryTestCase()
    {
    }

    public ParityTheoryTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
    {
    }

    public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource) =>
        ParityRunner.RunAsync(TestMethod, messageBus, bus => base.RunAsync(diagnosticMessageSink, bus, constructorArguments, aggregator, cancellationTokenSource));
}

public sealed class ParityFactDiscoverer(IMessageSink diagnosticMessageSink) : IXunitTestCaseDiscoverer
{
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        // Fail closed at discovery: an illegal KnownGap marker aborts the discovery of this method with an error.
        _ = ParityRunner.FindMethodGap(testMethod);
        yield return new ParityTestCase(diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
    }
}

public sealed class ParityTheoryDiscoverer(IMessageSink diagnosticMessageSink) : TheoryDiscoverer(diagnosticMessageSink)
{
    private readonly IMessageSink _sink = diagnosticMessageSink;

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForTheory(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo theoryAttribute)
    {
        _ = ParityRunner.FindMethodGap(testMethod);
        yield return new ParityTheoryTestCase(_sink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
    }

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForDataRow(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo theoryAttribute, object[] dataRow)
    {
        _ = ParityRunner.FindMethodGap(testMethod);
        yield return new ParityTestCase(_sink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod, dataRow);
    }
}

[XunitTestCaseDiscoverer("Autheris.Tests.Parity.Infrastructure.ParityFactDiscoverer", "Autheris.Tests.Parity")]
public sealed class ParityFactAttribute : FactAttribute;

[XunitTestCaseDiscoverer("Autheris.Tests.Parity.Infrastructure.ParityTheoryDiscoverer", "Autheris.Tests.Parity")]
public sealed class ParityTheoryAttribute : TheoryAttribute;
