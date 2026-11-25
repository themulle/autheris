namespace Autheris.Application.Policy.Classification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Protection;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>
/// Resolved effective access and winning mask candidate for a single column under a single grant.
/// </summary>
public sealed record ColumnClassificationGrantOutcome(
    ColumnAccessLevel AccessLevel,
    MaskFunction? WinningMaskFunction,
    string? WinningClassificationCode,
    MaskSpec? MaskSpec,
    string? ReasonCode,
    CatalogTextVisibility CatalogText,
    int WinningRestrictionRank = 0,
    string? KeyRef = null,
    IReadOnlyList<string>? ClassificationCodes = null);

/// <summary>
/// Immutable tenant-effective classification and mask-rule catalog snapshot for a policy decision.
/// </summary>
public sealed record ClassificationPolicyCatalog(
    string TenantScope,
    IReadOnlyDictionary<string, DataClassification> Definitions,
    IReadOnlyDictionary<string, ClassificationMaskRule> MaskRules);

/// <summary>
/// Resolves per-grant and multi-grant column access and winning typed masks from column classifications, hierarchy mask
/// inheritance, and per-column overrides. The taxonomy only decides how a column is masked (#1009 A-3): every column carrying a
/// classification other than <c>PUBLIC</c> is masked unless an active unmask grant covers it; <c>CREDENTIALS</c> is never clear.
/// </summary>
public sealed partial class ClassificationAccessResolver
{
    private const int MaxHierarchyDepth = 16;

    private readonly SqlDataMaskingProvider _maskProvider;
    private readonly IClassificationRepository? _repository;
    private readonly TimeProvider _timeProvider;

    internal IClassificationRepository? Repository => _repository;

    public ClassificationAccessResolver(
        SqlDataMaskingProvider maskProvider,
        IClassificationRepository? repository = null,
        TimeProvider? timeProvider = null)
    {
        _maskProvider = maskProvider ?? throw new ArgumentNullException(nameof(maskProvider));
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// True when a classification masks its column on default access: every classification except <c>PUBLIC</c> does.
    /// </summary>
    public static bool IsMaskingClassification(string? code) =>
        !string.Equals(code, ClassificationScopes.PublicCode, StringComparison.Ordinal);

    public bool HasRepository => _repository is not null;

    public async ValueTask<ClassificationPolicyCatalog> LoadCatalogAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        if (_repository is null)
        {
            return BuiltInCatalog(tenantId);
        }

        var defs = await _repository.GetClassificationsAsync(tenantId, includeInactive: true, ct).ConfigureAwait(false);
        var rules = await _repository.GetClassificationMaskRulesAsync(tenantId, ct).ConfigureAwait(false);
        return BuildCatalog(tenantId, defs, rules);
    }

    public static ClassificationPolicyCatalog BuiltInCatalog(string tenantId)
    {
        var epoch = DateTimeOffset.UnixEpoch;
        DataClassification Def(
            string code,
            string display,
            string? parent,
            short rank) => new()
        {
            Scope = ClassificationScopes.Global,
            Code = code,
            DisplayName = display,
            ParentCode = parent,
            RestrictionRank = rank,
            IsActive = true,
            Version = 1,
            UpdatedBy = "built-in",
            UpdatedAt = epoch
        };

        ClassificationMaskRule Rule(string code) => new()
        {
            Scope = ClassificationScopes.Global,
            Code = code,
            MaskFunction = MaskFunction.TypeSafeRedact,
            ParamsJson = "{\"constant\":\"***\"}",
            CatalogText = CatalogTextVisibility.Visible,
            Version = 1,
            UpdatedBy = "built-in",
            UpdatedAt = epoch
        };

        DataClassification[] defs =
        [
            Def(ClassificationScopes.PublicCode, "Public", null, 0),
            Def(ClassificationScopes.ConfidentialCode, "Confidential", null, 20),
            Def(ClassificationScopes.FinancialCode, "Financial", null, 30),
            Def(ClassificationScopes.PiiCode, "Personal Data (PII)", null, 40),
            Def(ClassificationScopes.PiiSpecialCode, "Special Category PII", ClassificationScopes.PiiCode, 60),
            Def(ClassificationScopes.CredentialsCode, "Credentials & Secrets", null, 90)
        ];

        ClassificationMaskRule[] rules =
        [
            Rule(ClassificationScopes.ConfidentialCode),
            Rule(ClassificationScopes.FinancialCode),
            Rule(ClassificationScopes.PiiCode),
            Rule(ClassificationScopes.PiiSpecialCode),
            Rule(ClassificationScopes.CredentialsCode),
            Rule(ClassificationScopes.DefaultMaskRuleCode)
        ];

        return BuildCatalog(tenantId, defs, rules);
    }

    public static ClassificationPolicyCatalog EmptyCatalog(string tenantId) =>
        new(
            tenantId,
            new Dictionary<string, DataClassification>(StringComparer.Ordinal),
            new Dictionary<string, ClassificationMaskRule>(StringComparer.Ordinal));

    public static ClassificationPolicyCatalog BuildCatalog(
        string tenantId,
        IReadOnlyList<DataClassification> definitions,
        IReadOnlyList<ClassificationMaskRule> maskRules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(maskRules);

        var effDefs = ResolveScopedMap(definitions, d => d.Scope, d => d.Code, tenantId);
        var effRules = ResolveScopedMap(maskRules, r => r.Scope, r => r.Code, tenantId);
        return new ClassificationPolicyCatalog(tenantId, effDefs, effRules);
    }

    public async ValueTask<(
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, ColumnClassificationGrantOutcome>> PerGrant,
        IReadOnlyDictionary<string, ColumnClassificationGrantOutcome> Combined)> ResolveForTableAsync(
        TableMetadata metadata,
        IReadOnlyList<Consent> allowGrants,
        TenantId tenant,
        IReadOnlySet<string>? callerScopes,
        ClassificationPolicyCatalog? preloadedCatalog = null,
        ColumnPolicyRowPrefetch? columnRowPrefetch = null,
        UnmaskCoverageSource? unmask = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(allowGrants);

        var catalog = preloadedCatalog
            ?? columnRowPrefetch?.CatalogFor(tenant.Value, _timeProvider.GetUtcNow())
            ?? await LoadCatalogAsync(tenant.Value, ct).ConfigureAwait(false);
        var dialect = PolicyParameterNamer.ToTargetDialect(metadata.Dialect);

        var colClassifications = await LoadEffectiveColumnClassificationsAsync(metadata, tenant.Value, columnRowPrefetch, ct).ConfigureAwait(false);
        var colOverrides = await LoadEffectiveColumnOverridesAsync(metadata, tenant.Value, columnRowPrefetch, ct).ConfigureAwait(false);

        // Without a coverage source nothing is unmasked (fail closed); the coverage is only resolved when a classified column exists.
        var coverage = unmask is not null && allowGrants.Count > 0 && colClassifications.Count > 0
            ? await unmask.GetAsync(ct).ConfigureAwait(false)
            : UnmaskCoverage.None;

        var perGrant = new Dictionary<Guid, IReadOnlyDictionary<string, ColumnClassificationGrantOutcome>>();
        var combinedWinners = new Dictionary<string, ColumnClassificationGrantOutcome>(StringComparer.OrdinalIgnoreCase);

        foreach (var grant in allowGrants)
        {
            var perCol = new Dictionary<string, ColumnClassificationGrantOutcome>(StringComparer.OrdinalIgnoreCase);

            foreach (var col in metadata.Columns)
            {
                if (!colClassifications.TryGetValue(col.ColumnName, out var classifications) || classifications.Count == 0)
                {
                    continue;
                }

                var columnLevel = ConsentResolutionService.ResolveConsentColumnLevel(grant, col.ColumnName, callerScopes);
                colOverrides.TryGetValue(col.ColumnName, out var overrides);

                var outcome = ResolveColumnForGrant(
                    grant,
                    col.ColumnName,
                    col.DataType,
                    columnLevel,
                    classifications,
                    overrides ?? Array.Empty<ColumnMaskOverride>(),
                    catalog,
                    tenant,
                    dialect,
                    unmasked: coverage.Covers(col.ColumnName));

                perCol[col.ColumnName] = outcome;

                if (outcome.AccessLevel != ColumnAccessLevel.Clear)
                {
                    if (!combinedWinners.TryGetValue(col.ColumnName, out var current))
                    {
                        combinedWinners[col.ColumnName] = outcome;
                    }
                    else
                    {
                        combinedWinners[col.ColumnName] = PickStricterOutcome(current, outcome);
                    }
                }
            }

            perGrant[grant.Id] = perCol;
        }

        return (perGrant, combinedWinners);
    }

    /// <summary>
    /// Resolves one column under one grant. A column classification other than <c>PUBLIC</c> masks the column unless
    /// <paramref name="unmasked"/> (an active unmask grant covers the column); <c>CREDENTIALS</c> stays masked even then. An unknown
    /// or inactive classification denies the column (fail closed).
    /// </summary>
    public ColumnClassificationGrantOutcome ResolveColumnForGrant(
        Consent grant,
        string columnName,
        string? dataType,
        ColumnAccessLevel grantColumnSetLevel,
        IReadOnlyList<ColumnClassification> columnClassifications,
        IReadOnlyList<ColumnMaskOverride> columnOverrides,
        ClassificationPolicyCatalog catalog,
        TenantId tenant,
        TargetSqlDialect dialect,
        bool unmasked = false)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentNullException.ThrowIfNull(columnClassifications);
        ArgumentNullException.ThrowIfNull(columnOverrides);
        ArgumentNullException.ThrowIfNull(catalog);

        if (grantColumnSetLevel == ColumnAccessLevel.Deny)
        {
            return new ColumnClassificationGrantOutcome(
                ColumnAccessLevel.Deny, null, null, null, "COLUMN_DENIED", CatalogTextVisibility.Visible);
        }

        var activeOnColumn = DeduplicateScopedClassifications(columnClassifications, tenant.Value);
        if (activeOnColumn.Count == 0)
        {
            return new ColumnClassificationGrantOutcome(
                grantColumnSetLevel, null, null, null, null, CatalogTextVisibility.Visible);
        }

        var defs = new List<DataClassification>(activeOnColumn.Count);
        foreach (var cc in activeOnColumn)
        {
            if (!ClassificationHierarchyValidator.IsValidClassificationCode(cc.Code) ||
                !catalog.Definitions.TryGetValue(cc.Code, out var def) ||
                !def.IsActive)
            {
                return new ColumnClassificationGrantOutcome(
                    ColumnAccessLevel.Deny, null, cc.Code, null, "CLASSIFICATION_UNKNOWN_OR_INACTIVE", CatalogTextVisibility.Hidden);
            }

            defs.Add(def);
        }

        var maskDefs = new List<DataClassification>(defs.Count);
        var credentials = false;
        foreach (var def in defs)
        {
            if (string.Equals(def.Code, ClassificationScopes.CredentialsCode, StringComparison.Ordinal))
            {
                credentials = true;
            }

            if (IsMaskingClassification(def.Code))
            {
                maskDefs.Add(def);
            }
        }

        var canBeClear = grantColumnSetLevel == ColumnAccessLevel.Clear &&
                         (maskDefs.Count == 0 || (unmasked && !credentials));
        if (canBeClear)
        {
            return new ColumnClassificationGrantOutcome(
                ColumnAccessLevel.Clear, null, null, null, null, CatalogTextVisibility.Visible);
        }

        return ResolveMaskOutcome(
            maskDefs.Count > 0 ? maskDefs : defs,
            defs.Select(static d => d.Code).ToList(),
            columnName,
            dataType,
            columnOverrides,
            catalog,
            tenant,
            dialect);
    }

    private ColumnClassificationGrantOutcome ResolveMaskOutcome(
        IReadOnlyList<DataClassification> defs,
        IReadOnlyList<string> allColumnCodes,
        string columnName,
        string? dataType,
        IReadOnlyList<ColumnMaskOverride> columnOverrides,
        ClassificationPolicyCatalog catalog,
        TenantId tenant,
        TargetSqlDialect dialect)
    {
        var (keyedOverrides, unkeyedOverride) = ResolveEffectiveOverrides(columnOverrides, tenant.Value);

        var candidates = new List<MaskCandidate>(defs.Count);
        var catalogText = CatalogTextVisibility.Visible;
        bool hasRestrictedClass = defs.Any(static d => IsMaskingClassification(d.Code));

        foreach (var def in defs)
        {
            var candidate = ResolveCandidateForClassification(
                def, keyedOverrides, unkeyedOverride, catalog, hasRestrictedClass);
            if (candidate is null)
            {
                if (HasHiddenCatalogTextRule(def, keyedOverrides, unkeyedOverride, catalog))
                {
                    catalogText = CatalogTextVisibility.Hidden;
                }

                continue;
            }

            if (candidate.CatalogText == CatalogTextVisibility.Hidden)
            {
                catalogText = CatalogTextVisibility.Hidden;
            }

            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
        {
            var fallback = ResolveDefaultFallbackCandidate(defs[0], catalog);
            catalogText = fallback.CatalogText;
            candidates.Add(fallback);
        }

        var winner = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            if (IsMoreRestrictive(candidates[i], winner))
            {
                winner = candidates[i];
            }
        }

        var resolution = _maskProvider.ResolveClassificationMask(
            winner.Function,
            winner.ParamsJson,
            columnName,
            dataType,
            dialect,
            tenant,
            winner.KeyRef);

        if (resolution.IsDenied || resolution.Spec is null)
        {
            return new ColumnClassificationGrantOutcome(
                ColumnAccessLevel.Deny,
                winner.Function,
                winner.ClassificationCode,
                null,
                resolution.DenialReason ?? "MASK_RESOLUTION_FAILED",
                catalogText,
                winner.RestrictionRank,
                KeyRef: null,
                ClassificationCodes: allColumnCodes);
        }

        string? auditKeyRef = resolution.EffectiveFunction is MaskFunction.Hash or MaskFunction.Tokenize
            ? winner.KeyRef
            : null;
        return new ColumnClassificationGrantOutcome(
            ColumnAccessLevel.Mask,
            resolution.EffectiveFunction,
            winner.ClassificationCode,
            resolution.Spec,
            null,
            catalogText,
            winner.RestrictionRank,
            KeyRef: auditKeyRef,
            ClassificationCodes: allColumnCodes);
    }

    private static MaskCandidate? ResolveCandidateForClassification(
        DataClassification def,
        IReadOnlyDictionary<string, ColumnMaskOverride> keyedOverrides,
        ColumnMaskOverride? unkeyedOverride,
        ClassificationPolicyCatalog catalog,
        bool hasRestrictedClass)
    {
        if (keyedOverrides.TryGetValue(def.Code, out var keyed))
        {
            return new MaskCandidate(
                def.Code, def.RestrictionRank, keyed.MaskFunction, keyed.ParamsJson, keyed.KeyRef, keyed.CatalogText);
        }

        if (unkeyedOverride is not null)
        {
            return new MaskCandidate(
                def.Code, def.RestrictionRank, unkeyedOverride.MaskFunction, unkeyedOverride.ParamsJson, unkeyedOverride.KeyRef, unkeyedOverride.CatalogText);
        }

        if (TryResolveRuleFromHierarchy(def.Code, catalog, out var rule))
        {
            return new MaskCandidate(
                def.Code, def.RestrictionRank, rule.MaskFunction, rule.ParamsJson, rule.KeyRef, rule.CatalogText);
        }

        if (!IsMaskingClassification(def.Code) && hasRestrictedClass)
        {
            return null;
        }

        return ResolveDefaultFallbackCandidate(def, catalog);
    }

    private static bool HasHiddenCatalogTextRule(
        DataClassification def,
        IReadOnlyDictionary<string, ColumnMaskOverride> keyedOverrides,
        ColumnMaskOverride? unkeyedOverride,
        ClassificationPolicyCatalog catalog)
    {
        if (keyedOverrides.TryGetValue(def.Code, out var keyed))
            return keyed.CatalogText == CatalogTextVisibility.Hidden;
        if (unkeyedOverride is not null)
            return unkeyedOverride.CatalogText == CatalogTextVisibility.Hidden;
        return TryResolveRuleFromHierarchy(def.Code, catalog, out var rule)
            ? rule.CatalogText == CatalogTextVisibility.Hidden
            : ResolveDefaultFallbackCandidate(def, catalog).CatalogText == CatalogTextVisibility.Hidden;
    }

    private static bool TryResolveRuleFromHierarchy(
        string startCode,
        ClassificationPolicyCatalog catalog,
        out ClassificationMaskRule rule)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? current = startCode;
        for (int depth = 0; depth < MaxHierarchyDepth && current is not null; depth++)
        {
            if (!visited.Add(current))
            {
                break;
            }

            if (catalog.MaskRules.TryGetValue(current, out var found))
            {
                rule = found;
                return true;
            }

            if (!catalog.Definitions.TryGetValue(current, out var def) || !def.IsActive)
            {
                break;
            }

            current = def.ParentCode;
        }

        rule = null!;
        return false;
    }

    private static MaskCandidate ResolveDefaultFallbackCandidate(DataClassification def, ClassificationPolicyCatalog catalog)
    {
        if (catalog.MaskRules.TryGetValue(ClassificationScopes.DefaultMaskRuleCode, out var defRule))
        {
            return new MaskCandidate(
                def.Code, def.RestrictionRank, defRule.MaskFunction, defRule.ParamsJson, defRule.KeyRef, defRule.CatalogText);
        }

        return new MaskCandidate(
            def.Code, def.RestrictionRank, MaskFunction.TypeSafeRedact, "{\"constant\":\"***\"}", null, CatalogTextVisibility.Visible);
    }

    private static bool IsMoreRestrictive(MaskCandidate candidate, MaskCandidate current)
    {
        int candFnRank = ClassificationMaskFunctions.GetRestrictivenessRank(candidate.Function);
        int curFnRank = ClassificationMaskFunctions.GetRestrictivenessRank(current.Function);
        if (candFnRank != curFnRank)
        {
            return candFnRank > curFnRank;
        }

        if (candidate.RestrictionRank != current.RestrictionRank)
        {
            return candidate.RestrictionRank > current.RestrictionRank;
        }

        return string.CompareOrdinal(candidate.ClassificationCode, current.ClassificationCode) < 0;
    }

    private static ColumnClassificationGrantOutcome PickStricterOutcome(
        ColumnClassificationGrantOutcome current,
        ColumnClassificationGrantOutcome candidate)
    {
        var mergedText = current.CatalogText == CatalogTextVisibility.Hidden || candidate.CatalogText == CatalogTextVisibility.Hidden
            ? CatalogTextVisibility.Hidden
            : CatalogTextVisibility.Visible;
        var winner = PickStricterOutcomeCore(current, candidate);
        return winner.CatalogText == mergedText ? winner : winner with { CatalogText = mergedText };
    }

    private static ColumnClassificationGrantOutcome PickStricterOutcomeCore(
        ColumnClassificationGrantOutcome current,
        ColumnClassificationGrantOutcome candidate)
    {
        if (current.AccessLevel == ColumnAccessLevel.Deny)
        {
            return current;
        }

        if (candidate.AccessLevel == ColumnAccessLevel.Deny)
        {
            return candidate;
        }

        if (candidate.WinningMaskFunction is null)
        {
            return current;
        }

        if (current.WinningMaskFunction is null)
        {
            return candidate;
        }

        int curRank = ClassificationMaskFunctions.GetRestrictivenessRank(current.WinningMaskFunction.Value);
        int candRank = ClassificationMaskFunctions.GetRestrictivenessRank(candidate.WinningMaskFunction.Value);
        if (candRank != curRank)
        {
            return candRank > curRank ? candidate : current;
        }

        if (candidate.WinningRestrictionRank != current.WinningRestrictionRank)
        {
            return candidate.WinningRestrictionRank > current.WinningRestrictionRank ? candidate : current;
        }

        return string.CompareOrdinal(candidate.WinningClassificationCode, current.WinningClassificationCode) < 0
            ? candidate
            : current;
    }

    private async ValueTask<Dictionary<string, IReadOnlyList<ColumnClassification>>> LoadEffectiveColumnClassificationsAsync(
        TableMetadata metadata,
        string tenantId,
        ColumnPolicyRowPrefetch? prefetch,
        CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase);
        var persistedByColumn = _repository is null
            ? null
            : await LoadPerTableAsync(
                metadata,
                tenantId,
                prefetch,
                static (p, id) => p.ClassificationsOf(id),
                (ids, token) => _repository.GetColumnClassificationsBatchAsync(ids, tenantId, token),
                ct).ConfigureAwait(false);
        foreach (var col in metadata.Columns)
        {
            var merged = new List<ColumnClassification>();
            if (metadata.ColumnClassifications.TryGetValue(col.ColumnName, out var metaList))
            {
                merged.AddRange(metaList);
            }

            if (persistedByColumn is not null && col.Id != Guid.Empty && persistedByColumn.TryGetValue(col.Id.ToString(), out var persisted))
            {
                merged.AddRange(persisted);
            }

            if (merged.Count > 0)
            {
                result[col.ColumnName] = merged;
            }
        }

        return result;
    }

    private async ValueTask<Dictionary<string, IReadOnlyList<ColumnMaskOverride>>> LoadEffectiveColumnOverridesAsync(
        TableMetadata metadata,
        string tenantId,
        ColumnPolicyRowPrefetch? prefetch,
        CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<ColumnMaskOverride>>(StringComparer.OrdinalIgnoreCase);
        var persistedByColumn = _repository is null
            ? null
            : await LoadPerTableAsync(
                metadata,
                tenantId,
                prefetch,
                static (p, id) => p.OverridesOf(id),
                (ids, token) => _repository.GetColumnMaskOverridesBatchAsync(ids, tenantId, token),
                ct).ConfigureAwait(false);
        foreach (var col in metadata.Columns)
        {
            var merged = new List<ColumnMaskOverride>();
            if (metadata.ColumnMaskOverrides.TryGetValue(col.ColumnName, out var metaList))
            {
                merged.AddRange(metaList);
            }

            if (persistedByColumn is not null && col.Id != Guid.Empty && persistedByColumn.TryGetValue(col.Id.ToString(), out var persisted))
            {
                merged.AddRange(persisted);
            }

            if (merged.Count > 0)
            {
                result[col.ColumnName] = merged;
            }
        }

        return result;
    }

    private static IReadOnlyList<ColumnClassification> DeduplicateScopedClassifications(IReadOnlyList<ColumnClassification> list, string tenantId) =>
        ClassificationMetadata.DeduplicateScoped(list, tenantId);

    private static (Dictionary<string, ColumnMaskOverride> Keyed, ColumnMaskOverride? Unkeyed) ResolveEffectiveOverrides(
        IReadOnlyList<ColumnMaskOverride> overrides,
        string tenantId)
    {
        var keyed = new Dictionary<string, ColumnMaskOverride>(StringComparer.Ordinal);
        ColumnMaskOverride? unkeyed = null;

        foreach (var ov in overrides)
        {
            if (!ClassificationScopes.IsGlobal(ov.Scope))
            {
                continue;
            }

            if (ov.Code is null)
            {
                unkeyed = ov;
            }
            else
            {
                keyed[ov.Code] = ov;
            }
        }

        foreach (var ov in overrides)
        {
            if (!string.Equals(ov.Scope, tenantId, StringComparison.Ordinal))
            {
                continue;
            }

            if (ov.Code is null)
            {
                unkeyed = ov;
            }
            else
            {
                keyed[ov.Code] = ov;
            }
        }

        return (keyed, unkeyed);
    }

    private static Dictionary<string, T> ResolveScopedMap<T>(
        IReadOnlyList<T> items,
        Func<T, string> scopeSelector,
        Func<T, string> keySelector,
        string tenantId)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (ClassificationScopes.IsGlobal(scopeSelector(item)))
            {
                map[keySelector(item)] = item;
            }
        }

        foreach (var item in items)
        {
            if (string.Equals(scopeSelector(item), tenantId, StringComparison.Ordinal))
            {
                map[keySelector(item)] = item;
            }
        }

        return map;
    }

    private sealed record MaskCandidate(
        string ClassificationCode,
        int RestrictionRank,
        MaskFunction Function,
        string? ParamsJson,
        string? KeyRef,
        CatalogTextVisibility CatalogText);

    public static Task<IReadOnlySet<string>> ResolveIndexingHiddenTextColumnsAsync(
        TableMetadata metadata,
        IClassificationRepository? repository,
        CancellationToken ct = default)
        => new IndexingHiddenTextResolver(repository).ResolveAsync(metadata, ct);

    /// <summary>
    /// Resolves the columns whose catalog text is HIDDEN for table rows, loading the classification definitions and mask rules once per
    /// tenant scope; create one per request or rebuild pass and reuse it across tables.
    /// </summary>
    public sealed class IndexingHiddenTextResolver(IClassificationRepository? repository)
    {
        private readonly Dictionary<string, ClassificationPolicyCatalog[]> _catalogsByScope = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PrefetchedRows> _prefetchedByScope = new(StringComparer.Ordinal);

        private sealed record PrefetchedRows(
            HashSet<string> Covered,
            IReadOnlyDictionary<string, IReadOnlyList<ColumnMaskOverride>> Overrides,
            IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>> Classifications);

        /// <summary>
        /// Issue #1043: reads the mask overrides and classifications of every (not discovery-hidden) column of <paramref name="tables"/> in
        /// set-based statements per tenant scope, for a rebuild pass over many tables. <see cref="ResolveAsync"/> then answers covered tables
        /// from memory with the same rows the per-table reads return.
        /// </summary>
        public async Task PrefetchAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(tables);
            if (repository is null)
            {
                return;
            }

            var idsByScope = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var table in tables)
            {
                var scopeKey = string.IsNullOrWhiteSpace(table.OwnerTenantId) ? string.Empty : table.OwnerTenantId;
                if (!idsByScope.TryGetValue(scopeKey, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    idsByScope[scopeKey] = ids;
                }

                foreach (var col in table.Columns)
                {
                    if (!col.DiscoveryHidden && col.Id != Guid.Empty) ids.Add(col.Id.ToString());
                }
            }

            foreach (var (scopeKey, ids) in idsByScope)
            {
                if (ids.Count == 0) continue;
                var tenantScope = scopeKey.Length == 0 ? null : scopeKey;
                var overrides = await repository.GetColumnMaskOverridesBatchAsync(ids, tenantScope, ct).ConfigureAwait(false);
                var classes = await repository.GetColumnClassificationsBatchAsync(ids, tenantScope, ct).ConfigureAwait(false);
                _prefetchedByScope[scopeKey] = new PrefetchedRows(ids, overrides, classes);
            }
        }

        public async Task<IReadOnlySet<string>> ResolveAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            var tenantScope = string.IsNullOrWhiteSpace(metadata.OwnerTenantId) ? null : metadata.OwnerTenantId;
            if (!_catalogsByScope.TryGetValue(tenantScope ?? string.Empty, out var catalogs))
            {
                catalogs = await LoadCatalogsAsync(tenantScope, ct).ConfigureAwait(false);
                _catalogsByScope[tenantScope ?? string.Empty] = catalogs;
            }

            return await ResolveColumnsAsync(metadata, tenantScope, catalogs, ct).ConfigureAwait(false);
        }

        private async Task<ClassificationPolicyCatalog[]> LoadCatalogsAsync(string? tenantScope, CancellationToken ct)
        {
            IReadOnlyList<DataClassification> defs = Array.Empty<DataClassification>();
            IReadOnlyList<ClassificationMaskRule> rules = Array.Empty<ClassificationMaskRule>();
            if (repository is not null)
            {
                defs = await repository.GetClassificationsAsync(tenantScope, includeInactive: true, ct).ConfigureAwait(false);
                rules = await repository.GetClassificationMaskRulesAsync(tenantScope, ct).ConfigureAwait(false);
            }

        var defMap = new Dictionary<string, DataClassification>(StringComparer.Ordinal);
        if (defs.Count > 0)
        {
            var tenantDefs = new List<DataClassification>();
            foreach (var d in defs)
            {
                if (ClassificationScopes.IsGlobal(d.Scope)) defMap[d.Code] = d;
                else if (tenantScope is not null && string.Equals(d.Scope, tenantScope, StringComparison.Ordinal)) tenantDefs.Add(d);
            }
            foreach (var d in tenantDefs) defMap[d.Code] = d;
        }
        var builtIn = BuiltInCatalog(tenantScope ?? ClassificationScopes.Global);
        var catalogDefs = new Dictionary<string, DataClassification>(builtIn.Definitions, StringComparer.Ordinal);
        foreach (var kv in defMap) catalogDefs[kv.Key] = kv.Value;

        var globalRules = new Dictionary<string, ClassificationMaskRule>(builtIn.MaskRules, StringComparer.Ordinal);
        var tenantRules = new Dictionary<string, ClassificationMaskRule>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            if (ClassificationScopes.IsGlobal(r.Scope)) globalRules[r.Code] = r;
            else if (tenantScope is not null && string.Equals(r.Scope, tenantScope, StringComparison.Ordinal)) tenantRules[r.Code] = r;
        }
        return new[]
        {
            new ClassificationPolicyCatalog(ClassificationScopes.Global, catalogDefs, globalRules),
            new ClassificationPolicyCatalog(tenantScope ?? ClassificationScopes.Global, catalogDefs, tenantRules)
        };
        }

        private async Task<IReadOnlySet<string>> ResolveColumnsAsync(TableMetadata metadata, string? tenantScope, ClassificationPolicyCatalog[] catalogs, CancellationToken ct)
        {
        HashSet<string>? hidden = null;

        // Issue #1043: the persisted rows of all (not discovery-hidden) columns in one read each, instead of two reads per column.
        IReadOnlyDictionary<string, IReadOnlyList<ColumnMaskOverride>>? persistedOverrides = null;
        IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>>? persistedClasses = null;
        if (repository is not null)
        {
            var ids = new List<string>(metadata.Columns.Count);
            foreach (var col in metadata.Columns)
            {
                if (!col.DiscoveryHidden && col.Id != Guid.Empty) ids.Add(col.Id.ToString());
            }

            if (ids.Count > 0)
            {
                if (_prefetchedByScope.TryGetValue(tenantScope ?? string.Empty, out var prefetched) && ids.TrueForAll(prefetched.Covered.Contains))
                {
                    persistedOverrides = prefetched.Overrides;
                    persistedClasses = prefetched.Classifications;
                }
                else
                {
                    persistedOverrides = await repository.GetColumnMaskOverridesBatchAsync(ids, tenantScope, ct).ConfigureAwait(false);
                    persistedClasses = await repository.GetColumnClassificationsBatchAsync(ids, tenantScope, ct).ConfigureAwait(false);
                }
            }
        }

        foreach (var col in metadata.Columns)
        {
            if (col.DiscoveryHidden) continue;
            var overrides = metadata.ColumnMaskOverrides.TryGetValue(col.ColumnName, out var memOv) ? memOv : Array.Empty<ColumnMaskOverride>();
            if (persistedOverrides is not null && col.Id != Guid.Empty && persistedOverrides.TryGetValue(col.Id.ToString(), out var persistedOv) && persistedOv.Count > 0)
            {
                overrides = overrides.Count == 0 ? persistedOv : [.. overrides, .. persistedOv];
            }
            if (overrides.Any(static o => o.CatalogText == CatalogTextVisibility.Hidden))
            {
                (hidden ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(col.ColumnName);
                continue;
            }

            var classes = metadata.ColumnClassifications.TryGetValue(col.ColumnName, out var memCc) ? memCc : Array.Empty<ColumnClassification>();
            if (persistedClasses is not null && col.Id != Guid.Empty && persistedClasses.TryGetValue(col.Id.ToString(), out var persistedCc) && persistedCc.Count > 0)
            {
                classes = classes.Count == 0 ? persistedCc : [.. classes, .. persistedCc];
            }
            if (classes.Any(cc => catalogs.Any(cat => TryResolveRuleFromHierarchy(cc.Code, cat, out var r) && r.CatalogText == CatalogTextVisibility.Hidden)))
            {
                (hidden ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(col.ColumnName);
            }
        }
        return hidden ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
