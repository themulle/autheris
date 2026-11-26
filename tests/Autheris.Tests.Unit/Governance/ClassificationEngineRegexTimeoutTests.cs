namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// Regex evaluation timeouts must fail closed: a pattern that cannot be decided never lowers the proposed class
/// or drops the mask category, and never lets an owner mapping be skipped.
/// </summary>
[Trait("Category", "Security")]
public sealed class ClassificationEngineRegexTimeoutTests
{
    private static readonly TableIdentifier Table = new("erp", "finance", "secret_table_name");

    private static ClassificationOptions DefaultOptions() => new();

    private static StaticOptionsMonitor<GatewayOptions> Monitor(ClassificationOptions options) =>
        new(new GatewayOptions { Classification = options });

    private static Func<Regex, string, bool> TimeoutOn(IEnumerable<string> patterns)
    {
        var targets = new HashSet<string>(patterns, StringComparer.Ordinal);
        return (regex, input) => targets.Contains(regex.ToString())
            ? throw new RegexMatchTimeoutException(input, regex.ToString(), regex.MatchTimeout)
            : regex.IsMatch(input);
    }

    private static Func<Regex, string, bool> TimeoutOnCategory(ClassificationOptions options, string key) =>
        TimeoutOn(options.PiiCategories.Single(c => c.Key == key).NamePatterns);

    private static TableClassificationContext Context(params string[] columns) =>
        new(Table, columns.Select(static c => new ColumnMetadataContext(c, "varchar(50)")).ToList());

    [Fact]
    public async Task ClassifyTableAsync_CategoryPatternTimesOut_ProposesCategoryClassAndMaskAndFlagsDisputed()
    {
        var options = DefaultOptions();
        var engine = new ClassificationEngine(Monitor(options), NullLogger<ClassificationEngine>.Instance, TimeoutOnCategory(options, "IBAN"));

        var proposal = await engine.ClassifyTableAsync(Context("notes_text"));

        var col = proposal.Columns.ShouldHaveSingleItem();
        col.DetectedPiiCategoryKey.ShouldBe("IBAN");
        col.ProposedSensitivityKey.ShouldBe("L3_CONFIDENTIAL");
        col.ProposedSensitivityRank.ShouldBe(3);
        col.ProposedMaskingRule.ShouldBe("IBAN_STANDARD_4_4");
        col.ProposedMaskCategory.ShouldBe(ClassificationScopes.FinancialCode);
        col.ProposedProtectionClass.ShouldBe(ProtectionClass.Confidential);
        col.IsDisputed.ShouldBeTrue();
        col.Confidence.ShouldBe(0.65);
        col.ProposedSensitivityKey.ShouldNotBe("L2_INTERNAL");
        col.Reasoning!.ShouldContain("IBAN");
        col.Reasoning!.ShouldContain("timed out");
    }

    [Fact]
    public async Task ClassifyTableAsync_TimeoutOnHigherRankCategory_WinsOverLowerRankMatch()
    {
        var options = DefaultOptions();
        var engine = new ClassificationEngine(Monitor(options), NullLogger<ClassificationEngine>.Instance, TimeoutOnCategory(options, "HEALTH_DATA"));

        var proposal = await engine.ClassifyTableAsync(Context("billing_email"));

        var col = proposal.Columns.ShouldHaveSingleItem();
        col.DetectedPiiCategoryKey.ShouldBe("HEALTH_DATA");
        col.ProposedSensitivityRank.ShouldBe(4);
        col.ProposedMaskCategory.ShouldBe(ClassificationScopes.PiiSpecialCode);
        col.IsDisputed.ShouldBeTrue();
        col.Confidence.ShouldBe(0.65);
    }

    [Fact]
    public async Task ClassifyTableAsync_CategoryPatternTimesOut_LogsSingleWarningWithCategoryKeyOnly()
    {
        var options = DefaultOptions();
        var logger = new CapturingLogger<ClassificationEngine>();
        var engine = new ClassificationEngine(Monitor(options), logger, TimeoutOnCategory(options, "IBAN"));

        _ = await engine.ClassifyTableAsync(Context("notes_text"));

        var line = logger.Lines.ShouldHaveSingleItem();
        logger.Entries.Single().Level.ShouldBe(LogLevel.Warning);
        logger.Entries.Single().Exception.ShouldBeNull();
        line.ShouldContain("IBAN");
        line.ShouldNotContain("notes_text");
        line.ShouldNotContain("secret_table_name");
        line.ShouldNotContain("erp");
        line.ShouldNotContain(".*iban.*");
        line.ShouldNotContain("RegexMatchTimeoutException");
    }

    [Fact]
    public async Task ClassifyTableAsync_OwnerMappingTimesOut_LeavesOwnerUnresolvedAndStatusUnclassified()
    {
        const string timeoutPattern = "^TIMEOUT_MAPPING$";
        var options = new ClassificationOptions
        {
            Workflow = new WorkflowOptions
            {
                ActiveProfile = "CUSTOM",
                Profiles = new Dictionary<string, ClassificationWorkflowProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CUSTOM"] = new(
                        new OwnerResolutionConfig(
                            RuleBasedMappings: new OrderedDictionary<string, string>
                            {
                                [timeoutPattern] = "owner-a",
                                [".*"] = "owner-b"
                            }),
                        new PreClassificationConfig(),
                        new ApprovalPipelineConfig())
                }
            }
        };
        var logger = new CapturingLogger<ClassificationEngine>();
        var engine = new ClassificationEngine(Monitor(options), logger, TimeoutOn([timeoutPattern]));

        var proposal = await engine.ClassifyTableAsync(Context("id"));

        proposal.DataOwnerSid.ShouldBeNull();
        proposal.Status.ShouldBe("UNCLASSIFIED");
        var line = logger.Lines.ShouldHaveSingleItem();
        line.ShouldContain("0");
        line.ShouldNotContain("owner-a");
        line.ShouldNotContain("owner-b");
        line.ShouldNotContain("secret_table_name");
        line.ShouldNotContain("TIMEOUT_MAPPING");
    }

    [Fact]
    public void CreatePatternRegex_SupportedPattern_UsesNonBacktrackingWithDefaultTimeout()
    {
        ClassificationEngine.DefaultRegexMatchTimeout.ShouldBe(TimeSpan.FromSeconds(1));

        var regex = ClassificationEngine.CreatePatternRegex(".*iban.*", ClassificationEngine.DefaultRegexMatchTimeout);

        regex.Options.HasFlag(RegexOptions.NonBacktracking).ShouldBeTrue();
        regex.Options.HasFlag(RegexOptions.IgnoreCase).ShouldBeTrue();
        regex.Options.HasFlag(RegexOptions.CultureInvariant).ShouldBeTrue();
        regex.MatchTimeout.ShouldBe(ClassificationEngine.DefaultRegexMatchTimeout);
        regex.IsMatch("Customer_IBAN").ShouldBeTrue();
    }

    [Fact]
    public void CreatePatternRegex_BackreferencePattern_FallsBackToCompiledBacktrackingWithTimeout()
    {
        var regex = ClassificationEngine.CreatePatternRegex(@"(a)\1", ClassificationEngine.DefaultRegexMatchTimeout);

        regex.Options.HasFlag(RegexOptions.NonBacktracking).ShouldBeFalse();
        regex.Options.HasFlag(RegexOptions.Compiled).ShouldBeTrue();
        regex.Options.HasFlag(RegexOptions.IgnoreCase).ShouldBeTrue();
        regex.MatchTimeout.ShouldBe(ClassificationEngine.DefaultRegexMatchTimeout);
        regex.IsMatch("AA").ShouldBeTrue();
    }

    [Fact]
    public void CreatePatternRegex_InvalidPattern_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => ClassificationEngine.CreatePatternRegex("([unclosed", ClassificationEngine.DefaultRegexMatchTimeout));
    }

    [Fact]
    public void DefaultPiiCategoryPatterns_AllCompileWithNonBacktracking()
    {
        var patterns = ClassificationOptions.GetDefaultPiiCategories().SelectMany(static c => c.NamePatterns).ToList();

        patterns.ShouldNotBeEmpty();
        foreach (var pattern in patterns)
        {
            var regex = ClassificationEngine.CreatePatternRegex(pattern, ClassificationEngine.DefaultRegexMatchTimeout);
            regex.Options.HasFlag(RegexOptions.NonBacktracking).ShouldBeTrue(pattern);
        }
    }

    [Fact]
    public async Task ClassifyTableAsync_PatternsDifferingOnlyByCase_AreCachedSeparately()
    {
        var options = new ClassificationOptions
        {
            PiiCategories =
            [
                new("DIGITS", "Digits", "Digits only", 3, "REDACT_COMPLETELY", [@"^\d+$"]),
                new("NON_DIGITS", "Non digits", "Non digits only", 4, "REDACT_COMPLETELY", [@"^\D+$"])
            ]
        };
        var engine = new ClassificationEngine(Monitor(options), NullLogger<ClassificationEngine>.Instance);

        var proposal = await engine.ClassifyTableAsync(Context("abc"));

        var col = proposal.Columns.ShouldHaveSingleItem();
        col.DetectedPiiCategoryKey.ShouldBe("NON_DIGITS");
        col.ProposedSensitivityRank.ShouldBe(4);
        col.IsDisputed.ShouldBeFalse();
    }

    [Fact]
    public async Task ClassifyTableAsync_CategoryPatternTimesOut_ProposalIsNotZeroTouchAutoApprovable()
    {
        var options = new ClassificationOptions
        {
            Workflow = new WorkflowOptions
            {
                ActiveProfile = "CUSTOM",
                Profiles = new Dictionary<string, ClassificationWorkflowProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CUSTOM"] = new(
                        new OwnerResolutionConfig(),
                        new PreClassificationConfig(
                            "Disabled",
                            ZeroTouchAutoApprovePublicAndLowRisk: true,
                            AutoApproveConfidenceThreshold: 0.0,
                            AutoApproveMaxSensitivityRank: 99),
                        new ApprovalPipelineConfig())
                }
            }
        };
        var monitor = Monitor(options);
        var engine = new ClassificationEngine(monitor, NullLogger<ClassificationEngine>.Instance, TimeoutOnCategory(options, "IBAN"));
        var stateMachine = new ClassificationWorkflowStateMachine(monitor, NullLogger<ClassificationWorkflowStateMachine>.Instance);

        var timedOut = await engine.ClassifyTableAsync(Context("notes_text"));
        var control = await new ClassificationEngine(monitor, NullLogger<ClassificationEngine>.Instance)
            .ClassifyTableAsync(Context("notes_text"));

        stateMachine.CanZeroTouchAutoApprove(control).ShouldBeTrue();
        stateMachine.CanZeroTouchAutoApprove(timedOut).ShouldBeFalse();
    }
}
