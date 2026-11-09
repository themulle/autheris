namespace Autheris.Infrastructure.Persistence;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class PostgreSqlGovernanceRepository : GovernanceRepository
{
    public PostgreSqlGovernanceRepository(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        IHostEnvironment environment,
        IKeyVaultSecretProvider secretProvider,
        IAuditChainAnchorStore auditAnchorStore,
        ICallerContextProvider callerContextProvider,
        ILogger<GovernanceRepository>? logger = null,
        IEventBus? eventBus = null,
        IConsentMaterializationGuard? materializationGuard = null)
        : base(
            epochValidationService,
            options,
            environment,
            secretProvider,
            auditAnchorStore,
            callerContextProvider,
            logger,
            eventBus,
            materializationGuard)
    {
    }

    public static class WebhookOutboxSql
    {
        public static string ClaimBatch() => GovernanceRepository.WebhookOutboxPostgreSql.ClaimBatch();
        public static string Enqueue => GovernanceRepository.WebhookOutboxPostgreSql.Enqueue;
        public static string FencedUpdate(string setClause) => GovernanceRepository.WebhookOutboxPostgreSql.FencedUpdate(setClause);
        public static string DeferSubscription => GovernanceRepository.WebhookOutboxPostgreSql.DeferSubscription;
        public static string ListDeadLetters(WebhookOwnerScope scope) => GovernanceRepository.WebhookOutboxPostgreSql.ListDeadLetters(scope);
        public static string CountPending => GovernanceRepository.WebhookOutboxPostgreSql.CountPending;
        public static string Purge => GovernanceRepository.WebhookOutboxPostgreSql.Purge;
    }

    internal static void BindFencedUpdate(DbCommand cmd, WebhookFencedValues v) =>
        GovernanceRepository.WebhookOutboxPostgreSql.BindFencedUpdate(cmd, v);

    public static class ConsentGrantSql
    {
        public static string ActiveRoleConsents(bool withTenant) => GovernanceRepository.ConsentGrantPostgreSql.ActiveRoleConsents(withTenant);
        public static string RevokeTarget => GovernanceRepository.ConsentGrantPostgreSql.RevokeTarget;
    }

    public static class AuditSql
    {
        public static string InsertPurgeCheckpoint => GovernanceRepository.AuditSql.InsertPurgeCheckpointPg;
    }
}

public class SqlServerGovernanceRepository : GovernanceRepository
{
    public SqlServerGovernanceRepository(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        IHostEnvironment environment,
        IKeyVaultSecretProvider secretProvider,
        IAuditChainAnchorStore auditAnchorStore,
        ICallerContextProvider callerContextProvider,
        ILogger<GovernanceRepository>? logger = null,
        IEventBus? eventBus = null,
        IConsentMaterializationGuard? materializationGuard = null)
        : base(
            epochValidationService,
            options,
            environment,
            secretProvider,
            auditAnchorStore,
            callerContextProvider,
            logger,
            eventBus,
            materializationGuard)
    {
    }

    public static class WebhookOutboxSql
    {
        public static string ClaimBatch() => GovernanceRepository.WebhookOutboxSqlServer.ClaimBatch();
        public static string Enqueue => GovernanceRepository.WebhookOutboxSqlServer.Enqueue;
        public static string FencedUpdate(string setClause) => GovernanceRepository.WebhookOutboxSqlServer.FencedUpdate(setClause);
        public static string DeferSubscription => GovernanceRepository.WebhookOutboxSqlServer.DeferSubscription;
        public static string ListDeadLetters(WebhookOwnerScope scope) => GovernanceRepository.WebhookOutboxSqlServer.ListDeadLetters(scope);
        public static string CountPending => GovernanceRepository.WebhookOutboxSqlServer.CountPending;
        public static string Purge => GovernanceRepository.WebhookOutboxSqlServer.Purge;
    }

    internal static void BindFencedUpdate(DbCommand cmd, WebhookFencedValues v) =>
        GovernanceRepository.WebhookOutboxSqlServer.BindFencedUpdate(cmd, v);

    public static class ConsentGrantSql
    {
        public static string ActiveRoleConsents(bool withTenant) => GovernanceRepository.ConsentGrantSqlServer.ActiveRoleConsents(withTenant);
        public static string RevokeTarget => GovernanceRepository.ConsentGrantSqlServer.RevokeTarget;
    }

    public static class AuditSql
    {
        public static string InsertPurgeCheckpoint => GovernanceRepository.AuditSql.InsertPurgeCheckpointSs;
    }
}
