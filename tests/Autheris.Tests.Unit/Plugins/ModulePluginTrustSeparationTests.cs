namespace Autheris.Tests.Unit.Plugins;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Plugins.Auth;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Services;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Plugins;
using Autheris.Domain.Plugins.Auth;
using Autheris.Infrastructure.Plugins;
using Autheris.Infrastructure.Plugins.Auth;
using Autheris.Tests.Unit.Hosting;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class PluginTrustTests
{
    [Fact]
    public void Untrusted_plugin_hash_is_not_loaded()
    {
        var tempPluginDir = Path.Combine(Path.GetTempPath(), $"autheris-untrusted-plugin-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempPluginDir);
            var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
            var dllPath = Path.Combine(tempPluginDir, "UntrustedCustomerPlugin.dll");
            File.WriteAllBytes(dllPath, dllBytes);

            var options = Options.Create(new GatewayOptions
            {
                Plugins = new PluginsOptions
                {
                    TrustedPluginHashes =
                    {
                        ["UntrustedCustomerPlugin.dll"] = "0000000000000000000000000000000000000000000000000000000000000000",
                    },
                },
            });

            using var manager = new PluginManager(NullLogger<PluginManager>.Instance, options: options);
            Should.Throw<System.Security.SecurityException>(() => manager.LoadPluginsFromDirectory(tempPluginDir));
            manager.GetAllPlugins().ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempPluginDir))
            {
                Directory.Delete(tempPluginDir, recursive: true);
            }
        }
    }
}

internal sealed class FixedConsentRepository(IReadOnlyList<Consent> consents) : IConsentRepository
{
    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) =>
        Task.FromResult(consents);

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
        Task.FromResult(consents);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) =>
        Task.FromResult(consents);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
        Task.FromResult(consents);

    public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) =>
        Task.FromResult(consent);

    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) =>
        Task.FromResult(consents.FirstOrDefault(c => c.Id == consentId));

    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());

    public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());

    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) =>
        Task.FromResult(new ConsentPage(Array.Empty<Consent>(), null));

    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) =>
        Task.CompletedTask;
}

[Trait("Category", "Security")]
public sealed class NarrowingTests
{
    private sealed class AlwaysAllowHook : IPluginAuthorizationHook
    {
        public string HookName => "always-allow";
        public int Order => 1;

        public ValueTask<PluginAuthorizationResult> EvaluateAccessAsync(PluginAuthorizationContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(PluginAuthorizationResult.Allow("always-allow"));
    }

    [Fact]
    public async Task AuthorizationHook_can_only_narrow()
    {
        var opts = new GatewayOptions();
        var host = new AuthPluginHost(Options.Create(opts), NullLogger<AuthPluginHost>.Instance);
        host.RegisterAuthorizationHook(new AlwaysAllowHook());

        // No consents granted: baseline policy denies access.
        var consentRepo = new FixedConsentRepository(Array.Empty<Consent>());
        var policy = new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            NullConsentCacheService.Instance,
            NullPolicyEnforcementService.Instance,
            NullRebacEvaluator.Instance,
            clientIpResolver: null,
            opts,
            NullMandatoryRowFilterResolver.Instance,
            authPluginHost: host);

        var table = new TableIdentifier("db", "public", "Payroll");
        var metadata = new TableMetadata
        {
            Identifier = table,
            Table = new Table
            {
                SourceName = "db",
                SchemaName = "public",
                TableName = "Payroll",
                DataSourceType = DataSourceType.Sql,
            },
            Columns = [new TableColumn { ColumnName = "salary", DataType = "DECIMAL" }],
        };

        var query = new TableAccessQuery(
            new Sid("S-1-5-21-10"),
            new TenantId("tenant-a"),
            new HashSet<Sid>(),
            new HashSet<string> { "Employee" },
            metadata,
            RequestedColumns: ["salary"]);

        var decision = await policy.DecideAsync(query, CancellationToken.None);

        // Even though the plugin hook returned Allow, baseline deny cannot be widened into an Allow!
        decision.IsAllowed.ShouldBeFalse();
    }
}

[Trait("Category", "Security")]
public sealed class PluginEnrichmentTests
{
    [Fact]
    public async Task PluginRoleEnrichment_never_grants_data_access()
    {
        var guard = new AuthPluginRoleEscalationGuard(
            new AuthPluginOptions(),
            NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var context = new PluginSecurityContext
        {
            TenantId = new TenantId("tenant-a"),
            UserSid = new Sid("S-1-5-21-55"),
            GroupSids = new HashSet<Sid>(),
            ExistingTenantRoles = new HashSet<string>(),
            Claims = [],
            AuthenticationScheme = "Bearer",
        };

        var raw = new PluginRoleEnrichmentResult(
            addedRoles: ["DataOwner", "GovernanceAdmin", "ClusterAdmin", "CustomPluginTag"],
            addedGroupSids: []);

        var sanitized = guard.Sanitize("role-plugin", raw, context);
        sanitized.AddedRoles.ShouldNotContain("DataOwner");
        sanitized.AddedRoles.ShouldNotContain("GovernanceAdmin");
        sanitized.AddedRoles.ShouldNotContain("ClusterAdmin");

        // Without an explicit Consent for CustomPluginTag, TableAccessPolicy denies data access.
        var opts = new GatewayOptions();
        var consentRepo = new FixedConsentRepository(Array.Empty<Consent>());
        var policy = new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            NullConsentCacheService.Instance,
            NullPolicyEnforcementService.Instance,
            NullRebacEvaluator.Instance,
            clientIpResolver: null,
            opts,
            NullMandatoryRowFilterResolver.Instance);

        var table = new TableIdentifier("db", "public", "Secrets");
        var metadata = new TableMetadata
        {
            Identifier = table,
            Table = new Table
            {
                SourceName = "db",
                SchemaName = "public",
                TableName = "Secrets",
                DataSourceType = DataSourceType.Sql,
            },
            Columns = [new TableColumn { ColumnName = "secret_value", DataType = "TEXT" }],
        };

        var query = new TableAccessQuery(
            context.UserSid,
            context.TenantId,
            new HashSet<Sid>(),
            new HashSet<string>(sanitized.AddedRoles),
            metadata,
            RequestedColumns: ["secret_value"]);

        var decision = await policy.DecideAsync(query, CancellationToken.None);
        decision.IsAllowed.ShouldBeFalse();
    }
}
