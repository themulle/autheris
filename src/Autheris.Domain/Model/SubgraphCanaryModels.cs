namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// F-OPS-03: Subgraph Dynamic Feature Flags & Canary Traffic Splitting configuration.
/// </summary>
public sealed record SubgraphCanaryRule(
    string RuleId,
    string SubgraphName,
    string VariantName,
    string TargetUrl,
    int WeightPercent = 0,
    string? HeaderValueMatch = null,
    string? RequiredRole = null,
    IReadOnlyList<string>? AllowedTenants = null,
    bool IsEnabled = true
);

public sealed record SubgraphRoutingDecision(
    string VariantName,
    Uri EffectiveUri,
    bool IsCanary,
    string Reason
);
