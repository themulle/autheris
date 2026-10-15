namespace Autheris.Application.Sql.Services;

using Autheris.Application.Governance.Contracts;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using TrinoSqlEngine;

/// <summary>Optional collaborators of the governed rewrite pipeline (forwarded to <c>GovernedSqlRewriter</c>).</summary>
public sealed record GovernedSqlRewriteDependencies(
    IPolicyEnforcementService? PolicyEnforcement = null,
    IConsentResolutionService? ConsentResolution = null,
    ITableMetadataRepository? TableRepository = null,
    IClientIpResolver? ClientIpResolver = null,
    IConsentRepository? ConsentRepository = null,
    ISqlEngine? SqlEngine = null,
    GovernancePolicyFactory? PolicyFactory = null,
    ISqlSecurityValidator? SqlSecurityValidator = null,
    IRebacEvaluator? RebacEvaluator = null,
    IConsentCacheService? ConsentCache = null,
    IMandatoryRowFilterResolver? MandatoryFilters = null,
    ISchemaContractManager? ContractManager = null,
    IAccessProfileRepository? AccessProfileRepository = null,
    IMemoryCache? MemoryCache = null,
    TypedPolicySources? TypedPolicies = null);

/// <summary>Optional collaborators of the governed database execution (forwarded to <c>GovernedSqlExecutor</c>).</summary>
public sealed record GovernedSqlExecutionDependencies(
    ISqlConnectionFactory? ConnectionFactory = null,
    IHostEnvironment? Environment = null,
    ITableReadConcurrencyGate? ConcurrencyGate = null,
    IDbSessionContextInitializer? SessionInitializer = null,
    IKeyVaultSecretProvider? SecretProvider = null,
    CompiledSqlAuditDigester? AuditDigester = null);
