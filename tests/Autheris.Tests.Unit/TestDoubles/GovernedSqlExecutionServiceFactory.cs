namespace Autheris.Tests.Unit.TestDoubles;

using System;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Pipeline;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;

/// <summary>
/// Test-only factory that keeps the long optional-parameter shape of the former constructor and maps it onto the grouped
/// dependency records of <see cref="GovernedSqlExecutionService"/> (R-4).
/// </summary>
public static class GovernedSqlExecutionServiceFactory
{
    public static GovernedSqlExecutionService Create(
        IOptions<GatewayOptions> options,
        IAuditLogRepository auditLogRepository,
        IProviderExecutionGuard executionGuard,
        IPolicyEnforcementService? policyEnforcement = null,
        IConsentResolutionService? consentResolution = null,
        ITableMetadataRepository? tableRepository = null,
        ISqlConnectionFactory? connectionFactory = null,
        IClientIpResolver? clientIpResolver = null,
        IHostEnvironment? environment = null,
        ILogger<GovernedSqlExecutionService>? logger = null,
        IConsentRepository? consentRepository = null,
        IKeyVaultSecretProvider? secretProvider = null,
        ISqlEngine? sqlEngine = null,
        ISqlSecurityValidator? sqlSecurityValidator = null,
        ITableReadConcurrencyGate? concurrencyGate = null,
        IDbSessionContextInitializer? sessionInitializer = null,
        IRebacEvaluator? rebacEvaluator = null,
        IConsentCacheService? consentCache = null,
        IMandatoryRowFilterResolver? mandatoryFilters = null,
        ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        IMemoryCache? memoryCache = null,
        IFederatedQueryExecutionService? federatedExecutionService = null,
        TypedPolicySources? typedPolicies = null,
        GovernancePolicyFactory? policyFactory = null,
        CompiledSqlAuditDigester? auditDigester = null)
    {
        var rewrite = new GovernedSqlRewriteDependencies(
            policyEnforcement, consentResolution, tableRepository, clientIpResolver, consentRepository, sqlEngine, policyFactory,
            sqlSecurityValidator, rebacEvaluator, consentCache, mandatoryFilters, contractManager, accessProfileRepository,
            memoryCache, typedPolicies);
        var execution = new GovernedSqlExecutionDependencies(connectionFactory, environment, concurrencyGate, sessionInitializer, secretProvider, auditDigester);
        var lazyFederated = federatedExecutionService is null ? null : new Lazy<IFederatedQueryExecutionService>(() => federatedExecutionService);
        return new GovernedSqlExecutionService(options, auditLogRepository, executionGuard, rewrite, execution, lazyFederated, logger);
    }
}
