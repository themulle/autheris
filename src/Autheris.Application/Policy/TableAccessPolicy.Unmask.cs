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
    private async Task<TableAccessDecision> ApplyProtectionMasksAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        EffectiveProtection protection,
        bool groupsComplete,
        CancellationToken ct)
    {
        if (!decision.IsAllowed || (query.AllowOnly && !AuthorizationHooksActive))
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

        var coverage = await ResolveUnmaskCoverageAsync(query, groupsComplete, ct).ConfigureAwait(false);
        if (coverage.WholeTable)
        {
            return decision;
        }

        if (!coverage.IsEmpty)
        {
            // In-place compaction: keep only the columns the unmask grants do not cover.
            var kept = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!coverage.Covers(candidates[i].Column))
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
        return await WithCategoryMaskSpecsAsync(withAccess, query, toMask, ct).ConfigureAwait(false);
    }

    private async ValueTask<UnmaskCoverage> ResolveUnmaskCoverageAsync(TableAccessQuery query, bool groupsComplete, CancellationToken ct)
    {
        if (_unmaskResolver is null || !groupsComplete)
        {
            return UnmaskCoverage.None;
        }

        IReadOnlyCollection<Sid> subjects = query.AllUserSids is { Count: > 0 } all ? all : SubjectsOf(query);
        try
        {
            return await _unmaskResolver.ResolveAsync(query.Tenant, subjects, groupsComplete, query.Metadata.Identifier, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return UnmaskCoverage.None;
        }
    }

    private static List<Sid> SubjectsOf(TableAccessQuery query)
    {
        var groups = query.GroupSids;
        var subjects = new List<Sid>(groups.Count + 1);
        subjects.AddRange(groups);
        if (!groups.Contains(query.UserSid))
        {
            subjects.Add(query.UserSid);
        }

        return subjects;
    }

    /// <summary>
    /// Attaches the mask function of the column's category (rule lookup by category). A category without a usable rule (unknown,
    /// deleted, unparsable) keeps the column masked and leaves the spec to the type default; it never becomes clear.
    /// </summary>
    private async Task<TableAccessDecision> WithCategoryMaskSpecsAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        List<(string Column, string? DataType, string Category)> masked,
        CancellationToken ct)
    {
        if (_classificationResolver is null || decision.ColumnMasks is not null and not ColumnMaskPolicy)
        {
            return decision;
        }

        ClassificationPolicyCatalog catalog;
        try
        {
            catalog = await _classificationResolver.LoadCatalogAsync(query.Tenant.Value, ct).ConfigureAwait(false);
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

            var spec = _classificationResolver.ResolveCategoryMaskSpec(
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
