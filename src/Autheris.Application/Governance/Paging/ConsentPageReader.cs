namespace Autheris.Application.Governance.Paging;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// WP-SC-10 / WP-SC-16 rule 5: reads one page of consents applying the read-time clamp of legacy individual grants. The repository supplies
/// keyset pages; this reader re-applies activity at read time (so a clamped, no longer active grant never shows up as active) and can
/// restrict the result to grants whose effective expiry was clamped (<c>clamped=true</c> report). Scanning is bounded.
/// </summary>
public static class ConsentPageReader
{
    /// <summary>Upper bound of repository pages scanned for a single request (a filter can skip many rows).</summary>
    public const int MaxScanPages = 20;

    public static async Task<ConsentPage> ReadAsync(
        IConsentRepository repository,
        ConsentPageQuery query,
        bool clampedOnly,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(query);

        var size = query.EffectivePageSize;
        var result = new List<Consent>(size);
        var after = query.AfterId;
        Guid? lastScanned = null;

        for (int scanned = 0; scanned < MaxScanPages; scanned++)
        {
            var page = await repository.GetConsentPageAsync(query with { AfterId = after, PageSize = size }, ct).ConfigureAwait(false);
            foreach (var consent in page.Items)
            {
                lastScanned = consent.Id;
                if (!query.IncludeInactive && !consent.IsActive(now))
                {
                    continue;
                }

                if (clampedOnly && !IsClamped(consent))
                {
                    continue;
                }

                result.Add(consent);
                if (result.Count == size)
                {
                    // More rows may follow; the cursor continues right after the last returned grant.
                    return new ConsentPage(result, consent.Id);
                }
            }

            if (page.NextAfterId is null)
            {
                return new ConsentPage(result, null);
            }

            after = page.NextAfterId;
        }

        // Scan budget exhausted without filling the page: continue from the last scanned row instead of scanning unboundedly.
        return new ConsentPage(result, lastScanned);
    }

    /// <summary>An individual grant whose effective expiry is earlier than the stored one (legacy permanent or over-limit grant).</summary>
    public static bool IsClamped(Consent consent) => consent.EffectiveValidTo < consent.ValidTo;
}
