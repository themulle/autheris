namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed partial class DeclarativeConsentApplyService
{
    private static string ComputeManifestSha256(DeclarativeConsentManifest manifest)
    {
        var utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        return Convert.ToHexStringLower(SHA256.HashData(utf8Bytes));
    }

    private static async Task<GranteeLockLease> AcquireGranteeLocksAsync(string[] lockKeys, CancellationToken ct)
    {
        if (lockKeys.Length == 0)
        {
            return GranteeLockLease.Empty;
        }

        var acquired = new List<(string Key, KeyedLockEntry Entry)>(lockKeys.Length);
        try
        {
            foreach (var key in lockKeys)
            {
                KeyedLockEntry entry;
                lock (GranteeLocksGate)
                {
                    if (!GranteeLocks.TryGetValue(key, out entry!))
                    {
                        entry = new KeyedLockEntry();
                        GranteeLocks[key] = entry;
                    }

                    entry.RefCount++;
                }

                try
                {
                    await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
                    acquired.Add((key, entry));
                }
                catch
                {
                    ReleaseLockEntry(key, entry, releaseSemaphore: false);
                    throw;
                }
            }

            return new GranteeLockLease(acquired);
        }
        catch
        {
            await new GranteeLockLease(acquired).DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ReleaseLockEntry(string key, KeyedLockEntry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }

        KeyedLockEntry? toDispose = null;
        lock (GranteeLocksGate)
        {
            entry.RefCount--;
            if (entry.RefCount <= 0 && GranteeLocks.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                GranteeLocks.Remove(key);
                toDispose = entry;
            }
        }

        toDispose?.Dispose();
    }

    private sealed class GranteeLockLease(List<(string Key, KeyedLockEntry Entry)> entries) : IAsyncDisposable
    {
        public static readonly GranteeLockLease Empty = new([]);

        public ValueTask DisposeAsync()
        {
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var (key, entry) = entries[i];
                ReleaseLockEntry(key, entry, releaseSemaphore: true);
            }

            entries.Clear();
            return ValueTask.CompletedTask;
        }
    }

    // A Clear column rule exposes the column in clear text: the grant is classified like an Unmasked one.
    private static bool HasClearColumnRule(DeclarativeConsentPolicy policy) =>
        policy.ColumnRules?.Any(r => ParseAccessLevel(r.AccessLevel) == ColumnAccessLevel.Clear) == true;

    private static bool MatchesGrantee(Consent consent, GranteeType type, Sid? sid)
    {
        if (consent.GranteeType != type) return false;
        return consent.GranteeSid == sid;
    }

    /// <summary>
    /// Issue #806: true only when <paramref name="existing"/> equals what the create path would store for <paramref name="policy"/>,
    /// so an unchanged manifest writes nothing and every real difference takes the revoke/create path. Column rules and row filters are compared
    /// as multisets independent of order; strings ordinally, because the create path stores them verbatim.
    /// </summary>
    /// <param name="desiredValidTo">
    /// The validUntil of the policy. An EARLIER expiry than the stored one is a change (the manifest shortens the grant); a later or equal one is not
    /// (a re-apply never extends a grant: an individual grant needs a renewal with its own approval).
    /// </param>
    private static bool IsIdentical(Consent existing, ConsentEffect desiredEffect, DeclarativeConsentPolicy policy, DateTimeOffset desiredValidTo)
    {
        if (existing.Effect != desiredEffect || desiredValidTo < existing.ValidTo)
        {
            return false;
        }

        // The manifest creates none of these, so an existing consent that carries them grants or restricts differently.
        if (existing.WriteRules is { Count: > 0 } || existing.RowScope is { Count: > 0 } || existing.ClassificationAccess is { Count: > 0 })
        {
            return false;
        }

        var desiredColumnRules = policy.ColumnRules ?? [];
        var desiredRowFilters = policy.RowFilters ?? [];
        return existing.ColumnRules.Count == desiredColumnRules.Count
            && existing.RowFilters.Count == desiredRowFilters.Count
            && SameMultiset(existing.ColumnRules.Select(ColumnRuleKey.Of), desiredColumnRules.Select(ColumnRuleKey.Of), existing.ColumnRules.Count)
            && SameMultiset(existing.RowFilters.Select(RowFilterKey.Of), desiredRowFilters.Select(RowFilterKey.Of), existing.RowFilters.Count);
    }

    /// <summary>Both sequences hold <paramref name="count"/> keys; true when every key occurs equally often in both.</summary>
    private static bool SameMultiset<TKey>(IEnumerable<TKey> existing, IEnumerable<TKey> desired, int count)
        where TKey : notnull
    {
        if (count == 0)
        {
            return true;
        }

        var remaining = new Dictionary<TKey, int>(count);
        foreach (var key in existing)
        {
            remaining[key] = remaining.GetValueOrDefault(key) + 1;
        }

        foreach (var key in desired)
        {
            if (!remaining.TryGetValue(key, out var left) || left == 0)
            {
                return false;
            }

            remaining[key] = left - 1;
        }

        return true;
    }

    /// <summary>The access level of a manifest column rule as stored: parsed by name (ignoring case); unknown or undefined values are <c>Deny</c>.</summary>
    private static ColumnAccessLevel ParseAccessLevel(string? accessLevel) =>
        Enum.TryParse<ColumnAccessLevel>(accessLevel, ignoreCase: true, out var level) && level is ColumnAccessLevel.Deny or ColumnAccessLevel.Mask or ColumnAccessLevel.Clear
            ? level
            : ColumnAccessLevel.Deny;

    /// <summary>
    /// The scopes of a column rule in their stored form (the repository writes them comma-joined and reads them back split, trimmed and without
    /// empty entries), de-duplicated and ordinally sorted: the rule applies when the caller holds any of them, so order and duplicates do not matter.
    /// </summary>
    private static string CanonicalScopes(IReadOnlyList<string?>? scopes)
    {
        if (scopes is not { Count: > 0 })
        {
            return string.Empty;
        }

        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            foreach (var part in (scope ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                set.Add(part);
            }
        }

        return string.Join(',', set);
    }

    private readonly record struct ColumnRuleKey(string Column, ColumnAccessLevel Level, string Scopes)
    {
        public static ColumnRuleKey Of(ConsentColumnRule rule) => new(rule.ColumnName, rule.AccessLevel, CanonicalScopes(rule.ApplicableScopes));

        public static ColumnRuleKey Of(DeclarativeColumnRule rule) => new(rule.ColumnName, ParseAccessLevel(rule.AccessLevel), CanonicalScopes(rule.ApplicableScopes));
    }

    private readonly record struct RowFilterKey(
        int FilterGroup,
        RowFilterType FilterType,
        string Column,
        string Operator,
        string ValueType,
        string ValueJson,
        string ValueSource,
        string? UserAttribute)
    {
        private const string DefaultValueSource = "LITERAL";

        public static RowFilterKey Of(ConsentRowFilter filter) => new(
            filter.FilterGroup, filter.FilterType, filter.ColumnName, filter.Operator, filter.ValueType, filter.ValueJson, filter.ValueSource ?? DefaultValueSource, filter.UserAttribute);

        // The create path writes every manifest filter into group 0 as a simple column predicate.
        public static RowFilterKey Of(DeclarativeRowFilter filter) => new(
            0, RowFilterType.SimpleColumnPredicate, filter.ColumnName, filter.Operator, filter.ValueType, filter.ValueJson, filter.ValueSource ?? DefaultValueSource, filter.UserAttribute);
    }

    private void AuthorizeCaller(ClaimsPrincipal caller, DeclarativeConsentManifest manifest, out Sid actorSid, out TenantId tenantId)
    {
        if (caller.Identity?.IsAuthenticated != true)
        {
            throw new ConsentApplyForbiddenException();
        }

        var isCanonicalClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(caller);
        try
        {
            tenantId = caller.GetTenantId();
        }
        catch (TenantUnresolvedException) when (isCanonicalClusterAdmin)
        {
            tenantId = TenantId.Platform;
        }
        catch (GatewaySecurityException)
        {
            throw new ConsentApplyForbiddenException();
        }

        if (!_roleEvaluator.HasAnyRole(caller, RequiredApplyRoles, tenantId.Value))
        {
            throw new ConsentApplyForbiddenException();
        }

        RejectForeignManifestTenant(manifest, tenantId);

        actorSid = caller.GetUserSid()
            ?? throw new System.Security.SecurityException("Caller must carry a user SID to apply declarative consent manifests.");
    }

    private static void RejectForeignManifestTenant(DeclarativeConsentManifest manifest, TenantId callerTenant)
    {
        if (!MatchesCallerTenant(manifest.TenantId, callerTenant))
        {
            throw new ConsentApplyForbiddenException();
        }

        if (manifest.Policies is not null)
        {
            foreach (var policy in manifest.Policies)
            {
                if (!MatchesCallerTenant(policy.TenantId, callerTenant))
                {
                    throw new ConsentApplyForbiddenException();
                }
            }
        }

        if (manifest.Datasets is not null)
        {
            foreach (var dataset in manifest.Datasets)
            {
                if (!MatchesCallerTenant(dataset.TenantId, callerTenant))
                {
                    throw new ConsentApplyForbiddenException();
                }
            }
        }
    }

    private static bool MatchesCallerTenant(string? declaredTenant, TenantId callerTenant)
    {
        if (string.IsNullOrWhiteSpace(declaredTenant))
        {
            return true;
        }

        return TenantId.TryParse(declaredTenant.Trim(), out var parsedTenant)
            && string.Equals(parsedTenant.Value, callerTenant.Value, StringComparison.OrdinalIgnoreCase);
    }
}
