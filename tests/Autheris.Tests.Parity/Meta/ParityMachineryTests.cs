using System.Reflection;
using Autheris.Tests.Parity.Infrastructure;
using Shouldly;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Meta;

[Trait("Category", "Parity")]
public sealed class ParityMachineryTests : ParityTestBase
{
#pragma warning disable xUnit1000 // sample classes for the scanner self-check; never discovered
    private sealed class ValidClass : ParityTestBase
    {
        [Fact]
        public void T() { }
    }

    [Trait("Category", "Pa" + "rity")]
    private sealed class GoodClass : ParityTestBase
    {
        [Fact]
        public void T() { }
    }

    private sealed class NoTraitClass : ParityTestBase
    {
        [Fact]
        public void T() { }
    }

    [Trait("Category", "Pa" + "rity")]
    private sealed class NoBaseClass
    {
        [Fact]
        public void T() { }
    }

#pragma warning restore xUnit1000

    /// <summary>Returns the offending type names: test classes without the Parity trait or without <see cref="ParityTestBase"/>.</summary>
    internal static IReadOnlyList<string> FindViolations(IEnumerable<Type> types)
    {
        var bad = new List<string>();
        foreach (var type in types)
        {
            var hasTests = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(m => m.GetCustomAttributes(typeof(FactAttribute), true).Length > 0);
            if (!hasTests)
            {
                continue;
            }

            var hasTrait = type.GetCustomAttributesData().Any(a =>
                a.AttributeType == typeof(TraitAttribute)
                && a.ConstructorArguments.Count == 2
                && (string?)a.ConstructorArguments[0].Value == "Category"
                && (string?)a.ConstructorArguments[1].Value == "Parity");
            if (!hasTrait || !typeof(ParityTestBase).IsAssignableFrom(type))
            {
                bad.Add(type.FullName ?? type.Name);
            }
        }

        return bad;
    }

    [Fact]
    public void ParityMeta_EveryTestClassHasParityTraitAndBase() =>
        FindViolations(typeof(ParityTestBase).Assembly.GetExportedTypes()).ShouldBeEmpty();

    [Fact]
    public void ParityMeta_ScannerFlagsMissingTraitAndBase() =>
        FindViolations([typeof(GoodClass), typeof(NoTraitClass), typeof(NoBaseClass), typeof(ValidClass)])
            .Select(n => n.Split('+').Last())
            .ShouldBe(["NoTraitClass", "NoBaseClass", "ValidClass"]);

    [Fact]
    public void ParityRunGuard_ThrowsWithoutEnv() =>
        Should.Throw<InvalidOperationException>(() => ParityRunGuard.EnsureEnabled(null)).Message.ShouldBe(ParityRunGuard.Message);

    [Fact]
    [Trait("Category", "Security")]
    public void ParityRunGuard_RejectsValuesOtherThanOne()
    {
        // SEC-PAR-04
        foreach (var value in new[] { "", "0", "true", "01", "1 ", " 1", "yes" })
        {
            Should.Throw<InvalidOperationException>(() => ParityRunGuard.EnsureEnabled(value), value);
        }

        Should.NotThrow(() => ParityRunGuard.EnsureEnabled("1"));
    }

    [ParityFact]
    public void ParityFact_IsDiscoveredAndRuns() => ParityRunGuard.EnsureEnabled();

    [ParityTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void ParityTheory_IsDiscoveredAndRuns(int value) => value.ShouldBeGreaterThan(0);

    // ---- KnownGap (D-5) ----
    private static ITest NewTest()
    {
        var assembly = new TestAssembly(Reflector.Wrap(typeof(ParityMachineryTests).Assembly));
        var collection = new TestCollection(assembly, null, "parity-meta");
        var testClass = new TestClass(collection, Reflector.Wrap(typeof(ParityMachineryTests)));
        var method = new TestMethod(testClass, Reflector.Wrap(typeof(ParityMachineryTests).GetMethod(nameof(Pending_IsReportedAsSkipped))!));
        var testCase = new XunitTestCase(new NullMessageSink(), TestMethodDisplay.ClassAndMethod, TestMethodDisplayOptions.None, method);
        return new XunitTest(testCase, "t");
    }

    private sealed class CaptureBus : IMessageBus
    {
        public List<IMessageSinkMessage> Messages { get; } = [];

        public bool QueueMessage(IMessageSinkMessage message)
        {
            Messages.Add(message);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static TestFailed Failed(Exception ex) =>
        new(NewTest(), 0m, string.Empty, [ex.GetType().FullName!], [ex.Message], [string.Empty], [-1]);

    [Fact]
    public async Task KnownGap_FailingBodyIsReportedAsSkipped()
    {
        var ex = await Should.ThrowAsync<KnownGapObservedException>(() =>
            KnownGap.RunAsync("PARITY-GAP-001", () => throw new InvalidOperationException("boom")));
        var capture = new CaptureBus();
        using var bus = new KnownGapMessageBus(capture);

        bus.QueueMessage(Failed(ex));

        var skipped = capture.Messages.ShouldHaveSingleItem().ShouldBeOfType<TestSkipped>();
        skipped.Reason.ShouldStartWith("PARITY-GAP-001: boom");
        bus.FailedToSkipped.ShouldBe(1);
    }

    [Fact]
    public async Task KnownGap_PassingBodyFails()
    {
        var ex = await Should.ThrowAsync<XunitException>(() => KnownGap.RunAsync("PARITY-GAP-001", () => Task.CompletedTask));
        ex.Message.ShouldBe("PARITY-GAP-001 is fixed: remove the KnownGap");
        var capture = new CaptureBus();
        new KnownGapMessageBus(capture).QueueMessage(Failed(ex));
        capture.Messages.ShouldHaveSingleItem().ShouldBeOfType<TestFailed>();
    }

    [Fact]
    public async Task KnownGap_NullIdRunsNormally()
    {
        var ran = false;
        await KnownGap.RunAsync(null, () =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        ran.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => KnownGap.RunAsync(null, () => throw new InvalidOperationException("x")));
    }

    [Theory]
    [InlineData("PARITY-GAP-001", true)]
    [InlineData("PARITY-GAP-002", true)]
    [InlineData("PARITY-GAP-1", false)]
    [InlineData("PARITY-GAP-0001", false)]
    [InlineData("parity-gap-001", false)]
    [InlineData("", false)]
    public void KnownGap_IdFormatValidated(string id, bool valid)
    {
        KnownGap.IsValidId(id).ShouldBe(valid);
        if (!valid)
        {
            Should.Throw<InvalidOperationException>(() => KnownGap.RunAsync(id, () => Task.CompletedTask).GetAwaiter().GetResult());
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task KnownGap_RejectedOnSecurityCell()
    {
        // SEC-PAR-05a
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            KnownGap.RunAsync("PARITY-GAP-001", () => throw new InvalidOperationException("deny"), ParityAspect.Security));
        ex.Message.ShouldContain("security cell");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void KnownGap_DoesNotConvertNonGapExceptions()
    {
        // SEC-PAR-05b
        var capture = new CaptureBus();
        using var bus = new KnownGapMessageBus(capture);

        bus.QueueMessage(Failed(new InvalidOperationException("plain failure")));
        bus.QueueMessage(Failed(new XunitException("assertion")));

        capture.Messages.ShouldAllBe(m => m is TestFailed);
        bus.FailedToSkipped.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task KnownGap_DeniedRequestReturning2xxStaysFailure()
    {
        // SEC-PAR-05c
        var ex = await Should.ThrowAsync<DeniedRequestSucceededException>(() =>
            KnownGap.RunAsync("PARITY-GAP-001", () => throw new DeniedRequestSucceededException("200 for a denied request")));
        var capture = new CaptureBus();
        using var bus = new KnownGapMessageBus(capture, new KnownGapAttribute("PARITY-GAP-001", "method level"));

        bus.QueueMessage(Failed(ex));

        capture.Messages.ShouldHaveSingleItem().ShouldBeOfType<TestFailed>();
    }

    [Fact]
    public void KnownGap_MethodLevelMarkerSkipsFailuresAndFailsPasses()
    {
        var capture = new CaptureBus();
        using var bus = new KnownGapMessageBus(capture, new KnownGapAttribute("PARITY-GAP-002", "why"));

        bus.QueueMessage(Failed(new XunitException("still broken")));
        bus.QueueMessage(new TestPassed(NewTest(), 0m, string.Empty));

        capture.Messages[0].ShouldBeOfType<TestSkipped>().Reason.ShouldBe("PARITY-GAP-002: why");
        capture.Messages[1].ShouldBeOfType<TestFailed>().Messages[0].ShouldBe("PARITY-GAP-002 is fixed: remove the KnownGap");
    }

    [Fact]
    public void Pending_IsReportedAsSkipped()
    {
        var capture = new CaptureBus();
        using var bus = new KnownGapMessageBus(capture);

        bus.QueueMessage(Failed(new PendingCellException("Mcp", "aggregate")));

        capture.Messages.ShouldHaveSingleItem().ShouldBeOfType<TestSkipped>().Reason.ShouldBe("PENDING: Mcp/aggregate");
    }
}
