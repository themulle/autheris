namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

/// <summary>
/// CR-ADG-11 / M-5: the Spark proxy tests are never silently green. Without the pinned image they are reported as skipped with
/// the reason; when <c>CI=true</c> they run and fail instead (a runner without the image is a broken pipeline, not a pass).
/// A proxy that is present but does not start fails the tests; a Spark without collation support skips (or fails on CI) the
/// collation tests.
/// </summary>
internal static class SparkAvailability
{
    private static readonly Lazy<bool> ImageLookup = new(ImageExists);

    public static bool IsCi => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool ImagePresent => ImageLookup.Value;

    /// <summary>The discovery-time skip reason, or null when the tests must run (image present, or CI where absence is a failure).</summary>
    public static string? SkipReason => ImagePresent || IsCi
        ? null
        : $"The Spark proxy image {SparkProxyFixture.Image} is not available locally (docker pull it, or run with CI=true to make this a failure).";

    private static bool ImageExists()
    {
        try
        {
            var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "image", "inspect", SparkProxyFixture.Image }) info.ArgumentList.Add(arg);
            using var probe = Process.Start(info);
            if (probe is null) return false;
            probe.WaitForExit(30000);
            return probe.HasExited && probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A Spark proxy test that is skipped when the image is missing (and fails on CI).</summary>
public sealed class SparkTheoryAttribute : TheoryAttribute
{
    public SparkTheoryAttribute()
    {
        Skip = SparkAvailability.SkipReason;
    }
}

/// <summary>Thrown from inside a <see cref="SparkFactAttribute"/> test to report it as skipped (never on CI).</summary>
public sealed class SparkSkipException : Exception
{
    public SparkSkipException(string reason) : base(reason)
    {
    }
}

/// <summary>A Spark proxy fact: skipped with a reason when the image is missing, skippable at run time, failing on CI.</summary>
[XunitTestCaseDiscoverer("Autheris.Tests.Integration.SparkFactDiscoverer", "Autheris.Tests.Integration")]
public sealed class SparkFactAttribute : FactAttribute
{
    public SparkFactAttribute()
    {
        Skip = SparkAvailability.SkipReason;
    }
}

public sealed class SparkFactDiscoverer(IMessageSink diagnosticMessageSink) : IXunitTestCaseDiscoverer
{
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        yield return new SparkSkippableTestCase(diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
    }
}

public sealed class SparkSkippableTestCase : XunitTestCase
{
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes", error: true)]
    public SparkSkippableTestCase()
    {
    }

    public SparkSkippableTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
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
            if (message is ITestFailed failed && failed.ExceptionTypes.Contains(typeof(SparkSkipException).FullName))
            {
                SkippedCount++;
                return inner.QueueMessage(new TestSkipped(failed.Test, string.Join(Environment.NewLine, failed.Messages)));
            }

            return inner.QueueMessage(message);
        }

        public void Dispose() => inner.Dispose();
    }
}
