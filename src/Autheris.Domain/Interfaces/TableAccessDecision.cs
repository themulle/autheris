namespace Autheris.Domain.Interfaces;

using Autheris.Domain.Exceptions;

public record TableAccessDecision(
    TableIdentifier Table,
    bool IsAllowed,
    IReadOnlyDictionary<string, ColumnAccessLevel> ColumnAccess,
    string? CombinedRowFilterSql,
    IReadOnlyList<string> DeniedReasons,
    bool HasUnconstrainedColumnAllow = false,
    IReadOnlyDictionary<string, object?>? RowFilterParameters = null,
    string? MandatoryRowPredicateSql = null,
    IReadOnlyList<string>? AppliedVirtualFilters = null
)
{
    /// <summary>Error code of a Virtual Filter that is applied but not (provably) enforced (F-GOV-14).</summary>
    public const string VirtualFilterEnforcementIncompleteCode = "VF_ENFORCEMENT_INCOMPLETE";

    /// <summary>
    /// The typed row policy (SEC-ADG-02 item 3, SEC-ADG-25). When set, <see cref="CombinedRowFilterSql"/> is its text projection and
    /// <see cref="RowFilterParameters"/> carries its values. It is only ever changed through the typed helpers below.
    /// </summary>
    public IRowPolicy? RowPolicy { get; init; }

    /// <summary>The typed Virtual Filter policy behind <see cref="MandatoryRowPredicateSql"/> (null when it was added as text).</summary>
    public IRowPolicy? MandatoryRowPolicy { get; init; }

    /// <summary>The typed row policy before the Virtual Filter policy was added.</summary>
    public IRowPolicy? RowPolicyBeforeVirtualFilters { get; init; }

    /// <summary>Typed row policies added after the Virtual Filter policy, in order.</summary>
    public IReadOnlyList<IRowPolicy>? RowPoliciesAfterVirtualFilters { get; init; }

    /// <summary>
    /// Typed variant of <see cref="WithAdditionalRowFilter"/>: AND-merges <paramref name="policy"/> into <see cref="RowPolicy"/> and
    /// re-projects the text. On a decision that carries only a string filter the text is composed and the decision stays string-only
    /// (and is therefore denied by <see cref="RequireTypedRowPolicy"/>).
    /// </summary>
    /// <exception cref="System.Security.SecurityException">The policies bind a parameter name to conflicting values.</exception>
    public TableAccessDecision WithAdditionalRowPolicy(IRowPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var projection = policy.Project();
        if (RowPolicy == null && !string.IsNullOrWhiteSpace(CombinedRowFilterSql))
        {
            return WithAdditionalRowFilter(projection.Sql) with { RowFilterParameters = MergeStrict(RowFilterParameters, projection.Parameters) };
        }

        var next = RowPolicy == null ? policy : RowPolicy.And(policy);
        var typed = WithPolicy(next);
        return MandatoryRowPredicateSql == null
            ? typed
            : typed with
            {
                RowFiltersAfterVirtualFilters = [.. RowFiltersAfterVirtualFilters ?? [], projection.Sql],
                RowPoliciesAfterVirtualFilters = [.. RowPoliciesAfterVirtualFilters ?? [], policy]
            };
    }

    /// <summary>Typed variant of <see cref="WithMandatoryPredicate(string, IReadOnlyList{string}, IReadOnlyList{AppliedVirtualFilterBinding}?)"/>.</summary>
    public TableAccessDecision WithMandatoryPredicate(
        IRowPolicy policy,
        IReadOnlyList<string> appliedFilters,
        IReadOnlyList<AppliedVirtualFilterBinding>? appliedBindings = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(appliedFilters);
        if (MandatoryRowPredicateSql != null)
        {
            throw new InvalidOperationException("The decision already carries a Virtual Filter predicate.");
        }

        var projection = policy.Project();
        if (RowPolicy == null && !string.IsNullOrWhiteSpace(CombinedRowFilterSql))
        {
            return WithMandatoryPredicate(projection.Sql, appliedFilters, appliedBindings) with
            {
                RowFilterParameters = MergeStrict(RowFilterParameters, projection.Parameters)
            };
        }

        var next = RowPolicy == null ? policy : RowPolicy.And(policy);
        return WithPolicy(next) with
        {
            MandatoryRowPredicateSql = projection.Sql,
            MandatoryRowPolicy = policy,
            AppliedVirtualFilters = appliedFilters,
            AppliedVirtualFilterBindings = appliedBindings,
            RowFilterSqlBeforeVirtualFilters = CombinedRowFilterSql,
            RowPolicyBeforeVirtualFilters = RowPolicy,
            RowFiltersAfterVirtualFilters = null,
            RowPoliciesAfterVirtualFilters = null
        };
    }

    /// <summary>
    /// SEC-ADG-02 item 3: the decision as the AST compiler may consume it. A decision whose row filter exists only as text, or whose text
    /// no longer equals the projection of its typed policy, is denied (no drift by construction). A decision without any row filter,
    /// and a denied decision, are returned unchanged.
    /// </summary>
    public TableAccessDecision RequireTypedRowPolicy()
    {
        if (!IsAllowed)
        {
            return this;
        }

        if (RowPolicy == null)
        {
            return string.IsNullOrWhiteSpace(CombinedRowFilterSql) && string.IsNullOrWhiteSpace(MandatoryRowPredicateSql)
                ? this
                : Denied(Table, TypedRowPolicyRequiredReason);
        }

        return string.Equals(CombinedRowFilterSql, RowPolicy.Project().Sql, StringComparison.Ordinal)
            ? this
            : Denied(Table, TypedRowPolicyRequiredReason);
    }

    /// <summary>Reason of a decision that the compiler path rejects because its row filter is not (consistently) typed.</summary>
    public const string TypedRowPolicyRequiredReason = "The row-level policy is not available as a typed policy (fail-closed).";

    private TableAccessDecision WithPolicy(IRowPolicy next)
    {
        var projection = next.Project();
        var previous = RowPolicy?.Project().Parameters;
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in RowFilterParameters ?? new Dictionary<string, object?>())
        {
            if (previous == null || !previous.ContainsKey(name))
            {
                parameters[name] = value;
            }
        }

        foreach (var (name, value) in projection.Parameters)
        {
            parameters[name] = value;
        }

        return this with
        {
            RowPolicy = next,
            CombinedRowFilterSql = projection.Sql,
            RowFilterParameters = parameters
        };
    }

    private static IReadOnlyDictionary<string, object?> MergeStrict(
        IReadOnlyDictionary<string, object?>? left, IReadOnlyDictionary<string, object?> right)
    {
        var merged = new Dictionary<string, object?>(left ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in right)
        {
            if (merged.TryGetValue(name, out var existing) && !Equals(existing, value))
            {
                throw new System.Security.SecurityException("Conflicting row-level security parameters.");
            }

            merged[name] = value;
        }

        return merged;
    }

    /// <summary>F-GOV-14 (M1): the (filter, binding) pairs behind <see cref="MandatoryRowPredicateSql"/>.</summary>
    public IReadOnlyList<AppliedVirtualFilterBinding>? AppliedVirtualFilterBindings { get; init; }

    /// <summary>F-GOV-14 (M1): the row filter before the Virtual Filter predicate was added (consent filters).</summary>
    public string? RowFilterSqlBeforeVirtualFilters { get; init; }

    /// <summary>F-GOV-14 (M1): row filters added after the Virtual Filter predicate (ABAC), in order.</summary>
    public IReadOnlyList<string>? RowFiltersAfterVirtualFilters { get; init; }

    /// <summary>
    /// Virtual filters (design 3.3): adds a restrictive predicate with AND. It narrows what the consents allow and can
    /// never be lifted by another consent (consent filters are combined with OR, this one is not). The applied pairs
    /// are kept so the gateway can enforce them itself (<see cref="WithoutEnforcedVirtualFilters"/>).
    /// </summary>
    public TableAccessDecision WithMandatoryPredicate(
        string predicateSql,
        IReadOnlyList<string> appliedFilters,
        IReadOnlyList<AppliedVirtualFilterBinding>? appliedBindings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predicateSql);
        ArgumentNullException.ThrowIfNull(appliedFilters);
        if (MandatoryRowPredicateSql != null)
        {
            throw new InvalidOperationException("The decision already carries a Virtual Filter predicate.");
        }

        return this with
        {
            CombinedRowFilterSql = Compose(CombinedRowFilterSql, predicateSql),
            MandatoryRowPredicateSql = predicateSql,
            AppliedVirtualFilters = appliedFilters,
            AppliedVirtualFilterBindings = appliedBindings,
            RowFilterSqlBeforeVirtualFilters = CombinedRowFilterSql,
            RowFiltersAfterVirtualFilters = null
        };
    }

    /// <summary>
    /// Adds a restrictive row filter with AND (<c>({combined}) AND ({added})</c>). Every code path that narrows the row
    /// filter of a decision after the Virtual Filter predicate must use this method, so the filter survives
    /// <see cref="WithoutEnforcedVirtualFilters"/>.
    /// </summary>
    public TableAccessDecision WithAdditionalRowFilter(string rowFilterSql)
    {
        if (string.IsNullOrWhiteSpace(rowFilterSql))
        {
            return this;
        }

        var combined = Compose(CombinedRowFilterSql, rowFilterSql);
        return MandatoryRowPredicateSql == null
            ? this with { CombinedRowFilterSql = combined }
            : this with { CombinedRowFilterSql = combined, RowFiltersAfterVirtualFilters = [.. RowFiltersAfterVirtualFilters ?? [], rowFilterSql] };
    }

    /// <summary>
    /// F-GOV-14 (M1): the residual decision once the gateway enforced the Virtual Filters itself. Only the Virtual
    /// Filter predicate is removed; every other row filter stays. Denies with
    /// <see cref="VirtualFilterEnforcementIncompleteCode"/> when <paramref name="enforced"/> is not exactly the set of
    /// applied pairs (by value identity, including the snapshot generation) or when the row filter text was changed
    /// without <see cref="WithAdditionalRowFilter"/>.
    /// </summary>
    public TableAccessDecision WithoutEnforcedVirtualFilters(IReadOnlyCollection<AppliedVirtualFilterBinding> enforced)
    {
        ArgumentNullException.ThrowIfNull(enforced);
        var applied = AppliedVirtualFilterBindings ?? [];
        if (MandatoryRowPredicateSql == null && applied.Count == 0 && enforced.Count == 0)
        {
            return this;
        }

        if (MandatoryRowPredicateSql == null || applied.Count == 0)
        {
            throw Incomplete("the decision does not identify the applied Virtual Filters");
        }

        var appliedKeys = applied.Select(a => a.Key).ToHashSet();
        var enforcedKeys = enforced.Select(e => e.Key).ToHashSet();
        if (!appliedKeys.SetEquals(enforcedKeys))
        {
            throw Incomplete("the enforcement does not cover exactly the applied Virtual Filters");
        }

        if (RowPolicy != null)
        {
            return WithoutEnforcedTyped();
        }

        var afters = RowFiltersAfterVirtualFilters ?? [];
        var expected = afters.Aggregate(Compose(RowFilterSqlBeforeVirtualFilters, MandatoryRowPredicateSql), Compose);
        if (!string.Equals(expected, CombinedRowFilterSql, StringComparison.Ordinal))
        {
            throw Incomplete("the row filter was changed outside the decision helpers");
        }

        var residual = afters.Aggregate(RowFilterSqlBeforeVirtualFilters, Compose);
        return this with
        {
            CombinedRowFilterSql = string.IsNullOrWhiteSpace(residual) ? null : residual,
            MandatoryRowPredicateSql = null,
            AppliedVirtualFilters = [],
            AppliedVirtualFilterBindings = [],
            RowFilterSqlBeforeVirtualFilters = null,
            RowFiltersAfterVirtualFilters = null
        };
    }

    private TableAccessDecision WithoutEnforcedTyped()
    {
        if (MandatoryRowPolicy == null || RowPolicy == null)
        {
            throw Incomplete("the Virtual Filter predicate was not added as a typed policy");
        }

        var afters = RowPoliciesAfterVirtualFilters ?? [];
        IRowPolicy? expected;
        IRowPolicy? residual;
        try
        {
            expected = RowPolicyBeforeVirtualFilters == null ? MandatoryRowPolicy : RowPolicyBeforeVirtualFilters.And(MandatoryRowPolicy);
            residual = RowPolicyBeforeVirtualFilters;
            foreach (var after in afters)
            {
                expected = expected.And(after);
                residual = residual == null ? after : residual.And(after);
            }
        }
        catch (System.Security.SecurityException)
        {
            throw Incomplete("the typed policies conflict");
        }

        if (!string.Equals(expected.Fingerprint, RowPolicy.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(expected.Project().Sql, CombinedRowFilterSql, StringComparison.Ordinal))
        {
            throw Incomplete("the row filter was changed outside the decision helpers");
        }

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var projected = RowPolicy.Project().Parameters;
        foreach (var (name, value) in RowFilterParameters ?? new Dictionary<string, object?>())
        {
            if (!projected.ContainsKey(name))
            {
                parameters[name] = value;
            }
        }

        if (residual != null)
        {
            foreach (var (name, value) in residual.Project().Parameters)
            {
                parameters[name] = value;
            }
        }

        return this with
        {
            RowPolicy = residual,
            CombinedRowFilterSql = residual?.Project().Sql,
            RowFilterParameters = parameters.Count == 0 ? null : parameters,
            MandatoryRowPredicateSql = null,
            MandatoryRowPolicy = null,
            RowPolicyBeforeVirtualFilters = null,
            RowPoliciesAfterVirtualFilters = null,
            AppliedVirtualFilters = [],
            AppliedVirtualFilterBindings = [],
            RowFilterSqlBeforeVirtualFilters = null,
            RowFiltersAfterVirtualFilters = null
        };
    }

    private GatewaySecurityException Incomplete(string why) =>
        new($"Virtual filters on '{Table}' are not enforced completely: {why}.", VirtualFilterEnforcementIncompleteCode);

    private static string? Compose(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) ? right : string.IsNullOrWhiteSpace(right) ? left : $"({left}) AND ({right})";

    public ColumnAccessLevel GetColumnAccess(string columnName)
    {
        if (ColumnAccess.TryGetValue(columnName, out var lvl))
        {
            return lvl;
        }
        return HasUnconstrainedColumnAllow ? ColumnAccessLevel.Clear : ColumnAccessLevel.Deny;
    }

    public bool HasExplicitClear(string columnName) =>
        ColumnAccess.TryGetValue(columnName, out var lvl) && lvl == ColumnAccessLevel.Clear;

    /// <summary>
    /// SEC H-10/C-03: Single source of truth for the effective access level of a column, used for projection AND filtering.
    /// Deny stays Deny; catalog-sensitive columns (IsSensitive or a ColumnMaskingRule) are masked unless an explicit Clear rule exists.
    /// Filtering on a column is only permitted when this method returns <see cref="ColumnAccessLevel.Clear"/>.
    /// </summary>
    public ColumnAccessLevel GetEffectiveColumnAccess(string columnName, TableMetadata? metadata)
    {
        var access = GetColumnAccess(columnName);
        if (access == ColumnAccessLevel.Deny)
        {
            return ColumnAccessLevel.Deny;
        }

        bool isSensitiveInCatalog = false;
        if (metadata != null)
        {
            var column = metadata.GetColumn(columnName);
            isSensitiveInCatalog = column?.IsSensitive == true ||
                                   metadata.ColumnMaskingRules.ContainsKey(columnName) ||
                                   (column != null && metadata.ColumnMaskingRules.ContainsKey(column.ColumnName));
        }

        if (isSensitiveInCatalog && !HasExplicitClear(columnName))
        {
            return ColumnAccessLevel.Mask;
        }

        return access;
    }

    public static TableAccessDecision Denied(TableIdentifier table, params string[] reasons) =>
        new(table, false, new Dictionary<string, ColumnAccessLevel>(), null, reasons, false);

    public static TableAccessDecision Allowed(
        TableIdentifier table,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess,
        string? rowFilterSql = null,
        bool hasUnconstrainedColumnAllow = false,
        IReadOnlyDictionary<string, object?>? rowFilterParameters = null) =>
        new(table, true, columnAccess, rowFilterSql, Array.Empty<string>(), hasUnconstrainedColumnAllow, rowFilterParameters);
}
