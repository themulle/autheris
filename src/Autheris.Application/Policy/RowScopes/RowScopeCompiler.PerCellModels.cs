namespace Autheris.Application.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Typed per-cell column policy backed by <see cref="PerCellTablePolicy"/> and ordered signature scopes.
/// Evaluates both SQL CASE compilation and streaming per-row column resolution.
/// </summary>
public sealed class PerCellColumnPolicy : IPerCellColumnPolicy
{
    private readonly IReadOnlyList<IReadOnlySet<string>> _implicitClearMaskedPerSignature;
    private readonly TargetSqlDialect _dialect;

    public PerCellColumnPolicy(
        PerCellTablePolicy typedPolicy,
        IReadOnlyList<GrantSignatureScope> signatures,
        IReadOnlySet<string> rowDependentColumns,
        IReadOnlyList<IReadOnlySet<string>>? implicitClearMaskedPerSignature = null,
        TargetSqlDialect dialect = TargetSqlDialect.PostgreSql)
    {
        TypedPolicy = typedPolicy ?? throw new ArgumentNullException(nameof(typedPolicy));
        Signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        RowDependentColumns = rowDependentColumns ?? throw new ArgumentNullException(nameof(rowDependentColumns));
        _implicitClearMaskedPerSignature = implicitClearMaskedPerSignature ?? Array.Empty<IReadOnlySet<string>>();
        _dialect = dialect;
    }

    public PerCellTablePolicy TypedPolicy { get; }
    public string Fingerprint => TypedPolicy.Fingerprint;
    public IReadOnlyList<GrantSignatureScope> Signatures { get; }
    public IReadOnlySet<string> RowDependentColumns { get; }
    public bool AnySignatureUnfiltered => TypedPolicy.AnySignatureUnfiltered;

    public IPerCellColumnPolicy? Narrow(
        IReadOnlyDictionary<string, ColumnAccessLevel> effectiveColumnAccess,
        IReadOnlySet<string>? explicitMaskColumns = null)
    {
        ArgumentNullException.ThrowIfNull(effectiveColumnAccess);

        var nextSignatures = new List<GrantSignatureScope>(Signatures.Count);
        foreach (var sig in Signatures)
        {
            var nextMap = new Dictionary<string, ColumnAccessLevel>(sig.ColumnAccess, StringComparer.OrdinalIgnoreCase);
            foreach (var col in nextMap.Keys.ToList())
            {
                var lvl = nextMap[col];
                if (effectiveColumnAccess.TryGetValue(col, out var cap))
                {
                    if (cap == ColumnAccessLevel.Deny)
                    {
                        lvl = ColumnAccessLevel.Deny;
                    }
                    else if (cap == ColumnAccessLevel.Mask && lvl == ColumnAccessLevel.Clear)
                    {
                        lvl = ColumnAccessLevel.Mask;
                    }
                }

                if (explicitMaskColumns is not null && explicitMaskColumns.Contains(col) && lvl == ColumnAccessLevel.Clear)
                {
                    lvl = ColumnAccessLevel.Mask;
                }

                nextMap[col] = lvl;
            }

            nextSignatures.Add(sig with { ColumnAccess = nextMap });
        }

        return RebuildFromSignatures(nextSignatures, _implicitClearMaskedPerSignature, TypedPolicy.NarrowingPredicate, _dialect);
    }

    public IPerCellColumnPolicy WithNarrowing(IRowPolicy narrowingPolicy)
    {
        ArgumentNullException.ThrowIfNull(narrowingPolicy);
        if (narrowingPolicy is not PolicyPredicateRowPolicy typedNarrowing)
        {
            return this;
        }

        var nextNarrowing = TypedPolicy.NarrowingPredicate is null
            ? typedNarrowing.Predicate
            : TypedPolicy.NarrowingPredicate.And(typedNarrowing.Predicate);

        var nextTyped = new PerCellTablePolicy(TypedPolicy.Signatures, TypedPolicy.Columns, nextNarrowing);
        return new PerCellColumnPolicy(nextTyped, Signatures, RowDependentColumns, _implicitClearMaskedPerSignature, _dialect);
    }

    internal (
        IReadOnlyDictionary<string, ColumnAccessLevel> ColumnAccess,
        IReadOnlySet<string>? RowDependentColumns,
        IReadOnlyList<GrantSignatureScope>? ColumnSignatures,
        IPerCellColumnPolicy? PerCellPolicy) ReprojectForProfile(MaskingPolicyMode mode, TableMetadata metadata)
    {
        var reprojected = new List<(Dictionary<string, ColumnAccessLevel> Map, HashSet<string> ImplicitMasked, PerCellSignatureScopeRef Source)>(Signatures.Count);
        for (int i = 0; i < Signatures.Count; i++)
        {
            var sig = Signatures[i];
            var implicitSet = i < _implicitClearMaskedPerSignature.Count
                ? _implicitClearMaskedPerSignature[i]
                : (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<string, ColumnAccessLevel>(sig.ColumnAccess, StringComparer.OrdinalIgnoreCase);
            var nextImplicit = new HashSet<string>(implicitSet, StringComparer.OrdinalIgnoreCase);

            foreach (var col in metadata.Columns)
            {
                var raw = map.TryGetValue(col.ColumnName, out var lvl) ? lvl : ColumnAccessLevel.Deny;
                bool sensitive = col.IsSensitive || metadata.ColumnMaskingRules.ContainsKey(col.ColumnName);
                bool wasImplicitMask = implicitSet.Contains(col.ColumnName);

                ColumnAccessLevel updated = raw;
                if (raw == ColumnAccessLevel.Deny)
                {
                    updated = ColumnAccessLevel.Deny;
                }
                else
                {
                    updated = mode switch
                    {
                        MaskingPolicyMode.Unmasked => raw == ColumnAccessLevel.Mask && wasImplicitMask ? ColumnAccessLevel.Clear : raw,
                        MaskingPolicyMode.Default => raw,
                        MaskingPolicyMode.Strict => sensitive ? ColumnAccessLevel.Deny : raw,
                        _ => sensitive ? ColumnAccessLevel.Deny : raw
                    };
                }

                if (updated == ColumnAccessLevel.Clear)
                {
                    nextImplicit.Remove(col.ColumnName);
                }

                map[col.ColumnName] = updated;
            }

            reprojected.Add((map, nextImplicit, new PerCellSignatureScopeRef(sig, TypedPolicy.Signatures[i])));
        }

        // Merge signatures that now share the identical column map.
        var mergedGroups = new Dictionary<string, (Dictionary<string, ColumnAccessLevel> Map, HashSet<string> Implicit, List<PerCellSignatureSpec> Scopes)>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();
        foreach (var item in reprojected)
        {
            string key = BuildSignatureKey(item.Map);
            if (!mergedGroups.TryGetValue(key, out var group))
            {
                group = (item.Map, new HashSet<string>(item.ImplicitMasked, StringComparer.OrdinalIgnoreCase), []);
                mergedGroups[key] = group;
                orderedKeys.Add(key);
            }
            else
            {
                group.Implicit.IntersectWith(item.ImplicitMasked);
            }

            group.Scopes.Add(item.Source.Spec);
        }

        if (orderedKeys.Count <= 1)
        {
            var single = orderedKeys.Count == 1
                ? mergedGroups[orderedKeys[0]].Map
                : new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
            return (single, null, null, null);
        }

        var mergedSpecs = new List<PerCellSignatureSpec>(orderedKeys.Count);
        var mergedSigs = new List<GrantSignatureScope>(orderedKeys.Count);
        var mergedImplicit = new List<IReadOnlySet<string>>(orderedKeys.Count);
        foreach (string key in orderedKeys)
        {
            var g = mergedGroups[key];
            bool unfiltered = g.Scopes.Any(static s => s.IsUnfiltered);
            var scopePred = unfiltered
                ? TautologyPredicate()
                : g.Scopes.Select(static s => s.Scope).Aggregate(static (l, r) => l.Or(r));
            mergedSpecs.Add(new PerCellSignatureSpec(scopePred, unfiltered));
            var scopePolicy = new PolicyPredicateRowPolicy(scopePred, _dialect, new PolicyPredicateRenderer());
            mergedSigs.Add(new GrantSignatureScope(key, g.Map, scopePolicy));
            mergedImplicit.Add(g.Implicit);
        }

        var rebuilt = RebuildCore(mergedSpecs, mergedSigs, mergedImplicit, TypedPolicy.NarrowingPredicate, _dialect, out var combinedAccess, out var rowDeps);
        if (rebuilt is null)
        {
            return (combinedAccess, null, null, null);
        }

        return (combinedAccess, rowDeps, mergedSigs, rebuilt);
    }

    internal bool TryEvaluateRowForSignature(int signatureIndex, IReadOnlyDictionary<string, object?> payload, TableMetadata metadata)
    {
        if ((uint)signatureIndex >= (uint)TypedPolicy.Signatures.Count)
        {
            return false;
        }

        var sig = TypedPolicy.Signatures[signatureIndex];
        if (sig.IsUnfiltered)
        {
            return true;
        }

        return PolicyPredicateRowEvaluator.Filter([payload], sig.Scope, metadata).Count == 1;
    }

    internal bool TryEvaluateNarrowing(IReadOnlyDictionary<string, object?> payload, TableMetadata metadata) =>
        TypedPolicy.NarrowingPredicate is null ||
        PolicyPredicateRowEvaluator.Filter([payload], TypedPolicy.NarrowingPredicate, metadata).Count == 1;

    internal ColumnAccessLevel ResolveColumnForSignatures(string columnName, IReadOnlyList<int> matchingSignatures)
    {
        if (matchingSignatures.Count == 0)
        {
            return ColumnAccessLevel.Deny;
        }

        var best = ColumnAccessLevel.Deny;
        foreach (int idx in matchingSignatures)
        {
            if ((uint)idx >= (uint)Signatures.Count)
            {
                continue;
            }

            if (Signatures[idx].ColumnAccess.TryGetValue(columnName, out var lvl) && lvl > best)
            {
                best = lvl;
                if (best == ColumnAccessLevel.Clear)
                {
                    return ColumnAccessLevel.Clear;
                }
            }
        }

        return best;
    }

    internal static PolicyPredicate TautologyPredicate() =>
        PolicyPredicate.Create(
            new BinaryExpression(new LiteralExpression(1L, LiteralType.Integer), BinaryOperator.Equal, new LiteralExpression(1L, LiteralType.Integer)),
            new Dictionary<string, PolicyValue>());

    internal static string BuildSignatureKey(IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess)
    {
        return string.Join(
            ';',
            columnAccess.OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(static kv => $"{kv.Key}={(int)kv.Value}"));
    }

    private PerCellColumnPolicy? RebuildFromSignatures(
        IReadOnlyList<GrantSignatureScope> updatedSignatures,
        IReadOnlyList<IReadOnlySet<string>> implicitClearMaskedPerSignature,
        PolicyPredicate? narrowing,
        TargetSqlDialect dialect) =>
        RebuildCore(TypedPolicy.Signatures, updatedSignatures, implicitClearMaskedPerSignature, narrowing, dialect, out _, out _);

    internal static PerCellColumnPolicy? RebuildCore(
        IReadOnlyList<PerCellSignatureSpec> specs,
        IReadOnlyList<GrantSignatureScope> signatures,
        IReadOnlyList<IReadOnlySet<string>> implicitClearMaskedPerSignature,
        PolicyPredicate? narrowing,
        TargetSqlDialect dialect,
        out IReadOnlyDictionary<string, ColumnAccessLevel> combinedAccess,
        out IReadOnlySet<string> rowDependentColumns)
    {
        var allColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sig in signatures)
        {
            foreach (var col in sig.ColumnAccess.Keys)
            {
                allColumns.Add(col);
            }
        }

        var colAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        var rules = new Dictionary<string, PerCellColumnRule>(StringComparer.OrdinalIgnoreCase);
        var rowDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string col in allColumns)
        {
            var clear = ImmutableArray.CreateBuilder<int>();
            var masked = ImmutableArray.CreateBuilder<int>();
            ColumnAccessLevel? first = null;
            bool differs = false;

            for (int i = 0; i < signatures.Count; i++)
            {
                var lvl = signatures[i].ColumnAccess.GetValueOrDefault(col, ColumnAccessLevel.Deny);
                if (first is null)
                {
                    first = lvl;
                }
                else if (first.Value != lvl)
                {
                    differs = true;
                }

                if (lvl == ColumnAccessLevel.Clear)
                {
                    clear.Add(i);
                }
                else if (lvl == ColumnAccessLevel.Mask)
                {
                    masked.Add(i);
                }
            }

            if (differs)
            {
                colAccess[col] = ColumnAccessLevel.RowDependent;
                rowDeps.Add(col);
                rules[col] = new PerCellColumnRule(clear.ToImmutable(), masked.ToImmutable());
            }
            else
            {
                colAccess[col] = first ?? ColumnAccessLevel.Deny;
            }
        }

        combinedAccess = colAccess;
        rowDependentColumns = rowDeps;
        if (rowDeps.Count == 0)
        {
            return null;
        }

        var typed = new PerCellTablePolicy(specs, rules, narrowing);
        return new PerCellColumnPolicy(typed, signatures, rowDeps, implicitClearMaskedPerSignature, dialect);
    }

    private sealed record PerCellSignatureScopeRef(GrantSignatureScope Scope, PerCellSignatureSpec Spec);
}

internal sealed record PerCellCompileResult(
    TableAccessDecision Decision,
    IReadOnlyList<CallerAttributeAuditEntry> CallerAttributes);
