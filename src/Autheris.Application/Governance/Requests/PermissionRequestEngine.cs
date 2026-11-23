using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Notifications;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Products;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Governance.Requests;

/// <summary>
/// Unified permission request and single-decision engine covering Data, Unmask, Catalog, and ProfileAssignment requests.
/// All decisions commit CAS state update, grant effect materialization, and fail-closed audit in a single transaction.
/// </summary>
public sealed partial class PermissionRequestEngine : IPermissionRequestEngine
{
    private readonly IConsentApprovalRepository _approvalRepository;
    private readonly IAuditWriter _auditWriter;
    private readonly ApproverResolver _approverResolver;
    private readonly ApproverEligibility _approverEligibility;
    private readonly ISeparationOfDutiesService _sodService;
    private readonly IGrantPolicy _grantPolicy;
    private readonly IGrantValidityPolicy _validityPolicy;
    private readonly IReadOnlyDictionary<ConsentRequestKind, IGrantEffectApplier> _appliers;
    private readonly ITableMetadataRepository? _metadataRepository;
    private readonly IGovernanceDomainRepository? _domainRepository;
    private readonly IDataProductRepository? _productRepository;
    private readonly IProtectionSnapshotProvider? _protectionProvider;
    private readonly IAccessPolicySettingsProvider? _accessPolicyProvider;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly ICatalogVisibilityGrantRepository? _catalogVisibilityRepository;
    private readonly IConsentWorkflowConfigRepository? _workflowConfigRepository;
    private readonly IIdentitySubjectResolver? _subjectResolver;
    private readonly IRateLimiterService? _rateLimiter;
    private readonly IGovernanceNotificationDispatcher? _notificationDispatcher;
    private readonly IGovernanceCapabilities? _capabilities;
    private readonly IGovernanceCacheInvalidator? _cacheInvalidator;
    private readonly IPolicyEpochRepository? _policyEpochRepository;
    private readonly GatewayOptions? _gatewayOptions;
    private readonly CatalogDiscoveryOptions? _discoveryOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<PermissionRequestEngine>? _logger;

    public PermissionRequestEngine(
        IConsentApprovalRepository approvalRepository,
        IAuditWriter auditWriter,
        ApproverResolver approverResolver,
        ApproverEligibility approverEligibility,
        ISeparationOfDutiesService sodService,
        IGrantPolicy grantPolicy,
        IGrantValidityPolicy validityPolicy,
        IEnumerable<IGrantEffectApplier> appliers,
        ITableMetadataRepository? metadataRepository = null,
        IGovernanceDomainRepository? domainRepository = null,
        IDataProductRepository? productRepository = null,
        IProtectionSnapshotProvider? protectionProvider = null,
        IAccessPolicySettingsProvider? accessPolicyProvider = null,
        IAccessProfileRepository? accessProfileRepository = null,
        ICatalogVisibilityGrantRepository? catalogVisibilityRepository = null,
        IConsentWorkflowConfigRepository? workflowConfigRepository = null,
        IIdentitySubjectResolver? subjectResolver = null,
        IRateLimiterService? rateLimiter = null,
        IGovernanceNotificationDispatcher? notificationDispatcher = null,
        IGovernanceCapabilities? capabilities = null,
        IGovernanceCacheInvalidator? cacheInvalidator = null,
        IPolicyEpochRepository? policyEpochRepository = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        IOptions<CatalogDiscoveryOptions>? discoveryOptions = null,
        TimeProvider? timeProvider = null,
        ILogger<PermissionRequestEngine>? logger = null)
    {
        _approvalRepository = approvalRepository ?? throw new ArgumentNullException(nameof(approvalRepository));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _approverResolver = approverResolver ?? throw new ArgumentNullException(nameof(approverResolver));
        _approverEligibility = approverEligibility ?? throw new ArgumentNullException(nameof(approverEligibility));
        _sodService = sodService ?? throw new ArgumentNullException(nameof(sodService));
        _grantPolicy = grantPolicy ?? throw new ArgumentNullException(nameof(grantPolicy));
        _validityPolicy = validityPolicy ?? throw new ArgumentNullException(nameof(validityPolicy));
        ArgumentNullException.ThrowIfNull(appliers);
        _appliers = appliers.ToDictionary(a => a.RequestKind);
        _metadataRepository = metadataRepository;
        _domainRepository = domainRepository;
        _productRepository = productRepository;
        _protectionProvider = protectionProvider;
        _accessPolicyProvider = accessPolicyProvider;
        _accessProfileRepository = accessProfileRepository;
        _catalogVisibilityRepository = catalogVisibilityRepository;
        _workflowConfigRepository = workflowConfigRepository;
        _subjectResolver = subjectResolver;
        _rateLimiter = rateLimiter;
        _notificationDispatcher = notificationDispatcher;
        _capabilities = capabilities;
        _cacheInvalidator = cacheInvalidator;
        _policyEpochRepository = policyEpochRepository;
        _gatewayOptions = gatewayOptions?.Value;
        _discoveryOptions = discoveryOptions?.Value;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    private static PermissionGrantKind MapGrantKind(ConsentRequestKind kind) => kind switch
    {
        ConsentRequestKind.Unmask => PermissionGrantKind.Unmask,
        ConsentRequestKind.Catalog => PermissionGrantKind.Catalog,
        _ => PermissionGrantKind.Data
    };

    private static GrantKind MapPolicyGrantKind(ConsentRequestKind kind) => kind switch
    {
        ConsentRequestKind.ProfileAssignment => GrantKind.AccessProfileAssignment,
        _ => GrantKind.Consent
    };

    private static string FormatTargetObject(ConsentRequest req)
    {
        if (req.RequestKind == ConsentRequestKind.Catalog && req.ContainerKind.HasValue && req.ContainerId.HasValue)
        {
            return $"{req.ContainerKind.Value.ToSqlString()}:{req.ContainerId.Value:D}";
        }

        if (req.RequestKind == ConsentRequestKind.ProfileAssignment && req.ProfileId.HasValue)
        {
            return $"PROFILE:{req.ProfileId.Value:D}";
        }

        return !string.IsNullOrWhiteSpace(req.TableIdentifier.TableName)
            ? req.TableIdentifier.ToString()
            : req.TableId.ToString("D");
    }

    private static string GetTraceId() => Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
}
