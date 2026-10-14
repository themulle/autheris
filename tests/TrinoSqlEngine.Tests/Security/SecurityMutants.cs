#if DEBUG
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>
/// A named security mutant (plan M-3 and the WP-X0v Phase 3 amendment): one control deliberately switched off, either by a control
/// switch of the Debug-only test seam or by a mutation operator in test code applied to the injector output. At least one gate
/// probe must fail for every mutant. A mutant that no probe notices is a gap in the gate.
/// </summary>
internal sealed record SecurityMutant(string Id, string Description, string PlanRef, CompilerTestSeams Seam)
{
    public override string ToString() => Id;
}

/// <summary>A mutant whose target control is not on this branch yet; it becomes active when the control appears (see the activation check).</summary>
internal sealed record PendingMutant(string Id, string Description, string PlanRef, string TargetWorkPackage, string SourcePattern, string ActivationRegex, bool ActivatesWhenAbsent = false)
{
    public override string ToString() => Id;
}

internal static class SecurityMutants
{
    private static CompilerTestSeams Off(params SecurityControl[] controls) => new() { DisabledControls = controls.ToHashSet() };

    private static CompilerTestSeams Operator(SqlAstRewriter rewriter, params SecurityControl[] also) =>
        new() { FaultyInjector = SecurityProbes.Rewrite(rewriter), DisabledControls = also.ToHashSet() };

    /// <summary>A DML guard relaxed after the strict-guard check (the operator is test code; production never builds relaxed options).</summary>
    private static CompilerTestSeams Relax(Func<DmlGuardOptions, DmlGuardOptions> relax) => new() { DmlGuardMutator = relax };

    private static CompilerTestSeams OmitFromKey(params string[] fields) => new() { OmittedCacheKeyFields = fields.ToHashSet() };

    /// <summary>
    /// The injector fault is always run twice: alone (the production coverage verifier is the backstop and must notice) and with
    /// the verifier switched off as well (a double fault: only the emitted-text assertions of the probes can notice).
    /// </summary>
    private static IEnumerable<SecurityMutant> ScopeDrop(string id, string description, string planRef, params SecurityScope[] scopes)
    {
        yield return new SecurityMutant(id, description, planRef, Operator(new DropScopeRewriter(scopes)));
        yield return new SecurityMutant(id + "+no-verifier", description + " (the coverage verifier is off as well)", planRef,
            Operator(new DropScopeRewriter(scopes), SecurityControl.CoverageVerifier));
    }

    public static IReadOnlyList<SecurityMutant> Active { get; } = Build();

    private static List<SecurityMutant> Build()
    {
        var m = new List<SecurityMutant>();

        // section 14 item 6: the eight proposed mutants
        m.AddRange(ScopeDrop("drop-root-tenant-predicate", "tenant predicate injection is dropped at the root scope", "S14/6, M-3", SecurityScope.Root));
        m.AddRange(ScopeDrop("drop-cte-rls", "drop the CTE body RLS", "S14/6, M-3", SecurityScope.CteBody));
        m.AddRange(ScopeDrop("drop-set-operation-branch-rls", "drop the RLS of a set-operation branch", "S14/6, M-3", SecurityScope.SetOperationBranch));
        m.AddRange(ScopeDrop("skip-scalar-subquery", "skip the RLS of a scalar subquery", "S14/6, M-3", SecurityScope.ScalarSubquery));
        m.AddRange(ScopeDrop("drop-subquery-rls", "drop the RLS of IN and EXISTS subqueries", "M-3 (extension)", SecurityScope.Subquery, SecurityScope.InSubquery, SecurityScope.ExistsSubquery));
        m.AddRange(ScopeDrop("drop-policysubquery-tenant-predicate", "drop the PolicySubquery tenant predicate", "M-3, SEC-ADG-11", SecurityScope.PolicySubquery));
        m.AddRange(ScopeDrop("drop-dml-target-predicate", "drop the tenant predicate of an UPDATE or DELETE target", "M-3 (extension)", SecurityScope.DmlTarget));
        m.AddRange(ScopeDrop("drop-merge-predicates", "drop the MERGE target (ON) and source tenant predicates", "M-3 (extension)", SecurityScope.MergeOn, SecurityScope.MergeTarget, SecurityScope.MergeSource));
        m.Add(new SecurityMutant("unmask-wildcard-expansion", "the wildcard expansion no longer masks", "S14/6, M-3", Operator(new UnmaskRewriter())));
        m.Add(new SecurityMutant("unmask-wildcard-expansion+no-verifier", "the wildcard expansion no longer masks (verifier off as well)", "S14/6, M-3",
            Operator(new UnmaskRewriter(), SecurityControl.CoverageVerifier)));
        m.Add(new SecurityMutant("allow-tenant-column-in-update", "allow the tenant column in UPDATE SET", "S14/6, M-3", Relax(d => d with { DisallowTenantColumnModificationInUpdate = false })));
        m.Add(new SecurityMutant("accept-trivially-true-merge-on", "accept an unfiltered or trivially true DML and MERGE ON condition", "S14/6, M-3", Relax(d => d with { RejectUnfilteredDml = false })));
        m.Add(new SecurityMutant("emit-string-literal-unbound", "emit a string literal inline instead of binding it", "S14/6, M-3", Off(SecurityControl.UnboundStringLiteral)));
        m.Add(new SecurityMutant("emit-string-literal-unbound+no-checker", "emit a string literal inline (the emitted-text checker is off as well)", "S14/6, M-3",
            Off(SecurityControl.UnboundStringLiteral, SecurityControl.EmittedTextChecker)));
        m.Add(new SecurityMutant("bypass-in-list-limit", "bypass the IN-list limit", "S14/6, M-3", Off(SecurityControl.InListLimit)));

        // M-3: the further named mutants
        m.Add(new SecurityMutant("skip-coverage-verifier", "skip the coverage verifier", "M-3, CR-ADG-04", Off(SecurityControl.CoverageVerifier)));
        m.Add(new SecurityMutant("skip-emitted-text-checker", "skip the emitted-text invariant checker", "M-3, CR-ADG-04", Off(SecurityControl.EmittedTextChecker)));
        m.Add(new SecurityMutant("disable-bind-by-name", "disable BindByName on the Oracle binder", "M-3, SEC-ADG-03", Off(SecurityControl.OracleBindByName)));
        m.Add(new SecurityMutant("drop-binary-tenant-template", "drop the SQL Server binary tenant template (DATALENGTH conjunct)", "M-3, SEC-ADG-04", Off(SecurityControl.TenantBinaryLengthConjunct)));
        m.Add(new SecurityMutant("plain-tenant-equality", "compare the tenant with a plain equality (no byte-exact comparison)", "brief, SEC-ADG-04", Off(SecurityControl.TenantByteExactCompare)));
        m.Add(new SecurityMutant("allow-policy-column-assignment-in-update", "allow a policy-column assignment in UPDATE", "M-3, SEC-ADG-06", Relax(d => d with { RejectPolicyColumnAssignment = false })));
        m.Add(new SecurityMutant("accept-when-not-matched-by-source", "accept an unknown MERGE clause such as WHEN NOT MATCHED BY SOURCE", "M-3, SEC-ADG-08", Off(SecurityControl.MergeClauseClosedSet)));
        m.Add(new SecurityMutant("omit-policy-values-from-cache-rebind", "omit the tenant and policy values from the cache rebind", "M-3, M-8", Off(SecurityControl.CacheRebindPolicyValues)));
        m.Add(new SecurityMutant("cte-case-insensitive-scope", "resolve a CTE case-insensitively against a quoted physical name", "M-3, INV-11", Off(SecurityControl.CteCaseInsensitiveScope)));
        m.Add(new SecurityMutant("accept-dollar-brace-in-databricks-identifier", "accept ${ in a Databricks identifier", "M-3, SEC-ADG-10", Off(SecurityControl.VariableSubstitution)));
        m.Add(new SecurityMutant("skip-execution-stack-guard", "skip the execution-stack guard", "M-3, SEC-ADG-05", Off(SecurityControl.ExecutionStackGuard)));

        // brief: binder refusal, checked DML, cache key fields, sanitizer
        m.Add(new SecurityMutant("binder-no-refusal", "plain Bind no longer refuses a statement that needs the checked executor", "brief, CR-ADG-43", Off(SecurityControl.BinderRefusal)));
        m.Add(new SecurityMutant("binder-no-autheris-ins-marker", "plain Bind ignores the reserved autheris_ins alias when the flag was lost", "brief, CR-ADG-45", Off(SecurityControl.BinderAliasMarker)));
        m.Add(new SecurityMutant("checked-dml-skip-row-count", "skip DmlCheckOption.Enforce in the checked executor (the limit)", "brief, CR-ADG-35", Off(SecurityControl.CheckedDmlRowCount)));
        m.Add(new SecurityMutant("checked-dml-commit-before-enforce", "commit before DmlCheckOption.Enforce in the checked executor", "Phase 3 amendment, CR-ADG-35", Off(SecurityControl.CheckedDmlCommitBeforeEnforce)));
        m.Add(new SecurityMutant("error-sanitizer-returns-message", "the DML error sanitizer returns the provider message", "brief, CR-ADG-34", Off(SecurityControl.ErrorSanitizer)));
        m.Add(new SecurityMutant("cache-key-omits-dialect", "the plan-cache key omits the target dialect", "M-8", OmitFromKey("TargetDialect")));
        m.Add(new SecurityMutant("cache-key-omits-row-limit", "the plan-cache key omits the enforced row limit", "M-8", OmitFromKey("EnforcedMaxRows")));
        m.Add(new SecurityMutant("cache-key-omits-statement-permissions", "the plan-cache key omits the statement permissions", "M-8", OmitFromKey("Statements")));
        m.Add(new SecurityMutant("cache-key-omits-token-guards", "the plan-cache key omits the token guards", "M-8", OmitFromKey("TokenGuards")));
        m.Add(new SecurityMutant("cache-key-omits-versions", "the plan-cache key omits the compiler and capability version", "M-8", OmitFromKey("Versions")));

        // WP-X0v activation (the controls of X0a, X0b, X0c, X0j, X0k, X0p are on this branch)
        m.Add(new SecurityMutant("skip-session-pin-read-back", "skip the session pin read-back on a connection rental", "Phase 3 amendment, SEC-X1-08", Off(SecurityControl.SessionPinReadBack)));
        m.Add(new SecurityMutant("skip-max-affected-rows", "skip MaxAffectedRows in ExecuteInTransactionAsync", "Phase 3 amendment, SEC-X1-07", Off(SecurityControl.CheckedDmlMaxAffectedRows)));
        m.Add(new SecurityMutant("commit-before-enforce-in-transaction", "commit before DmlCheckOption.Enforce in ExecuteInTransactionAsync", "Phase 3 amendment, SEC-X1-07", Off(SecurityControl.CheckedDmlCommitBeforeEnforce)));
        m.Add(new SecurityMutant("error-mapper-returns-exception-message", "the error mapper returns Exception.Message", "Phase 3 amendment, SEC-X1-12", Off(SecurityControl.ErrorMapperFixedMessage)));
        m.Add(new SecurityMutant("tenant-registry-registered-when-unavailable", "the tenant registry returns Registered when it is unavailable", "Phase 3 amendment, SEC-X1-13", Off(SecurityControl.TenantRegistryFailClosed)));
        m.Add(new SecurityMutant("catalog-load-failure-returns-empty-entry", "a catalog load failure returns an empty (allow-all) entry", "Phase 3 amendment, SEC-X1-09", Off(SecurityControl.CatalogLoadFailureDenies)));
        m.Add(new SecurityMutant("compiled-sql-binders-for-non-refusing", "CompiledSqlBinders.For returns a non-refusing binder", "Phase 3 amendment, SEC-X1-07", Off(SecurityControl.BinderFactoryRefusal)));
        m.Add(new SecurityMutant("reenable-databricks-utf8-binary-relaxation", "re-enable the Databricks UTF8_BINARY relaxation", "Phase 3 amendment, SEC-X1-10", Off(SecurityControl.DatabricksUtf8BinaryRelaxation)));

        return m;
    }


    /// <summary>
    /// Mutants of controls that live on other X0 branches (not on the base of this package). Each has an automatic activation
    /// check: once the control is on this branch, the check fails until the mutant is implemented and moved to <see cref="Active"/>.
    /// </summary>
    public static IReadOnlyList<PendingMutant> Pending { get; } = Array.Empty<PendingMutant>();
}
#endif
