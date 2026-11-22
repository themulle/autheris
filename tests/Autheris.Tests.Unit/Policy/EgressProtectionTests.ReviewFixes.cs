namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
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
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class EgressProtectionTests
{
    [Fact]
    public void ComputePrincipalCacheKey_distinct_for_user_groups_roles_scopes_and_realm()
    {
        static ClaimsPrincipal Make(string sid, string[] groups, string[] roles, string[] scopes, string realmId, string realmKind)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.PrimarySid, sid),
                new("tenant_id", Tenant.Value),
                new("autheris:realm", realmId),
                new("autheris:realm_kind", realmKind, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline)
            };
            claims.AddRange(groups.Select(g => new Claim(ClaimTypes.GroupSid, g, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline)));
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            if (scopes.Length > 0)
            {
                claims.Add(new Claim("scp", string.Join(' ', scopes)));
            }

            return PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")));
        }

        var baseline = GovernancePolicyFactory.ComputePrincipalCacheKey(Make("S-1-1", ["S-1-5-21-100-200-300-1001"], ["Reader"], ["Sql.Read"], "realm-a", "EntraWorkforce"));
        var diffUser = GovernancePolicyFactory.ComputePrincipalCacheKey(Make("S-1-2", ["S-1-5-21-100-200-300-1001"], ["Reader"], ["Sql.Read"], "realm-a", "EntraWorkforce"));
        var diffGroup = GovernancePolicyFactory.ComputePrincipalCacheKey(Make("S-1-1", ["S-1-5-21-100-200-300-1002"], ["Reader"], ["Sql.Read"], "realm-a", "EntraWorkforce"));
        var diffRole = GovernancePolicyFactory.ComputePrincipalCacheKey(Make("S-1-1", ["S-1-5-21-100-200-300-1001"], ["Analyst"], ["Sql.Read"], "realm-a", "EntraWorkforce"));
        var diffScope = GovernancePolicyFactory.ComputePrincipalCacheKey(Make("S-1-1", ["S-1-5-21-100-200-300-1001"], ["Reader"], ["Agent.Read"], "realm-a", "EntraWorkforce"));
        var diffRealmId = GovernancePolicyFactory.ComputePrincipalCacheKey(Make("S-1-1", ["S-1-5-21-100-200-300-1001"], ["Reader"], ["Sql.Read"], "realm-b", "EntraWorkforce"));
        var diffRealmKind = GovernancePolicyFactory.ComputePrincipalCacheKey(Make("S-1-1", ["S-1-5-21-100-200-300-1001"], ["Reader"], ["Sql.Read"], "realm-a", "GenericOidc"));

        new[] { baseline, diffUser, diffGroup, diffRole, diffScope, diffRealmId, diffRealmKind }
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(7);
    }

    [Fact]
    public async Task GovernedSqlRewriter_and_TreeSecuredSourceBuilder_bind_distinct_principal_cache_keys()
    {
        var orders = new TableMetadata
        {
            Identifier = TableId,
            TenantColumnName = "tenant_id",
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
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };

        var tables = new InMemoryTableMetadataRepository();
        await tables.UpsertTableMetadataAsync(orders);

        var tableIdentity = new TableIdentity("dbo", "orders");
        var catalog = new InMemoryTableCatalog(
            [new TableCatalogEntry(tableIdentity, [new CatalogColumn("id", "int"), new CatalogColumn("tenant_id", "varchar")], "tenant_id")],
            "dbo");

        var gatewayOpts = Options.Create(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = Tenant.Value },
            WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "sales" },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sales"] = new() { Provider = "PostgreSql", ConnectionString = "Host=h;Database=d", DefaultSchema = "dbo" }
                }
            }
        });

        var factory = new GovernancePolicyFactory(new StubCatalogProvider(catalog), tables, gatewayOpts);
        var spyEngine = new CapturingSqlEngine(FastSqlEngine.Default);
        var consents = new FakeConsentRepository();
        var pdp = new AccessDecisionPoint(
            gatewayOpts,
            new ConsentResolutionService(),
            consents,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            protectionProvider: new MutableProtectionProvider(Protection(ProtectionClass.Public)),
            accessPolicySettings: new StubPolicyProvider());

        var rewriter = new GovernedSqlRewriter(
            gatewayOpts,
            new ConsentResolutionService(),
            tables,
            consentRepository: consents,
            sqlEngine: spyEngine,
            policyFactory: factory,
            accessDecisionPoint: pdp);

        var p1 = PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-P1"), new Claim("tenant_id", Tenant.Value), new Claim("autheris:realm", "realm-1")], "Bearer")));
        var p2 = PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-P2"), new Claim("tenant_id", Tenant.Value), new Claim("autheris:realm", "realm-2")], "Bearer")));

        await rewriter.CompileAsync("SELECT id FROM dbo.orders", p1, Tenant, CancellationToken.None);
        await rewriter.CompileAsync("SELECT id FROM dbo.orders", p2, Tenant, CancellationToken.None);

        spyEngine.CapturedRequests.Count.ShouldBe(2);
        var rewriterKey1 = spyEngine.CapturedRequests[0].Policy.PrincipalCacheKey;
        var rewriterKey2 = spyEngine.CapturedRequests[1].Policy.PrincipalCacheKey;
        rewriterKey1.ShouldNotBeNullOrWhiteSpace();
        rewriterKey2.ShouldNotBeNullOrWhiteSpace();
        rewriterKey1.ShouldNotBe(rewriterKey2);

        var treeSpy = new CapturingSqlEngine(FastSqlEngine.Default);
        var treeBuilder = new TreeSecuredSourceBuilder(factory, treeSpy);
        var policyPredicate = new Autheris.Application.Policy.PolicyPredicateRowPolicy(
            PolicyPredicate.DenyAll,
            TargetSqlDialect.PostgreSql,
            new PolicyPredicateRenderer());
        var sharedDecision = TableAccessDecision.Allowed(TableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(policyPredicate);

        await treeBuilder.BuildAsync(new ResolvedTableAccess(orders, sharedDecision, Tenant, new Sid("S-1-5-21-P1"), p1), "sales", DatabaseDialect.PostgreSql, CancellationToken.None);
        await treeBuilder.BuildAsync(new ResolvedTableAccess(orders, sharedDecision, Tenant, new Sid("S-1-5-21-P2"), p2), "sales", DatabaseDialect.PostgreSql, CancellationToken.None);

        treeSpy.CapturedRequests.Count.ShouldBe(2);
        treeSpy.CapturedRequests[0].Policy.PrincipalCacheKey.ShouldBe(rewriterKey1);
        treeSpy.CapturedRequests[1].Policy.PrincipalCacheKey.ShouldBe(rewriterKey2);
        treeSpy.CapturedRequests[0].Policy.PrincipalCacheKey.ShouldNotBe(treeSpy.CapturedRequests[1].Policy.PrincipalCacheKey);
    }

    [Fact]
    public async Task Async_job_result_denied_when_governance_epoch_changes_after_completion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"egress-job-epoch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tables = new InMemoryTableMetadataRepository();
            await tables.UpsertTableMetadataAsync(MakeOrdersTable());
            var consents = new FakeConsentRepository();
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
                ValidTo = DateTimeOffset.UtcNow.AddDays(30)
            });

            var gatewayOptions = Options.Create(new GatewayOptions { AsyncJobs = new AsyncJobOptions { ScratchDirectory = tempDir } });
            var pdp = new AccessDecisionPoint(
                gatewayOptions,
                new ConsentResolutionService(),
                consents,
                NullConsentCacheService.Instance,
                mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
                protectionProvider: new MutableProtectionProvider(Protection(ProtectionClass.Confidential)),
                accessPolicySettings: new StubPolicyProvider());

            var epochStore = new MutableEpochStore(1);
            var epochProvider = new GovernanceEpochProvider(epochStore);
            var audit = new InMemoryAuditLogRepository();
            var manager = new AsyncQueryJobManager(audit, options: gatewayOptions, epochProvider: epochProvider);
            var user = CallerPrincipal();

            var job = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT id FROM sales.dbo.orders", Format: "json"), user, Tenant);
            var tenantDir = Path.Combine(tempDir, Tenant.Value);
            Directory.CreateDirectory(tenantDir);
            var filePath = Path.Combine(tenantDir, $"{job.JobId}.json");
            await File.WriteAllTextAsync(filePath, "[{\"id\":1}]");
            manager.UpdateJob(job with { State = AsyncJobState.Completed, CompletedAt = DateTimeOffset.UtcNow, ResultFilePath = filePath });

            await using (var okStream = await manager.GetJobResultStreamAsync(job.JobId, Tenant, user, pdp, tables))
            {
                okStream.ShouldNotBeNull();
            }

            epochStore.Epoch = 2;
            epochProvider.InvalidateTenant(Tenant.Value);
            await Should.ThrowAsync<GatewayForbiddenException>(() =>
                manager.GetJobResultStreamAsync(job.JobId, Tenant, user, pdp, tables));
            (await audit.GetAuditLogEntriesAsync(tenantId: Tenant))
                .ShouldContain(a => a.EventType == AuditEventTypes.AsyncJobDownloaded && a.Decision == "DENY");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Async_job_cross_node_fetch_reauthorizes_persisted_target_tables_and_fails_closed_when_missing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"egress-job-xnode-{Guid.NewGuid():N}");
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

            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var clusterState = new InMemoryClusterStateProvider(time);
            var topology = new ClusterStateTopology(ClusterStateMode.External);
            var jobOpts = Options.Create(new AsyncJobOptions { ScratchDirectory = tempDir, SharedScratchDirectory = true });
            var epochStore = new MutableEpochStore(1);
            var epochProvider = new GovernanceEpochProvider(epochStore);

            var nodeA = new AsyncQueryJobManager(new InMemoryAuditLogRepository(), jobOptions: jobOpts, clusterState: clusterState, topology: topology, timeProvider: time, epochProvider: epochProvider);
            var nodeB = new AsyncQueryJobManager(new InMemoryAuditLogRepository(), jobOptions: jobOpts, clusterState: clusterState, topology: topology, timeProvider: time, epochProvider: epochProvider);

            var pdp = new AccessDecisionPoint(
                Options.Create(new GatewayOptions()),
                new ConsentResolutionService(),
                consents,
                NullConsentCacheService.Instance,
                mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
                protectionProvider: new MutableProtectionProvider(Protection(ProtectionClass.Confidential)),
                accessPolicySettings: new StubPolicyProvider());

            var user = CallerPrincipal();
            var job = await nodeA.SubmitJobAsync(new AsyncQueryJobRequest("SELECT id FROM sales.dbo.orders", Format: "json"), user, Tenant);
            var tenantDir = Path.Combine(tempDir, Tenant.Value);
            Directory.CreateDirectory(tenantDir);
            var filePath = Path.Combine(tenantDir, $"{job.JobId}.json");
            await File.WriteAllTextAsync(filePath, "[{\"id\":1}]");
            await nodeA.TryTransitionAsync(job with { State = AsyncJobState.Completed, CompletedAt = time.GetUtcNow(), ResultFilePath = filePath }, CancellationToken.None);

            await using (var s = await nodeB.GetJobResultStreamAsync(job.JobId, Tenant, user, pdp, tables))
            {
                s.ShouldNotBeNull();
            }

            await consents.RevokeConsentAsync(grant.Id, CallerSid, "Revoked", ConsentRevocationAuthority.FromPrincipal(user));
            await Should.ThrowAsync<GatewayForbiddenException>(() =>
                nodeB.GetJobResultStreamAsync(job.JobId, Tenant, user, pdp, tables));

            var tablelessJob = await nodeA.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1", Format: "json"), user, Tenant);
            var tablelessPath = Path.Combine(tenantDir, $"{tablelessJob.JobId}.json");
            await File.WriteAllTextAsync(tablelessPath, "[{}]");
            await nodeA.TryTransitionAsync(tablelessJob with { State = AsyncJobState.Completed, CompletedAt = time.GetUtcNow(), ResultFilePath = tablelessPath }, CancellationToken.None);

            await Should.ThrowAsync<GatewayForbiddenException>(() =>
                nodeB.GetJobResultStreamAsync(tablelessJob.JobId, Tenant, user, pdp, tables));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Cdc_all_wildcard_subscription_terminates_on_tightening_or_role_loss()
    {
        var tables = new InMemoryTableMetadataRepository();
        await tables.UpsertTableMetadataAsync(MakeOrdersTable());
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

        var streamStore = new Autheris.Infrastructure.Streaming.InMemoryCdcStreamStore();
        var streamDesc = await streamStore.EnsureStreamAsync("mssql", TableId);
        await streamStore.SetStatusAsync(streamDesc.Id, CdcStreamStatus.Active, null);

        var services = new ServiceCollection()
            .AddSingleton<IAccessDecisionPoint>(pdp)
            .AddSingleton<ITableMetadataRepository>(tables)
            .AddSingleton<ICdcStreamStore>(streamStore)
            .BuildServiceProvider();

        var adminPrincipal = PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, CallerSid.Value),
            new Claim(ClaimTypes.NameIdentifier, CallerSid.Value),
            new Claim("tenant_id", Tenant.Value),
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ], "Bearer")));

        var epochStore = new MutableEpochStore(1);
        var epochProvider = new GovernanceEpochProvider(epochStore);
        using var activeCts = new CancellationTokenSource();
        var lastEpoch = new[] { 1L };
        var syncLock = new Lock();

        epochStore.Epoch = 2;
        epochProvider.InvalidateTenant(Tenant.Value);
        await Subscription.PollEpochChangeAsync(activeCts, epochProvider, Tenant, services, adminPrincipal, tableId: null, lastEpoch, syncLock, NullLogger.Instance);
        activeCts.IsCancellationRequested.ShouldBeFalse();

        protectionProvider.Current = Protection(ProtectionClass.Confidential);
        epochStore.Epoch = 3;
        epochProvider.InvalidateTenant(Tenant.Value);
        await Subscription.PollEpochChangeAsync(activeCts, epochProvider, Tenant, services, adminPrincipal, tableId: null, lastEpoch, syncLock, NullLogger.Instance);
        activeCts.IsCancellationRequested.ShouldBeTrue();

        protectionProvider.Current = Protection(ProtectionClass.Public);
        using var nonAdminCts = new CancellationTokenSource();
        epochStore.Epoch = 4;
        epochProvider.InvalidateTenant(Tenant.Value);
        await Subscription.PollEpochChangeAsync(nonAdminCts, epochProvider, Tenant, services, CallerPrincipal(), tableId: null, new[] { 3L }, new Lock(), NullLogger.Instance);
        nonAdminCts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Idle_subscription_terminates_on_group_ttl_expiry_without_epoch_advance()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        var groupExpiry = time.GetUtcNow().AddSeconds(15);
        var principal = PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, CallerSid.Value),
            new Claim("tenant_id", Tenant.Value),
            new Claim("exp", time.GetUtcNow().AddHours(2).ToUnixTimeSeconds().ToString()),
            GroupResolutionClaims.CreateExpiresAt(groupExpiry)
        ], "Bearer")));

        var resolver = new StubTableAccessResolver((p, table) => new ResolvedTableAccess(
            new TableMetadata { Identifier = table },
            TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            Tenant,
            CallerSid,
            p!));

        var options = Options.Create(new GatewayOptions { GraphQL = new GraphQLOptions { SubscriptionRevalidationSeconds = 5 } });
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(time)
            .AddSingleton(options)
            .AddSingleton(new CdcSubscriptionGovernor(options, NullLogger<CdcSubscriptionGovernor>.Instance))
            .AddSingleton<ITableAccessResolver>(resolver)
            .AddSingleton<IAuditLogRepository>(new InMemoryAuditLogRepository())
            .AddSingleton<Autheris.Application.DataSources.IDataSourceTransportGate>(AllowAllTransportGate.Instance)
            .BuildServiceProvider();

        await using var enumerator = new Subscription().SubscribeToTableEventsAsync(
            "orders",
            null,
            new IdleCdcChannel(),
            new NoopStreamRlsEnforcer(),
            services,
            principal,
            CancellationToken.None).GetAsyncEnumerator();

        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        moveNextTask.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(16));

        var hasNext = await moveNextTask.WaitAsync(TimeSpan.FromSeconds(5));
        hasNext.ShouldBeFalse();
    }

    private sealed class CapturingSqlEngine(ISqlEngine inner) : ISqlEngine
    {
        public List<CompileRequest> CapturedRequests { get; } = [];

        public CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken cancellationToken)
        {
            CapturedRequests.Add(request);
            return inner.Compile(sql, request, cancellationToken);
        }

        public (SqlBaseParser.SingleStatementContext Tree, Antlr4.Runtime.CommonTokenStream Tokens) Parse(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? tokenOptions = null,
            CancellationToken cancellationToken = default)
            => inner.Parse(sql, tokenOptions, cancellationToken);

        public (SqlBaseParser.StandaloneExpressionContext Tree, Antlr4.Runtime.CommonTokenStream Tokens) ParseExpression(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? tokenOptions = null,
            CancellationToken cancellationToken = default)
            => inner.ParseExpression(sql, tokenOptions, cancellationToken);

        public SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql)
            => inner.Analyze(sql);

        public SqlQueryMetadata Analyze(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? tokenOptions,
            CancellationToken cancellationToken = default)
            => inner.Analyze(sql, tokenOptions, cancellationToken);
    }

    private sealed class IdleCdcChannel : ICdcEventChannel
    {
        public async IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Autheris.Tests.Common.Hang.UntilCancelledAsync(ct).ConfigureAwait(false);
            yield break;
        }

        public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class NoopStreamRlsEnforcer : IStreamRlsPolicyEnforcer
    {
        public ValueTask<StreamSecurityDecision> EvaluateAndMaskAsync(
            CdcEvent cdcEvent,
            ClaimsPrincipal subscriber,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
