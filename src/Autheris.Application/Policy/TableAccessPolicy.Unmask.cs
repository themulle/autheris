using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Policy;

public sealed partial class TableAccessPolicy
{
    /// <summary>
    /// Masks every column with an effective mask category on default access. A column is only returned in clear form when an
    /// active UNMASK grant (user or current groups) covers it. An unmask grant never widens access: it only changes the form of a
    /// column the caller may already read, so denied columns stay denied and denied tables stay denied.
    /// </summary>
    private Task<TableAccessDecision> ApplyProtectionMasksAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        EffectiveProtection protection,
        bool groupsComplete,
        CancellationToken ct) =>
        ApplyProtectionMasksCoreAsync(decision, query, protection, groupsComplete, _unmaskResolver, _classificationResolver, AuthorizationHooksActive, ct);

    internal static async Task<TableAccessDecision> ApplyProtectionMasksCoreAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        EffectiveProtection protection,
        bool groupsComplete,
        IUnmaskGrantResolver? unmaskResolver,
        ClassificationAccessResolver? classificationResolver,
        bool authorizationHooksActive,
        CancellationToken ct)
    {
        if (!decision.IsAllowed || (query.AllowOnly && !authorizationHooksActive))
        {
            return decision;
        }

        var metadata = query.Metadata;
        List<(string Column, string? DataType, string Category)>? candidates = null;
        foreach (var col in metadata.Columns)
        {
            if (string.IsNullOrWhiteSpace(col.ColumnName))
            {
                continue;
            }

            var effectiveCategory = protection.Columns.TryGetValue(col.ColumnName, out var colProt)
                ? colProt.EffectiveMaskCategory
                : protection.TableMaskCategory;
            if (string.IsNullOrWhiteSpace(effectiveCategory))
            {
                continue;
            }

            var currentLevel = decision.GetEffectiveColumnAccess(col.ColumnName, metadata);
            if (currentLevel is ColumnAccessLevel.Deny or ColumnAccessLevel.RowDependent or ColumnAccessLevel.Mask)
            {
                continue;
            }

            (candidates ??= []).Add((col.ColumnName, col.DataType, effectiveCategory));
        }

        if (candidates is null)
        {
            return decision;
        }

        var coverage = query.Unmask is { } unmask
            ? await unmask.GetAsync(ct).ConfigureAwait(false)
            : await ResolveUnmaskCoverageCoreAsync(query, groupsComplete, unmaskResolver, ct).ConfigureAwait(false);
        if (!coverage.IsEmpty)
        {
            // In-place compaction: keep only the columns the unmask grants do not cover. CREDENTIALS is never lifted (#1009 A-3).
            var kept = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!coverage.Covers(candidates[i].Column) || IsNeverUnmaskedCategory(candidates[i].Category))
                {
                    candidates[kept++] = candidates[i];
                }
            }

            candidates.RemoveRange(kept, candidates.Count - kept);
        }

        var toMask = candidates;
        if (toMask.Count == 0)
        {
            return decision;
        }

        var updatedAccess = new Dictionary<string, ColumnAccessLevel>(decision.ColumnAccess, StringComparer.OrdinalIgnoreCase);
        foreach (var (column, _, _) in toMask)
        {
            updatedAccess[column] = ColumnAccessLevel.Mask;
        }

        var withAccess = decision with { ColumnAccess = updatedAccess };
        return await WithCategoryMaskSpecsCoreAsync(withAccess, query, toMask, classificationResolver, ct).ConfigureAwait(false);
    }

    private static bool IsNeverUnmaskedCategory(string category) =>
        string.Equals(category, ClassificationScopes.CredentialsCode, StringComparison.OrdinalIgnoreCase);

    private ValueTask<UnmaskCoverage> ResolveUnmaskCoverageAsync(TableAccessQuery query, bool groupsComplete, CancellationToken ct) =>
        ResolveUnmaskCoverageCoreAsync(query, groupsComplete, _unmaskResolver, ct);

    internal static async ValueTask<UnmaskCoverage> ResolveUnmaskCoverageCoreAsync(
        TableAccessQuery query,
        bool groupsComplete,
        IUnmaskGrantResolver? unmaskResolver,
        CancellationToken ct)
    {
        if (unmaskResolver is null || !groupsComplete)
        {
            return UnmaskCoverage.None;
        }

        var subjects = query.GetEffectiveSubjectSids();
        try
        {
            return await unmaskResolver.ResolveAsync(query.Tenant, subjects, groupsComplete, query.Metadata.Identifier, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return UnmaskCoverage.None;
        }
    }

    /// <summary>
    /// Attaches the mask function of the column's category (rule lookup by category). A category without a usable rule (unknown,
    /// deleted, unparsable) keeps the column masked and leaves the spec to the type default; it never becomes clear.
    /// </summary>
    private static async Task<TableAccessDecision> WithCategoryMaskSpecsCoreAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        List<(string Column, string? DataType, string Category)> masked,
        ClassificationAccessResolver? classificationResolver,
        CancellationToken ct)
    {
        if (classificationResolver is null || decision.ColumnMasks is not null and not ColumnMaskPolicy)
        {
            return decision;
        }

        ClassificationPolicyCatalog catalog;
        try
        {
            catalog = await classificationResolver.LoadCatalogAsync(query.Tenant.Value, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return decision;
        }

        Dictionary<string, MaskSpec>? specs = null;
        foreach (var (column, dataType, category) in masked)
        {
            if (decision.ColumnMasks is ColumnMaskPolicy existing && existing.HasMask(column))
            {
                continue;
            }

            var spec = classificationResolver.ResolveCategoryMaskSpec(
                catalog, category, column, dataType, query.Metadata.Dialect, query.Tenant);
            if (spec is null)
            {
                continue;
            }

            specs ??= decision.ColumnMasks is ColumnMaskPolicy policy
                ? new Dictionary<string, MaskSpec>(policy.Specs, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, MaskSpec>(StringComparer.OrdinalIgnoreCase);
            specs[column] = spec;
        }

        return specs is null ? decision : decision with { ColumnMasks = new ColumnMaskPolicy(specs) };
    }
}
