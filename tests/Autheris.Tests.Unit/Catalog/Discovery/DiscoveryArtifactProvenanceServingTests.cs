namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DiscoveryArtifactProvenanceServingTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private static (
        DiscoveryArtifactService Artifacts,
        DiscoveryService Discovery,
        FakeDiscoveryTableRepository Tables,
        FakeDiscoveryArtifactStore Store,
        TableMetadata Table) CreateHarness(
            FakeAccessDecisionPoint pdp,
            EffectiveProtection? protection = null,
            bool serveImportSamples = true,
            bool quarantineSuspectedPii = false,
            bool samplesDisabled = false)
    {
        var tables = new FakeDiscoveryTableRepository();
        var orders = DiscoveryTestBuilder.MakeTable(
            "sales",
            "orders",
            ownerTenantId: Tenant.Value,
            samplesDisabled: samplesDisabled,
            columns:
            [
                ("id", false, false),
                ("email", true, false),
                ("ssn", true, false)
            ]);
        tables.Tables.Add(orders);

        var store = new FakeDiscoveryArtifactStore();
        var gatewayOptions = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
        var discoveryOptions = Options.Create(new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings() });
        var policy = TenantAccessPolicy.FromDeploymentDefaults(Tenant, new AccessPolicyOptions
        {
            ServeImportSamples = serveImportSamples,
            QuarantineSuspectedPiiSamples = quarantineSuspectedPii
        });
        var effProtection = protection ?? new EffectiveProtection(
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

        var artifactService = new DiscoveryArtifactService(
            tables,
            store,
            new FakeDiscoveryOwnershipRepository(),
            new FakeAuditWriter(),
            new GatewayRoleEvaluator(),
            gatewayOptions,
            discoveryOptions,
            decisionPoint: pdp,
            catalogVisibility: new VisibleOnlyCatalogVisibility(orders),
            accessPolicySettings: new StubPolicyProvider(policy),
            protectionProvider: new StubProtectionProvider(_ => effProtection),
            maskingProvider: new ColumnMaskingProvider(gatewayOptions));

        var discoveryService = new DiscoveryService(
            tables,
            pdp,
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            store,
            new FakeAuditWriter(),
            gatewayOptions,
            discoveryOptions,
            logger: NullLogger<DiscoveryService>.Instance,
            protectionProvider: new StubProtectionProvider(_ => effProtection),
            accessPolicySettings: new StubPolicyProvider(policy));

        return (artifactService, discoveryService, tables, store, orders);
    }

    [Fact]
    public async Task Import_catalog_only_serves_secret_samples()
    {
        var (artifacts, _, _, store, table) = CreateHarness(FakeAccessDecisionPoint.DenyAll());
        await store.UpsertAsync(new DiscoveryArtifacts(
            table.Table.Id,
            Tenant.Value,
            """[{"id": 10, "email": "synthetic@example.invalid", "ssn": "000-00-0000"}]""",
            StatisticsJson: null,
            "sha-imp",
            "dbt-ingestion",
            DateTimeOffset.UtcNow,
            1,
            ArtifactProvenance.Import), new Sid("S-1-5-21-SYS-1"));

        var user = DiscoveryTestBuilder.User("S-1-5-21-U1", Tenant.Value);
        var res = await artifacts.GetSamplesAsync(user, table.Identifier.ToString());

        res.Outcome.ShouldBe(DiscoveryArtifactOutcome.Ok);
        res.Payload!.Available.ShouldBeTrue();
        res.Payload.Samples.Count.ShouldBe(1);
        res.Payload.Samples[0]["email"]?.ToString().ShouldBe("synthetic@example.invalid");
    }

    [Fact]
    public async Task Upload_catalog_only_serves_nothing()
    {
        var (artifacts, _, _, store, table) = CreateHarness(FakeAccessDecisionPoint.DenyAll());
        await store.UpsertAsync(new DiscoveryArtifacts(
            table.Table.Id,
            Tenant.Value,
            """[{"id": 1, "email": "real@corp.test", "ssn": "123-45-6789"}]""",
            StatisticsJson: null,
            "sha-up",
            "uploader",
            DateTimeOffset.UtcNow,
            1,
            ArtifactProvenance.Upload), new Sid("S-1-5-21-U2"));

        var user = DiscoveryTestBuilder.User("S-1-5-21-U1", Tenant.Value);
        var res = await artifacts.GetSamplesAsync(user, table.Identifier.ToString());

        res.Outcome.ShouldBe(DiscoveryArtifactOutcome.Ok);
        res.Payload!.Available.ShouldBeFalse();
        res.Payload.Samples.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_with_data_grant_masks_pii()
    {
        var pdp = new FakeAccessDecisionPoint(req => TableAccessDecision.Allowed(
            req.Object.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Mask,
                ["ssn"] = ColumnAccessLevel.Deny
            }));

        var (artifacts, _, _, store, table) = CreateHarness(pdp);
        await store.UpsertAsync(new DiscoveryArtifacts(
            table.Table.Id,
            Tenant.Value,
            """[{"id": 1, "email": "alice@corp.test", "ssn": "123-45-6789"}]""",
            StatisticsJson: null,
            "sha-up",
            "uploader",
            DateTimeOffset.UtcNow,
            1,
            ArtifactProvenance.Upload), new Sid("S-1-5-21-U2"));

        var user = DiscoveryTestBuilder.User("S-1-5-21-U1", Tenant.Value);
        var res = await artifacts.GetSamplesAsync(user, table.Identifier.ToString());

        res.Payload!.Available.ShouldBeTrue();
        res.Payload.Samples[0].ContainsKey("ssn").ShouldBeFalse();
        res.Payload.Samples[0]["email"]?.ToString().ShouldNotBe("alice@corp.test");
    }

    [Fact]
    public async Task Unknown_treated_as_upload()
    {
        var (artifacts, _, _, store, table) = CreateHarness(FakeAccessDecisionPoint.DenyAll());
        await store.UpsertAsync(new DiscoveryArtifacts(
            table.Table.Id,
            Tenant.Value,
            """[{"id": 1, "email": "legacy@corp.test"}]""",
            StatisticsJson: null,
            "sha-unk",
            "legacy",
            DateTimeOffset.UtcNow,
            1,
            ArtifactProvenance.Unknown), new Sid("S-1-5-21-U2"));

        var user = DiscoveryTestBuilder.User("S-1-5-21-U1", Tenant.Value);
        var res = await artifacts.GetSamplesAsync(user, table.Identifier.ToString());

        res.Payload!.Available.ShouldBeFalse();
    }

    [Fact]
    public async Task Import_row_has_no_statistics()
    {
        var store = new FakeDiscoveryArtifactStore();
        await Should.ThrowAsync<ArgumentException>(async () => await store.UpsertAsync(new DiscoveryArtifacts(
            Guid.NewGuid(),
            Tenant.Value,
            """[{"id":1}]""",
            StatisticsJson: """{"table":{"approxRowCount":10}}""",
            "sha",
            "dbt",
            DateTimeOffset.UtcNow,
            1,
            ArtifactProvenance.Import), new Sid("S-1-5-21-U1")));
    }

    [Fact]
    public async Task Statistics_via_pdp()
    {
        var (_, discoveryDenied, _, storeDenied, tableDenied) = CreateHarness(FakeAccessDecisionPoint.DenyAll());
        await storeDenied.UpsertAsync(new DiscoveryArtifacts(
            tableDenied.Table.Id,
            Tenant.Value,
            null,
            StatisticsJson: """{"table":{"approxRowCount":99},"columns":{"id":{"approxDistinctCount":5},"email":{"approxDistinctCount":5}}}""",
            "sha-st",
            "uploader",
            DateTimeOffset.UtcNow,
            1,
            ArtifactProvenance.Upload), new Sid("S-1-5-21-U2"));

        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-1-5-21-U1", Tenant.Value), Tenant);
        var deniedObj = await discoveryDenied.GetAsync(tableDenied.Identifier, caller);
        deniedObj.ShouldNotBeNull();
        (deniedObj!.Statistics?.PartitionColumns ?? []).ShouldBeEmpty();
        deniedObj.Columns.ShouldAllBe(c => c.Statistics == null);

        var pdpAllow = new FakeAccessDecisionPoint(req => TableAccessDecision.Allowed(
            req.Object.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Clear,
                ["ssn"] = ColumnAccessLevel.Deny
            }));
        var (_, discoveryAllowed, _, storeAllowed, tableAllowed) = CreateHarness(pdpAllow);
        await storeAllowed.UpsertAsync(new DiscoveryArtifacts(
            tableAllowed.Table.Id,
            Tenant.Value,
            null,
            StatisticsJson: """{"table":{"approxRowCount":99},"columns":{"id":{"approxDistinctCount":5},"ssn":{"approxDistinctCount":99}}}""",
            "sha-st2",
            "uploader",
            DateTimeOffset.UtcNow,
            1,
            ArtifactProvenance.Upload), new Sid("S-1-5-21-U2"));

        var allowedObj = await discoveryAllowed.GetAsync(tableAllowed.Identifier, caller);
        allowedObj.ShouldNotBeNull();
        allowedObj!.Statistics.ShouldNotBeNull();
        allowedObj.Columns.First(c => c.Name == "id").Statistics.ShouldNotBeNull();
        allowedObj.Columns.First(c => c.Name == "ssn").State.ShouldBe(DiscoveryColumnState.NoAccess);
        allowedObj.Columns.First(c => c.Name == "ssn").Statistics.ShouldBeNull();
        CatalogDiscoveryProjection.ToDatasetDetail(allowedObj, null).Columns.ShouldNotContain(c => c.Name == "ssn");
    }

    [Fact]
    public async Task Samples_disabled_honoured()
    {
        var (artifacts, _, _, store, table) = CreateHarness(FakeAccessDecisionPoint.AllowAll(), samplesDisabled: true);
        await store.UpsertAsync(new DiscoveryArtifacts(
            table.Table.Id,
            Tenant.Value,
            """[{"id": 1, "email": "synthetic@example.invalid"}]""",
            null,
            "sha-imp",
            "dbt-ingestion",
            DateTimeOffset.UtcNow,
            1,
            ArtifactProvenance.Import), new Sid("S-1-5-21-SYS-1"));

        var user = DiscoveryTestBuilder.User("S-1-5-21-U1", Tenant.Value);
        var res = await artifacts.GetSamplesAsync(user, table.Identifier.ToString());

        res.Payload!.Available.ShouldBeFalse();
        res.Payload.Samples.ShouldBeEmpty();
    }

    private sealed class StubProtectionProvider(Func<TableIdentifier, EffectiveProtection> factory) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default)
            => ValueTask.FromResult(factory(table));
    }

    private sealed class StubPolicyProvider(TenantAccessPolicy policy) : IAccessPolicySettingsProvider
    {
        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(policy);
        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenant, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class VisibleOnlyCatalogVisibility(TableMetadata table) : ICatalogVisibilityService
    {
        public ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, SchemaContractDefinition? contract = null, CancellationToken ct = default)
            => ValueTask.FromResult(CatalogVisibleSet.Empty);

        public ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier tableId, CancellationToken ct = default)
            => ValueTask.FromResult<CatalogVisibleRow?>(tableId.Equals(table.Identifier) ? new CatalogVisibleRow(table, CatalogOutputShape.Full) : null);

        public ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlySet<TableIdentifier>>(new HashSet<TableIdentifier>([table.Identifier]));
    }
}
