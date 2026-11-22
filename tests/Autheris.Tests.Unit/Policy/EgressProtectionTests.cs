namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Runtime.Jobs.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Application.State;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Subscriptions;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class EgressProtectionTests
{
    private static readonly TenantId Tenant = new("tenant-egress-iso");
    private static readonly TableIdentifier TableId = new("sales", "dbo", "orders");
    private static readonly Sid CallerSid = new("S-1-5-21-USER-99");

    private static TableMetadata MakeOrdersTable() => new()
    {
        Identifier = TableId,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "orders",
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "ssn", DataType = "varchar" },
            new TableColumn { ColumnName = "hidden_note", DataType = "varchar", DiscoveryHidden = true }
        ]
    };

    private static EffectiveProtection Protection(ProtectionClass cls) => new(
        new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
        null,
        cls,
        cls,
        null,
        new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
        CatalogVisibilityMode.Open);

    private static ClaimsPrincipal CallerPrincipal(string sid = "S-1-5-21-USER-99") =>
        PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, sid),
            new Claim(ClaimTypes.NameIdentifier, sid),
            new Claim("tenant_id", Tenant.Value)
        ], "Bearer")));

    [Fact]
    public async Task Stream_terminates_on_revoke()
    {
        var tables = new InMemoryTableMetadataRepository();
        var orders = MakeOrdersTable();
        await tables.UpsertTableMetadataAsync(orders);

        var consents = new FakeConsentRepository();
        var protectionProvider = new MutableProtectionProvider(Protection(ProtectionClass.Public));
        var gatewayOptions = Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = Tenant.Value } });

        var pdp = new AccessDecisionPoint(
            gatewayOptions,
            new ConsentResolutionService(),
            consents,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            protectionProvider: protectionProvider,
            accessPolicySettings: new StubPolicyProvider());

        var enforcer = StreamRlsPolicyEnforcerTestFactory.Create(
            pdp,
            tables,
            new ColumnMaskingProvider(gatewayOptions),
            new AlwaysValidEpochService(),
            consents,
            new ConsentResolutionService(),
            NullConsentCacheService.Instance,
            options: gatewayOptions);

        var principal = CallerPrincipal();
        var cdcEvent = new CdcEvent(
            "evt-1",
            TableId,
            CdcOperation.Insert,
            Tenant.Value,
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 42, ["ssn"] = "123-45-6789" },
            DateTimeOffset.UtcNow);

        var allowed = await enforcer.EvaluateAndMaskAsync(cdcEvent, principal);
        allowed.IsAllowed.ShouldBeTrue();

        protectionProvider.Current = Protection(ProtectionClass.Confidential);
        var revoked = await enforcer.EvaluateAndMaskAsync(cdcEvent, principal);
        revoked.IsAllowed.ShouldBeFalse();

        var services = new ServiceCollection()
            .AddSingleton<IAccessDecisionPoint>(pdp)
            .AddSingleton<ITableMetadataRepository>(tables)
            .BuildServiceProvider();

        var epochStore = new MutableEpochStore(1);
        var epochProvider = new GovernanceEpochProvider(epochStore);
        using var streamCts = new CancellationTokenSource();
        var lastEpoch = new[] { 1L };
        var syncLock = new Lock();

        epochStore.Epoch = 2;
        epochProvider.InvalidateTenant(Tenant.Value);
        await Subscription.PollEpochChangeAsync(
            streamCts,
            epochProvider,
            Tenant,
            services,
            principal,
            TableId,
            lastEpoch,
            syncLock,
            NullLogger.Instance);

        streamCts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Async_job_result_reauthorized_at_fetch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"egress-job-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tables = new InMemoryTableMetadataRepository();
            await tables.UpsertTableMetadataAsync(MakeOrdersTable());

            var consents = new FakeConsentRepository();
            var grant = new Consent
            {
                Id = Guid.NewGuid(),
                TenantId = Tenant,
                GranteeType = GranteeType.User,
                GranteeSid = CallerSid,
                TableIdentifier = TableId,
                Effect = ConsentEffect.Allow,
                ColumnSetMode = ColumnSetMode.Explicit,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30)
            };
            await consents.CreateConsentAsync(grant);

            var gatewayOptions = Options.Create(new GatewayOptions { AsyncJobs = new AsyncJobOptions { ScratchDirectory = tempDir } });
            var protectionProvider = new MutableProtectionProvider(Protection(ProtectionClass.Confidential));
            var pdp = new AccessDecisionPoint(
                gatewayOptions,
                new ConsentResolutionService(),
                consents,
                NullConsentCacheService.Instance,
                mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
                protectionProvider: protectionProvider,
                accessPolicySettings: new StubPolicyProvider());

            var audit = new InMemoryAuditLogRepository();
            var manager = new AsyncQueryJobManager(audit, options: gatewayOptions);
            var user = CallerPrincipal();

            var job = await manager.SubmitJobAsync(
                new AsyncQueryJobRequest("SELECT id FROM sales.dbo.orders", Format: "json"),
                user,
                Tenant);

            var tenantDir = Path.Combine(tempDir, Tenant.Value);
            Directory.CreateDirectory(tenantDir);
            var filePath = Path.Combine(tenantDir, $"{job.JobId}.json");
            await File.WriteAllTextAsync(filePath, "[{\"id\":1}]");

            manager.UpdateJob(job with
            {
                State = AsyncJobState.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultFilePath = filePath
            });

            await consents.RevokeConsentAsync(grant.Id, CallerSid, "Revoked", ConsentRevocationAuthority.FromPrincipal(user));

            await Should.ThrowAsync<GatewayForbiddenException>(async () =>
                await manager.GetJobResultStreamAsync(job.JobId, Tenant, user, pdp, tables));

            var audits = await audit.GetAuditLogEntriesAsync(tenantId: Tenant);
            audits.ShouldContain(a => a.EventType == AuditEventTypes.AsyncJobDownloaded && a.Decision == "DENY");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Result_cache_key_includes_epoch()
    {
        var tables = new InMemoryTableMetadataRepository();
        var orders = MakeOrdersTable();
        await tables.UpsertTableMetadataAsync(orders);

        var tableIdentity = new TableIdentity("dbo", "orders");
        var catalog = new InMemoryTableCatalog(
            [
                new TableCatalogEntry(
                    tableIdentity,
                    [new CatalogColumn("id", "int"), new CatalogColumn("ssn", "varchar")],
                    "tenant_id")
            ],
            "dbo");

        var epochStore = new MutableEpochStore(1);
        var epochProvider = new GovernanceEpochProvider(epochStore);
        var factory = new GovernancePolicyFactory(
            new StubCatalogProvider(catalog),
            tables,
            Options.Create(new GatewayOptions()),
            epochProvider);

        var decision = TableAccessDecision.Allowed(TableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var decisions = new Dictionary<TableIdentity, TableAccessDecision> { [tableIdentity] = decision };
        var noRls = new HashSet<TableIdentity> { tableIdentity };
        var principalA = CallerPrincipal("S-1-5-21-A");
        var principalB = CallerPrincipal("S-1-5-21-B");

        var inputA1 = new GovernedPolicyInput("sales", Tenant, decisions, noRls, GovernedSubqueryStrategy.Correlated)
        {
            Metadata = new Dictionary<TableIdentity, TableMetadata> { [tableIdentity] = orders },
            PrincipalCacheKey = GovernancePolicyFactory.ComputePrincipalCacheKey(principalA)
        };
        var pol1 = await factory.CreateAsync(inputA1, CancellationToken.None);
        var req1 = CompileRequestFactory.Create(pol1, TargetSqlDialect.PostgreSql, new WebSqlOptions(), false, 10, new HashSet<string>());
        var keyEpoch1 = CompileCacheKey.Material(req1, "SELECT id FROM dbo.orders", DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql));

        epochStore.Epoch = 2;
        epochProvider.InvalidateTenant(Tenant.Value);

        var pol2 = await factory.CreateAsync(inputA1, CancellationToken.None);
        var req2 = CompileRequestFactory.Create(pol2, TargetSqlDialect.PostgreSql, new WebSqlOptions(), false, 10, new HashSet<string>());
        var keyEpoch2 = CompileCacheKey.Material(req2, "SELECT id FROM dbo.orders", DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql));

        var inputB2 = inputA1 with { PrincipalCacheKey = GovernancePolicyFactory.ComputePrincipalCacheKey(principalB) };
        var polB2 = await factory.CreateAsync(inputB2, CancellationToken.None);
        var reqB2 = CompileRequestFactory.Create(polB2, TargetSqlDialect.PostgreSql, new WebSqlOptions(), false, 10, new HashSet<string>());
        var keyPrincipalB = CompileCacheKey.Material(reqB2, "SELECT id FROM dbo.orders", DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql));

        keyEpoch1.ShouldNotBe(keyEpoch2);
        keyEpoch2.ShouldNotBe(keyPrincipalB);
        keyEpoch1.ShouldNotContain(Tenant.Value);
    }

    [Fact]
    public async Task Explain_does_not_reveal_unseen_columns()
    {
        var orders = MakeOrdersTable();
        var consents = new FakeConsentRepository();
        var protectionProvider = new MutableProtectionProvider(Protection(ProtectionClass.Confidential));
        var pdp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            new ConsentResolutionService(),
            consents,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            protectionProvider: protectionProvider,
            accessPolicySettings: new StubPolicyProvider());

        var subject = SecurityPrincipalContext.FromPrincipal(CallerPrincipal(), Tenant);
        var deniedExplain = await pdp.ExplainAsync(new AccessRequest(subject, orders, AccessOperation.Read, AccessTransport.Rest));
        deniedExplain.Allowed.ShouldBeFalse();
        deniedExplain.Columns.ShouldBeEmpty();

        await consents.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = CallerSid,
            TableIdentifier = TableId,
            Effect = ConsentEffect.Allow,
            ColumnSetMode = ColumnSetMode.Explicit,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "hidden_note", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Deny }
            ]
        });

        var allowedExplain = await pdp.ExplainAsync(new AccessRequest(subject, orders, AccessOperation.Read, AccessTransport.Rest));
        allowedExplain.Allowed.ShouldBeTrue();
        allowedExplain.Columns.Select(c => c.ColumnName).ShouldBe(["id"]);
    }

    private sealed class MutableProtectionProvider(EffectiveProtection initial) : IProtectionSnapshotProvider
    {
        public EffectiveProtection Current { get; set; } = initial;

        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default)
            => ValueTask.FromResult(Current);
    }

    private sealed class StubPolicyProvider : IAccessPolicySettingsProvider
    {
        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default)
            => Task.FromResult(TenantAccessPolicy.FromDeploymentDefaults(tenant, new AccessPolicyOptions()));
        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenant, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class AlwaysValidEpochService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(tables.ToDictionary(t => t, _ => 1L));
    }

    private sealed class MutableEpochStore(long initial = 1) : IGovernanceEpochStore
    {
        public long Epoch { get; set; } = initial;
        public Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(Epoch);
        public Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(++Epoch);
    }

    private sealed class StubCatalogProvider(ITableCatalog catalog) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct)
            => ValueTask.FromResult(catalog);
    }
}
