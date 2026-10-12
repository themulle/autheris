namespace Autheris.Application.Governance.Diagnostics;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>
/// OpenTelemetry Diagnostics Provider for Data Classification, AI Pre-Classification,
/// and Four-Eyes Governance Workflows.
/// </summary>
public static class ClassificationDiagnostics
{
    public const string ActivitySourceName = "Autheris.Governance.Classification";
    public const string MeterName = "Autheris.Governance.Classification";
    public const string Version = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);
    public static readonly Meter Meter = new(MeterName, Version);

    public static readonly Counter<long> ProposalsCreatedCounter = Meter.CreateCounter<long>(
        "autheris.classification.proposals.created",
        unit: "{proposal}",
        description: "Number of table classification proposals generated.");

    public static readonly Counter<long> TransitionsCounter = Meter.CreateCounter<long>(
        "autheris.classification.transitions",
        unit: "{transition}",
        description: "Number of classification workflow transitions executed.");

    public static readonly Counter<long> SodViolationsCounter = Meter.CreateCounter<long>(
        "autheris.classification.sod_violations",
        unit: "{violation}",
        description: "Number of Segregation of Duties (SoD) anti-self-approval violations blocked.");

    public static readonly Counter<long> StepUpChallengesCounter = Meter.CreateCounter<long>(
        "autheris.classification.stepup_challenges",
        unit: "{challenge}",
        description: "Number of Step-Up 2FA/MFA authentication challenges prompted.");

    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "autheris.classification.operation.duration",
        unit: "ms",
        description: "Duration of classification and workflow operations in milliseconds.");

    public static void RecordProposalCreated(string status, string mode)
    {
        ProposalsCreatedCounter.Add(1,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("mode", mode));
    }

    public static void RecordTransition(string action, string reviewerRole, string outcome)
    {
        TransitionsCounter.Add(1,
            new KeyValuePair<string, object?>("action", action),
            new KeyValuePair<string, object?>("reviewer_role", reviewerRole),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public static void RecordSodViolation(string table)
    {
        SodViolationsCounter.Add(1,
            new KeyValuePair<string, object?>("table", table));
    }

    public static void RecordStepUpChallenge(int rank)
    {
        StepUpChallengesCounter.Add(1,
            new KeyValuePair<string, object?>("rank", rank));
    }
}
