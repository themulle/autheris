namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class McpCatalogSampleMaskingTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly TableIdentifier TableId = new("sales", "dbo", "customers");

    [Fact]
    public async Task Same_as_rest()
    {
        var tables = new FakeDiscoveryTableRepository();
        var customers = DiscoveryTestBuilder.MakeTable(
            "sales",
            "customers",
            ownerTenantId: Tenant.Value,
            columns:
            [
                ("id", false, false),
                ("email", true, false),
                ("ssn", true, false)
            ]);
        tables.Tables.Add(customers);

        var consents = new FakeConsentRepository();
        var callerSid = new Sid("S-1-5-21-USER-1");
        await consents.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = callerSid,
            TableIdentifier = TableId,
            Effect = ConsentEffect.Allow,
            ColumnSetMode = ColumnSetMode.Explicit,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Deny }
            ]
        });

        var protection = new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            null,
            ProtectionClass.Secret,
            ProtectionClass.Secret,
            null,
            new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new(ProtectionClass.Secret, "email")
            },
            CatalogVisibilityMode.Open);

        var gatewayOptions = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
        var discoveryOptions = Options.Create(new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings() });
        var policy = TenantAccessPolicy.FromDeploymentDefaults(Tenant, new AccessPolicyOptions());
        var pdp = new AccessDecisionPoint(
            gatewayOptions,
            new ConsentResolutionService(),
            consents,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            protectionProvider: new StubProtectionProvider(protection),
            accessPolicySettings: new StubPolicyProvider(policy));

        var artifactStore = new FakeDiscoveryArtifactStore();
        await artifactStore.UpsertAsync(new DiscoveryArtifacts(
            customers.Table.Id,
            Tenant.Value,
            """[{"id": 1, "email": "alice@corp.test", "ssn": "123-45-6789"}]""",
            """{"table":{"approxRowCount":100},"columns":{"id":{"approxDistinctCount":10},"email":{"approxDistinctCount":10},"ssn":{"approxDistinctCount":10}}}""",
            "sha-up",
            "uploader",
            DateTimeOffset.UtcNow,
            1,
            ArtifactProvenance.Upload), new Sid("S-1-5-21-ADM-1"));

        var restArtifacts = new DiscoveryArtifactService(
            tables,
            artifactStore,
            new FakeDiscoveryOwnershipRepository(),
            new FakeAuditWriter(),
            new GatewayRoleEvaluator(),
            gatewayOptions,
            discoveryOptions,
            decisionPoint: pdp,
            protectionProvider: new StubProtectionProvider(protection),
            maskingProvider: new ColumnMaskingProvider(gatewayOptions));

        var discoveryService = new DiscoveryService(
            tables,
            pdp,
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            artifactStore,
            new FakeAuditWriter(),
            gatewayOptions,
            discoveryOptions,
            logger: NullLogger<DiscoveryService>.Instance,
            protectionProvider: new StubProtectionProvider(protection),
            accessPolicySettings: new StubPolicyProvider(policy));

        var maskingProvider = new ColumnMaskingProvider(gatewayOptions);
        var exec = new CapturingExecutionService((p, t) =>
        {
            var req = new AccessRequest(SecurityPrincipalContext.FromPrincipal(p!, Tenant), customers, AccessOperation.Read, AccessTransport.Mcp);
            var dec = pdp.DecideAsync(req).AsTask().GetAwaiter().GetResult();
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = 1,
                ["email"] = maskingProvider.MaskValue("email", "alice@corp.test", new MaskingRule { RuleType = "REDACT", Replacement = "[REDACTED]" })
            };
            return ([row], dec);
        });

        var mcp = new McpDatasetCatalog(
            tables,
            consents,
            exec,
            new NoopAuditRepo(),
            options: gatewayOptions,
            visibilityService: new CatalogVisibilityAdapter(discoveryService),
            transportGate: AllowAllTransportGate.Instance,
            discoveryService: discoveryService);

        var callerPrincipal = DiscoveryTestBuilder.User(callerSid.Value, Tenant.Value);
        var restSamples = await restArtifacts.GetSamplesAsync(callerPrincipal, TableId.ToString());
        var restDescribe = await discoveryService.GetAsync(TableId, DiscoveryTestBuilder.Caller(callerPrincipal, Tenant));
        var mcpDescribe = await mcp.DescribeDatasetAsync(callerPrincipal, TableId.ToString());
        var mcpSample = await mcp.SampleRowsAsync(callerPrincipal, TableId.ToString(), 5);

        restDescribe.ShouldNotBeNull();
        restDescribe!.Columns.Single(c => c.Name == "ssn").State.ShouldBe(DiscoveryColumnState.NoAccess);
        CatalogDiscoveryProjection.ToDatasetDetail(restDescribe, null).Columns.ShouldNotContain(c => c.Name == "ssn");
        mcpDescribe.Columns.ShouldNotContain(c => c.Name == "ssn");
        restDescribe.Columns.Single(c => c.Name == "email").State.ShouldBe(DiscoveryColumnState.Masked);
        mcpDescribe.Columns.Single(c => c.Name == "email").Access.ShouldBe("mask");

        restSamples.Payload!.Samples[0].ContainsKey("ssn").ShouldBeFalse();
        restSamples.Payload.Samples[0]["email"]?.ToString().ShouldNotBe("alice@corp.test");
        mcpSample.Rows[0].ContainsKey("ssn").ShouldBeFalse();
        mcpSample.Rows[0]["email"]?.ToString().ShouldNotBe("alice@corp.test");
    }

    private sealed class StubProtectionProvider(EffectiveProtection protection) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default)
            => ValueTask.FromResult(protection);
    }

    private sealed class StubPolicyProvider(TenantAccessPolicy policy) : IAccessPolicySettingsProvider
    {
        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(policy);
        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenant, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class CapturingExecutionService(Func<ClaimsPrincipal?, TableIdentifier, (IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> execute) : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal,
            TableIdentifier table,
            int? first = null,
            int? after = null,
            CancellationToken ct = default)
            => Task.FromResult(execute(principal, table));

        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal,
            TableIdentifier table,
            int? first,
            int? after,
            IReadOnlyDictionary<string, object?>? queryArguments,
            IReadOnlyList<string>? requestedFields = null,
            IReadOnlyDictionary<string, string[]>? requestHeaders = null,
            CancellationToken ct = default)
            => Task.FromResult(execute(principal, table));

        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(execute(principal, table).Decision);
    }

    private sealed class NoopAuditRepo : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class CatalogVisibilityAdapter(DiscoveryService discovery) : ICatalogVisibilityService
    {
        public ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, SchemaContractDefinition? contract = null, CancellationToken ct = default) =>
            ValueTask.FromResult(CatalogVisibleSet.Empty);

        public async ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier tableId, CancellationToken ct = default)
        {
            var obj = await discovery.GetAsync(tableId, caller, ct);
            if (obj is null) return null;
            var meta = new TableMetadata
            {
                Identifier = tableId,
                Table = new Table { Id = Guid.NewGuid(), SourceName = tableId.Domain, SchemaName = tableId.Schema, TableName = tableId.TableName, IsActive = true },
                Columns = obj.Columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.DataType }).ToList()
            };
            return new CatalogVisibleRow(meta, CatalogOutputShape.Full);
        }

        public ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlySet<TableIdentifier>>(new HashSet<TableIdentifier>(candidates));
    }
}
