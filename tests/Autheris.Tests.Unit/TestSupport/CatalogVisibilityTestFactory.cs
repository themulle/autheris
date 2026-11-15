namespace Autheris.Tests.Unit.TestSupport;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// #138 INC-1 (A-1 decision 10): builds the real <see cref="CatalogVisibilityService"/> for tests of the channels. Production code has no
/// default service (a null service answers empty, SEC-CAT138-01); tests that need visibility create it here.
/// </summary>
internal static class CatalogVisibilityTestFactory
{
    public static CatalogVisibilityService Create(
        ITableMetadataRepository? tables,
        IConsentRepository? consents,
        GatewayOptions? options = null,
        ILogger<CatalogVisibilityService>? logger = null,
        Func<IReadOnlyList<TableMetadata>, CallerScope, CancellationToken, Task<IReadOnlyList<TableMetadata>>>? engineOutput = null)
        => new(
            tables,
            consents,
            Options.Create(options ?? new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            logger ?? NullLogger<CatalogVisibilityService>.Instance)
        {
            EngineOutputForTesting = engineOutput
        };

    /// <summary>
    /// Single-row reads (<c>GetTableMetadataAsync</c>, used by <c>FindVisibleAsync</c>) answer from the catalog a test configured for
    /// <c>GetAllTablesAsync</c>. Real repositories do the same; hand-written substitutes configured only the list.
    /// </summary>
    public static ITableMetadataRepository ServeSingleReadsFromCatalog(this ITableMetadataRepository repo)
    {
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(async ci =>
            (await repo.GetAllTablesAsync(ci.Arg<CancellationToken>())).FirstOrDefault(t => t.Identifier == ci.Arg<TableIdentifier>()));
        return repo.ServeCatalogReads();
    }

    public static ITableMetadataRepository ServeCatalogReads(this ITableMetadataRepository repo)
    {
        repo.GetTablesAsync(Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var wanted = ci.Arg<IReadOnlyCollection<TableIdentifier>>().ToHashSet();
                if (wanted.Count == 0)
                {
                    return Array.Empty<TableMetadata>();
                }

                var all = await repo.GetAllTablesAsync(ci.Arg<CancellationToken>()).ConfigureAwait(false);
                return all.Where(t => wanted.Contains(t.Identifier)).ToList();
            });
        repo.GetTablesForRequestAsync(Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<TableIdentifier>>();
                Autheris.Application.Catalog.CatalogTableQuery.ValidateRequestIdentifiers(ids);
                return await repo.GetTablesAsync(ids, ci.Arg<CancellationToken>()).ConfigureAwait(false);
            });
        repo.QueryTablesAsync(Arg.Any<Autheris.Application.Catalog.CatalogTableQuery>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var query = ci.Arg<Autheris.Application.Catalog.CatalogTableQuery>();
                query.EnsureValid(out var domain, out var schema, out var namePrefix, out var after);
                var limit = query.EffectiveLimit;
                var all = await repo.GetAllTablesAsync(ci.Arg<CancellationToken>()).ConfigureAwait(false);
                var matched = all
                    .Where(t => query.IncludeInactive || t.Table.IsActive)
                    .Where(t => domain is null || string.Equals(t.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
                    .Where(t => schema is null || string.Equals(t.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase))
                    .Where(t => namePrefix is null || t.Identifier.TableName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                    .Where(t => after is null || Autheris.Application.Catalog.CatalogTableCursor.Compare(t.Identifier, after.Value) > 0)
                    .OrderBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Identifier.Schema, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Identifier.TableName, StringComparer.OrdinalIgnoreCase)
                    .Take(limit + 1)
                    .ToList();
                var hasMore = matched.Count > limit;
                var items = hasMore ? matched.Take(limit).ToList() : matched;
                var nextAfter = hasMore ? Autheris.Application.Catalog.CatalogTableCursor.Encode(items[^1].Identifier) : null;
                return new Autheris.Application.Catalog.CatalogTablePage(items, nextAfter);
            });
        return repo;
    }

    public static CatalogCaller Caller(ClaimsPrincipal? principal, CatalogChannel channel = CatalogChannel.Rest) =>
        new(principal, principal?.FindFirst("tenant_id") is { } c ? new TenantId(c.Value) : Autheris.Tests.Common.TestTenants.Default, channel);
}
