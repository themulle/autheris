namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Governance.Protection;
using Autheris.Application.Governance.Services;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Protection;
using Autheris.Application.Policy.Services;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.Governance.Classification;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class Issue1144And1150ClassificationAndOpenDomainTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-1144-1-1001");
    private static readonly Sid GroupSid = new("S-1-5-21-1144-1-3001");
    private static readonly TableIdentifier OrdersId = new("demo", "main", "auftraege");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Issue1144_GroupDataGrant_WithClearBaseRule_MasksPiiWithoutUnmaskGrant_AcrossAllSurfaces()
    {
        using var harness = new TestHarness(CatalogVisibilityMode.Open, ProtectionClass.Confidential);
        await harness.SeedOrdersTableAsync(withPiiClassifications: true);
        await harness.SeedGroupDataGrantAsync();

        var principal = CreateCompletePrincipal(UserSid, GroupSid);
        var subject = SecurityPrincipalContext.FromPrincipal(principal, Tenant, "Bearer", timeProvider: harness.Time);

        var pdpDecision = await harness.Pdp.DecideAsync(new AccessRequest(subject, harness.Orders, AccessOperation.Read, AccessTransport.GraphQl));
        pdpDecision.IsAllowed.ShouldBeTrue(string.Join("; ", pdpDecision.DeniedReasons));
        pdpDecision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        pdpDecision.GetColumnAccess("rabatt_hinweis").ShouldBe(ColumnAccessLevel.Mask);
        pdpDecision.GetColumnAccess("interne_kalkulation").ShouldBe(ColumnAccessLevel.Mask);
        (pdpDecision.ColumnMasks as ColumnMaskPolicy)?.HasMask("rabatt_hinweis").ShouldBeTrue();
        (pdpDecision.ColumnMasks as ColumnMaskPolicy)?.HasMask("interne_kalkulation").ShouldBeTrue();

        var describe = await harness.McpCatalog.DescribeDatasetAsync(principal, OrdersId.ToString());
        describe.Columns.Single(c => c.Name == "id").Access.ShouldBe("clear");
        describe.Columns.Single(c => c.Name == "rabatt_hinweis").Access.ShouldBe("mask");
        describe.Columns.Single(c => c.Name == "interne_kalkulation").Access.ShouldBe("mask");

        var access = await harness.Explainer.ExplainMyAccessAsync(principal, OrdersId);
        access.Decision.ShouldBe("ALLOWED");
        access.AllowedColumns.ShouldBe(["id"]);
        access.MaskedColumns.OrderBy(static c => c, StringComparer.Ordinal).ShouldBe(["interne_kalkulation", "rabatt_hinweis"]);
    }

    [Fact]
    public async Task Issue1144_ActiveGroupUnmaskGrant_LiftsClassificationMask_AcrossAllSurfaces()
    {
        using var harness = new TestHarness(CatalogVisibilityMode.Open, ProtectionClass.Confidential);
        await harness.SeedOrdersTableAsync(withPiiClassifications: true);
        await harness.SeedGroupDataGrantAsync();
        await harness.SeedGroupUnmaskGrantAsync("rabatt_hinweis", "interne_kalkulation");

        var principal = CreateCompletePrincipal(UserSid, GroupSid);
        var subject = SecurityPrincipalContext.FromPrincipal(principal, Tenant, "Bearer", timeProvider: harness.Time);

        var pdpDecision = await harness.Pdp.DecideAsync(new AccessRequest(subject, harness.Orders, AccessOperation.Read, AccessTransport.GraphQl));
        pdpDecision.IsAllowed.ShouldBeTrue(string.Join("; ", pdpDecision.DeniedReasons));
        pdpDecision.GetColumnAccess("rabatt_hinweis").ShouldBe(ColumnAccessLevel.Clear);
        pdpDecision.GetColumnAccess("interne_kalkulation").ShouldBe(ColumnAccessLevel.Clear);

        var describe = await harness.McpCatalog.DescribeDatasetAsync(principal, OrdersId.ToString());
        describe.Columns.Single(c => c.Name == "rabatt_hinweis").Access.ShouldBe("clear");
        describe.Columns.Single(c => c.Name == "interne_kalkulation").Access.ShouldBe("clear");

        var access = await harness.Explainer.ExplainMyAccessAsync(principal, OrdersId);
        access.Decision.ShouldBe("ALLOWED");
        access.AllowedColumns.OrderBy(static c => c, StringComparer.Ordinal).ShouldBe(["id", "interne_kalkulation", "rabatt_hinweis"]);
        access.MaskedColumns.ShouldBeEmpty();
    }

    [Fact]
    public async Task Issue1144_BaselineAllowedPublicTable_MasksClassifiedColumnWithoutUnmaskGrant()
    {
        using var harness = new TestHarness(CatalogVisibilityMode.Open, ProtectionClass.Public);
        await harness.SeedOrdersTableAsync(withPiiClassifications: true);

        var principal = CreateCompletePrincipal(UserSid, GroupSid, includeRealmKind: true);
        var subject = SecurityPrincipalContext.FromPrincipal(principal, Tenant, "Bearer", timeProvider: harness.Time);

        var pdpDecision = await harness.Pdp.DecideAsync(new AccessRequest(subject, harness.Orders, AccessOperation.Read, AccessTransport.GraphQl));
        pdpDecision.IsAllowed.ShouldBeTrue(string.Join("; ", pdpDecision.DeniedReasons));
        pdpDecision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        pdpDecision.GetColumnAccess("rabatt_hinweis").ShouldBe(ColumnAccessLevel.Mask);

        var access = await harness.Explainer.ExplainMyAccessAsync(principal, OrdersId);
        access.Decision.ShouldBe("ALLOWED");
        access.AllowedColumns.ShouldBe(["id"]);
        access.MaskedColumns.OrderBy(static c => c, StringComparer.Ordinal).ShouldBe(["interne_kalkulation", "rabatt_hinweis"]);

        using var unmaskedHarness = new TestHarness(CatalogVisibilityMode.Open, ProtectionClass.Public);
        await unmaskedHarness.SeedOrdersTableAsync(withPiiClassifications: true);
        await unmaskedHarness.SeedGroupUnmaskGrantAsync("rabatt_hinweis", "interne_kalkulation");
        var unmaskedAccess = await unmaskedHarness.Explainer.ExplainMyAccessAsync(principal, OrdersId);
        unmaskedAccess.Decision.ShouldBe("ALLOWED");
        unmaskedAccess.AllowedColumns.OrderBy(static c => c, StringComparer.Ordinal).ShouldBe(["id", "interne_kalkulation", "rabatt_hinweis"]);
        unmaskedAccess.MaskedColumns.ShouldBeEmpty();
    }

    [Fact]
    public async Task Issue1150_OpenDomain_WithoutDataGrant_AllowsDescribeDataset_AndExplainsMissingGrant()
    {
        using var harness = new TestHarness(CatalogVisibilityMode.Open, ProtectionClass.Confidential);
        await harness.SeedOrdersTableAsync(withPiiClassifications: false);

        var principal = CreateCompletePrincipal(UserSid, GroupSid);
        var session = CreateSession();

        var describeResult = await harness.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.DescribeDataset, $$"""{"dataset":"{{OrdersId}}","brief":false}"""),
            session);

        describeResult.IsSuccess.ShouldBeTrue(describeResult.ErrorMessage ?? describeResult.ContentJson);
        using (var doc = JsonDocument.Parse(describeResult.ContentJson))
        {
            doc.RootElement.GetProperty("id").GetString().ShouldBe(OrdersId.ToString());
            doc.RootElement.GetProperty("accessStatus").GetString().ShouldBe("REQUEST_ACCESS");
        }

        var access = await harness.Explainer.ExplainMyAccessAsync(principal, OrdersId);
        access.Decision.ShouldBe("DENIED");
        access.ConsentDescription.ShouldNotBeNullOrWhiteSpace();
        access.ConsentDescription.ShouldNotBe(EffectiveAccessExplanationService.TableNotFoundMessage);
        access.DeniedColumns.OrderBy(static c => c, StringComparer.Ordinal).ShouldBe(["id", "interne_kalkulation", "rabatt_hinweis"]);
    }

    [Fact]
    public async Task Issue1150_ClosedDomain_WithoutDataGrant_FailsClosed_OnDescribeDatasetAndDescribeMyAccess()
    {
        using var harness = new TestHarness(CatalogVisibilityMode.OnRequest, ProtectionClass.Confidential);
        await harness.SeedOrdersTableAsync(withPiiClassifications: false);

        var principal = CreateCompletePrincipal(UserSid, GroupSid);
        var session = CreateSession();

        var describeResult = await harness.Guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.DescribeDataset, $$"""{"dataset":"{{OrdersId}}","brief":false}"""),
            session);

        describeResult.IsSuccess.ShouldBeFalse();
        (describeResult.ErrorMessage ?? string.Empty).ShouldContain("Access denied to tool 'describe_dataset' by ABAC security policy.");

        var access = await harness.Explainer.ExplainMyAccessAsync(principal, OrdersId);
        access.Decision.ShouldBe("DENIED");
        access.ConsentDescription.ShouldBe(EffectiveAccessExplanationService.TableNotFoundMessage);
        access.DeniedColumns.ShouldBeEmpty();
    }

    private static ClaimsPrincipal CreateCompletePrincipal(Sid userSid, Sid groupSid, bool includeRealmKind = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, userSid.Value),
            new("oid", Guid.NewGuid().ToString("D")),
            IdentityClaimIssuers.CreateGroupSidClaim(groupSid.Value),
            new("tenant_id", Tenant.Value),
            new("scp", "Apps.Read")
        };
        if (includeRealmKind)
        {
            claims.Add(new Claim("autheris:realm_kind", nameof(IdentityRealmKind.EntraWorkforce), ClaimValueTypes.String, IdentityClaimIssuers.Pipeline));
        }

        return PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")));
    }

    private static McpSessionContext CreateSession() => new(
        "sess-1150",
        "apps-client",
        Tenant.Value,
        FixedNow,
        FixedNow.AddHours(1),
        UserSid: UserSid.Value,
        GroupSids: [GroupSid.Value],
        AdditionalClaims: new Dictionary<string, string> { ["scp"] = "Apps.Read" });

    private sealed class TestHarness : IDisposable
    {
        private readonly FakeDiscoveryTableRepository _tables = new();
        private readonly FakeConsentRepository _rawConsents = new();
        private readonly AttributeValidatingConsentRepository _consents;
        private readonly FakeContainerDomainRepository _domains = new();
        private readonly FakeCatalogGrantRepository _catalogGrants = new();
        private readonly Dictionary<TableIdentifier, EffectiveProtection> _protections = [];
        private readonly GovernanceEpochProvider _epochs;
        private readonly ContainerVisibilitySnapshotProvider _containerVisibility;
        private readonly UnmaskGrantResolver _unmaskResolver;
        private readonly TestInMemoryClassificationStore _classStore = new(Tenant.Value);
        private readonly GovernanceDomain _domain;
        private readonly ProtectionClass _tableClass;

        public AdjustableClassificationTimeProvider Time { get; } = new(FixedNow);
        public AccessDecisionPoint Pdp { get; }
        public CatalogVisibilityService Visibility { get; }
        public DiscoveryService Discovery { get; }
        public McpDatasetCatalog McpCatalog { get; }
        public EffectiveAccessExplanationService Explainer { get; }
        public AiDataGuardrailService Guardrail { get; }
        public TableMetadata Orders { get; private set; } = null!;

        public TestHarness(CatalogVisibilityMode visibilityMode, ProtectionClass tableClass)
        {
            _tableClass = tableClass;
            var opts = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
            var discoveryOpts = Options.Create(new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings() });
            _consents = new AttributeValidatingConsentRepository(_rawConsents, new NoOpMaterializationGuard());
            _domain = _domains.Add(Tenant, "demo", visibilityMode);
            _epochs = new GovernanceEpochProvider(_domains, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }));
            _containerVisibility = new ContainerVisibilitySnapshotProvider(
                _domains, _catalogGrants, _consents, new FakeGovernanceCapabilities(), _epochs);
            _unmaskResolver = new UnmaskGrantResolver(_consents, _epochs, timeProvider: Time);

            var maskingProvider = new SqlDataMaskingProvider(opts, timeProvider: Time);
            var classResolver = new ClassificationAccessResolver(maskingProvider, _classStore, Time);
            var typed = new TypedPolicySources(
                new RowPolicyFactory(new PolicyPredicateRenderer()),
                new ColumnMaskPolicyFactory(maskingProvider),
                ClassificationResolver: classResolver);
            var policySettings = new StubPolicyProvider(TenantAccessPolicy.FromDeploymentDefaults(Tenant, new AccessPolicyOptions()));
            var protectionProvider = new MapProtectionProvider(_protections);

            Pdp = new AccessDecisionPoint(
                opts,
                new ConsentResolutionService(),
                _consents,
                NullConsentCacheService.Instance,
                mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
                typedPolicies: typed,
                timeProvider: Time,
                protectionProvider: protectionProvider,
                accessPolicySettings: policySettings,
                unmaskResolver: _unmaskResolver);

            Visibility = new CatalogVisibilityService(
                _tables,
                _consents,
                opts,
                NullLogger<CatalogVisibilityService>.Instance,
                protectionProvider: protectionProvider,
                containerVisibility: _containerVisibility,
                unmaskResolver: _unmaskResolver);

            Discovery = new DiscoveryService(
                _tables,
                Pdp,
                _consents,
                new FakeDiscoveryApprovalRepository(),
                new FakeDiscoveryOwnershipRepository(),
                new FakeDiscoveryArtifactStore(),
                new FakeAuditWriter(),
                opts,
                discoveryOpts,
                timeProvider: Time,
                logger: NullLogger<DiscoveryService>.Instance,
                classificationRepository: _classStore,
                protectionProvider: protectionProvider,
                accessPolicySettings: policySettings,
                containerVisibility: _containerVisibility,
                unmaskResolver: _unmaskResolver);

            Explainer = new EffectiveAccessExplanationService(
                _tables,
                _consents,
                new ConsentResolutionService(),
                new FakeDiscoveryOwnershipRepository(),
                opts,
                timeProvider: Time,
                decisionPoint: Pdp,
                catalogVisibility: Visibility);

            McpCatalog = new McpDatasetCatalog(
                _tables,
                _consents,
                new NoopExecutionService(),
                NullAuditLogRepository.Instance,
                options: opts,
                explanationService: Explainer,
                visibilityService: Visibility,
                transportGate: AllowAllTransportGate.Instance,
                discoveryService: Discovery,
                classificationResolver: classResolver);

            var registry = new McpToolRegistry(opts, new FixedDemoSwitch());
            Guardrail = new AiDataGuardrailService(
                registry,
                opts,
                NullLogger<AiDataGuardrailService>.Instance,
                NullAuditLogRepository.Instance,
                queryExecutor: new DescribeOperationExecutor(McpCatalog, Time),
                decisionPoint: Pdp,
                tableMetadataRepository: _tables,
                consentRepository: _consents,
                catalogVisibility: Visibility,
                timeProvider: Time);
        }

        public Task SeedOrdersTableAsync(bool withPiiClassifications)
        {
            var tableId = Guid.NewGuid();
            var colId = Guid.NewGuid();
            var colRabatt = Guid.NewGuid();
            var colKalk = Guid.NewGuid();

            Dictionary<string, IReadOnlyList<ColumnClassification>>? colClasses = withPiiClassifications
                ? new(StringComparer.OrdinalIgnoreCase)
                {
                    ["rabatt_hinweis"] = [DbtPii(colRabatt)],
                    ["interne_kalkulation"] = [DbtPii(colKalk)]
                }
                : null;

            Orders = new TableMetadata
            {
                Identifier = OrdersId,
                OwnerTenantId = Tenant.Value,
                Table = new Table
                {
                    Id = tableId,
                    SourceName = OrdersId.Domain,
                    SchemaName = OrdersId.Schema,
                    TableName = OrdersId.TableName,
                    SourceType = "PostgreSql",
                    IsActive = true
                },
                Columns =
                [
                    new TableColumn { Id = colId, TableId = tableId, ColumnName = "id", DataType = "int" },
                    new TableColumn { Id = colRabatt, TableId = tableId, ColumnName = "rabatt_hinweis", DataType = "text" },
                    new TableColumn { Id = colKalk, TableId = tableId, ColumnName = "interne_kalkulation", DataType = "text" }
                ],
                ColumnClassifications = colClasses ?? new(StringComparer.OrdinalIgnoreCase)
            };

            _tables.Tables.Add(Orders);
            _protections[OrdersId] = new EffectiveProtection(
                new ContainerRef(ContainerKind.Domain, _domain.Id),
                OwnerGroupSid: null,
                _tableClass,
                _tableClass,
                TableMaskCategory: null,
                new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
                _domain.Visibility);
            return Task.CompletedTask;
        }

        public Task SeedGroupDataGrantAsync() =>
            _rawConsents.CreateConsentAsync(new Consent
            {
                Id = Guid.NewGuid(),
                TenantId = Tenant,
                TableIdentifier = OrdersId,
                GranteeType = GranteeType.Group,
                GranteeSid = GroupSid,
                Effect = ConsentEffect.Allow,
                GrantKind = PermissionGrantKind.Data,
                GrantSource = ConsentGrantSource.Declarative,
                ValidFrom = FixedNow.AddDays(-1),
                ValidTo = FixedNow.AddDays(5),
                ColumnRules =
                [
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "rabatt_hinweis", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "rabatt_hinweis", AccessLevel = ColumnAccessLevel.Mask, ApplicableScopes = ["Agent.Read"] },
                    new ConsentColumnRule { ColumnName = "interne_kalkulation", AccessLevel = ColumnAccessLevel.Clear }
                ]
            });

        public Task SeedGroupUnmaskGrantAsync(params string[] columns) =>
            _rawConsents.CreateConsentAsync(new Consent
            {
                Id = Guid.NewGuid(),
                TenantId = Tenant,
                TableIdentifier = OrdersId,
                GranteeType = GranteeType.Group,
                GranteeSid = GroupSid,
                Effect = ConsentEffect.Allow,
                GrantKind = PermissionGrantKind.Unmask,
                TargetColumns = [.. columns],
                ValidFrom = FixedNow.AddDays(-1),
                ValidTo = FixedNow.AddDays(5)
            });

        private static ColumnClassification DbtPii(Guid columnId) => new()
        {
            TableColumnId = columnId.ToString("D"),
            Scope = ClassificationScopes.Global,
            Code = ClassificationScopes.PiiCode,
            Source = "DBT",
            AssignedBy = "dbt-sync",
            AssignedAt = FixedNow.AddDays(-1)
        };

        public void Dispose()
        {
            _unmaskResolver.Dispose();
            _containerVisibility.Dispose();
            _epochs.Dispose();
        }
    }

    private sealed class DescribeOperationExecutor(IMcpDatasetCatalog catalog, TimeProvider time) : IMcpQueryExecutor
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public async Task<string> ExecuteOperationAsync(
            McpToolDefinition tool, string argumentsJson, McpSessionContext sessionContext, CancellationToken cancellationToken = default)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var dataset = doc.RootElement.GetProperty("dataset").GetString()!;
            var brief = doc.RootElement.TryGetProperty("brief", out var b) && b.ValueKind == JsonValueKind.True;
            var principal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext, time)!;
            var result = await catalog.DescribeDatasetAsync(principal, dataset, language: null, brief, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
    }

    private sealed class FixedDemoSwitch : IDemoDataSwitch { public bool Enabled => false; }

    private sealed class NoOpMaterializationGuard : IConsentMaterializationGuard
    {
        public ValueTask EnsureMayMaterializeAsync(TenantId tenant, TableIdentifier table, IReadOnlyList<ConsentRowFilter> filters, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class MapProtectionProvider(IReadOnlyDictionary<TableIdentifier, EffectiveProtection> map) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(map[table]);
    }

    private sealed class StubPolicyProvider(TenantAccessPolicy policy) : IAccessPolicySettingsProvider
    {
        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenantId, CancellationToken ct = default) => Task.FromResult(policy);
        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenantId, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoopExecutionService : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal, TableIdentifier table, int? first = null, int? after = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal, TableIdentifier table, int? first, int? after, IReadOnlyDictionary<string, object?>? queryArguments,
            IReadOnlyList<string>? requestedFields = null, IReadOnlyDictionary<string, string[]>? requestHeaders = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
