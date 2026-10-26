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
internal static class GatewayStartupChecks
{
    /// <summary>The checks in execution order; the first failing check wins (fail-fast, no aggregation).</summary>
    internal static IReadOnlyList<StartupCheck> All { get; } =
    [
        new("ObjectGraph", GatewayStartupChecks.ObjectGraph),
        new("OracleRuntime", GatewayStartupChecks.OracleRuntime),
        new("SqlServerWindowsAuth", GatewayStartupChecks.SqlServerWindowsAuth),
        new("Databricks", GatewayStartupChecks.Databricks),
        new("DefaultSchemas", GatewayStartupChecks.DefaultSchemas),
        new("TenantCollisions", GatewayStartupChecks.TenantCollisions),
        new("WebhookSubscriptions", GatewayStartupChecks.WebhookSubscriptions),
        new("QueryCostOptions", GatewayStartupChecks.QueryCostOptions),
        new("RecursiveCteWarning", GatewayStartupChecks.RecursiveCteWarning),
        new("Egress", PolicyAndNetworkStartupChecks.Egress),
        new("CasbinModel", PolicyAndNetworkStartupChecks.CasbinModel),
        new("CasbinPolicy", PolicyAndNetworkStartupChecks.CasbinPolicy),
        new("ReverseProxy", PolicyAndNetworkStartupChecks.ReverseProxy),
        new("DevelopmentInContainer", GraphQlAndSwitchStartupChecks.DevelopmentInContainer),
        new("GraphQlTrustedDocuments", GraphQlAndSwitchStartupChecks.GraphQlTrustedDocuments),
        new("FederationSigning", GraphQlAndSwitchStartupChecks.FederationSigning),
        new("SecuritySwitchReport", GraphQlAndSwitchStartupChecks.SecuritySwitchReport),
        new("OperationalInvariants", GraphQlAndSwitchStartupChecks.OperationalInvariants),
        new("BasicAuthSession", AuthenticationStartupChecks.BasicAuthSession),
        new("BasicAuthUsers", AuthenticationStartupChecks.BasicAuthUsers),
        new("ForwardAuth", AuthenticationStartupChecks.ForwardAuth),
        new("ProductionGovernance", ProductionStartupChecks.ProductionGovernance),
        new("ProductionTransportAndOrigins", ProductionStartupChecks.ProductionTransportAndOrigins),
        new("ProductionAuthentication", AuthenticationStartupChecks.ProductionAuthentication),
        new("ProductionStorageAndAudit", StorageStartupChecks.ProductionStorageAndAudit),
        new("GovernanceDbProvider", StorageStartupChecks.GovernanceDbProvider),
        new("ClusterState", StorageStartupChecks.ClusterState),
    ];

    internal static void ObjectGraph(StartupCheckContext context)
    {
        GatewayStartupValidator.ValidateObjectRecursively(context.Options);
    }

    internal static void OracleRuntime(StartupCheckContext context)
    {
        GatewayStartupValidator.ValidateOracleRuntime(context.Options, context.Environment, context.Logger);
    }

    internal static void SqlServerWindowsAuth(StartupCheckContext context)
    {
        GatewayStartupValidator.ValidateSqlServerWindowsAuth(context.Options, context.Environment, context.GetEnvironmentVariable, context.Logger);
    }

    internal static void Databricks(StartupCheckContext context)
    {
        GatewayStartupValidator.ValidateDatabricks(context.Options, context.Environment);
    }

    internal static void DefaultSchemas(StartupCheckContext context)
    {
        GatewayStartupValidator.ValidateDefaultSchemas(context.Options);
    }

    internal static void TenantCollisions(StartupCheckContext context)
    {
        GatewayStartupValidator.ValidateTenantCollisions(context.Options, context.Environment, context.Logger);
    }

    internal static void WebhookSubscriptions(StartupCheckContext context)
    {
        GatewayStartupValidator.ValidateWebhookSubscriptions(context.Options);
    }

    internal static void QueryCostOptions(StartupCheckContext context)
    {
        GatewayStartupValidator.ValidateQueryCostOptions(context.Options.QueryCost, context.Environment);
    }

    internal static void RecursiveCteWarning(StartupCheckContext context)
    {
        GatewayStartupValidator.WarnAboutRecursiveCte(context.Options, context.Logger);
    }
}
