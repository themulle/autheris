namespace Autheris.Application.Governance;

using System;
using System.Collections.Generic;
using System.Net;
using Casbin;
using Casbin.Model;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed record CasbinModelCapabilities(bool SupportsWildcardTenant);

public sealed class CasbinModelValidationException : Exception
{
    public IReadOnlyList<string> Violations { get; }

    public CasbinModelValidationException(IReadOnlyList<string> violations, Exception? inner = null)
        : base("The Casbin model does not satisfy the gateway contract: " + string.Join("; ", violations), inner)
    {
        Violations = violations;
    }
}

/// <summary>
/// E-3: verifies a Casbin model by behaviour (probe policies on a throw-away enforcer), not by text search.
/// Pure and side-effect free; safe to call at startup, in the enforcement service and in the policy simulation.
/// </summary>
public static class CasbinModelContract
{
    private const string UserA = "probe_user", UserB = "probe_other", Role = "probe_role";
    private const string TenantA = "probe_a", TenantB = "probe_b";
    private const string Obj = "probe_table", OtherObj = "probe_other_table", Act = "read";

    private sealed record Probe(string Id, bool Mandatory, string[][] Policies, string[][] Grouping, string[] Request, bool Expected);

    private static readonly SecurityEvaluationContext ProbeContext = new(
        UserSid: new Sid("S-1-5-21-PROBE-USER"),
        GroupSids: [],
        Tenant: new TenantId(TenantA),
        TargetTable: new TableIdentifier("probe", "probe", Obj),
        RequestedColumns: [],
        ClientIp: IPAddress.Loopback,
        Timestamp: DateTimeOffset.UnixEpoch,
        PurposeId: null);

    private static readonly Probe[] MandatoryProbes =
    [
        // M1: Basic function, arity of r and p
        new("M1", true, [[UserA, TenantA, Obj, Act, "true", "allow"]], [], [UserA, TenantA, Obj, Act], true),
        // M2: Tenant separation
        new("M2", true, [[UserA, TenantA, Obj, Act, "true", "allow"]], [], [UserA, TenantB, Obj, Act], false),
        // M3: Subject check
        new("M3", true, [[UserA, TenantA, Obj, Act, "true", "allow"]], [], [UserB, TenantA, Obj, Act], false),
        // M4: Object check
        new("M4", true, [[UserA, TenantA, Obj, Act, "true", "allow"]], [], [UserA, TenantA, OtherObj, Act], false),
        // M5: Deny overrides allow (policy_effect)
        new("M5", true,
            [
                [UserA, TenantA, Obj, Act, "true", "allow"],
                [UserA, TenantA, Obj, Act, "true", "deny"]
            ],
            [], [UserA, TenantA, Obj, Act], false),
        // M6: eval(p.sub_rule) is evaluated
        new("M6", true, [[UserA, TenantA, Obj, Act, "false", "allow"]], [], [UserA, TenantA, Obj, Act], false),
        // M7: Roles via g
        new("M7", true, [[Role, TenantA, Obj, Act, "true", "allow"]], [[UserA, Role]], [UserA, TenantA, Obj, Act], true),
        // M8: Role does not apply to other users
        new("M8", true, [[Role, TenantA, Obj, Act, "true", "allow"]], [[UserA, Role]], [UserB, TenantA, Obj, Act], false),
    ];

    private static readonly Probe WildcardCapabilityProbe =
        // W1: Wildcard tenant is supported
        new("W1", false, [[UserA, "*", Obj, Act, "true", "allow"]], [], [UserA, TenantB, Obj, Act], true);

    private static readonly Probe[] WildcardSafetyProbes =
    [
        // W2: '*' does not bypass subject check (parentheses bug)
        new("W2", true, [[UserA, "*", Obj, Act, "true", "allow"]], [], [UserB, TenantB, Obj, Act], false),
        // W3: '*' does not bypass object check
        new("W3", true, [[UserA, "*", Obj, Act, "true", "allow"]], [], [UserA, TenantB, OtherObj, Act], false),
        // W4: Tenant deny overrides '*' allow
        new("W4", true,
            [
                [UserA, "*", Obj, Act, "true", "allow"],
                [UserA, TenantB, Obj, Act, "true", "deny"]
            ],
            [], [UserA, TenantB, Obj, Act], false),
        // W5: '*' deny overrides tenant allow
        new("W5", true,
            [
                [UserA, "*", Obj, Act, "true", "deny"],
                [UserA, TenantA, Obj, Act, "true", "allow"]
            ],
            [], [UserA, TenantA, Obj, Act], false),
    ];

    public static CasbinModelCapabilities Verify(string modelText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelText);
        var violations = new List<string>();

        // M1–M8: mandatory
        foreach (var probe in MandatoryProbes)
        {
            Check(modelText, probe, violations);
        }

        // W1: capability
        var supportsWildcard = Run(modelText, WildcardCapabilityProbe, violations) == true;
        if (supportsWildcard)
        {
            foreach (var probe in WildcardSafetyProbes) // W2–W5
            {
                Check(modelText, probe, violations);
            }
        }

        if (violations.Count > 0)
        {
            throw new CasbinModelValidationException(violations);
        }

        return new CasbinModelCapabilities(supportsWildcard);
    }

    private static void Check(string modelText, Probe probe, List<string> violations)
    {
        var result = Run(modelText, probe, violations);
        if (result is not null && result != probe.Expected)
        {
            violations.Add($"{probe.Id}: expected {probe.Expected} for ({string.Join(", ", probe.Request)}), got {result}");
        }
    }

    /// <returns>Enforce result, or null if the model could not be built or evaluated (violation recorded).</returns>
    private static bool? Run(string modelText, Probe probe, List<string> violations)
    {
        try
        {
            var enforcer = new Enforcer(DefaultModel.CreateFromText(modelText));
            foreach (var p in probe.Policies)
            {
                enforcer.AddPolicy(p);
            }
            foreach (var g in probe.Grouping)
            {
                enforcer.AddGroupingPolicy(g);
            }
            return enforcer.Enforce(probe.Request[0], probe.Request[1], probe.Request[2], probe.Request[3], ProbeContext);
        }
        catch (Exception ex)
        {
            violations.Add($"{probe.Id}: model could not be evaluated ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }
}
