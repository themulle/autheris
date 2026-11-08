namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Hosting;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Models;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Federation.Services;
using Autheris.Application.Identity;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Application.Webhooks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.Dbt;
using Autheris.Extensions.OpenMetadata;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security unit tests for Batch 5 advisories (Group B):
/// 1. GHSA-xpw9-4m63-69pf
/// 2. GHSA-95f9-ffc7-vw2c
/// 3. GHSA-jfrx-2mgc-pfh5
/// 4. GHSA-547h-29x4-m4ch
/// 5. GHSA-hw85-rqjh-cwxx
/// 6. GHSA-wvwx-q6jg-44wv
/// 7. GHSA-4qx8-fv2p-q8r2
/// </summary>
[Trait("Category", "Security")]
public sealed class Batch5SecurityTestsB : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    private string CreateTempDirectory(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirectories.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    // =========================================================================
    // 1. GHSA-xpw9-4m63-69pf
    // =========================================================================

    [Fact]
    public void Ghsa_xpw9_69pf_regression()
    {
        Case_xpw9_69pf_1();
        Case_xpw9_69pf_2();
    }

    private void Case_xpw9_69pf_1()
    {
        var options = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true
            }
        };

        // Service provider without ISubgraphResultMasker registered
        var servicesWithoutMasker = new ServiceCollection().BuildServiceProvider();

        var ex = Should.Throw<InvalidOperationException>(() =>
            FederationMaskingStartupCheck.Validate(options, servicesWithoutMasker));

        ex.Message.ShouldContain("Federation result masking is enabled");
        ex.Message.ShouldContain("ISubgraphResultMasker");

        // HostedService StartAsync must also fail
        var hostedService = new FederationMaskingStartupCheck(Options.Create(options.Federation), servicesWithoutMasker);
        Should.Throw<InvalidOperationException>(async () =>
            await hostedService.StartAsync(CancellationToken.None));

        // When masking is disabled, it does not throw
        var disabledOptions = new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = false
            }
        };
        Should.NotThrow(() => FederationMaskingStartupCheck.Validate(disabledOptions, servicesWithoutMasker));

        // When masker is registered, it does not throw
        var servicesWithMasker = new ServiceCollection()
            .AddSingleton(Substitute.For<ISubgraphResultMasker>())
            .BuildServiceProvider();
        Should.NotThrow(() => FederationMaskingStartupCheck.Validate(options, servicesWithMasker));
    }

    private void Case_xpw9_69pf_2()
    {
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        maskingProvider.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>())
            .Returns(ci => $"[MASKED:{ci.Arg<object?>()}]");

        var options = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true
            }
        });

        var masker = new SubgraphResultMasker(maskingProvider, options, NullLogger<SubgraphResultMasker>.Instance);
        var ctx = new SubgraphResultMasker.MaskContext(
            new Dictionary<string, MaskingRule?>(),
            new Dictionary<string, MaskingRule?>(),
            maskUnknownScalars: false);

        // 1. Verify recursion into JsonElement scalar (top-level response/field name is 'metadata', which is non-sensitive)
        using var jsonDoc = JsonDocument.Parse("""{"email":"alice@example.com","name":"Alice Corp"}""");
        var maskedJson = masker.MaskLeaf("metadata", "metadata", "Query", jsonDoc.RootElement, ctx, out var jsonWasMasked);

        jsonWasMasked.ShouldBeTrue();
        var resultElement = maskedJson.ShouldBeOfType<JsonElement>();
        resultElement.GetProperty("email").GetString().ShouldBe("[MASKED:alice@example.com]");
        resultElement.GetProperty("name").GetString().ShouldBe("Alice Corp");

        // 2. Verify recursion into Dictionary scalar
        var dictionary = new Dictionary<string, object?>
        {
            ["email"] = "bob@example.com",
            ["company"] = "Contoso"
        };
        var maskedDict = masker.MaskLeaf("customPayload", "customPayload", "Query", dictionary, ctx, out var dictWasMasked);

        dictWasMasked.ShouldBeTrue();
        var resultDict = maskedDict.ShouldBeOfType<Dictionary<string, object?>>();
        resultDict["email"]?.ToString().ShouldBe("[MASKED:bob@example.com]");
        resultDict["company"]?.ToString().ShouldBe("Contoso");
    }

    // =========================================================================
    // 2. GHSA-95f9-ffc7-vw2c
    // =========================================================================

    [Fact]
    public void Ghsa_95f9_vw2c_regression()
    {
        var tableId = new TableIdentifier("sales", "public", "customers");

        // Full metadata has 'ssn' which will be hidden from the permitted view
        var fullMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { TableName = "customers", Description = "Customer information" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "text" },
                new TableColumn { ColumnName = "ssn", DataType = "text", IsSensitive = true }
            ]
        };

        // Permitted metadata has restricted columns (ssn is withheld)
        var restrictedMeta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { TableName = "customers", Description = "Customer information" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "text" }
            ]
        };

        var allTables = new Dictionary<TableIdentifier, TableMetadata> { [tableId] = fullMeta };
        var permittedTables = new Dictionary<TableIdentifier, TableMetadata> { [tableId] = restrictedMeta };

        var rawHit = new CatalogSearchHit(
            TableIdentifier: tableId,
            DisplayName: "customers",
            Description: "Customer information",
            Domain: "sales",
            Sensitivity: "HIGH",
            CombinedScore: 0.876543,
            Bm25Score: 14.52,
            VectorScore: 0.95,
            MatchedTerms: ["customers"],
            RelevantColumns: ["name"],
            RelatedJoinPaths: [],
            SuggestedGraphQlField: "customers");

        // When table is restricted, continuous scores are scrubbed to prevent inference side-channels
        var shapedRestricted = CatalogDiscoveryService.ShapeHit(rawHit, permittedTables, allTables, ["customers"]);

        shapedRestricted.ShouldNotBeNull();
        shapedRestricted.Bm25Score.ShouldBeNull();
        shapedRestricted.VectorScore.ShouldBeNull();
        shapedRestricted.CombinedScore.ShouldBe(1.0); // Bucketed: Math.Round((double)1 / 1, 1) = 1.0

        // Contrast: when table is unrestricted (all columns visible), BM25 & vector scores are retained
        var unrestrictedPermitted = new Dictionary<TableIdentifier, TableMetadata> { [tableId] = fullMeta };
        var shapedUnrestricted = CatalogDiscoveryService.ShapeHit(rawHit, unrestrictedPermitted, allTables, ["customers"]);

        shapedUnrestricted.ShouldNotBeNull();
        shapedUnrestricted.Bm25Score.ShouldBe(14.52);
        shapedUnrestricted.VectorScore.ShouldBe(0.95);
        shapedUnrestricted.CombinedScore.ShouldBe(0.876543);
    }

    // =========================================================================
    // 3. GHSA-jfrx-2mgc-pfh5
    // =========================================================================

    [Fact]
    public async Task Ghsa_jfrx_pfh5_regression()
    {
        await Case_jfrx_pfh5_1();
        await Case_jfrx_pfh5_2();
    }

    private async Task Case_jfrx_pfh5_1()
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        var tableId = new TableIdentifier("sales", "public", "orders");
        var existingTable = new TableMetadata
        {
            Identifier = tableId,
            OwnerTenantId = "tenant-a",
            Table = new Table { TableName = "orders", SourceType = "PostgreSQL" }
        };
        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(existingTable);

        // OpenMetadata table whose identity resolves to tenant-b (via DefaultTenantId: tenant-b)
        var omTable = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "orders",
            FullyQualifiedName = "sales.public.orders",
            Service = new OpenMetadataEntityReference { Name = "sales" },
            DatabaseSchema = new OpenMetadataEntityReference { Name = "public" },
            Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
        };
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([omTable]);

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                DefaultTenantId = "tenant-b"
            }
        });

        var syncService = new OpenMetadataSyncService(
            client, metadataRepo, consentRepo, epochRepo, options, NullLogger<OpenMetadataSyncService>.Instance);

        var result = await syncService.SyncPermissionsAsync(dryRun: false);

        result.SyncedTables.ShouldBe(0);
        result.InvalidationTables.ShouldNotContain(tableId);
        result.Warnings.ShouldContain(w =>
            w.Contains("Cross-tenant overwrite prevented") &&
            w.Contains("tenant-a") &&
            w.Contains("tenant-b"));
        await metadataRepo.DidNotReceiveWithAnyArgs().UpsertTableMetadataAsync(default!, default);
    }

    private async Task Case_jfrx_pfh5_2()
    {
        var client = Substitute.For<IDataCatalogClient>();
        client.ProviderType.Returns(DataCatalogProviderType.OpenMetadata);

        var tableId = new TableIdentifier("finance", "dbo", "invoices");
        var tableAsset = new CatalogTableAsset
        {
            Identifier = tableId,
            CustomProperties = new Dictionary<string, string> { ["tenant_id"] = "tenant-b" }
        };
        client.GetTablesAsync(null, Arg.Any<CancellationToken>()).Returns([tableAsset]);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var existingMeta = new TableMetadata
        {
            Identifier = tableId,
            OwnerTenantId = "tenant-a",
            Table = new Table { TableName = "invoices", IsActive = true }
        };
        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(existingMeta);

        var epochService = Substitute.For<IEpochValidationService>();
        var options = Options.Create(new GatewayOptions());

        var syncService = new DataCatalogSyncService(
            client,
            metadataRepo,
            epochService,
            options,
            Substitute.For<IPhysicalKeyCatalog>(),
            Substitute.For<IAuditWriter>(),
            NullLogger<DataCatalogSyncService>.Instance);

        var result = await syncService.SyncCatalogAsync(dryRun: false);

        result.SyncedTablesCount.ShouldBe(0);
        result.AffectedTables.ShouldNotContain(tableId);
        result.Warnings.ShouldContain(w =>
            w.Contains("Cross-tenant overwrite prevented") &&
            w.Contains("tenant-a") &&
            w.Contains("tenant-b"));
        await metadataRepo.DidNotReceiveWithAnyArgs().UpsertTableMetadataAsync(default!, default);
    }

    // =========================================================================
    // 4. GHSA-547h-29x4-m4ch
    // =========================================================================

    [Fact]
    public async Task Ghsa_547h_m4ch_regression()
    {
        var tableId = new TableIdentifier("postgres", "public", "stg_orders");
        var stored = new TableMetadata
        {
            Identifier = tableId,
            MinGroupSize = 5,
            NonPersonal = false,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "postgres",
                SchemaName = "public",
                TableName = "stg_orders",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                MinGroupSize = 5,
                NonPersonal = false
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var auditWriter = Substitute.For<IAuditWriter>();
        var ownershipRepo = Substitute.For<IDataOwnershipRepository>();

        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(stored);
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([stored]);

        TableMetadata? persisted = null;
        metadataRepo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                persisted = ci.Arg<TableMetadata>();
                return persisted;
            });

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = "tenant-a" }
        });

        var callerSid = new Sid("user-dataowner-unauthorized");
        // Caller has DataOwner role globally, but is NOT the authorized owner/approver for this specific table
        ownershipRepo.IsAuthorizedApproverForTableAsync(tableId, callerSid, Arg.Any<CancellationToken>())
            .Returns(false);

        var service = new DbtMetadataIngestionService(
            proposalRepo,
            metadataRepo,
            graphStore,
            null,
            gatewayOptions,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            NullLogger<DbtMetadataIngestionService>.Instance,
            auditLog: Substitute.For<IAuditLogRepository>(),
            ownerAssignments: null,
            ownerIdentityVerifier: null,
            physicalKeys: null,
            auditWriter: auditWriter,
            ownershipRepo: ownershipRepo);

        var json = """
        {
          "metadata": { "dbt_version": "1.8.0", "project_name": "test_corp" },
          "nodes": {
            "model.test_corp.stg_orders": {
              "name": "stg_orders",
              "database": "postgres",
              "schema": "public",
              "config": { "materialized": "table" },
              "tags": [],
              "meta": {
                "autheris.min_group_size": "2",
                "autheris.non_personal": "true"
              },
              "columns": {
                "id": { "name": "id", "data_type": "integer", "meta": {} }
              }
            }
          }
        }
        """;

        var caller = new DbtGovernanceImportCaller(
            new VirtualFilterActor(callerSid, IsSync: false, null),
            new TenantId("postgres"),
            IsClusterAdmin: false,
            IsGovernanceAdmin: false,
            IsDataOwner: true); // Has DataOwner role, but lacks table owner standing

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await service.IngestManifestStreamAsync(stream, caller, dryRun: false);

        result.Success.ShouldBeTrue();
        persisted.ShouldNotBeNull();
        persisted.Table.MinGroupSize.ShouldBe(5); // Retained: cannot lower without table owner standing
        persisted.Table.NonPersonal.ShouldBeFalse(); // Retained: cannot set non_personal without table owner standing
        result.Warnings.ShouldContain(w => w.Contains("standing"));
    }

    // =========================================================================
    // 5. GHSA-hw85-rqjh-cwxx
    // =========================================================================

    [Fact]
    public async Task Ghsa_hw85_cwxx_regression()
    {
        Case_hw85_cwxx_1();
        await Case_hw85_cwxx_2();
    }

    private void Case_hw85_cwxx_1()
    {
        var delegatedToken = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "11111111-2222-3333-4444-555555555555"),
                new Claim("azp", "unauthorized-third-party-app"),
                new Claim("scp", "user_impersonation"),
                new Claim("preferred_username", "alice@corp.example")
            ],
            "Bearer"));

        var options = new EntraIdAuthOptions
        {
            AllowedClientIds = ["authorized-portal-app", "authorized-cli-app"]
        };

        var error = EntraTokenPolicy.Apply(delegatedToken, options, new IdentitySubjectResolver());
        error.ShouldNotBeNull();
        error.ShouldContain("unauthorized-third-party-app");
        error.ShouldContain("not in the list of allowed clients");
    }

    private async Task Case_hw85_cwxx_2()
    {
        var realm = new IdentityRealm(
            "realm-entra",
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, FixedTenantId: new TenantId("tenant-a")),
            groupSource: RealmGroupSource.Resolver,
            allowedClientIds: ["authorized-client-1", "authorized-client-2"]);

        var registry = Substitute.For<IIdentityRealmRegistry>();
        registry.GetDefaultRealm().Returns(realm);
        registry.GetRealm("realm-entra").Returns(realm);
        registry.ResolveRealmForScheme(Arg.Any<string?>(), Arg.Any<string?>()).Returns(realm);

        var resolver = Substitute.For<IRoleResolver>();
        var transformation = new EnterpriseClaimsTransformation(registry, resolver);

        // Client ID not in realm allowed clients -> marked Degraded
        var unlistedPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "user-1"),
                new Claim("azp", "unlisted-client-3"),
                new Claim("autheris:realm", "realm-entra")
            ],
            "Bearer"));

        var result = await transformation.TransformAsync(unlistedPrincipal);
        result.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeTrue();

        // Allowed client -> not degraded by client allowlist check
        resolver.ResolveRolesAsync(Arg.Any<ResolvedIdentity>(), Arg.Any<CancellationToken>())
            .Returns(new RoleResolution(
                new HashSet<string>(), new HashSet<string>(), new Dictionary<string, string>(),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "resolved"));

        var listedPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "user-1"),
                new Claim("azp", "authorized-client-1"),
                new Claim("autheris:realm", "realm-entra")
            ],
            "Bearer"));

        var listedResult = await transformation.TransformAsync(listedPrincipal);
        listedResult.Claims.Any(c => c.Type == RoleResolutionClaims.ClaimType && c.Value == RoleResolutionClaims.Degraded).ShouldBeFalse();
    }

    // =========================================================================
    // 6. GHSA-wvwx-q6jg-44wv
    // =========================================================================

    private sealed class FakeOutbox : IWebhookOutbox
    {
        public ConcurrentQueue<WebhookOutboxMessage> Rows { get; } = new();

        public Task<bool> EnqueueAsync(WebhookOutboxMessage message, CancellationToken ct = default)
        {
            Rows.Enqueue(message);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WebhookOutboxMessage>> ClaimBatchAsync(int limit, DateTimeOffset now, TimeSpan lease, string leaseOwner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(string id, string leaseOwner, int statusCode, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RescheduleAsync(string id, string leaseOwner, DateTimeOffset nextAttemptAt, int? statusCode, string errorCode, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeadLetterAsync(string id, string leaseOwner, int? statusCode, string errorCode, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeferSubscriptionAsync(WebhookSubscriptionSource source, string subscriptionId, DateTimeOffset until, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WebhookDeadLetter>> ListDeadLettersAsync(WebhookOwnerScope scope, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountPendingAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(DateTimeOffset deliveredBefore, DateTimeOffset deadBefore, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeChannel : ICdcEventChannel
    {
        public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default) => ValueTask.CompletedTask;
        public IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Ghsa_wvwx_44wv_regression()
    {
        await Case_wvwx_44wv_1();
        await Case_wvwx_44wv_2();
    }

    private async Task Case_wvwx_44wv_1()
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var outbox = new FakeOutbox();
        var channel = new FakeChannel();
        var store = new InMemoryCloudEventSubscriptionStore();
        var audit = Substitute.For<IAuditWriter>();
        var optionsMonitor = Substitute.For<IOptionsMonitor<CdcWebhooksOptions>>();
        optionsMonitor.CurrentValue.Returns(new CdcWebhooksOptions { Enabled = true, SubscriptionCacheSeconds = 5 });

        // Non-default realm configured in the registry
        var realmRegistry = Substitute.For<IIdentityRealmRegistry>();
        var nonDefaultRealm = new IdentityRealm(
            "corp-realm",
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, FixedTenantId: new TenantId("tenant-a")));
        realmRegistry.GetAllRealms().Returns([nonDefaultRealm]);

        var identityStore = Substitute.For<IIdentityStoreRepository>();
        var enforcer = Substitute.For<IStreamRlsPolicyEnforcer>();
        enforcer.EvaluateAndMaskAsync(Arg.Any<CdcEvent>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(new StreamSecurityDecision(true, (ci.Arg<CdcEvent>().After ?? ci.Arg<CdcEvent>().Before)!)));

        var services = new ServiceCollection();
        services.AddScoped(_ => enforcer);
        services.AddScoped(_ => identityStore);
        services.AddScoped(_ => realmRegistry);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var deliveryService = new CdcWebhookDeliveryService(
            channel, store, scopeFactory, new CloudEventTransformer(), outbox, audit, optionsMonitor,
            Options.Create(new GatewayOptions()), time, NullLogger<CdcWebhookDeliveryService>.Instance);

        const string creatorSid = "S-1-5-21-LOCKED-CREATOR";
        var subscription = new CloudEventWebhookSubscription(
            "sub-1", "tenant-a", "https://example.test/webhook", "*", [], "enc:v1:secret",
            CreatedBySid: creatorSid,
            CreatedByGroupSids: ["G-1"],
            CreatedByRoles: ["Analyst"],
            CreatedAt: now);
        await store.RegisterSubscriptionAsync(subscription);

        var cdcEvent = new CdcEvent(
            "evt-1", new TableIdentifier("sales", "public", "orders"), CdcOperation.Insert, "tenant-a", null,
            new Dictionary<string, object?> { ["id"] = 42, ["amount"] = 100 }, now);

        // Case 1: Subject is locked in non-default realm ('corp-realm')
        identityStore.IsSubjectLockedAsync(new TenantId("tenant-a"), "default", creatorSid, Arg.Any<CancellationToken>()).Returns(false);
        identityStore.IsSubjectLockedAsync(new TenantId("tenant-a"), "corp-realm", creatorSid, Arg.Any<CancellationToken>()).Returns(true);

        await deliveryService.ProcessEventAsync(cdcEvent, CancellationToken.None);
        outbox.Rows.ShouldBeEmpty();

        // Case 2: Subject is inactive in non-default realm ('corp-realm')
        identityStore.IsSubjectLockedAsync(new TenantId("tenant-a"), "corp-realm", creatorSid, Arg.Any<CancellationToken>()).Returns(false);
        identityStore.GetUserAsync(new TenantId("tenant-a"), "corp-realm", creatorSid, Arg.Any<CancellationToken>())
            .Returns(new ScimUser(creatorSid, null, "user-1", "User 1", null, Active: false, null, null, null));

        await deliveryService.ProcessEventAsync(cdcEvent, CancellationToken.None);
        outbox.Rows.ShouldBeEmpty();

        // Case 3: Subject is active and not locked -> Event is delivered
        identityStore.GetUserAsync(new TenantId("tenant-a"), "corp-realm", creatorSid, Arg.Any<CancellationToken>())
            .Returns(new ScimUser(creatorSid, null, "user-1", "User 1", null, Active: true, null, null, null));

        await deliveryService.ProcessEventAsync(cdcEvent, CancellationToken.None);
        outbox.Rows.Count.ShouldBe(1);
    }

    private async Task Case_wvwx_44wv_2()
    {
        var restrictedPolicy = RestrictedScopePolicy.FromOptions(new RestrictedScopeOptions
        {
            Enabled = true,
            Scopes = ["restricted-analytics", "restricted-export"]
        });

        var options = new CdcWebhooksOptions { Enabled = true };

        var restrictedPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-RESTRICTED"),
                new Claim(ClaimTypes.Role, "TenantAdmin"),
                new Claim("tenant_id", "tenant-a"),
                new Claim("scp", "restricted-analytics")
            ],
            "TestAuth"));

        // 1. Verify Capture fails with RestrictedScope error
        var capture = CdcSubscriptionPrincipal.Capture(restrictedPrincipal, options, restrictedPolicy);
        capture.IsSuccess.ShouldBeFalse();
        capture.Error.ShouldBe(CdcSnapshotCaptureError.RestrictedScope);

        // 2. Verify endpoint rejects subscription creation with 403 CDC_RESTRICTED_SCOPE_FORBIDDEN
        var store = Substitute.For<ICloudEventSubscriptionStore>();
        var protector = Substitute.For<IWebhookSecretProtector>();
        protector.IsAvailable.Returns(true);
        protector.Protect(default!, default!, default!).ReturnsForAnyArgs("enc:v1:test");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(store);
        services.AddSingleton(protector);
        services.AddSingleton<IRestrictedScopePolicy>(restrictedPolicy);

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.User = restrictedPrincipal;

        var sub = new CloudEventWebhookSubscription("sub-2", "tenant-a", "https://93.184.216.34/hook", "public.orders", [], new string('s', 40));
        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(context, sub);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        problem.ProblemDetails.Extensions["code"].ShouldBe("CDC_RESTRICTED_SCOPE_FORBIDDEN");
        problem.ProblemDetails.Detail.ShouldNotBeNull();
        problem.ProblemDetails.Detail.ShouldContain("restricted scopes");
    }

    // =========================================================================
    // 7. GHSA-4qx8-fv2p-q8r2
    // =========================================================================

    [Fact]
    public void Ghsa_4qx8_q8r2_regression()
    {
        Case_4qx8_q8r2_1();
        Case_4qx8_q8r2_2();
    }

    private void Case_4qx8_q8r2_1()
    {
        var primary = new InMemoryAuditChainAnchorStore();
        var wormDir = CreateTempDirectory("worm_store_signer");
        var options = new AuditOptions
        {
            ChainAnchorWormDirectory = wormDir
        };

        // Without an external signer, factory must throw ValidationException
        var ex = Should.Throw<ValidationException>(() =>
            AuditChainAnchorStoreFactory.Create(options, primary, secrets: null, signer: null, logger: null));

        ex.Message.ShouldContain("ChainAnchorWormDirectory is configured, but no external audit anchor signer is registered");

        // With an external signer, factory succeeds
        var signer = Substitute.For<IAuditAnchorSigner>();
        Should.NotThrow(() =>
            AuditChainAnchorStoreFactory.Create(options, primary, secrets: null, signer: signer, logger: null));
    }

    private void Case_4qx8_q8r2_2()
    {
        var wormDir = CreateTempDirectory("worm_sequence_gap");
        var store = new WormDirectoryAuditChainAnchorStore(wormDir);

        // Consecutive anchors must not have gaps: Sequence 1 then Sequence 3 (Sequence 2 is missing)
        var anchor1 = new AuditChainAnchor(1, "entryhash-1", DateTimeOffset.UtcNow, "hmac-1");
        var anchor3 = new AuditChainAnchor(3, "entryhash-3", DateTimeOffset.UtcNow.AddMinutes(1), "hmac-3");

        store.Save(anchor1);
        store.Save(anchor3);

        var loaded = store.Load();
        loaded.ShouldNotBeNull();
        loaded.Sequence.ShouldBeLessThan(0); // Returns Invalid anchor (sequence -1)
    }
}
