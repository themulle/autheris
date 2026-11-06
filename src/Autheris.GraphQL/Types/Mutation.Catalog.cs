using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Autheris.GraphQL.Types;

public sealed partial class Mutation
{
    public async Task<bool> ReloadSchemaAsync(
        [Service] IEventBus eventBus = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] Autheris.Application.Common.Interfaces.IAuditLogRepository auditLog = default!,
        [Service] Autheris.Application.Audit.IAuditWriter? auditWriter = null,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required.")
                .Build());
        }

        // GQL-08: the reload is a cluster-wide side effect, so a tenant-scoped GovernanceAdmin may not trigger it.
        if (!Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Only cluster administrators may reload the schema.")
                .Build());
        }

        // GQL-09: publish on the channel the catalog snapshot / GraphQL type module actually listen to ("schema:reload" had no subscriber).
        await eventBus.PublishAsync(
            Autheris.Application.Catalog.ICatalogVersionProvider.CatalogChangedEventChannel,
            new Autheris.Application.Catalog.CatalogChangedEvent(0, 0, DateTimeOffset.UtcNow),
            ct);

        // Review E-10: schema reloads change what the gateway exposes and belong in the audit chain.
        TenantId reloadTenant;
        try
        {
            reloadTenant = principal.GetTenantId();
        }
        catch (Autheris.Domain.Exceptions.GatewaySecurityException)
        {
            reloadTenant = TenantId.Platform; // cluster admins may have no tenant: audit-only platform record (DT-A-6)
        }

        var entry = new AuditLogEntry
        {
            TenantId = reloadTenant,
            EventType = "SCHEMA_RELOAD",
            ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "ALLOW",
            TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = "{}"
        };

        if (auditWriter != null)
        {
            await auditWriter.RecordRequiredAsync(entry, ct);
        }
        else
        {
            await auditLog.RecordAuditEventAsync(entry, ct);
        }

        return true;
    }

    public async Task<OpenMetadataSyncPayload> SyncOpenMetadataAsync(
        bool dryRun = false,
        [Service] IOpenMetadataSyncService syncService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required.")
                .Build());
        }

        // SR-L-23: only the canonical ClusterAdmin (claims-based, never ForwardAuth) or a GovernanceAdmin may synchronise.
        var roles = principal.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isClusterAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        if (!isClusterAdmin && !roles.Contains("GovernanceAdmin"))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Only governance or cluster administrators may synchronize OpenMetadata.")
                .Build());
        }

        // Non-cluster administrators are confined to their own tenant (fail-closed on an invalid tenant claim).
        TenantId? restrictToTenant = null;
        if (!isClusterAdmin)
        {
            try
            {
                restrictToTenant = principal.GetTenantId();
            }
            catch (Autheris.Domain.Exceptions.GatewaySecurityException)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("UNAUTHORIZED")
                    .SetMessage("No valid tenant in the authentication token.")
                    .Build());
            }
        }

        OpenMetadataSyncResult result;
        try
        {
            result = await syncService.SyncPermissionsAsync(dryRun, restrictToTenant, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GraphQLException)
        {
            // Full detail stays in the server log (the sync service logs it); the caller gets a fixed code only.
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("SYNC_FAILED")
                .SetMessage("OpenMetadata synchronization failed.")
                .Build());
        }

        return new OpenMetadataSyncPayload
        {
            Success = result.Success,
            SyncedTables = result.SyncedTables,
            SyncedConsents = result.SyncedConsents,
            SyncedMaskingRules = result.SyncedMaskingRules,
            Warnings = result.SanitizedWarnings.ToList()
        };
    }

    public async Task<DataCatalogSyncPayload> SyncDataCatalogAsync(
        bool dryRun = false,
        [Service] IDataCatalogSyncService catalogSyncService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] Autheris.Application.Audit.IAuditWriter? auditWriter = null,
        [Service] Autheris.Application.Common.Interfaces.IAuditLogRepository? auditLog = null,
        [Service] ILogger<Mutation>? logger = null,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required.")
                .Build());
        }

        var roles = principal.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isClusterAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        if (!isClusterAdmin && !roles.Contains("GovernanceAdmin"))
        {
            TenantId denyTenant;
            try
            {
                denyTenant = principal.GetTenantId();
            }
            catch (Autheris.Domain.Exceptions.GatewaySecurityException)
            {
                denyTenant = TenantId.Platform; // audit-only platform record (DT-A-6)
            }

            var denyEntry = new AuditLogEntry
            {
                TenantId = denyTenant,
                EventType = AuditEventTypes.DataCatalogSync,
                ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-ANONYMOUS"),
                TargetTable = string.Empty,
                Decision = "DENY",
                TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { dryRun, reason = "ForbiddenRole" })
            };

            if (auditWriter != null)
            {
                await auditWriter.RecordRequiredAsync(denyEntry, ct).ConfigureAwait(false);
            }
            else if (auditLog != null)
            {
                await auditLog.RecordAuditEventAsync(denyEntry, ct).ConfigureAwait(false);
            }

            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Only governance or cluster administrators may synchronize the data catalog.")
                .Build());
        }

        TenantId? restrictToTenant = null;
        if (!isClusterAdmin)
        {
            try
            {
                restrictToTenant = principal.GetTenantId();
            }
            catch (Autheris.Domain.Exceptions.GatewaySecurityException)
            {
                var denyEntry = new AuditLogEntry
                {
                    TenantId = TenantId.Platform,
                    EventType = AuditEventTypes.DataCatalogSync,
                    ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
                    TargetTable = string.Empty,
                    Decision = "DENY",
                    TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { dryRun, reason = "InvalidTenant" })
                };

                if (auditWriter != null)
                {
                    await auditWriter.RecordRequiredAsync(denyEntry, ct).ConfigureAwait(false);
                }
                else if (auditLog != null)
                {
                    await auditLog.RecordAuditEventAsync(denyEntry, ct).ConfigureAwait(false);
                }

                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("FORBIDDEN")
                    .SetMessage("No valid tenant in the authentication token.")
                    .Build());
            }
        }

        TenantId auditTenant = restrictToTenant ?? TenantId.Platform; // cluster-wide record: platform bucket (DT-A-6)
        var writeAheadEntry = new AuditLogEntry
        {
            TenantId = auditTenant,
            EventType = AuditEventTypes.DataCatalogSync,
            ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "ALLOW",
            TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                dryRun,
                scope = restrictToTenant?.Value ?? "cluster"
            })
        };

        if (auditWriter != null)
        {
            await auditWriter.RecordRequiredAsync(writeAheadEntry, ct).ConfigureAwait(false);
        }
        else if (auditLog != null)
        {
            await auditLog.RecordAuditEventAsync(writeAheadEntry, ct).ConfigureAwait(false);
        }

        Autheris.Application.Catalog.Models.CatalogSyncResult result;
        try
        {
            result = await catalogSyncService.SyncCatalogAsync(dryRun, restrictToTenant, ct).ConfigureAwait(false);
        }
        catch (Autheris.Domain.Exceptions.GatewayForbiddenException)
        {
            await RecordSyncFailureAsync(auditWriter, auditLog, auditTenant, principal, dryRun, restrictToTenant, "Forbidden", ct).ConfigureAwait(false);
            throw new GraphQLException(ErrorBuilder.New().SetCode("ACCESS_DENIED").SetMessage("Access denied.").Build());
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GraphQLException)
        {
            // #341 (C341-6): the cause (an egress refusal, an unreachable source) is logged here, server side only; the caller gets the
            // same fixed code as SyncOpenMetadata.
            logger?.LogError(ex, "Data catalog synchronization failed ({ExceptionType}).", ex.GetType().Name);
            await RecordSyncFailureAsync(auditWriter, auditLog, auditTenant, principal, dryRun, restrictToTenant, "Failed", ct).ConfigureAwait(false);
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("SYNC_FAILED")
                .SetMessage("The data catalog synchronization failed.")
                .Build());
        }

        var completionEntry = new AuditLogEntry
        {
            TenantId = auditTenant,
            EventType = AuditEventTypes.DataCatalogSync,
            ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "ALLOW",
            TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                dryRun,
                scope = restrictToTenant?.Value ?? "cluster",
                syncedTables = result.SyncedTablesCount,
                syncedColumns = result.SyncedColumnsCount,
                maskedColumns = result.MaskedColumnsCount,
                art9Tables = result.Art9ProtectedTablesCount
            })
        };

        if (auditWriter != null)
        {
            await auditWriter.RecordRequiredAsync(completionEntry, ct).ConfigureAwait(false);
        }
        else if (auditLog != null)
        {
            await auditLog.RecordAuditEventAsync(completionEntry, ct).ConfigureAwait(false);
        }

        return new DataCatalogSyncPayload
        {
            Success = result.Success,
            SyncedTablesCount = result.SyncedTablesCount,
            SyncedColumnsCount = result.SyncedColumnsCount,
            MaskedColumnsCount = result.MaskedColumnsCount,
            Art9ProtectedTablesCount = result.Art9ProtectedTablesCount,
            Warnings = result.Warnings.ToList()
        };
    }

    /// <summary>#341 (C341-7): closes the write-ahead sync entry with a failure outcome; an unavailable audit sink never replaces the governed error.</summary>
    private static async Task RecordSyncFailureAsync(
        Autheris.Application.Audit.IAuditWriter? auditWriter,
        Autheris.Application.Common.Interfaces.IAuditLogRepository? auditLog,
        TenantId tenant,
        ClaimsPrincipal principal,
        bool dryRun,
        TenantId? restrictToTenant,
        string outcome,
        CancellationToken ct)
    {
        var entry = new AuditLogEntry
        {
            TenantId = tenant,
            EventType = AuditEventTypes.DataCatalogSync,
            ActorSid = principal.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "ERROR",
            TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { dryRun, scope = restrictToTenant?.Value ?? "cluster", outcome })
        };

        try
        {
            if (auditWriter != null)
            {
                await auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
            }
            else if (auditLog != null)
            {
                await auditLog.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The failure entry is best effort; the write-ahead entry already exists.
        }
    }
}
