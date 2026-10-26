namespace Autheris.Api.Extensions.DependencyInjection.StartupChecks;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Autheris.Api.Configuration;
using Autheris.Api.Security;
using Autheris.Application.Governance;
using Autheris.Application.Policy.Lint;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Dialects;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>Ordered startup checks of one area; each method is one contiguous section of the former <c>ValidateStartup</c> body (ARCH-MNT-12).</summary>
internal static class StorageStartupChecks
{
    internal static void ProductionStorageAndAudit(StartupCheckContext context)
    {
        var options = context.Options;
        var environment = context.Environment;
        var getEnvironmentVariable = context.GetEnvironmentVariable;
        var logger = context.Logger;

        if (context.IsDevelopment)
        {
            return;
        }

        if (options.Audit.Worm.Enabled && string.Equals(options.Audit.Worm.StorageType, "S3", StringComparison.OrdinalIgnoreCase) && options.Audit.Worm.EnforceObjectLock &&
            (string.IsNullOrWhiteSpace(options.Audit.Worm.S3AccessKey) || string.IsNullOrWhiteSpace(options.Audit.Worm.S3SecretKey)))
        {
            throw new ValidationException("Security violation: Outside Development, S3 WORM with EnforceObjectLock requires S3AccessKey and S3SecretKey to be configured.");
        }

        // WP-ARC-B-13: an unset connection string resolves to the state-directory file outside Development/Test, so only an
        // explicitly configured in-memory database can be in effect here (and is rejected).
        if (DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite) &&
            GatewayStartupValidator.IsInMemorySqlite(options.GovernanceDb.ConnectionString))
        {
            throw new ValidationException("Security violation: In-memory SQLite databases (GovernanceDb.ConnectionString) are strictly prohibited outside Development.");
        }

        // DEP-4: In container environments, a relative SQLite database path in /app is unwritable for non-root APP_UID
        if (GatewayStartupValidator.IsTruthy(getEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")) &&
            DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite))
        {
            try
            {
                var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(options.GovernanceDb.ConnectionString);
                if (!string.IsNullOrWhiteSpace(builder.DataSource) &&
                    !builder.DataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase) &&
                    builder.Mode != Microsoft.Data.Sqlite.SqliteOpenMode.Memory)
                {
                    var isDirectRootFile = !builder.DataSource.Contains('/') && !builder.DataSource.Contains('\\');
                    var isAppRoot = builder.DataSource.Equals("/app/governance.db", StringComparison.OrdinalIgnoreCase);
                    if (isDirectRootFile || isAppRoot)
                    {
                        throw new ValidationException(
                            "Security and configuration error (DEP-4): In the container, the process runs as non-root user ($APP_UID). " +
                            $"The SQLite path '{builder.DataSource}' is located directly in the non-writable application directory (/app). " +
                            "Use the writable data directory '/app/data' (for example Gateway:State:StateDirectory=/app/data).");
                    }
                }
            }
            catch (ValidationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to parse SQLite connection string during container path check.");
            }
        }

        // AU-01 & AU-03: Startup Fail-Closed & Umgebungsvalidierung für Audit
        var envName = getEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? environment.EnvironmentName;
        bool isDevOrTest = string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(envName, "Test", StringComparison.OrdinalIgnoreCase);

        if (!isDevOrTest)
        {
            if (options.Audit.HmacKeyIsFallback)
            {
                throw new ValidationException(
                    "CRITICAL SECURITY VIOLATION: Hardcoded or fallback HMAC audit keys are strictly prohibited in production.");
            }

            // Produktion: Leerer Wert gilt zwingend als Produktion! Every provider, SQLite files included, needs an explicit anchor:
            // the anchor must not live only next to the database it protects (consistent with AuditChainAnchorStoreFactory).
            if (string.IsNullOrWhiteSpace(options.Audit.ChainAnchorPath) &&
                string.IsNullOrWhiteSpace(options.Audit.ChainAnchorWormDirectory) &&
                string.IsNullOrWhiteSpace(options.Audit.ChainAnchorSignerKeyVaultRef))
            {
                throw new ValidationException(
                    "CRITICAL AUDIT MISCONFIGURATION (AU-01/AU-03): In production environments, " +
                    "a persistent audit anchor store (ChainAnchorPath, ChainAnchorWormDirectory, or KMS Signer) " +
                    "is mandatory. In-memory anchor stores are strictly prohibited outside Development/Test.");
            }
        }
    }

    internal static void GovernanceDbProvider(StartupCheckContext context)
    {
        var options = context.Options;

        if (!GatewayStartupValidator.IsSupportedGovernanceDbProvider(options.GovernanceDb.Provider))
        {
            throw new ValidationException($"GovernanceDb provider '{options.GovernanceDb.Provider}' is not supported. Allowed values are 'Sqlite', 'PostgreSql' or 'SqlServer'.");
        }
    }

    internal static void ClusterState(StartupCheckContext context)
    {
        var options = context.Options;

        if ((options.HighAvailability.MultiNodeClusterMode || options.HighAvailability.Replicas > 1) &&
            DataSourceProvider.Is(options.GovernanceDb.Provider, DatabaseDialect.Sqlite))
        {
            throw new ValidationException("Security violation (E-2): Multi-node cluster mode and more than 1 replica are not allowed with SQLite, because SQLite uses local database files per instance. Configure GovernanceDb.Provider = 'PostgreSql' or 'SqlServer' for cluster operation.");
        }

        // WP-ARC-A-09: an embedded Garnet server lives inside one process and is shared with no other replica, so it cannot
        // provide cluster-wide state. This also catches EnableEmbeddedServer = true combined with Redis.Enabled = true,
        // where the embedded server silently wins the registration.
        if ((options.HighAvailability.MultiNodeClusterMode || options.HighAvailability.Replicas > 1) &&
            options.Caching.Garnet.EnableEmbeddedServer)
        {
            throw new ValidationException("NF-HA-02 violation: Multi-node cluster mode or more than one replica requires an external Redis/Garnet. The embedded Garnet server (Caching.Garnet.EnableEmbeddedServer = true) is local to one process and cannot provide cluster-wide state; disable it and configure Caching.Redis.");
        }
    }
}
