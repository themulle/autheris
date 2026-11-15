namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Exceptions;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed partial class AccessDecisionPoint
{
    private async ValueTask<TableAccessDecision> DecideWriteAsync(AccessRequest request, CancellationToken ct)
    {
        var subject = request.Subject;
        var metadata = request.Object;
        var table = metadata.Identifier;

        if (!subject.IsAuthenticated)
        {
            return PreconditionDenied(table, "IDENTITY_INCOMPLETE", "Identity", null, "Access denied: write operations require an authenticated caller.");
        }

        if (request.Transport is not (AccessTransport.WebSql or AccessTransport.Discovery))
        {
            return PreconditionDenied(
                table,
                "WRITE_TRANSPORT_NOT_WIRED",
                "Transport",
                request.Transport.ToString(),
                $"Write access on transport '{request.Transport}' is not permitted (dml_transport_not_wired).");
        }

        if (_writeGrantEvaluator == null)
        {
            return TableAccessDecision.Denied(table, $"Write access to table '{table.ToQualifiedName()}' denied by policy (write grant evaluator unavailable).") with
            {
                Reasons = [new DecisionReason(DecisionStep.DefaultDeny, "POLICY_UNAVAILABLE", "WriteGrant", null)],
                EvaluatedByPdp = true
            };
        }

        var writeOp = request.Operation switch
        {
            AccessOperation.Insert => WriteOperation.Insert,
            AccessOperation.Update => WriteOperation.Update,
            AccessOperation.Delete => WriteOperation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };

        var rawWritten = request.Write?.WrittenColumns ?? (IReadOnlyCollection<string>?)request.Columns;
        var writtenColumns = rawWritten is { Count: > 0 }
            ? rawWritten.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (writeOp == WriteOperation.Update
            && metadata.PrimaryKeyColumns.Count > 0
            && writtenColumns.Any(wc => metadata.PrimaryKeyColumns.Contains(wc, StringComparer.OrdinalIgnoreCase)))
        {
            return TableAccessDecision.Denied(table, "Primary key column cannot be modified by an UPDATE statement.") with
            {
                Reasons = [new DecisionReason(DecisionStep.ExplicitDeny, "write_protected_pk_column", "Policy", null)],
                EvaluatedByPdp = true
            };
        }

        var roles = new HashSet<string>(subject.TenantRoles.Concat(subject.ClusterRoles), StringComparer.OrdinalIgnoreCase);
        var clientIp = subject.ClientIp ?? _clientIpResolver?.ResolveClientIp() ?? IPAddress.None;
        var principal = subject.Principal ?? BuildWritePrincipal(subject);
        var query = new TableAccessQuery(
            subject.UserSid,
            subject.TenantId,
            subject.GroupSids,
            roles,
            metadata,
            Claims: subject.Claims,
            RequestedColumns: request.Columns,
            ClientIp: clientIp,
            ObjectKind: FilterObjectKinds.Relation,
            AllUserSids: principal.GetAllUserSids(),
            Principal: principal,
            SupplementalConsents: request.SupplementalConsents);

        if (TableAccessPolicy.CheckSchemaContract(query, _contractManager) is { } contractDenial)
        {
            return contractDenial with
            {
                Reasons = [new DecisionReason(DecisionStep.Precondition, "CONTRACT_DENY", "SchemaContract", null)],
                EvaluatedByPdp = true
            };
        }

        WriteGrantDecision grantDecision;
        try
        {
            grantDecision = await _writeGrantEvaluator.EvaluateAsync(
                new Autheris.Application.Policy.WriteGrants.WriteGrantQuery(principal, subject.TenantId, metadata, writeOp, writtenColumns),
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            grantDecision = WriteGrantDecision.Denied("evaluator_error");
        }

        if (!grantDecision.IsAllowed)
        {
            var denyKind = string.Equals(grantDecision.DenyCode, "no_write_grant", StringComparison.Ordinal)
                ? TableAccessDenialKind.ConsentMissing
                : TableAccessDenialKind.Policy;
            return TableAccessDecision.Denied(table, $"Write access to table '{table.ToQualifiedName()}' is not permitted ({grantDecision.DenyCode ?? "no_write_grant"}).") with
            {
                DenialKind = denyKind,
                Reasons = [new DecisionReason(DecisionStep.DefaultDeny, grantDecision.DenyCode ?? "no_write_grant", "WriteGrant", null)],
                WriteGrant = new TableWriteDecision(
                    grantDecision.RowScope,
                    grantDecision.WritableColumns,
                    grantDecision.MaxRowsPerStatement,
                    grantDecision.GrantIds,
                    grantDecision.DenyCode),
                EvaluatedByPdp = true
            };
        }

        var now = _timeProvider.GetUtcNow();
        var matchingConsents = await LoadMatchingWriteConsentsAsync(principal, subject, table, now, request.SupplementalConsents, ct).ConfigureAwait(false);
        if (matchingConsents.HasUnconditionalDeny)
        {
            return TableAccessDecision.Denied(table, $"Access denied: explicit DENY consent on table '{table.ToQualifiedName()}'.") with
            {
                DenialKind = TableAccessDenialKind.ConsentDeny,
                Reasons = [new DecisionReason(DecisionStep.ExplicitDeny, "CONSENT_DENY", "Consent", null)],
                EvaluatedByPdp = true
            };
        }

        var grantIdSet = grantDecision.GrantIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var (readAccess, readUnconstrained) = ResolveReadColumnAccessForWrite(principal, subject, metadata, roles, matchingConsents);
        var columnAccess = AddFilterableColumnsForWriteOnlyCaller(readAccess, metadata, matchingConsents);
        var decision = TableAccessDecision.Allowed(
            table,
            columnAccess,
            rowFilterSql: null,
            hasUnconstrainedColumnAllow: readUnconstrained);

        if (grantDecision.RowScope == WriteRowScope.OwnRows)
        {
            decision = await ApplyOwnRowsScopeAsync(decision, query, metadata, writeOp, grantIdSet, matchingConsents, ct).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return decision with { EvaluatedByPdp = true };
            }
        }
        else if (matchingConsents.DenyRowFilters.Count > 0)
        {
            decision = await ApplyRowFilterRecipeAsync(decision, query, metadata, [], matchingConsents.DenyRowFilters, ct).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return decision with { EvaluatedByPdp = true };
            }
        }

        if (_rowScopeCompiler is { CanResolveHardPolicies: true } &&
            _mandatoryFilters is NullMandatoryRowFilterResolver or MandatoryRowFilterResolver)
        {
            decision = await TableAccessPolicy.ApplyCompiledHardRowPoliciesAsync(
                decision,
                query,
                _rowScopeCompiler,
                _typedPolicies?.CallerAttributes,
                ct).ConfigureAwait(false);
        }
        else
        {
            decision = await TableAccessPolicy.ApplyMandatoryVirtualFiltersAsync(
                decision,
                query,
                _mandatoryFilters,
                _typedPolicies?.RowPolicies,
                _typedPolicies?.CallerAttributes,
                ct).ConfigureAwait(false);
        }

        if (!decision.IsAllowed)
        {
            return decision with { EvaluatedByPdp = true };
        }

        var authOpt = _options.Value.Plugins?.Auth;
        if (authOpt?.Enabled == true
            && authOpt.AllowAuthorizationHooks
            && _authPluginHost != null
            && _authPluginHost.HasAuthorizationHooks)
        {
            decision = await TableAccessPolicy.ApplyAuthorizationHooksAsync(decision, query, _authPluginHost, ct).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return decision with { EvaluatedByPdp = true };
            }
        }

        var grantRefs = BuildWriteGrantRefs(grantDecision.GrantIds, matchingConsents.MatchingAllows);
        var grantReasons = grantDecision.GrantIds
            .Select(id => new DecisionReason(DecisionStep.Grant, "WRITE_GRANT_ALLOW", "WriteGrant", id))
            .ToList();

        var writableColumns = FilterWritableColumns(grantDecision.WritableColumns, metadata, writeOp);
        if (decision.ColumnAccess.Count > 0 && authOpt?.Enabled == true && _authPluginHost?.HasAuthorizationHooks == true)
        {
            // Preserve hook-denied column restrictions if any hook explicitly narrowed writable columns.
            writableColumns = writableColumns
                .Where(c => !decision.ColumnAccess.TryGetValue(c, out var lvl) || lvl != ColumnAccessLevel.Deny)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (decision.ColumnMasks == null && _typedPolicies?.MaskPolicies != null && !metadata.IsDelegatedSource)
        {
            try
            {
                var typedMasks = _typedPolicies.MaskPolicies.ForDecision(decision, metadata, subject.TenantId);
                if (typedMasks != null)
                {
                    decision = decision with { ColumnMasks = typedMasks };
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException)
            {
                return TableAccessDecision.Denied(table, "Column masking policy invalid for write target.") with
                {
                    EvaluatedByPdp = true
                };
            }
        }

        return decision with
        {
            WriteGrant = new TableWriteDecision(
                grantDecision.RowScope,
                writableColumns,
                grantDecision.MaxRowsPerStatement,
                grantDecision.GrantIds),
            Grants = grantRefs,
            Reasons = grantReasons,
            EvaluatedByPdp = true
        };
    }

    // A caller whose only grants on the table are write grants holds no read-side column rules. Its statements may still address
    // the non-sensitive columns in WHERE; catalog-sensitive columns stay Mask/Deny, so RejectMaskedReads keeps refusing them.
    private static IReadOnlyDictionary<string, ColumnAccessLevel> AddFilterableColumnsForWriteOnlyCaller(
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess,
        TableMetadata metadata,
        LoadedWriteConsents consents)
    {
        bool hasReadSide = consents.AllMatchingDenies.Count > 0
            || consents.MatchingAllows.Any(c => c.WriteRules.Count == 0 || c.ColumnRules.Count > 0);
        if (hasReadSide)
        {
            return columnAccess;
        }

        var result = new Dictionary<string, ColumnAccessLevel>(columnAccess, StringComparer.OrdinalIgnoreCase);
        foreach (var column in metadata.Columns)
        {
            if (!TableAccessDecision.IsCatalogSensitive(column.ColumnName, metadata))
            {
                result[column.ColumnName] = ColumnAccessLevel.Clear;
            }
        }

        return result;
    }

    private (IReadOnlyDictionary<string, ColumnAccessLevel> Access, bool Unconstrained) ResolveReadColumnAccessForWrite(
        ClaimsPrincipal principal,
        SecurityPrincipalContext subject,
        TableMetadata metadata,
        HashSet<string> roles,
        LoadedWriteConsents matchingConsents)
    {
        var none = (Access: (IReadOnlyDictionary<string, ColumnAccessLevel>)new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase), Unconstrained: false);
        if (_resolutionService == null || (matchingConsents.MatchingAllows.Count == 0 && matchingConsents.AllMatchingDenies.Count == 0))
        {
            return none;
        }

        var readRelevant = matchingConsents.MatchingAllows
            .Where(c => c.WriteRules.Count == 0 || c.ColumnRules.Count > 0)
            .Concat(matchingConsents.AllMatchingDenies)
            .ToList();
        if (readRelevant.Count == 0)
        {
            return none;
        }

        var allUserSids = principal.GetAllUserSids();
        var scopes = TokenAccessScope.GetScopeSet(subject.Claims);
        var readDecision = _resolutionService.ResolveAccess(
            subject.UserSid,
            principal.GetGroupSids(),
            roles,
            metadata.Identifier,
            readRelevant,
            metadata.Dialect,
            allUserSids.Any(s => s != subject.UserSid) ? allUserSids : null,
            scopes is { Count: > 0 } ? scopes : null);

        return readDecision is { IsAllowed: true }
            ? (readDecision.ColumnAccess, readDecision.HasUnconstrainedColumnAllow)
            : none;
    }

    private async Task<TableAccessDecision> ApplyOwnRowsScopeAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        TableMetadata metadata,
        WriteOperation writeOp,
        HashSet<string> grantIdSet,
        LoadedWriteConsents matchingConsents,
        CancellationToken ct)
    {
        IReadOnlyList<AccessProfile> profiles;
        try
        {
            profiles = await TableAccessPolicy.ResolveActiveAccessProfilesCoreAsync(
                query,
                _accessProfileCache,
                _accessProfileRepository,
                _memoryCache,
                _options.Value,
                ct).ConfigureAwait(false);
        }
        catch (AccessProfileSourceUnavailableException ex)
        {
            return TableAccessDecision.Denied(metadata.Identifier, $"Access profile source unavailable (fail-closed): {ex.Message}");
        }

        static IReadOnlyList<GrantRowScopeEntry> WriteEntriesFor(Consent consent, WriteOperation op, HashSet<string> ids) =>
            ids.Contains(consent.Id.ToString("D"))
                ? consent.WriteRules
                    .Where(w => w.Operation == op && w.RowScope == WriteRowScope.OwnRows && w.RowFilter is { Count: > 0 })
                    .SelectMany(static w => w.RowFilter!)
                    .ToList()
                : Array.Empty<GrantRowScopeEntry>();

        // Only active Allow grants that carry row scopes/filters OR are read grants contribute to the union RLS scope;
        // unfiltered write-only grants do not widen a constrained read grant when OwnRows applies.
        var rlsAllows = matchingConsents.MatchingAllows
            .Where(c => c.RowFilters.Count > 0 ||
                        c.RowScope.Count > 0 ||
                        c.WriteRules.Count == 0 ||
                        c.ColumnRules.Count > 0 ||
                        WriteEntriesFor(c, writeOp, grantIdSet).Count > 0)
            .ToList();

        bool hasStructuredRowScopes = rlsAllows.Any(c => c.RowScope.Count > 0 || WriteEntriesFor(c, writeOp, grantIdSet).Count > 0);
        if (hasStructuredRowScopes)
        {
            if (_rowScopeCompiler == null)
            {
                return TableAccessDecision.Denied(metadata.Identifier, "Write grant row-scope filters require RowScopeCompiler.") with
                {
                    Reasons = [new DecisionReason(DecisionStep.DefaultDeny, "write_grant_row_scope_unsupported", "WriteGrant", null)]
                };
            }

            if (!metadata.IsDelegatedSource)
            {
                var grantInputs = rlsAllows
                    .Select(c =>
                    {
                        var combinedEntries = new List<GrantRowScopeEntry>(c.RowScope);
                        combinedEntries.AddRange(WriteEntriesFor(c, writeOp, grantIdSet));
                        return new Autheris.Application.Policy.RowScopes.RowScopeGrant(
                            c.Id.ToString("D"),
                            c.GrantSource,
                            combinedEntries,
                            c.RowFilters.Count > 0 ? c.RowFilters : null);
                    })
                    .ToList();
                var denyRowFilters = matchingConsents.DenyRowFilters
                    .Select(static c => c.RowFilters)
                    .Where(static f => f.Count > 0)
                    .ToList();

                var compileRequest = new Autheris.Application.Policy.RowScopes.RowScopeCompileRequest(
                    query.Tenant,
                    query.UserSid,
                    query.GroupSids,
                    metadata,
                    grantInputs,
                    DenyRowFilters: denyRowFilters,
                    ObjectKind: query.ObjectKind,
                    AllUserSids: query.AllUserSids,
                    IncludeHardPolicies: false);

                var outcome = await _rowScopeCompiler.CompileGrantsOnlyAsync(compileRequest, ct).ConfigureAwait(false);
                if (outcome.IsDenied)
                {
                    return TableAccessDecision.Denied(metadata.Identifier, outcome.DenyReason ?? "Denied by write grant row scope.") with
                    {
                        DenialKind = TableAccessDenialKind.Policy,
                        Reasons = [new DecisionReason(outcome.DenyStep ?? DecisionStep.DefaultDeny, outcome.DenyCode ?? "ROW_SCOPE_DENIED", "WriteGrant", null)]
                    };
                }

                if (outcome.Predicate != null)
                {
                    var predicate = outcome.Predicate;
                    if (outcome.CallerAttributes.Count > 0)
                    {
                        (predicate, _) = await TableAccessPolicy.BindVirtualFilterAttributesCoreAsync(
                            predicate,
                            outcome.CallerAttributes,
                            query,
                            _typedPolicies?.CallerAttributes,
                            ct).ConfigureAwait(false);
                    }

                    decision = decision.WithAdditionalRowPolicy(_rowScopeCompiler.BuildRowPolicy(predicate, metadata.Dialect));
                }
            }
        }
        else if (rlsAllows.Count > 0 || matchingConsents.DenyRowFilters.Count > 0)
        {
            decision = await ApplyRowFilterRecipeAsync(decision, query, metadata, rlsAllows, matchingConsents.DenyRowFilters, ct).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return decision;
            }
        }

        if (profiles.Count > 0 && !metadata.IsDelegatedSource)
        {
            var profiled = TableAccessPolicy.ApplyAccessProfilesCore(decision, profiles, metadata, _typedPolicies?.RowPolicies);
            if (!profiled.IsAllowed)
            {
                return profiled;
            }

            decision = profiled with { ColumnAccess = decision.ColumnAccess };
            if (profiles.Any(p => p.RowScope.Count > 0))
            {
                decision = await TableAccessPolicy.ApplyAccessProfileRowScopesAsync(
                    decision,
                    query,
                    profiles,
                    _rowScopeCompiler,
                    _typedPolicies?.CallerAttributes,
                    ct).ConfigureAwait(false);
                if (!decision.IsAllowed)
                {
                    return decision;
                }
            }
        }

        return decision;
    }

    private async Task<TableAccessDecision> ApplyRowFilterRecipeAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        TableMetadata metadata,
        IReadOnlyList<Consent> allowConsents,
        IReadOnlyList<Consent> denyRowFilterConsents,
        CancellationToken ct)
    {
        if (metadata.IsDelegatedSource)
        {
            return decision;
        }

        var sqlBuilder = new RowFilterSqlBuilder(options: _options);
        var rowFilterSql = sqlBuilder.BuildCombinedRowFilter(allowConsents, denyRowFilterConsents, metadata.Dialect);
        if (rowFilterSql is null)
        {
            return decision;
        }

        var recipe = new ConsentRowFilterRecipe(
            allowConsents.Select(static c => c.RowFilters).ToList(),
            denyRowFilterConsents.Select(static c => c.RowFilters).ToList());

        var withRecipe = decision with
        {
            CombinedRowFilterSql = rowFilterSql,
            ConsentFilterRecipe = recipe
        };

        return await TableAccessPolicy.BuildTypedConsentPolicyAsync(
            withRecipe,
            query,
            _typedPolicies?.RowPolicies,
            _typedPolicies?.CallerAttributes,
            ct).ConfigureAwait(false);
    }

    private async Task<LoadedWriteConsents> LoadMatchingWriteConsentsAsync(
        ClaimsPrincipal principal,
        SecurityPrincipalContext subject,
        TableIdentifier table,
        DateTimeOffset now,
        IReadOnlyList<Consent>? supplementalConsents,
        CancellationToken ct)
    {
        if (_consentRepository == null)
        {
            return new LoadedWriteConsents(false, [], [], []);
        }

        var allUserSids = principal.GetAllUserSids();
        var groupSids = principal.GetGroupSids();
        var subjects = allUserSids.Concat(groupSids).Distinct().ToList();
        var loaded = (await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, table, now, subject.TenantId, ct).ConfigureAwait(false))
            .Where(c => c.TenantId == subject.TenantId && c.TableIdentifier == table && c.IsActive(now))
            .ToList();

        if (supplementalConsents is { Count: > 0 })
        {
            foreach (var extra in supplementalConsents)
            {
                if (extra.TenantId == subject.TenantId && extra.TableIdentifier == table && extra.IsActive(now) && loaded.All(e => e.Id != extra.Id))
                {
                    loaded.Add(extra);
                }
            }
        }

        var userRoles = principal.GetUserRoles();
        var matchingAllows = new List<Consent>();
        var denyRowFilters = new List<Consent>();
        var allMatchingDenies = new List<Consent>();
        var hasUnconditionalDeny = false;

        foreach (var consent in loaded)
        {
            if (consent.Effect == ConsentEffect.Allow)
            {
                if (GranteeMatcher.MatchesGrant(consent.GranteeType, consent.GranteeSid, subject.UserSid, groupSids, allUserSids))
                {
                    matchingAllows.Add(consent);
                }
            }
            else if (consent.Effect == ConsentEffect.Deny
                && GranteeMatcher.MatchesRestriction(consent.GranteeType, consent.GranteeSid, consent.RoleName, consent.RoleId, subject.UserSid, groupSids, userRoles, allUserSids))
            {
                allMatchingDenies.Add(consent);
                if (consent.ColumnRules.Count == 0 && consent.RowFilters.Count == 0 && consent.WriteRules.Count == 0)
                {
                    hasUnconditionalDeny = true;
                }
                else if (consent.ColumnRules.Count == 0 && consent.RowFilters.Count > 0)
                {
                    denyRowFilters.Add(consent);
                }
            }
        }

        return new LoadedWriteConsents(hasUnconditionalDeny, matchingAllows, denyRowFilters, allMatchingDenies);
    }

    private static IReadOnlyList<DecisionGrantRef> BuildWriteGrantRefs(IReadOnlyList<string> grantIds, IReadOnlyList<Consent> matchingAllows)
    {
        if (grantIds.Count == 0)
        {
            return Array.Empty<DecisionGrantRef>();
        }

        var byId = matchingAllows.ToDictionary(c => c.Id.ToString("D"), StringComparer.OrdinalIgnoreCase);
        var refs = new List<DecisionGrantRef>(grantIds.Count);
        foreach (var id in grantIds)
        {
            if (byId.TryGetValue(id, out var consent))
            {
                refs.Add(new DecisionGrantRef(id, GrantSourceKind.ConsentRequest, consent.ValidFrom, consent.EffectiveValidTo));
            }
            else
            {
                refs.Add(new DecisionGrantRef(id, GrantSourceKind.ConsentRequest, DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
            }
        }

        return refs;
    }

    private static ClaimsPrincipal BuildWritePrincipal(SecurityPrincipalContext subject)
    {
        var claims = new List<Claim>(subject.GroupSids.Count + 4)
        {
            new(ClaimTypes.PrimarySid, subject.UserSid.Value),
            new(GroupResolutionClaims.StatusClaimType, subject.GroupResolutionStatus == GroupResolutionStatus.Complete
                ? GroupResolutionClaims.CompleteValue
                : GroupResolutionClaims.DegradedValue)
        };

        foreach (var groupSid in subject.GroupSids)
        {
            claims.Add(new Claim(ClaimTypes.GroupSid, groupSid.Value));
        }

        if (subject.ResolvedTenant is { Value: { Length: > 0 } resolvedTenant })
        {
            claims.Add(new Claim("tenant_id", resolvedTenant));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "AccessDecisionPoint"));
    }

    private static IReadOnlySet<string> FilterWritableColumns(
        IReadOnlySet<string> grantedColumns,
        TableMetadata metadata,
        WriteOperation writeOp)
    {
        if (grantedColumns.Count == 0)
        {
            return grantedColumns;
        }

        var protectedCols = metadata.Columns
            .Where(static c => c.WriteProtected)
            .Select(static c => c.ColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (writeOp == WriteOperation.Update && metadata.PrimaryKeyColumns.Count > 0)
        {
            foreach (var pk in metadata.PrimaryKeyColumns)
            {
                protectedCols.Add(pk);
            }
        }

        if (protectedCols.Count == 0)
        {
            return grantedColumns;
        }

        return grantedColumns
            .Where(c => !protectedCols.Contains(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record LoadedWriteConsents(
        bool HasUnconditionalDeny,
        IReadOnlyList<Consent> MatchingAllows,
        IReadOnlyList<Consent> DenyRowFilters,
        IReadOnlyList<Consent> AllMatchingDenies);
}
