namespace Autheris.Tests.Unit.TestSupport;

using Autheris.Application.Catalog.Services;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>
/// Integration-side counterpart of the Unit factory with the same name, built only through the public
/// <see cref="CatalogVisibilityService"/> constructor (the Application assembly exposes no internals to Integration).
/// </summary>
internal static class CatalogVisibilityTestFactory
{
    public static CatalogVisibilityService Create(
        ITableMetadataRepository? tables,
        IConsentRepository? consents,
        GatewayOptions? options = null,
        IRebacEvaluator? rebac = null,
        ILogger<CatalogVisibilityService>? logger = null)
        => new(
            tables,
            consents,
            Options.Create(options ?? new GatewayOptions { Tenancy = TenancyShape.Multi.Options(), Rebac = new RebacOptions { Enabled = false } }),
            rebac ?? DisabledRebac(),
            logger ?? NullLogger<CatalogVisibilityService>.Instance);

    public static IRebacEvaluator DisabledRebac()
    {
        var evaluator = Substitute.For<IRebacEvaluator>();
        evaluator.IsEnabled.Returns(false);
        return evaluator;
    }
}
