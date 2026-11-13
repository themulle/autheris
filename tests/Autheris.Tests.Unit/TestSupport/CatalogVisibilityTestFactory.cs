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
        return repo;
    }

    public static CatalogCaller Caller(ClaimsPrincipal? principal, CatalogChannel channel = CatalogChannel.Rest) =>
        new(principal, principal?.FindFirst("tenant_id") is { } c ? new TenantId(c.Value) : Autheris.Tests.Common.TestTenants.Default, channel);
}
