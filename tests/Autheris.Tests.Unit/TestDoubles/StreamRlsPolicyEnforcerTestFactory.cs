namespace Autheris.Tests.Unit.TestDoubles;

using System;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Streaming.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static class StreamRlsPolicyEnforcerTestFactory
{
    public static StreamRlsPolicyEnforcer Create(
        IAccessDecisionPoint decisionPoint,
        ITableMetadataRepository metadataRepository,
        IColumnMaskingProvider maskingProvider,
        IEpochValidationService epochValidationService,
        IConsentRepository consentRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        ILogger<StreamRlsPolicyEnforcer>? logger = null,
        IClientIpResolver? clientIpResolver = null,
        IOptions<GatewayOptions>? options = null,
        IMandatoryRowFilterResolver? mandatoryFilters = null,
        ICallerAttributeResolver? callerAttributes = null,
        TimeProvider? time = null,
        IDataSourceTransportGate? transportGate = null) =>
        new(
            decisionPoint,
            metadataRepository,
            maskingProvider,
            epochValidationService,
            logger ?? NullLogger<StreamRlsPolicyEnforcer>.Instance,
            consentRepository,
            resolutionService,
            cacheService,
            clientIpResolver ?? NullClientIpResolver.Instance,
            options ?? Options.Create(new GatewayOptions()),
            mandatoryFilters ?? NullMandatoryRowFilterResolver.Instance,
            callerAttributes ?? DisabledCallerAttributeResolver.Instance,
            time ?? TimeProvider.System,
            transportGate ?? AllowAllTransportGate.Instance);
}
