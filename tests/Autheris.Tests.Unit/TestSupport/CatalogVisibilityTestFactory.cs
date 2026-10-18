namespace Autheris.Tests.Unit.TestSupport;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
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
        IRebacEvaluator? rebac = null,
        ILogger<CatalogVisibilityService>? logger = null,
        Func<IReadOnlyList<TableMetadata>, CallerScope, CancellationToken, Task<IReadOnlyList<TableMetadata>>>? engineOutput = null)
        => new(
            tables,
            consents,
            Options.Create(options ?? new GatewayOptions { Tenancy = new TenancyOptions { Mode = nameof(TenancyMode.Multi) }, Rebac = new RebacOptions { Enabled = false } }),
            rebac,
            logger ?? NullLogger<CatalogVisibilityService>.Instance)
        {
            EngineOutputForTesting = engineOutput
        };

    public static CatalogCaller Caller(ClaimsPrincipal? principal, CatalogChannel channel = CatalogChannel.Rest) =>
        new(principal, principal?.FindFirst("tenant_id") is { } c ? new TenantId(c.Value) : TenantId.LegacySingleTenant, channel);
}
