namespace Autheris.Application.Identity.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Products;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Resolves tenant-effective internal employee group SIDs from <see cref="IAccessPolicySettingsProvider"/>,
/// falling back to deployment <see cref="AccessPolicyOptions"/> when no settings provider is wired.
/// </summary>
public sealed class AccessPolicyEmployeeGroupLookup : IEmployeeGroupLookup
{
    private readonly IAccessPolicySettingsProvider? _settingsProvider;
    private readonly IOptions<AccessPolicyOptions>? _fallbackOptions;

    public AccessPolicyEmployeeGroupLookup(
        IAccessPolicySettingsProvider? settingsProvider = null,
        IOptions<AccessPolicyOptions>? fallbackOptions = null)
    {
        _settingsProvider = settingsProvider;
        _fallbackOptions = fallbackOptions;
    }

    public IReadOnlyCollection<string> GetEmployeeGroupSids(TenantId tenantId) => GetFallbackSids();

    public async ValueTask<IReadOnlyCollection<string>> GetEmployeeGroupSidsAsync(
        TenantId tenantId,
        CancellationToken ct = default)
    {
        if (_settingsProvider is null || string.IsNullOrWhiteSpace(tenantId.Value))
        {
            return GetFallbackSids();
        }

        var policy = await _settingsProvider.GetEffectiveAsync(tenantId, ct).ConfigureAwait(false);
        return policy.Internal.EmployeeGroupSids;
    }

    private IReadOnlyCollection<string> GetFallbackSids() =>
        _fallbackOptions?.Value.Internal.EmployeeGroupSids is { Count: > 0 } sids
            ? sids
            : [];
}

internal static class PrivilegedGroupLookupHelper
{
    private const int MaxDomainPages = 200;
    private const int ProductPageSize = 200;
    private const int MaxProductPages = 200;

    public static HashSet<string> BuildCandidateKeys(
        string realmId,
        string groupId,
        string? groupObjectSid,
        string? previousGroupObjectSid)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            keys.Add(groupId.Trim());
            if (!string.IsNullOrWhiteSpace(realmId) && SidFormat.TryFormatRealmGroupKey(realmId, groupId, out var realmKey))
            {
                keys.Add(realmKey);
            }
        }

        if (!string.IsNullOrWhiteSpace(groupObjectSid))
        {
            keys.Add(groupObjectSid.Trim());
        }

        if (!string.IsNullOrWhiteSpace(previousGroupObjectSid))
        {
            keys.Add(previousGroupObjectSid.Trim());
        }

        return keys;
    }

    public static async Task<IReadOnlyList<string>> FindMatchingDomainOwnerGroupSidsAsync(
        TenantId tenantId,
        IReadOnlySet<string> candidates,
        IGovernanceDomainRepository domainRepository,
        bool stopOnFirst,
        CancellationToken ct)
    {
        List<string>? matches = null;
        var offset = 0;
        for (var pageIndex = 0; pageIndex < MaxDomainPages; pageIndex++)
        {
            var domains = await domainRepository.ListAsync(
                tenantId, IGovernanceDomainRepository.MaxPageSize, offset, ct).ConfigureAwait(false);
            if (domains.Count == 0)
            {
                break;
            }

            foreach (var domain in domains)
            {
                if (domain.OwnerGroupSid is { Value: var ownerSid } && candidates.Contains(ownerSid))
                {
                    if (stopOnFirst)
                    {
                        return [ownerSid];
                    }

                    matches ??= [];
                    matches.Add(ownerSid);
                }
            }

            if (domains.Count < IGovernanceDomainRepository.MaxPageSize)
            {
                break;
            }

            offset += domains.Count;
        }

        return (IReadOnlyList<string>?)matches ?? [];
    }

    public static async Task<IReadOnlyList<string>> FindMatchingProductOwnerGroupSidsAsync(
        TenantId tenantId,
        IReadOnlySet<string> candidates,
        IDataProductRepository productRepository,
        bool stopOnFirst,
        CancellationToken ct)
    {
        List<string>? matches = null;
        string? afterKey = null;
        for (var pageIndex = 0; pageIndex < MaxProductPages; pageIndex++)
        {
            var products = await productRepository.ListProductsAsync(
                tenantId, afterKey, ProductPageSize, ct).ConfigureAwait(false);
            if (products.Count == 0)
            {
                break;
            }

            foreach (var product in products)
            {
                if (product.OwnerGroupSid is { Value: var ownerSid } && candidates.Contains(ownerSid))
                {
                    if (stopOnFirst)
                    {
                        return [ownerSid];
                    }

                    matches ??= [];
                    matches.Add(ownerSid);
                }
            }

            var last = products[^1];
            var nextKey = $"{last.Schema}/{last.Name}";
            if (products.Count < ProductPageSize || string.Equals(nextKey, afterKey, StringComparison.Ordinal))
            {
                break;
            }

            afterKey = nextKey;
        }

        return (IReadOnlyList<string>?)matches ?? [];
    }

    public static async Task<IReadOnlyList<string>> FindMatchingEmployeeGroupSidsAsync(
        TenantId tenantId,
        IReadOnlySet<string> candidates,
        IEmployeeGroupLookup employeeGroupLookup,
        IEnumerable<string>? configuredEmployeeGroupSids,
        bool stopOnFirst,
        CancellationToken ct)
    {
        List<string>? matches = null;
        var dynamicSids = await employeeGroupLookup.GetEmployeeGroupSidsAsync(tenantId, ct).ConfigureAwait(false);
        foreach (var sid in dynamicSids)
        {
            if (string.IsNullOrWhiteSpace(sid))
            {
                continue;
            }

            var trimmed = sid.Trim();
            if (candidates.Contains(trimmed))
            {
                if (stopOnFirst)
                {
                    return [trimmed];
                }

                matches ??= [];
                matches.Add(trimmed);
            }
        }

        if (configuredEmployeeGroupSids is not null)
        {
            foreach (var sid in configuredEmployeeGroupSids)
            {
                if (candidates.Contains(sid))
                {
                    if (stopOnFirst)
                    {
                        return [sid];
                    }

                    matches ??= [];
                    matches.Add(sid);
                }
            }
        }

        return (IReadOnlyList<string>?)matches ?? [];
    }
}
