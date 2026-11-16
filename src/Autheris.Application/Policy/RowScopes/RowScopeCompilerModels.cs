namespace Autheris.Application.Policy.RowScopes;

using System;
using System.Collections.Generic;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>One contributing allow grant's row-scope entries and any legacy simple consent row filters.</summary>
public sealed record RowScopeGrant(
    string GrantId,
    ConsentGrantSource GrantSource,
    IReadOnlyList<GrantRowScopeEntry> RowScope,
    IReadOnlyList<ConsentRowFilter>? LegacyRowFilters = null);

/// <summary>Input to <see cref="RowScopeCompiler"/> describing the caller, target table, grants, and narrowing inputs.</summary>
public sealed record RowScopeCompileRequest(
    TenantId Tenant,
    Sid UserSid,
    IReadOnlySet<Sid> GroupSids,
    TableMetadata Metadata,
    IReadOnlyList<RowScopeGrant> AllowGrants,
    IReadOnlyList<IReadOnlyList<ConsentRowFilter>>? DenyRowFilters = null,
    IReadOnlyList<PolicyPredicate>? AdditionalNarrowingPredicates = null,
    FilterObjectKinds ObjectKind = FilterObjectKinds.Relation,
    IReadOnlySet<Sid>? AllUserSids = null,
    VirtualFilterSnapshot? Snapshot = null,
    bool IncludeHardPolicies = true,
    TargetSqlDialect? TargetDialect = null)
{
    public static RowScopeGrant FromConsent(Consent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        return new RowScopeGrant(
            consent.Id.ToString("D"),
            consent.GrantSource,
            consent.RowScope,
            consent.RowFilters.Count > 0 ? consent.RowFilters : null);
    }
}

/// <summary>Result of compiling grant row scopes and hard row policies into a typed AST predicate.</summary>
public sealed record RowScopeCompileOutcome(
    bool IsDenied,
    string? DenyReason,
    string? DenyCode,
    DecisionStep? DenyStep,
    PolicyPredicate? Predicate,
    IRowPolicy? RowPolicy,
    IReadOnlyList<string> AppliedHardPolicies,
    IReadOnlyList<AppliedVirtualFilterBinding> AppliedHardBindings,
    IReadOnlyList<string> CallerAttributes,
    bool IsUnfilteredGrantUnion)
{
    public static RowScopeCompileOutcome Deny(
        string code,
        string reason,
        DecisionStep step = DecisionStep.DefaultDeny) =>
        new(
            IsDenied: true,
            DenyReason: reason,
            DenyCode: code,
            DenyStep: step,
            Predicate: null,
            RowPolicy: null,
            AppliedHardPolicies: Array.Empty<string>(),
            AppliedHardBindings: Array.Empty<AppliedVirtualFilterBinding>(),
            CallerAttributes: Array.Empty<string>(),
            IsUnfilteredGrantUnion: false);
}
