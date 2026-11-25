namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Governance.Protection;
using Autheris.Application.Governance.Services;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Runtime.Jobs.Services;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
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
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Security regression tests for async query job download re-authorization, GraphQL cdc_all subscription epoch revalidation,
/// and transactional WORM-audit / multi-step protection atomicity.
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue1097RuntimeAuthAndAtomicityTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid AliceSid = new("S-1-5-21-1097-ALICE");
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("postgres", null, "SELECT id FROM postgres.public.orders", "postgres", "public", "orders")]
    [InlineData("postgres", null, "SELECT id FROM analytics.events", "postgres", "analytics", "events")]
    [InlineData("postgres", null, "SELECT id FROM orders", "postgres", "public", "orders")]
    [InlineData("mssql", "custom_schema", "SELECT id FROM items", "mssql", "custom_schema", "items")]
    public async Task AsyncQueryJobManager_ParseTargetTable_ResolvesCanonicalSchemasWithoutHardcodingDbo(
        string defaultDataSource,
        string? configuredDefaultSchema,
        string query,
        string expectedDomain,
        string expectedSchema,
        string expectedTable)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-1097-parse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var expectedId = new TableIdentifier(expectedDomain, expectedSchema, expectedTable);
            var tables = new InMemoryTableMetadataRepository(new TableMetadata
            {
                Identifier = expectedId,
                OwnerTenantId = TenantA.Value,
                Table = new Table { Id = Guid.NewGuid(), SourceName = expectedDomain, SchemaName = expectedSchema, TableName = expectedTable, IsActive = true },
                Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
            });

            var gatewayOptions = new GatewayOptions
            {
                WebSql = new WebSqlOptions { DefaultDataSourceName = defaultDataSource },
                AsyncJobs = new AsyncJobOptions { ScratchDirectory = tempDir }
            };
            if (configuredDefaultSchema is not null)
            {
                gatewayOptions.DataSources.Connections[defaultDataSource] = new DataSourceConnectionOptions
                {
                    Provider = "SqlServer",
                    ConnectionString = "Server=stub;",
                    DefaultSchema = configuredDefaultSchema
                };
            }

            var manager = new AsyncQueryJobManager(new InMemoryAuditLogRepository(), options: Options.Create(gatewayOptions));
            var user = CompletePrincipal(AliceSid.Value);
            var job = await manager.SubmitJobAsync(new AsyncQueryJobRequest(query, Format: "json"), user, TenantA);
            var path = CreateCompletedFile(tempDir, TenantA.Value, job.JobId, "[{\"id\":1}]");
            manager.UpdateJob(job with { State = AsyncJobState.Completed, CompletedAt = T0, ResultFilePath = path });

            var pdp = new DelegatingPdp((_, _, _) =>
                TableAccessDecision.Allowed(expectedId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

            await using var stream = await manager.GetJobResultStreamAsync(job.JobId, TenantA, user, pdp, tables);
            stream.ShouldNotBeNull();
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AsyncQueryJobManager_ParseTargetTable_DoesNotFallbackToDboWhenOnlyDboTableExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-1097-nodbo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dboOnly = new TableIdentifier("postgres", "dbo", "orders");
            var tables = new InMemoryTableMetadataRepository(new TableMetadata
            {
                Identifier = dboOnly,
                OwnerTenantId = TenantA.Value,
                Table = new Table { Id = Guid.NewGuid(), SourceName = "postgres", SchemaName = "dbo", TableName = "orders", IsActive = true },
                Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
            });
            var gatewayOptions = new GatewayOptions
            {
                WebSql = new WebSqlOptions { DefaultDataSourceName = "postgres" },
                AsyncJobs = new AsyncJobOptions { ScratchDirectory = tempDir }
            };
            var manager = new AsyncQueryJobManager(new InMemoryAuditLogRepository(), options: Options.Create(gatewayOptions));
            var user = CompletePrincipal(AliceSid.Value);
            var job = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT id FROM orders"), user, TenantA);
            var path = CreateCompletedFile(tempDir, TenantA.Value, job.JobId, "[{\"id\":1}]");
            manager.UpdateJob(job with { State = AsyncJobState.Completed, CompletedAt = T0, ResultFilePath = path });

            var pdp = new DelegatingPdp((t, _, _) =>
                TableAccessDecision.Allowed(t, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

            await Should.ThrowAsync<GatewayForbiddenException>(() =>
                manager.GetJobResultStreamAsync(job.JobId, TenantA, user, pdp, tables));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AsyncQueryJobManager_DownloadReauth_DeniesWhenUnmaskGrantExpiresOrMaskChangesWithoutEpochBump()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-1097-unmask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tableId = new TableIdentifier("postgres", "public", "customers");
            var meta = new TableMetadata
            {
                Identifier = tableId,
                OwnerTenantId = TenantA.Value,
                Table = new Table { Id = Guid.NewGuid(), SourceName = "postgres", SchemaName = "public", TableName = "customers", IsActive = true },
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "int" },
                    new TableColumn { ColumnName = "ssn", DataType = "varchar" }
                ]
            };
            var tables = new InMemoryTableMetadataRepository(meta);
            var clock = new FakeTimeProvider(T0);
            var epochStore = new FixedGovernanceEpochStore(1);
            var epochProvider = new GovernanceEpochProvider(epochStore);
            var options = Options.Create(new GatewayOptions
            {
                WebSql = new WebSqlOptions { DefaultDataSourceName = "postgres" },
                AsyncJobs = new AsyncJobOptions { ScratchDirectory = tempDir }
            });

            var emptyParams = ImmutableDictionary<string, PolicyValue>.Empty.ToFrozenDictionary();
            var maskHash = new ColumnMaskPolicy(new Dictionary<string, MaskSpec>
            {
                ["ssn"] = new MaskSpec(MaskKind.Hmac, new MaskArguments(), emptyParams)
            });
            var maskRedact = new ColumnMaskPolicy(new Dictionary<string, MaskSpec>
            {
                ["ssn"] = new MaskSpec(MaskKind.Redact, new MaskArguments(), emptyParams)
            });

            var clearDecision = TableAccessDecision.Allowed(
                tableId,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["ssn"] = ColumnAccessLevel.Clear
                });
            var maskedHashDecision = TableAccessDecision.Allowed(
                tableId,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["ssn"] = ColumnAccessLevel.Mask
                }) with { ColumnMasks = maskHash };
            var maskedRedactDecision = maskedHashDecision with { ColumnMasks = maskRedact };

            var currentDecision = clearDecision;
            var pdp = new DelegatingPdp((_, _, _) => currentDecision);
            var audit = new InMemoryAuditLogRepository();
            var manager = new AsyncQueryJobManager(audit, options: options, epochProvider: epochProvider, timeProvider: clock);
            var sql = new StaticBufferedSqlService();

            var services = new ServiceCollection()
                .AddSingleton<IGovernedSqlExecutionService>(sql)
                .AddSingleton<IAccessDecisionPoint>(pdp)
                .AddSingleton<ITableMetadataRepository>(tables)
                .BuildServiceProvider();

            var worker = new AsyncQueryJobBackgroundWorker(
                manager,
                manager,
                services.GetRequiredService<IServiceScopeFactory>(),
                audit,
                NullLogger<AsyncQueryJobBackgroundWorker>.Instance,
                options);

            var user = CompletePrincipal(AliceSid.Value, groups: ["grp-finance"], roles: ["Analyst"], scopes: ["sql:read"]);

            // Job 1: executed while UNMASK grant made ssn Clear; later UNMASK expires (becomes Mask) WITHOUT epoch bump.
            var jobUnmask = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT id, ssn FROM postgres.public.customers", Format: "json"), user, TenantA);
            // Job 2: executed while ssn is Mask(Hash); later mask changes to Mask(Redact) WITHOUT epoch bump.
            await worker.StartAsync(CancellationToken.None);
            await WaitHelper.WaitUntil((Func<Task<bool>>)(async () => (await manager.GetJobAsync(jobUnmask.JobId, TenantA, AliceSid.Value))?.State == AsyncJobState.Completed), TimeSpan.FromSeconds(10), pollInterval: TimeSpan.FromMilliseconds(25));

            currentDecision = maskedHashDecision;
            var jobMaskSwap = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT id, ssn FROM postgres.public.customers", Format: "json"), user, TenantA);
            await WaitHelper.WaitUntil((Func<Task<bool>>)(async () => (await manager.GetJobAsync(jobMaskSwap.JobId, TenantA, AliceSid.Value))?.State == AsyncJobState.Completed), TimeSpan.FromSeconds(10), pollInterval: TimeSpan.FromMilliseconds(25));
            await worker.StopAsync(CancellationToken.None);

            // Simulate TTL expiry of Unmask grant -> PDP now returns Mask(Hash), epoch stays 1.
            currentDecision = maskedHashDecision;
            await Should.ThrowAsync<GatewayForbiddenException>(() =>
                manager.GetJobResultStreamAsync(jobUnmask.JobId, TenantA, user, pdp, tables));

            // Simulate category mask swap (Hash -> Redact) -> epoch still 1.
            currentDecision = maskedRedactDecision;
            await Should.ThrowAsync<GatewayForbiddenException>(() =>
                manager.GetJobResultStreamAsync(jobMaskSwap.JobId, TenantA, user, pdp, tables));

            // Verify degraded groups or narrowed role/scope at download also fail closed even if PDP returns Allow.
            currentDecision = clearDecision;
            var degradedUser = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.PrimarySid, AliceSid.Value), new Claim("tenant_id", TenantA.Value), new Claim("scp", "sql:read")], "Bearer"));
            await Should.ThrowAsync<GatewayForbiddenException>(() =>
                manager.GetJobResultStreamAsync(jobUnmask.JobId, TenantA, degradedUser, pdp, tables));

            var droppedRoleUser = CompletePrincipal(AliceSid.Value, groups: ["grp-finance"], roles: [], scopes: ["sql:read"]);
            await Should.ThrowAsync<GatewayForbiddenException>(() =>
                manager.GetJobResultStreamAsync(jobUnmask.JobId, TenantA, droppedRoleUser, pdp, tables));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Subscription_CdcAll_SingleTenantAndGlobalInvalidation_TerminatesRevokedAdminAndKeepsValidAdminAlive()
    {
        var epochStore = new FixedGovernanceEpochStore(1);
        var epochProvider = new GovernanceEpochProvider(epochStore);
        var options = Options.Create(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = TenantA.Value },
            GraphQL = new GraphQLOptions { MaxSubscriptionsPerSubject = 10, SubscriptionRevalidationSeconds = 60 }
        });

        var orders = new TableIdentifier("default", "public", "orders");
        var activeStream = new CdcStreamDescriptor(
            CdcStreamId.New(),
            "orders-stream",
            orders,
            CdcStreamStatus.Active,
            ActivationCode: null,
            HeadOffset: 0,
            TailOffset: 0,
            SourceWatermark: null,
            LastPolledAt: DateTimeOffset.UtcNow);

        var streamAllowed = true;
        var resolveCalls = 0;
        var resolver = new StubTableAccessResolver((p, t) =>
        {
            Interlocked.Increment(ref resolveCalls);
            return new ResolvedTableAccess(
                new TableMetadata { Identifier = t },
                streamAllowed
                    ? TableAccessDecision.Allowed(t, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
                    : TableAccessDecision.Denied(t, "revoked"),
                TenantA,
                AliceSid,
                p!);
        });

        var streamStore = NullFake.Create<ICdcStreamStore>((m, _) =>
            m.Name == nameof(ICdcStreamStore.ListStreamsAsync)
                ? Task.FromResult<IReadOnlyList<CdcStreamDescriptor>>([activeStream])
                : NullFake.UseDefault);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(options)
            .AddSingleton(epochProvider)
            .AddSingleton<CdcSubscriptionGovernor>()
            .AddSingleton<ITableAccessResolver>(resolver)
            .AddSingleton<IAuditLogRepository>(new InMemoryAuditLogRepository())
            .AddSingleton<Autheris.Application.DataSources.IDataSourceTransportGate>(AllowAllTransportGate.Instance)
            .AddSingleton<ICdcStreamStore>(streamStore)
            .BuildServiceProvider();

        var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, AliceSid.Value),
            new Claim("tenant_id", TenantA.Value),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("exp", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString())
        ], "Bearer"));

        // 1. Valid admin stays connected after a tenant epoch advance.
        var keepAliveChannel = new ControlledCdcChannel();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var validEnum = new Subscription()
            .SubscribeToTableEventsAsync("cdc_all", null, keepAliveChannel, AllowAllStreamEnforcer.Instance, services, adminPrincipal, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        keepAliveChannel.Publish(new CdcEvent("e-1", orders, CdcOperation.Insert, TenantA.Value, null, new Dictionary<string, object?> { ["id"] = 1 }, DateTimeOffset.UtcNow));
        (await validEnum.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).ShouldBeTrue();

        var callsBeforeBump = Volatile.Read(ref resolveCalls);
        epochStore.Bump();
        epochProvider.InvalidateTenant(TenantA.Value);
        await WaitHelper.WaitUntil(() => Volatile.Read(ref resolveCalls) > callsBeforeBump, TimeSpan.FromSeconds(3));

        keepAliveChannel.Publish(new CdcEvent("e-2", orders, CdcOperation.Insert, TenantA.Value, null, new Dictionary<string, object?> { ["id"] = 2 }, DateTimeOffset.UtcNow));
        (await validEnum.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).ShouldBeTrue("valid GovernanceAdmin cdc_all must stay connected across single-table epoch bumps");
        await validEnum.DisposeAsync();

        // 2. When stream access is revoked and global invalidation fires (changedTenant == ""), cdc_all must terminate promptly.
        var revokeChannel = new ControlledCdcChannel();
        using var revokeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var revokedEnum = new Subscription()
            .SubscribeToTableEventsAsync("cdc_all", null, revokeChannel, AllowAllStreamEnforcer.Instance, services, adminPrincipal, revokeCts.Token)
            .GetAsyncEnumerator(revokeCts.Token);

        revokeChannel.Publish(new CdcEvent("e-3", orders, CdcOperation.Insert, TenantA.Value, null, new Dictionary<string, object?> { ["id"] = 3 }, DateTimeOffset.UtcNow));
        (await revokedEnum.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).ShouldBeTrue();

        var nextTask = revokedEnum.MoveNextAsync().AsTask();
        streamAllowed = false;
        epochStore.Bump();
        epochProvider.InvalidateAll();

        (await nextTask.WaitAsync(TimeSpan.FromSeconds(3))).ShouldBeFalse("global InvalidateAll() must trigger revalidation and close revoked SingleTenant cdc_all");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GovernanceDomainService_And_ClassificationWorkflowStateMachine_RollBackDbTransactionWhenAuditOrStepFails()
    {
        var ordersId = new TableIdentifier("postgres", "public", "orders");
        var tableGuid = Guid.NewGuid();
        var conn = new TrackingDomainConnection();
        var dialect = new PostgreSqlGovernanceDialect(() => conn);
        var store = new GovernanceDomainStore(dialect, new FakeTimeProvider(T0));
        var tables = new InMemoryTableMetadataRepository(new TableMetadata
        {
            Identifier = ordersId,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = tableGuid, SourceName = "postgres", SchemaName = "public", TableName = "orders", IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "email", DataType = "varchar" },
                new TableColumn { ColumnName = "ssn", DataType = "varchar" }
            ]
        });

        var consentRepo = NullFake.Create<IConsentRepository>((m, _) =>
            m.Name == nameof(IConsentRepository.GetConsentPageAsync)
                ? Task.FromResult(new Autheris.Application.Governance.Paging.ConsentPage([], NextAfterId: null))
                : NullFake.UseDefault);

        var failingAudit = new InMemoryAuditWriter(new AuditUnavailableException("worm audit down"), throwOnAllWrites: true);
        var domainSvcFailingAudit = new GovernanceDomainService(
            store,
            tables,
            consentRepo,
            failingAudit,
            new GovernanceCapabilities(domainRepository: store),
            timeProvider: new FakeTimeProvider(T0));

        var govAdmin = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, AliceSid.Value),
            new Claim("tenant_id", TenantA.Value),
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ], "Bearer"));
        var govSecCtx = SecurityPrincipalContext.FromPrincipal(govAdmin, TenantA) with { GroupResolutionStatus = GroupResolutionStatus.Complete };

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            domainSvcFailingAudit.SetTableProtectionAsync(govSecCtx, TenantA, ordersId.ToString(), ProtectionClass.Confidential, null));

        conn.CommittedCount.ShouldBe(0, "DB protection mutation must NOT commit when required WORM audit fails");
        conn.RolledBackCount.ShouldBeGreaterThanOrEqualTo(1);

        // Multi-step classification workflow: failing the second column mutation rolls back the entire protection batch.
        conn.ResetCounters();
        conn.ThrowOnNonQueryMatch = "OBJECT_PROTECTION";
        conn.ThrowAfterMatchingNonQueries = 2; // table + 1st column write succeed; 2nd column write throws

        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            ordersId,
            DataOwnerSid: null,
            Status: "PENDING_OWNER_REVIEW",
            Columns:
            [
                new ColumnClassificationProposal("email", "varchar", "Confidential", 1, "PII_EMAIL", "MaskEmail", 0.99, false, ProposedProtectionClass: ProtectionClass.Confidential, ProposedMaskCategory: "PII_EMAIL"),
                new ColumnClassificationProposal("ssn", "varchar", "Secret", 1, "PII_SSN", "MaskHash", 0.99, false, ProposedProtectionClass: ProtectionClass.Secret, ProposedMaskCategory: "PII_SSN")
            ],
            CreatedAtUtc: T0);
        var proposalStore = new InMemoryClassificationProposalStore();
        await proposalStore.SaveProposalAsync(proposal);

        var okAudit = new InMemoryAuditWriter();
        var domainSvcOk = new GovernanceDomainService(
            store,
            tables,
            consentRepo,
            okAudit,
            new GovernanceCapabilities(domainRepository: store),
            timeProvider: new FakeTimeProvider(T0));

        var stateMachine = new ClassificationWorkflowStateMachine(
            new FakeOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<ClassificationWorkflowStateMachine>.Instance,
            proposalStore,
            governanceDomainService: domainSvcOk,
            capabilities: new GovernanceCapabilities(domainRepository: store),
            domainRepository: store,
            tableMetadataRepository: tables);

        var approvalCmd = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            ordersId,
            AliceSid,
            "DATA_GOVERNANCE_REVIEWER",
            "APPROVE",
            [
                new ColumnClassificationDecision("email", "ACCEPT_PROPOSAL", "Confidential", 1, "PII_EMAIL", "MaskEmail", EffectiveProtectionClass: ProtectionClass.Confidential, EffectiveMaskCategory: "PII_EMAIL"),
                new ColumnClassificationDecision("ssn", "ACCEPT_PROPOSAL", "Secret", 1, "PII_SSN", "MaskHash", EffectiveProtectionClass: ProtectionClass.Secret, EffectiveMaskCategory: "PII_SSN")
            ],
            TenantId: TenantA,
            Caller: govAdmin);

        await Should.ThrowAsync<InvalidOperationException>(async () => await stateMachine.TransitionAsync(proposal, approvalCmd));

        conn.CommittedCount.ShouldBe(0, "multi-step table + column protection write must roll back completely on mid-batch failure");
        conn.RolledBackCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    // Helper types after all [Fact]/[Theory] tests.

    private static ClaimsPrincipal CompletePrincipal(string sid, string[]? groups = null, string[]? roles = null, string[]? scopes = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", TenantA.Value),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker(),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete),
            new(Autheris.Domain.Identity.GroupResolutionClaims.ExpiresAtClaimType, T0.AddHours(1).ToString("O"))
        };
        if (groups is not null)
        {
            foreach (var g in groups) claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(g));
        }

        if (roles is not null)
        {
            foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));
        }

        if (scopes is { Length: > 0 })
        {
            claims.Add(new Claim("scp", string.Join(' ', scopes)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static string CreateCompletedFile(string root, string tenant, string jobId, string content)
    {
        var dir = Path.Combine(root, tenant);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{jobId}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private sealed class DelegatingPdp(Func<TableIdentifier, SecurityPrincipalContext, TableMetadata, TableAccessDecision> decide) : IAccessDecisionPoint
    {
        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(decide(request.Object.Identifier, request.Subject, request.Object));

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            SecurityPrincipalContext subject,
            IReadOnlyList<TableMetadata> objects,
            AccessOperation operation,
            AccessTransport transport,
            CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<TableAccessDecision>>(objects.Select(o => decide(o.Identifier, subject, o)).ToArray());

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StaticBufferedSqlService : IGovernedSqlExecutionService
    {
        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => Task.FromResult(new GovernedSqlResult("s", ["id", "ssn"], [new Dictionary<string, object?> { ["id"] = 1, ["ssn"] = "123-45-6789" }], 1, 1));

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FixedGovernanceEpochStore(long epoch) : IGovernanceEpochStore
    {
        private long _epoch = epoch;
        public void Bump() => Interlocked.Increment(ref _epoch);
        public Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(Volatile.Read(ref _epoch));
        public Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(Interlocked.Increment(ref _epoch));
    }

    private sealed class ControlledCdcChannel : ICdcEventChannel
    {
        private readonly System.Threading.Channels.Channel<CdcEvent> _ch = System.Threading.Channels.Channel.CreateUnbounded<CdcEvent>();
        public void Publish(CdcEvent evt) => _ch.Writer.TryWrite(evt);
        public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default)
        {
            _ch.Writer.TryWrite(cdcEvent);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var e in _ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return e;
            }
        }
    }

    private sealed class AllowAllStreamEnforcer : IStreamRlsPolicyEnforcer
    {
        public static readonly AllowAllStreamEnforcer Instance = new();
        public ValueTask<StreamSecurityDecision> EvaluateAndMaskAsync(CdcEvent cdcEvent, ClaimsPrincipal user, CancellationToken ct = default)
            => ValueTask.FromResult(StreamSecurityDecision.Allowed(cdcEvent.After ?? new Dictionary<string, object?>()));
    }

    private sealed class TrackingDomainConnection : DbConnection
    {
        private int _matchingNonQueries;
        public int CommittedCount { get; private set; }
        public int RolledBackCount { get; private set; }
        public string? ThrowOnNonQueryMatch { get; set; }
        public int ThrowAfterMatchingNonQueries { get; set; }
        public void ResetCounters() { CommittedCount = 0; RolledBackCount = 0; _matchingNonQueries = 0; ThrowOnNonQueryMatch = null; ThrowAfterMatchingNonQueries = 0; }
        [AllowNull] public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "stub";
        public override string DataSource => "stub";
        public override string ServerVersion => "0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new TrackingTx(this);
        protected override DbCommand CreateDbCommand() => new TrackingCmd(this);

        private sealed class TrackingTx(TrackingDomainConnection owner) : DbTransaction
        {
            private bool _done;
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            protected override DbConnection DbConnection => owner;
            public override void Commit() { _done = true; owner.CommittedCount++; }
            public override void Rollback() { if (!_done) { _done = true; owner.RolledBackCount++; } }
            protected override void Dispose(bool disposing)
            {
                if (disposing && !_done) owner.RolledBackCount++;
                base.Dispose(disposing);
            }
        }

        private sealed class TrackingCmd(TrackingDomainConnection owner) : DbCommand
        {
            private readonly ParamCol _params = new();
            [AllowNull] public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get => owner; set { } }
            protected override DbParameterCollection DbParameterCollection => _params;
            protected override DbTransaction? DbTransaction { get; set; }
            public override void Cancel() { }
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => new Param();
            public override object? ExecuteScalar() => 1L;
            public override int ExecuteNonQuery()
            {
                if (owner.ThrowOnNonQueryMatch is { } match &&
                    CommandText.Contains(match, StringComparison.OrdinalIgnoreCase) &&
                    ++owner._matchingNonQueries > owner.ThrowAfterMatchingNonQueries)
                {
                    throw new InvalidOperationException("simulated mid-batch persistence failure");
                }

                return 1;
            }

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new EmptyReader();
        }

        private sealed class Param : DbParameter
        {
            public override DbType DbType { get; set; }
            public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
            public override bool IsNullable { get; set; }
            [AllowNull] public override string ParameterName { get; set; } = string.Empty;
            [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
            public override object? Value { get; set; }
            public override bool SourceColumnNullMapping { get; set; }
            public override int Size { get; set; }
            public override void ResetDbType() { }
        }

        private sealed class ParamCol : DbParameterCollection
        {
            private readonly List<DbParameter> _items = [];
            public override int Count => _items.Count;
            public override object SyncRoot => _items;
            public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
            public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
            public override void Clear() => _items.Clear();
            public override bool Contains(object value) => _items.Contains((DbParameter)value);
            public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);
            public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
            public override IEnumerator GetEnumerator() => _items.GetEnumerator();
            public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
            public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
            public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
            public override void Remove(object value) => _items.Remove((DbParameter)value);
            public override void RemoveAt(int index) => _items.RemoveAt(index);
            public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) RemoveAt(i); }
            protected override DbParameter GetParameter(int index) => _items[index];
            protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
            protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
            protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
        }

        private sealed class EmptyReader : DbDataReader
        {
            public override int FieldCount => 0;
            public override bool HasRows => false;
            public override bool IsClosed => false;
            public override int RecordsAffected => 0;
            public override int Depth => 0;
            public override object this[int ordinal] => DBNull.Value;
            public override object this[string name] => DBNull.Value;
            public override bool Read() => false;
            public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(false);
            public override bool NextResult() => false;
            public override bool IsDBNull(int ordinal) => true;
            public override string GetString(int ordinal) => string.Empty;
            public override int GetInt32(int ordinal) => 0;
            public override long GetInt64(int ordinal) => 0;
            public override object GetValue(int ordinal) => DBNull.Value;
            public override int GetOrdinal(string name) => -1;
            public override string GetName(int ordinal) => string.Empty;
            public override bool GetBoolean(int ordinal) => false;
            public override byte GetByte(int ordinal) => 0;
            public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
            public override char GetChar(int ordinal) => '\0';
            public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
            public override string GetDataTypeName(int ordinal) => string.Empty;
            public override Type GetFieldType(int ordinal) => typeof(object);
            public override float GetFloat(int ordinal) => 0;
            public override double GetDouble(int ordinal) => 0;
            public override decimal GetDecimal(int ordinal) => 0;
            public override Guid GetGuid(int ordinal) => Guid.Empty;
            public override short GetInt16(int ordinal) => 0;
            public override DateTime GetDateTime(int ordinal) => DateTime.MinValue;
            public override int GetValues(object[] values) => 0;
            public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
        }
    }
}
