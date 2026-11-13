namespace Autheris.Application.Policy.WriteGrants;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;

public sealed class WriteGrantEvaluator : IWriteGrantEvaluator
{

    private readonly IConsentRepository _consentRepository;
    private readonly IRowPolicyDependencyIndex? _policyDependencyIndex;
    private readonly TimeProvider _timeProvider;
    private readonly bool _allowDeleteGrants;

    public WriteGrantEvaluator(
        IConsentRepository consentRepository,
        IRowPolicyDependencyIndex? policyDependencyIndex = null,
        TimeProvider? timeProvider = null,
        bool allowDeleteGrants = false)
    {
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _policyDependencyIndex = policyDependencyIndex;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _allowDeleteGrants = allowDeleteGrants;
    }

    public async Task<WriteGrantDecision> EvaluateAsync(WriteGrantQuery query, CancellationToken ct = default)
    {
        try
        {
            return await EvaluateInternalAsync(query, ct).ConfigureAwait(false);
        }
        catch
        {
            return WriteGrantDecision.Denied("evaluator_error");
        }
    }

    private async Task<WriteGrantDecision> EvaluateInternalAsync(WriteGrantQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.User);
        ArgumentNullException.ThrowIfNull(query.Table);

        if (IsGroupResolutionDegraded(query.User))
        {
            return WriteGrantDecision.Denied("identity_groups_unresolved");
        }

        if (query.Operation == WriteOperation.Delete && !_allowDeleteGrants)
        {
            return WriteGrantDecision.Denied("delete_grants_disabled");
        }

        if (_policyDependencyIndex != null && _policyDependencyIndex.IsPolicyDependency(query.Tenant, query.Table.Identifier))
        {
            return WriteGrantDecision.Denied("write_grant_policy_dependency");
        }

        if (query.WrittenColumns is { Count: > 0 } &&
            query.Table.Columns.Any(c => c.WriteProtected && query.WrittenColumns.Any(wc => string.Equals(wc, c.ColumnName, StringComparison.OrdinalIgnoreCase))))
        {
            return WriteGrantDecision.Denied("write_protected_column");
        }

        var userSid = query.User.GetUserSid();
        if (userSid is null)
        {
            return WriteGrantDecision.Denied("no_write_grant");
        }

        var allUserSids = query.User.GetAllUserSids();
        var groupSids = query.User.GetGroupSids();

        var allSubjects = new List<Sid>(groupSids.Count + 1);
        allSubjects.AddRange(groupSids);
        allSubjects.Add(userSid.Value);

        var now = _timeProvider.GetUtcNow();

        var loaded = await _consentRepository.GetActiveConsentsForSubjectsAsync(
            allSubjects, query.Table.Identifier, now, query.Tenant, ct).ConfigureAwait(false);

        var matchingConsents = new List<Consent>();
        foreach (var consent in loaded)
        {
            if (consent.TenantId == query.Tenant &&
                consent.TableIdentifier.Equals(query.Table.Identifier) &&
                consent.IsActive(now) &&
                GranteeMatcher.MatchesGrant(
                    consent.GranteeType,
                    consent.GranteeSid,
                    userSid.Value,
                    groupSids,
                    allUserSids))
            {
                matchingConsents.Add(consent);
            }
        }

        var opRulesWithGrant = new List<(Consent Grant, WriteRule Rule)>();
        foreach (var consent in matchingConsents)
        {
            if (consent.WriteRules is { Count: > 0 })
            {
                foreach (var rule in consent.WriteRules)
                {
                    if (rule.Operation == query.Operation)
                    {
                        opRulesWithGrant.Add((consent, rule));
                    }
                }
            }
        }

        if (opRulesWithGrant.Count == 0)
        {
            return WriteGrantDecision.Denied("no_write_grant");
        }

        if (query.Operation == WriteOperation.Delete)
        {
            var deleteScope = opRulesWithGrant.Any(r => r.Rule.RowScope == WriteRowScope.AllRows)
                ? WriteRowScope.AllRows
                : WriteRowScope.OwnRows;

            var deleteCaps = opRulesWithGrant
                .Where(r => r.Rule.MaxRowsPerStatement.HasValue)
                .Select(r => r.Rule.MaxRowsPerStatement!.Value)
                .ToList();
            long? maxRows = deleteCaps.Count > 0 ? deleteCaps.Min() : null;

            var grantIds = opRulesWithGrant.Select(r => r.Grant.Id.ToString()).Distinct().ToList();

            return WriteGrantDecision.Allowed(
                deleteScope,
                ImmutableHashSet<string>.Empty,
                maxRows,
                grantIds);
        }

        var allWritableColumns = opRulesWithGrant
            .SelectMany(r => r.Rule.Columns)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var contributingRulesWithGrant = new List<(Consent Grant, WriteRule Rule)>();
        var columnScopes = new List<WriteRowScope>();

        if (query.WrittenColumns is { Count: > 0 })
        {
            foreach (var col in query.WrittenColumns)
            {
                var covering = opRulesWithGrant
                    .Where(r => r.Rule.Columns.Contains(col, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (covering.Count == 0)
                {
                    return WriteGrantDecision.Denied("unauthorized_column");
                }

                var colScope = covering.Any(r => r.Rule.RowScope == WriteRowScope.AllRows)
                    ? WriteRowScope.AllRows
                    : WriteRowScope.OwnRows;

                columnScopes.Add(colScope);
                contributingRulesWithGrant.AddRange(covering);
            }
        }
        else
        {
            contributingRulesWithGrant.AddRange(opRulesWithGrant);
            var scope = opRulesWithGrant.All(r => r.Rule.RowScope == WriteRowScope.AllRows)
                ? WriteRowScope.AllRows
                : WriteRowScope.OwnRows;
            columnScopes.Add(scope);
        }

        var statementScope = columnScopes.Contains(WriteRowScope.OwnRows)
            ? WriteRowScope.OwnRows
            : WriteRowScope.AllRows;

        var caps = contributingRulesWithGrant
            .Where(r => r.Rule.MaxRowsPerStatement.HasValue)
            .Select(r => r.Rule.MaxRowsPerStatement!.Value)
            .ToList();
        long? statementMaxRows = caps.Count > 0 ? caps.Min() : null;

        var statementGrantIds = contributingRulesWithGrant
            .Select(r => r.Grant.Id.ToString())
            .Distinct()
            .ToList();

        return WriteGrantDecision.Allowed(
            statementScope,
            allWritableColumns,
            statementMaxRows,
            statementGrantIds);
    }

    private static bool IsGroupResolutionDegraded(ClaimsPrincipal user)
    {
        return GroupResolutionClaims.IsMarkedDegraded(user) ||
               user.HasClaim(c => c.Type == GroupResolutionClaims.StatusClaimType && c.Value == GroupResolutionClaims.DegradedValue);
    }
}
