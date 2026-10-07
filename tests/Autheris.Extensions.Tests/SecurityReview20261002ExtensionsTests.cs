namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.Dbt;
using Autheris.Extensions.Itsm;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Regression tests for the extension findings of security review 2026-10-02 (H-18 .. H-20, M-32 .. M-35, low findings).
/// </summary>
public sealed class SecurityReview20261002ExtensionsTests
{
    private static readonly string AzureKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("12345678901234567890123456789012"));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = [];

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
        {
            _responder = responder ?? (_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class PassThroughMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) =>
            rawValue == null ? null : $"MASKED[{rule.RuleType}]";
    }

    private static IOptions<GatewayOptions> AzureOptions(string account = "prodaccount") => Options.Create(new GatewayOptions
    {
        Lakehouse = new LakehouseOptions
        {
            Storage = new LakehouseStorageOptions
            {
                AzureAccountName = account,
                AzureContainer = "iceberg",
                AzureAccountKey = AzureKey
            }
        }
    });

    private static string Hmac(string content, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(content)));

    // ------------------------------------------------------------------ H-18

    [Fact]
    public async Task H18_AzureHttpsLocation_ForForeignAccount_IsRejectedAndNeverSigned()
    {
        var handler = new RecordingHandler();
        var provider = new AzureBlobStorageProvider(new HttpClient(handler), AzureOptions(), NullLogger<AzureBlobStorageProvider>.Instance);

        Should.Throw<SecurityException>(() =>
            provider.ResolveAzureUri("https://evil.blob.core.windows.net/iceberg/metadata/v1.metadata.json", out _, out _, out _));

        await Should.ThrowAsync<SecurityException>(async () =>
            await provider.ReadTextAsync("https://evil.blob.core.windows.net/iceberg/metadata/v1.metadata.json"));

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void H18_AbfssLocation_ForForeignAccount_IsRejected()
    {
        var provider = new AzureBlobStorageProvider(new HttpClient(new RecordingHandler()), AzureOptions(), NullLogger<AzureBlobStorageProvider>.Instance);

        Should.Throw<SecurityException>(() =>
            provider.ResolveAzureUri("abfss://iceberg@evil.dfs.core.windows.net/data/x.parquet", out _, out _, out _));
        Should.Throw<SecurityException>(() =>
            provider.ResolveAzureUri("https://prodaccount.blob.core.windows.net.evil.com/iceberg/x.json", out _, out _, out _));
        Should.Throw<SecurityException>(() =>
            provider.ResolveAzureUri("https://xprodaccount.blob.core.windows.net/iceberg/x.json", out _, out _, out _));
    }

    [Fact]
    public async Task H18_AzureConfiguredAccount_IsSignedForOwnAccountOnly()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
        var provider = new AzureBlobStorageProvider(new HttpClient(handler), AzureOptions(), NullLogger<AzureBlobStorageProvider>.Instance);

        var content = await provider.ReadTextAsync("https://PRODACCOUNT.dfs.core.windows.net/iceberg/metadata/v1.metadata.json");

        content.ShouldBe("{\"ok\":true}");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].RequestUri!.Host.ShouldBe("prodaccount.dfs.core.windows.net");
        handler.Requests[0].Headers.GetValues("Authorization").First().ShouldStartWith("SharedKey prodaccount:");
    }

    [Fact]
    public async Task H18_CompositeProvider_RoutesByParsedHost_NotBySubstring()
    {
        var azureHandler = new RecordingHandler();
        var s3Handler = new RecordingHandler();
        var options = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_allow_unsigned_s3_requests = true },
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    LocalBasePath = Path.GetTempPath(),
                    AzureAccountName = "prodaccount",
                    AzureAccountKey = AzureKey
                }
            }
        });

        var composite = new CompositeLakehouseStorageProvider(
            new LocalStorageProvider(options),
            new S3LakehouseStorageProvider(new HttpClient(s3Handler), options, NullLogger<S3LakehouseStorageProvider>.Instance),
            new AzureBlobStorageProvider(new HttpClient(azureHandler), options, NullLogger<AzureBlobStorageProvider>.Instance),
            options);

        // ".blob.core.windows.net" only in the path -> must not reach the Azure provider (and the S3 provider rejects the host)
        await Should.ThrowAsync<SecurityException>(async () =>
            await composite.ReadTextAsync("https://attacker.example/x.blob.core.windows.net/metadata.json"));

        azureHandler.Requests.ShouldBeEmpty();
        s3Handler.Requests.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ M-33

    [Fact]
    public void M33_S3HostWithoutDot_IsRejected()
    {
        var options = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_allow_unsigned_s3_requests = true },
            Lakehouse = new LakehouseOptions { Storage = new LakehouseStorageOptions { S3Bucket = "my-bucket" } }
        });
        var provider = new S3LakehouseStorageProvider(new HttpClient(new RecordingHandler()), options, NullLogger<S3LakehouseStorageProvider>.Instance);

        Should.Throw<SecurityException>(() => provider.ResolveS3Uri("https://attacker-amazonaws.com/bucket/key.json", out _, out _));
        Should.Throw<SecurityException>(() => provider.ResolveS3Uri("https://amazonaws.com.evil.net/bucket/key.json", out _, out _));

        // positive: dot-anchored amazonaws host
        var uri = provider.ResolveS3Uri("https://s3.eu-central-1.amazonaws.com/my-bucket/tables/x.json", out var bucket, out var key);
        uri.Host.ShouldBe("s3.eu-central-1.amazonaws.com");
        bucket.ShouldBe("my-bucket");
        key.ShouldBe("tables/x.json");
    }

    [Fact]
    public void M33_S3BucketOutsideConfiguredLocations_AndTraversalKeys_AreRejected()
    {
        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    S3Endpoint = "http://minio:9000",
                    S3Bucket = "lakehouse-bucket",
                    S3AccessKey = "ak",
                    S3SecretKey = "sk"
                },
                Tables = new Dictionary<string, LakehouseTableOptions>
                {
                    ["orders"] = new() { Location = "s3://orders-bucket/orders/metadata/v2.metadata.json" }
                }
            }
        });
        var provider = new S3LakehouseStorageProvider(new HttpClient(new RecordingHandler()), options, NullLogger<S3LakehouseStorageProvider>.Instance);

        Should.Throw<SecurityException>(() => provider.ResolveS3Uri("s3://foreign-bucket/secret.json", out _, out _));
        Should.Throw<SecurityException>(() => provider.ResolveS3Uri("s3://lakehouse-bucket/a/../../other/x.json", out _, out _));
        Should.Throw<SecurityException>(() => provider.ResolveS3Uri("s3://lakehouse-bucket/a/%2e%2e/x.json", out _, out _));
        Should.Throw<SecurityException>(() => provider.ResolveS3Uri("http://minio:9000/foreign-bucket/x.json", out _, out _));

        provider.ResolveS3Uri("s3://lakehouse-bucket/metadata/v1.json", out _, out _).ToString()
            .ShouldBe("http://minio:9000/lakehouse-bucket/metadata/v1.json");
        provider.ResolveS3Uri("s3://orders-bucket/orders/data/x.parquet", out _, out _).ToString()
            .ShouldBe("http://minio:9000/orders-bucket/orders/data/x.parquet");
    }

    [Fact]
    public void M33_ManifestLocations_MustResideUnderConfiguredTableLocation()
    {
        const string configured = "s3://lake/orders/metadata/v2.metadata.json";

        IcebergMetadataReader.ValidateManifestLocation("s3://lake/orders/metadata/snap-1.json", configured);
        IcebergMetadataReader.ValidateManifestLocation("s3://lake/orders/data/part-1.parquet", configured);

        Should.Throw<SecurityException>(() => IcebergMetadataReader.ValidateManifestLocation("s3://other-bucket/orders/data/x.parquet", configured));
        Should.Throw<SecurityException>(() => IcebergMetadataReader.ValidateManifestLocation("s3://lake/customers/data/x.parquet", configured));
        Should.Throw<SecurityException>(() => IcebergMetadataReader.ValidateManifestLocation("s3://lake/orders_evil/data/x.parquet", configured));
        Should.Throw<SecurityException>(() => IcebergMetadataReader.ValidateManifestLocation("s3://lake/orders/../customers/x.parquet", configured));
        Should.Throw<SecurityException>(() => IcebergMetadataReader.ValidateManifestLocation("https://s3.amazonaws.com/lake/orders/x.parquet", configured));
        Should.Throw<SecurityException>(() => IcebergMetadataReader.ValidateManifestLocation("data/x.parquet", configured));

        // http(s) references without configured anchor are rejected
        Should.Throw<SecurityException>(() => IcebergMetadataReader.ValidateManifestLocation("https://prodaccount.blob.core.windows.net/iceberg/x.json", null));

        // http(s) references matching the configured http location are accepted
        IcebergMetadataReader.ValidateManifestLocation(
            "https://prodaccount.blob.core.windows.net/iceberg/orders/data/x.parquet",
            "https://prodaccount.blob.core.windows.net/iceberg/orders/metadata/v1.metadata.json");
    }

    [Fact]
    public async Task M33_DataFiles_UseConfiguredLocation_NotTheLocationFromTheMetadataFile()
    {
        var storage = Substitute.For<ILakehouseStorageProvider>();
        storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        storage.ReadTextAsync("s3://lake/orders/metadata/v2.metadata.json", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("""
            {
              "table-uuid": "uuid-1",
              "location": "s3://victim-bucket/finance",
              "current-snapshot-id": 1,
              "snapshots": [ { "snapshot-id": 1, "timestamp-ms": 1, "manifest-list": "s3://victim-bucket/finance/metadata/list.json" } ]
            }
            """));

        var reader = new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance);
        var metadata = await reader.LoadTableMetadataAsync("s3://lake/orders/metadata/v2.metadata.json");

        await Should.ThrowAsync<SecurityException>(async () =>
            await reader.LoadDataFilesAsync(metadata, "s3://lake/orders/metadata/v2.metadata.json"));

        await storage.DidNotReceive().ReadTextAsync("s3://victim-bucket/finance/metadata/list.json", Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------ M-35

    [Fact]
    public async Task M35_ManifestCacheKey_IsBoundToConfiguredLocation_NotTableUuid()
    {
        const string tableA = "s3://lake/a/metadata/v1.metadata.json";
        const string tableB = "s3://lake/b/metadata/v1.metadata.json";

        var storage = Substitute.For<ILakehouseStorageProvider>();
        storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        storage.ReadTextAsync("s3://lake/a/metadata/list.json", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("""{ "entries": [ { "file_path": "s3://lake/a/data/secret-a.parquet", "partition": { "tenantId": "t1" } } ] }"""));
        storage.ReadTextAsync("s3://lake/b/metadata/list.json", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("""{ "entries": [ { "file_path": "s3://lake/b/data/b.parquet", "partition": { "tenantId": "t1" } } ] }"""));

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var reader = new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance, cache);

        // Both metadata files claim the same table-uuid and snapshot id
        var metaA = new IcebergTableMetadata("same-uuid", 2, "s3://lake/a", 1, 1, 7, new IcebergSchema(0, []), new IcebergPartitionSpec(0, []),
            [new IcebergSnapshot(7, 1, "s3://lake/a/metadata/list.json")]);
        var metaB = new IcebergTableMetadata("same-uuid", 2, "s3://lake/b", 1, 1, 7, new IcebergSchema(0, []), new IcebergPartitionSpec(0, []),
            [new IcebergSnapshot(7, 1, "s3://lake/b/metadata/list.json")]);

        var filesA = await reader.LoadDataFilesAsync(metaA, tableA);
        var filesB = await reader.LoadDataFilesAsync(metaB, tableB);

        filesA.Single().FilePath.ShouldEndWith("secret-a.parquet");
        filesB.Single().FilePath.ShouldEndWith("b.parquet");
        filesB.ShouldNotContain(f => f.FilePath.Contains("secret-a", StringComparison.Ordinal));
    }

    [Fact]
    public void M35_PartitionPruner_IsFailClosed_ForFilesWithoutTenantEvidence()
    {
        var pruner = new IcebergPartitionPruner(NullLogger<IcebergPartitionPruner>.Instance);
        var files = new List<IcebergDataFile>
        {
            new("tenant-a.parquet", "PARQUET", new Dictionary<string, string> { ["tenantId"] = "tenant-a" }, 10, 10),
            new("no-partition.parquet", "PARQUET", new Dictionary<string, string>(), 10, 10),
            new("other-tenant.parquet", "PARQUET", new Dictionary<string, string> { ["tenantId"] = "tenant-b" }, 10, 10)
        };
        var predicates = new Dictionary<string, string> { ["tenantId"] = "== tenant-a" };

        var kept = pruner.PruneDataFiles(files, predicates, new IcebergPartitionSpec(0, []), ["tenantId"]);

        kept.Count.ShouldBe(1);
        kept[0].FilePath.ShouldBe("tenant-a.parquet");

        // Mandatory column without predicate -> nothing is returned
        pruner.PruneDataFiles(files, new Dictionary<string, string>(), new IcebergPartitionSpec(0, []), ["tenantId"]).ShouldBeEmpty();
    }

    private static (LakehouseDataSourceExecutor Executor, TableMetadata Metadata) CreateLakehouseExecutor()
    {
        var storage = Substitute.For<ILakehouseStorageProvider>();
        storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        storage.ReadTextAsync("s3://lake/orders/metadata/v2.metadata.json", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("""
            {
              "table-uuid": "orders-uuid",
              "location": "s3://lake/orders",
              "current-snapshot-id": 1,
              "current-schema-id": 0,
              "schemas": [ { "schema-id": 0, "fields": [
                  { "id": 1, "name": "region", "type": "string" },
                  { "id": 2, "name": "note", "type": "string" },
                  { "id": 3, "name": "tenantId", "type": "string" } ] } ],
              "snapshots": [ { "snapshot-id": 1, "timestamp-ms": 1, "manifest-list": "s3://lake/orders/metadata/list.json" } ]
            }
            """));
        storage.ReadTextAsync("s3://lake/orders/metadata/list.json", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("""
            { "entries": [
                { "file_path": "s3://lake/orders/data/a.parquet", "record_count": 2, "partition": { "tenantId": "tenant-a", "region": "SECRET-REGION" } },
                { "file_path": "s3://lake/orders/data/unpartitioned.parquet", "record_count": 2 }
            ] }
            """));

        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Enabled = true,
                Tables = new Dictionary<string, LakehouseTableOptions>
                {
                    ["orders"] = new() { Location = "s3://lake/orders/metadata/v2.metadata.json" }
                }
            }
        });

        var executor = new LakehouseDataSourceExecutor(
            new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance),
            new IcebergPartitionPruner(NullLogger<IcebergPartitionPruner>.Instance),
            new PassThroughMaskingProvider(),
            options,
            NullLogger<LakehouseDataSourceExecutor>.Instance);

        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier("lake", "public", "orders"),
            Table = new Table { SourceName = "lake", SchemaName = "public", TableName = "orders", DataSourceType = DataSourceType.LakehouseIceberg },
            Columns =
            [
                new TableColumn { ColumnName = "region", IsSensitive = true },
                new TableColumn { ColumnName = "note" },
                new TableColumn { ColumnName = "tenantId" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["note"] = new MaskingRule { RuleType = "HMAC_SHA256" }
            }
        };

        return (executor, metadata);
    }

    private static DataSourceExecutionContext CreateContext(TableMetadata metadata, TenantId? tenant) => new(
        SourceName: "lake",
        Metadata: metadata,
        Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "analyst")], "Test")),
        AccessDecision: TableAccessDecision.Allowed(
            metadata.Identifier,
            new Dictionary<string, ColumnAccessLevel>
            {
                ["region"] = ColumnAccessLevel.Clear,
                ["note"] = ColumnAccessLevel.Clear,
                ["tenantId"] = ColumnAccessLevel.Clear
            }),
        Arguments: new Dictionary<string, object?>(),
        RequestedFields: ["region", "note", "tenantId"],
        Tenant: tenant,
        Limit: 10);

    [Fact]
    public async Task M35_LakehouseExecutor_WithoutTenant_IsRejected()
    {
        var (executor, metadata) = CreateLakehouseExecutor();

        var rows = await executor.ExecuteAsync(CreateContext(metadata, tenant: null));

        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task M35_LakehouseExecutor_MasksByRules_IncludingPartitionColumns_AndDropsUnpartitionedFiles()
    {
        var (executor, metadata) = CreateLakehouseExecutor();

        var rows = await executor.ExecuteAsync(CreateContext(metadata, new TenantId("tenant-a")));

        rows.Count.ShouldBe(2); // 2 records from the tenant-a file only; the unpartitioned file is pruned (fail-closed)
        foreach (var row in rows)
        {
            // Explicit Clear in the access decision wins over catalog sensitivity (same semantics as the SQL executor).
            row["tenantId"].ShouldBe("tenant-a");
            row["region"].ShouldBe("SECRET-REGION");
        }

        // Without explicit Clear, catalog sensitivity / masking rules force masking – also for partition values
        var maskedContext = CreateContext(metadata, new TenantId("tenant-a")) with
        {
            AccessDecision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };
        var maskedRows = await executor.ExecuteAsync(maskedContext);
        maskedRows.Count.ShouldBe(2);
        foreach (var row in maskedRows)
        {
            row["region"].ShouldBe("MASKED[REDACT]");
            row["note"].ShouldBe("MASKED[HMAC_SHA256]");
            row["region"].ShouldNotBe("SECRET-REGION");
        }
    }

    // ------------------------------------------------------------------ H-19

    private static (OpenMetadataSyncService Service, IConsentRepository ConsentRepo, List<Consent> Created) CreateOmSync(
        IReadOnlyList<OpenMetadataRule> rules,
        bool autoCreateConsents,
        IReadOnlyList<Consent>? activeConsents = null)
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        var policyId = Guid.NewGuid();
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>([
                new OpenMetadataTable
                {
                    Id = Guid.NewGuid(),
                    Name = "employees",
                    FullyQualifiedName = "hr_service.corp.dbo.employees",
                    Service = new OpenMetadataEntityReference { Name = "hr_service" },
                    DatabaseSchema = new OpenMetadataEntityReference { Name = "dbo" }
                }
            ]));
        client.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([
                new OpenMetadataPolicy { Id = policyId, Name = "HrPolicy", Enabled = true, Rules = rules.ToList() }
            ]));
        client.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([
                new OpenMetadataRole { Name = "HrSpecialist", Policies = [new OpenMetadataEntityReference { Id = policyId, Name = "HrPolicy" }] }
            ]));
        client.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        client.GetUsersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]));

        metadataRepo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<TableMetadata>()));

        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(activeConsents ?? (IReadOnlyList<Consent>)Array.Empty<Consent>()));
        // SEC E-05: the reconcile loads all sync-marked consents without subject/table filter.
        consentRepo.GetActiveConsentsByConsentRequestIdAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Consent>>(
                (activeConsents ?? Array.Empty<Consent>()).Where(c => c.ConsentRequestId == ci.Arg<Guid>()).ToList()));

        var created = new List<Consent>();
        consentRepo.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = ci.Arg<Consent>();
                created.Add(c);
                return Task.FromResult(c);
            });

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                AutoCreateConsents = autoCreateConsents,
                RoleToGatewayRoleMap = new Dictionary<string, string> { ["HrSpecialist"] = "HrSpecialist" }
            }
        });

        var service = new OpenMetadataSyncService(client, metadataRepo, consentRepo, epochRepo, options, NullLogger<OpenMetadataSyncService>.Instance);
        return (service, consentRepo, created);
    }

    [Fact]
    public async Task H19_MetadataOnlyOperations_ConditionalRules_AndResourceAll_DoNotCreateConsents()
    {
        var (service, _, created) = CreateOmSync(
        [
            new OpenMetadataRule { Name = "EditDocs", Effect = "allow", Resources = ["table"], Operations = ["EditDescription", "EditTags"] },
            new OpenMetadataRule { Name = "OwnerOnly", Effect = "allow", Resources = ["table"], Operations = ["ViewAll"], Condition = "isOwner()" },
            new OpenMetadataRule { Name = "Everything", Effect = "allow", Resources = ["all"], Operations = ["ViewAll"] },
            new OpenMetadataRule { Name = "DefaultAll", Effect = "allow", Resources = ["table"] } // default operation "All" is not a data-read allowlist entry
        ], autoCreateConsents: true);

        var result = await service.SyncPermissionsAsync();

        result.Success.ShouldBeTrue();
        created.ShouldBeEmpty();
    }

    [Fact]
    public async Task H19_AutoCreateConsentsDisabledByDefault_OnlyProposes()
    {
        new OpenMetadataOptions().AutoCreateConsents.ShouldBeFalse();

        var (service, _, created) = CreateOmSync(
            [new OpenMetadataRule { Name = "ViewEmployees", Effect = "allow", Resources = ["employees"], Operations = ["ViewAll"] }],
            autoCreateConsents: false);

        await service.SyncPermissionsAsync();

        created.ShouldBeEmpty();
    }

    [Fact]
    public async Task H19_ExplicitDataReadRule_WithAutoCreate_CreatesMarkedConsent()
    {
        var (service, _, created) = CreateOmSync(
            [new OpenMetadataRule { Name = "ViewEmployees", Effect = "allow", Resources = ["employees"], Operations = ["ViewSampleData"] }],
            autoCreateConsents: true);

        await service.SyncPermissionsAsync();

        created.Count.ShouldBe(1);
        created[0].GranteeType.ShouldBe(GranteeType.Role);
        created[0].RoleName.ShouldBe("HrSpecialist");
        created[0].Effect.ShouldBe(ConsentEffect.Allow);
        created[0].ConsentRequestId.ShouldBe(OpenMetadataSyncService.OpenMetadataSyncConsentMarker);
    }

    [Fact]
    public async Task H19_Reconcile_RevokesSyncCreatedConsent_WhenSourceRuleWasRemoved()
    {
        var table = new TableIdentifier("hr_service", "dbo", "employees");
        var staleSyncConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table,
            GranteeType = GranteeType.Role,
            RoleName = "HrSpecialist",
            Effect = ConsentEffect.Allow,
            ConsentRequestId = OpenMetadataSyncService.OpenMetadataSyncConsentMarker,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(300)
        };
        var manualConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table,
            GranteeType = GranteeType.Role,
            RoleName = "HrSpecialist",
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(300)
        };

        // The policy now only contains a metadata rule -> the earlier data grant has no source anymore
        var (service, consentRepo, _) = CreateOmSync(
            [new OpenMetadataRule { Name = "EditDocs", Effect = "allow", Resources = ["employees"], Operations = ["EditDescription"] }],
            autoCreateConsents: true,
            activeConsents: [staleSyncConsent, manualConsent]);

        await service.SyncPermissionsAsync();

        // SEC E-05: sync consents are revoked through the marker-bound system revocation.
        await consentRepo.Received(1).RevokeSystemConsentAsync(Arg.Is(staleSyncConsent.Id), Arg.Is(OpenMetadataSyncService.OpenMetadataSyncConsentMarker), Arg.Any<Sid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await consentRepo.DidNotReceive().RevokeSystemConsentAsync(Arg.Is(manualConsent.Id), Arg.Any<Guid>(), Arg.Any<Sid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await consentRepo.DidNotReceive().RevokeConsentAsync(manualConsent.Id, Arg.Any<Sid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------ M-32

    private static TableMetadata ProtectedExistingTable(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = id.Domain,
            SchemaName = id.Schema,
            TableName = id.TableName,
            SourceType = "SqlServer",
            Sensitivity = "HIGH",
            RequiresFourEyes = true,
            IsActive = false,
            DataSourceType = DataSourceType.HttpPlugin,
            PluginName = "billing-plugin"
        },
        Columns =
        [
            new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true },
            new TableColumn { ColumnName = "name", DataType = "varchar" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssn"] = new MaskingRule { RuleType = "REDACT" }
        }
    };

    private static void AssertProtectionRetained(TableMetadata m)
    {
        m.Table.Sensitivity.ShouldBe("HIGH");
        m.Table.RequiresFourEyes.ShouldBeTrue();
        m.Table.IsActive.ShouldBeFalse();
        m.Table.DataSourceType.ShouldBe(DataSourceType.HttpPlugin);
        m.Table.PluginName.ShouldBe("billing-plugin");
        m.Table.SourceType.ShouldBe("SqlServer");
        m.Columns.Single(c => c.ColumnName == "ssn").IsSensitive.ShouldBeTrue();
        m.ColumnMaskingRules["ssn"].RuleType.ShouldBe("REDACT");
    }

    [Fact]
    public async Task M32_CatalogMirrorSync_CannotDowngradeGovernanceFlags()
    {
        var id = new TableIdentifier("finance", "dbo", "payroll");
        var client = Substitute.For<IDataCatalogClient>();
        client.ProviderType.Returns(DataCatalogProviderType.Collibra);
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CatalogTableAsset>
            {
                // Tags removed and empty column list in the catalog (downgrade attempt)
                new() { Identifier = id, SourceType = "PostgreSQL", Columns = [new CatalogColumnAsset { ColumnName = "ssn" }] }
            });

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(ProtectedExistingTable(id)));
        TableMetadata? upserted = null;
        repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var m = ci.Arg<TableMetadata>();
                upserted = m;
                return Task.FromResult(m);
            });

        // EXT-MOVE: the remaining (merged) sync resolves the active client through IDataCatalogClientFactory.
        var factory = Substitute.For<IDataCatalogClientFactory>();
        factory.GetActiveClient().Returns(client);
        var sut = new DataCatalogSyncService(
            factory,
            repo,
            Substitute.For<IEpochValidationService>(),
            Options.Create(new GatewayOptions { Catalog = new DataCatalogOptions { Provider = DataCatalogProviderType.Collibra } }),
            NullLogger<DataCatalogSyncService>.Instance);

        var result = await sut.SyncCatalogAsync();

        result.Success.ShouldBeTrue();
        upserted.ShouldNotBeNull();
        AssertProtectionRetained(upserted);
        upserted.Columns.ShouldContain(c => c.ColumnName == "name"); // column missing upstream is retained
    }

    [Fact]
    public async Task M32_OpenMetadataWebhookTableUpdate_CannotDowngradeGovernanceFlags()
    {
        var id = new TableIdentifier("svc", "schema", "payroll");
        var client = Substitute.For<IOpenMetadataClient>();
        client.GetTableByFqnAsync("svc.db.schema.payroll", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OpenMetadataTable?>(new OpenMetadataTable
            {
                Id = Guid.NewGuid(),
                Name = "payroll",
                FullyQualifiedName = "svc.db.schema.payroll",
                Columns = [new OpenMetadataColumn { Name = "ssn", DataType = "VARCHAR" }]
            }));

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(ProtectedExistingTable(id)));
        TableMetadata? upserted = null;
        repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var m = ci.Arg<TableMetadata>();
                upserted = m;
                return Task.FromResult(m);
            });

        const string secret = "om-secret-m32";
        var options = Options.Create(new GatewayOptions { OpenMetadata = new OpenMetadataOptions { Enabled = true, WebhookSecret = secret } });
        var service = new OpenMetadataSyncService(client, repo, Substitute.For<IConsentRepository>(), Substitute.For<IPolicyEpochRepository>(),
            options, NullLogger<OpenMetadataSyncService>.Instance);

        var payload = $"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"svc.db.schema.payroll\"}}";

        var ok = await service.HandleWebhookEventAsync(payload, "sha256=" + Hmac(payload, secret));

        ok.ShouldBeTrue();
        upserted.ShouldNotBeNull();
        AssertProtectionRetained(upserted);
    }

    // ------------------------------------------------------------------ M-34

    private static (CatalogWebhookHandler Handler, IPolicyEpochRepository Epochs) CreateWebhookHandler(string secret, bool allowLegacy = false)
    {
        var epochs = Substitute.For<IPolicyEpochRepository>();
        var handler = new CatalogWebhookHandler(
            Options.Create(new GatewayOptions
            {
                Catalog = new DataCatalogOptions { WebhookSecret = secret, AllowLegacyPayloadOnlySignature = allowLegacy }
            }),
            epochs,
            Substitute.For<IDataCatalogSyncService>(),
            NullLogger<CatalogWebhookHandler>.Instance);
        return (handler, epochs);
    }

    private static string CatalogPayload(string table) =>
        $"{{\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"m34.public.{table}\",\"nonce\":\"{Guid.NewGuid():N}\"}}";

    [Fact]
    public async Task M34_PayloadOnlySignature_IsRejected_TimestampBoundSignature_IsAccepted()
    {
        const string secret = "catalog-secret-m34";
        var (handler, _) = CreateWebhookHandler(secret);
        var ts = DateTimeOffset.UtcNow;
        var payload = CatalogPayload("orders_a");

        var legacy = await handler.HandleWebhookAsync(payload, "sha256=" + Hmac(payload, secret), ts);
        legacy.Success.ShouldBeFalse();
        legacy.Status.ShouldBe("REJECTED_INVALID_SIGNATURE");

        // Same signature replayed with a fresh timestamp header must not verify either
        var bound = Hmac($"{ts.AddMinutes(-4).ToUnixTimeSeconds()}.{payload}", secret);
        (await handler.HandleWebhookAsync(payload, bound, ts)).Status.ShouldBe("REJECTED_INVALID_SIGNATURE");

        var ok = await handler.HandleWebhookAsync(payload, Hmac($"{ts.ToUnixTimeSeconds()}.{payload}", secret), ts);
        ok.Success.ShouldBeTrue();
        ok.Status.ShouldBe("INVALIDATED");
    }

    [Fact]
    public async Task M34_ReplayedDelivery_IsDeduplicated()
    {
        const string secret = "catalog-secret-m34-replay";
        var (handler, epochs) = CreateWebhookHandler(secret);
        var ts = DateTimeOffset.UtcNow;
        var payload = CatalogPayload("orders_b");
        var signature = Hmac($"{ts.ToUnixTimeSeconds()}.{payload}", secret);

        (await handler.HandleWebhookAsync(payload, signature, ts)).Status.ShouldBe("INVALIDATED");
        var replay = await handler.HandleWebhookAsync(payload, signature, ts);

        replay.Status.ShouldBe("IGNORED_DUPLICATE_EVENT");
        await epochs.Received(1).IncrementTableEpochAsync(Arg.Is<TableIdentifier>(t => t.TableName == "orders_b"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task M34_EventId_IsDeduplicated_AcrossDifferentTimestamps()
    {
        const string secret = "catalog-secret-m34-id";
        var (handler, _) = CreateWebhookHandler(secret);
        var eventId = Guid.NewGuid().ToString();
        var payload = $"{{\"id\":\"{eventId}\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"m34.public.orders_c\"}}";

        var ts1 = DateTimeOffset.UtcNow.AddSeconds(-30);
        var ts2 = DateTimeOffset.UtcNow;

        (await handler.HandleWebhookAsync(payload, Hmac($"{ts1.ToUnixTimeSeconds()}.{payload}", secret), ts1)).Status.ShouldBe("INVALIDATED");
        (await handler.HandleWebhookAsync(payload, Hmac($"{ts2.ToUnixTimeSeconds()}.{payload}", secret), ts2)).Status.ShouldBe("IGNORED_DUPLICATE_EVENT");
    }

    [Fact]
    public async Task M34_MissingTimestamp_IsRejected()
    {
        var (handler, _) = CreateWebhookHandler("catalog-secret-m34-ts");
        var payload = CatalogPayload("orders_d");

        var result = await handler.HandleWebhookAsync(payload, "sha256=" + Hmac(payload, "catalog-secret-m34-ts"), null);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe("REJECTED_MISSING_TIMESTAMP");
    }

    [Fact]
    public async Task M34_LegacyPayloadOnlySignature_OnlyWithExplicitOption()
    {
        const string secret = "catalog-secret-m34-legacy";
        var (handler, _) = CreateWebhookHandler(secret, allowLegacy: true);
        var payload = CatalogPayload("orders_e");

        var result = await handler.HandleWebhookAsync(payload, "sha256=" + Hmac(payload, secret), DateTimeOffset.UtcNow);

        result.Success.ShouldBeTrue();
        new DataCatalogOptions().AllowLegacyPayloadOnlySignature.ShouldBeFalse();
    }

    [Fact]
    public async Task M34_DbtWebhook_WithoutTimestampOrEventId_IsRejected()
    {
        const string secret = "dbt-secret-m34";
        var receiver = new DbtWebhookReceiver(
            Options.Create(new GatewayOptions { Dbt = new DbtOptions { WebhookSecret = secret } }),
            Substitute.For<IDbtHealthCircuitBreaker>(),
            NullLogger<DbtWebhookReceiver>.Instance);

        var noTimestamp = $"{{\"eventId\":\"evt_{Guid.NewGuid():N}\",\"eventType\":\"job_run.completed\"}}";
        var noEventId = $"{{\"eventType\":\"job_run.completed\",\"timestamp\":\"{DateTimeOffset.UtcNow:O}\"}}";

        (await receiver.ProcessWebhookAsync(noTimestamp, "sha256=" + Hmac(noTimestamp, secret))).Success.ShouldBeFalse();
        (await receiver.ProcessWebhookAsync(noEventId, "sha256=" + Hmac(noEventId, secret))).Success.ShouldBeFalse();
    }

    // ------------------------------------------------------------------ Low findings

    [Fact]
    public async Task Low_ItsmClients_NonJsonSuccessResponse_IsTreatedAsFailure()
    {
        var request = new ItsmTicketRequest(
            new TenantId("tenant-a"), new Sid("S-1-5-21-1"), new TableIdentifier("finance", "dbo", "invoices"), "Need access", 7, null, null);

        var htmlHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>login page</html>", Encoding.UTF8, "text/html")
        });

        // EXT-MOVE: ported from the removed extension duplicates (JiraClient/ServiceNowClient) to the remaining clients.
        var itsmOptions = Options.Create(new GatewayOptions
        {
            Itsm = new ItsmOptions { JiraBaseUrl = "https://jira.corp.local", ServiceNowBaseUrl = "https://snow.corp.local" }
        });

        var jira = new JiraCloudRestClient(new HttpClient(htmlHandler), itsmOptions, NullLogger<JiraCloudRestClient>.Instance);
        var jiraResult = await jira.CreateAccessTicketAsync(request);
        jiraResult.Success.ShouldBeFalse();
        jiraResult.TicketReference.ShouldBeNull();

        var snow = new ServiceNowTableApiClient(new HttpClient(htmlHandler), itsmOptions, NullLogger<ServiceNowTableApiClient>.Instance);
        var snowResult = await snow.CreateAccessTicketAsync(request);
        snowResult.Success.ShouldBeFalse();
        snowResult.TicketReference.ShouldBeNull();
    }

    [Fact]
    public async Task Low_LocalStorage_RejectsSymlinks_AndRequiresBasePathOutsideDevelopment()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "lh_sym_" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "lh_out_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(outside);
        try
        {
            var secretFile = Path.Combine(outside, "secret.json");
            await File.WriteAllTextAsync(secretFile, "{\"secret\":true}");
            File.CreateSymbolicLink(Path.Combine(baseDir, "link.json"), secretFile);

            var options = Options.Create(new GatewayOptions
            {
                Lakehouse = new LakehouseOptions { Storage = new LakehouseStorageOptions { LocalBasePath = baseDir } }
            });
            var provider = new LocalStorageProvider(options);

            await Should.ThrowAsync<SecurityException>(async () => await provider.ReadTextAsync("link.json"));

            var prodEnv = Substitute.For<IHostEnvironment>();
            prodEnv.EnvironmentName.Returns("Production");
            var unconfigured = new LocalStorageProvider(Options.Create(new GatewayOptions()), prodEnv);
            await Should.ThrowAsync<SecurityException>(async () => await unconfigured.ReadTextAsync("anything.json"));
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task Low_S3Read_IsBoundedByMaxReadBytes()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 4096))
        });
        var options = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_allow_unsigned_s3_requests = true },
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions { S3Endpoint = "http://minio:9000", MaxReadBytes = 1024, S3Bucket = "bucket" }
            }
        });
        var provider = new S3LakehouseStorageProvider(new HttpClient(handler), options, NullLogger<S3LakehouseStorageProvider>.Instance);

        await Should.ThrowAsync<InvalidDataException>(async () => await provider.ReadTextAsync("s3://bucket/big.json"));
    }

    [Fact]
    public async Task EX01_UserNameStartingWithS1_IsNotAcceptedAsSid()
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        var policyId = Guid.NewGuid();
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>([
                new OpenMetadataTable { Id = Guid.NewGuid(), Name = "orders", FullyQualifiedName = "corp.dbo.orders" }
            ]));
        client.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([
                new OpenMetadataPolicy
                {
                    Id = policyId,
                    Name = "OrderPolicy",
                    Enabled = true,
                    Rules = [new OpenMetadataRule { Name = "AllOrders", Effect = "allow", Resources = ["table"], Operations = ["ViewAll"] }]
                }
            ]));
        client.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([
                new OpenMetadataRole { Name = "OrderReader", Policies = [new OpenMetadataEntityReference { Id = policyId, Name = "OrderPolicy" }] }
            ]));
        client.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        client.GetUsersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([
            new OpenMetadataUser
            {
                Id = Guid.NewGuid(),
                Name = "S-1-5-21-ATTACKER-SID",
                Email = "attacker@evil.local",
                Roles = [new OpenMetadataEntityReference { Name = "OrderReader" }]
            }
        ]));

        var created = new List<Consent>();
        consentRepo.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var c = ci.Arg<Consent>(); created.Add(c); return Task.FromResult(c); });

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                AutoCreateConsents = true,
                RoleToGatewayRoleMap = new Dictionary<string, string> { ["OrderReader"] = "OrderReader" }
            }
        });

        var service = new OpenMetadataSyncService(client, metadataRepo, consentRepo, epochRepo, options, NullLogger<OpenMetadataSyncService>.Instance);
        var result = await service.SyncPermissionsAsync();

        result.Success.ShouldBeTrue();
        // User should NOT have received a consent via S-1- fallback name spoofing
        created.ShouldNotContain(c => c.GranteeType == GranteeType.User);
    }

    [Fact]
    public async Task EX01_UserNameMatchingMappedEmail_DoesNotInheritSid()
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        var policyId = Guid.NewGuid();
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>([
                new OpenMetadataTable { Id = Guid.NewGuid(), Name = "orders", FullyQualifiedName = "corp.dbo.orders" }
            ]));
        client.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([
                new OpenMetadataPolicy
                {
                    Id = policyId,
                    Name = "OrderPolicy",
                    Enabled = true,
                    Rules = [new OpenMetadataRule { Name = "AllOrders", Effect = "allow", Resources = ["table"], Operations = ["ViewAll"] }]
                }
            ]));
        client.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([
                new OpenMetadataRole { Name = "OrderReader", Policies = [new OpenMetadataEntityReference { Id = policyId, Name = "OrderPolicy" }] }
            ]));
        client.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        client.GetUsersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([
            new OpenMetadataUser
            {
                Id = Guid.NewGuid(),
                Name = "admin@corp.local", // Name is spoofed to match admin's email
                Email = "attacker@evil.local",
                Roles = [new OpenMetadataEntityReference { Name = "OrderReader" }]
            }
        ]));

        var created = new List<Consent>();
        consentRepo.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var c = ci.Arg<Consent>(); created.Add(c); return Task.FromResult(c); });

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                AutoCreateConsents = true,
                UserToUserSidMap = new Dictionary<string, string> { ["admin@corp.local"] = "S-1-5-21-ADMIN-SID" },
                RoleToGatewayRoleMap = new Dictionary<string, string> { ["OrderReader"] = "OrderReader" }
            }
        });

        var service = new OpenMetadataSyncService(client, metadataRepo, consentRepo, epochRepo, options, NullLogger<OpenMetadataSyncService>.Instance);
        await service.SyncPermissionsAsync();

        // Attacker user MUST NOT inherit admin's SID through user name matching
        created.ShouldNotContain(c => c.GranteeType == GranteeType.User && c.GranteeSid.HasValue && c.GranteeSid.Value.Value == "S-1-5-21-ADMIN-SID");
    }

    [Fact]
    public async Task EX01_RoleWithoutMappingInRoleToGatewayRoleMap_DoesNotCreateConsent()
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        var policyId = Guid.NewGuid();
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>([
                new OpenMetadataTable { Id = Guid.NewGuid(), Name = "orders", FullyQualifiedName = "corp.dbo.orders" }
            ]));
        client.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([
                new OpenMetadataPolicy
                {
                    Id = policyId,
                    Name = "OrderPolicy",
                    Enabled = true,
                    Rules = [new OpenMetadataRule { Name = "AllOrders", Effect = "allow", Resources = ["table"], Operations = ["ViewAll"] }]
                }
            ]));
        client.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([
                new OpenMetadataRole { Name = "UnmappedMaliciousRole", Policies = [new OpenMetadataEntityReference { Id = policyId, Name = "OrderPolicy" }] }
            ]));
        client.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        client.GetUsersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]));

        var created = new List<Consent>();
        consentRepo.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var c = ci.Arg<Consent>(); created.Add(c); return Task.FromResult(c); });

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                AutoCreateConsents = true,
                RoleToGatewayRoleMap = new Dictionary<string, string>() // empty allowlist
            }
        });

        var service = new OpenMetadataSyncService(client, metadataRepo, consentRepo, epochRepo, options, NullLogger<OpenMetadataSyncService>.Instance);
        await service.SyncPermissionsAsync();

        created.ShouldBeEmpty();
    }

    [Fact]
    public async Task EX01_SensitiveOrFourEyesOrArt9Table_NeverAutoCreatesAllowConsent()
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        var policyId = Guid.NewGuid();
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>([
                new OpenMetadataTable
                {
                    Id = Guid.NewGuid(),
                    Name = "patient_health",
                    FullyQualifiedName = "health.dbo.patient_health",
                    Tags = [new OpenMetadataTag { TagFQN = "GDPR.Art9_HealthData" }, new OpenMetadataTag { TagFQN = "Governance.FourEyes" }]
                }
            ]));
        client.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([
                new OpenMetadataPolicy
                {
                    Id = policyId,
                    Name = "HealthPolicy",
                    Enabled = true,
                    Rules = [new OpenMetadataRule { Name = "ReadHealth", Effect = "allow", Resources = ["table"], Operations = ["ViewAll"] }]
                }
            ]));
        client.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([
                new OpenMetadataRole { Name = "DoctorRole", Policies = [new OpenMetadataEntityReference { Id = policyId, Name = "HealthPolicy" }] }
            ]));
        client.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        client.GetUsersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]));

        var created = new List<Consent>();
        consentRepo.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var c = ci.Arg<Consent>(); created.Add(c); return Task.FromResult(c); });

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                AutoCreateConsents = true,
                RoleToGatewayRoleMap = new Dictionary<string, string> { ["DoctorRole"] = "Doctor" }
            }
        });

        var service = new OpenMetadataSyncService(client, metadataRepo, consentRepo, epochRepo, options, NullLogger<OpenMetadataSyncService>.Instance);
        await service.SyncPermissionsAsync();

        // Four-Eyes / Art. 9 tables MUST NOT be granted via AutoCreateConsents
        created.ShouldBeEmpty();
    }

    [Fact]
    public async Task EX03_OpenMetadataSync_AssignsTargetTenant_AndScopesReconcileByTenant()
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        var table1 = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "patients",
            FullyQualifiedName = "health-service.clinical.dbo.patients",
            Service = new OpenMetadataEntityReference { Name = "health-service" },
            Database = new OpenMetadataEntityReference { Name = "clinical" },
            DatabaseSchema = new OpenMetadataEntityReference { Name = "dbo" }
        };
        var table2 = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "accounts",
            FullyQualifiedName = "finance-service.ledger.dbo.accounts",
            Service = new OpenMetadataEntityReference { Name = "finance-service" },
            Database = new OpenMetadataEntityReference { Name = "ledger" },
            DatabaseSchema = new OpenMetadataEntityReference { Name = "dbo" }
        };

        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>([table1, table2]));

        var policyId = Guid.NewGuid();
        client.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([
                new OpenMetadataPolicy
                {
                    Id = policyId,
                    Name = "DenyPolicy",
                    Enabled = true,
                    Rules = [new OpenMetadataRule { Name = "DenyAll", Effect = "deny", Resources = ["table"], Operations = ["ViewAll"] }]
                }
            ]));
        client.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([
                new OpenMetadataRole { Name = "BlockedRole", Policies = [new OpenMetadataEntityReference { Id = policyId, Name = "DenyPolicy" }] }
            ]));
        client.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        client.GetUsersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]));

        var created = new List<Consent>();
        consentRepo.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var c = ci.Arg<Consent>(); created.Add(c); return Task.FromResult(c); });

        var foreignConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("other-service", "dbo", "records"),
            ConsentRequestId = OpenMetadataSyncService.OpenMetadataSyncConsentMarker,
            TenantId = new TenantId("tenant-unrelated"),
            GranteeType = GranteeType.Role,
            RoleName = "BlockedRole",
            Effect = ConsentEffect.Deny,
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddHours(1)
        };
        consentRepo.GetActiveConsentsByConsentRequestIdAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([foreignConsent]));

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                RoleToGatewayRoleMap = new Dictionary<string, string> { ["BlockedRole"] = "GatewayBlocked" },
                ServiceDatabaseToTenantMap = new Dictionary<string, string>
                {
                    ["health-service.clinical"] = "tenant-clinical",
                    ["finance-service.ledger"] = "tenant-finance"
                }
            }
        });

        var service = new OpenMetadataSyncService(client, metadataRepo, consentRepo, epochRepo, options, NullLogger<OpenMetadataSyncService>.Instance);
        var result = await service.SyncPermissionsAsync();

        result.Success.ShouldBeTrue();
        created.Count.ShouldBe(2);

        var clinicalConsent = created.FirstOrDefault(c => c.TableIdentifier.Domain == "health-service");
        clinicalConsent.ShouldNotBeNull();
        clinicalConsent.TenantId.Value.ShouldBe("tenant-clinical");
        clinicalConsent.Effect.ShouldBe(ConsentEffect.Deny);

        var financeConsent = created.FirstOrDefault(c => c.TableIdentifier.Domain == "finance-service");
        financeConsent.ShouldNotBeNull();
        financeConsent.TenantId.Value.ShouldBe("tenant-finance");
        financeConsent.Effect.ShouldBe(ConsentEffect.Deny);

        // foreignConsent for tenant-unrelated must NOT be revoked as it belongs to an unmanaged tenant
        await consentRepo.DidNotReceive().RevokeSystemConsentAsync(
            foreignConsent.Id,
            Arg.Any<Guid>(),
            Arg.Any<Sid>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EX04_LakehouseExecutor_EvaluatesRlsFilter_BeforeColumnMasking()
    {
        var (executor, metadata) = CreateLakehouseExecutor();

        // Query with an RLS row filter on a sensitive/masked column: region <> 'SECRET-REGION'.
        // In the mock storage (CreateLakehouseExecutor): partition region is 'SECRET-REGION'.
        // Evaluating on raw values MUST drop the row.
        // If it were evaluated on masked values ("MASKED[REDACT]"), "MASKED[REDACT]" <> 'SECRET-REGION' would evaluate to true and leak!
        var context = CreateContext(metadata, new TenantId("tenant-a")) with
        {
            AccessDecision = TableAccessDecision.Allowed(
                metadata.Identifier,
                new Dictionary<string, ColumnAccessLevel>
                {
                    ["region"] = ColumnAccessLevel.Mask,
                    ["note"] = ColumnAccessLevel.Clear,
                    ["tenantId"] = ColumnAccessLevel.Clear
                },
                rowFilterSql: "region <> 'SECRET-REGION'")
        };

        var rows = await executor.ExecuteAsync(context);

        // Row had region == 'SECRET-REGION', so region <> 'SECRET-REGION' is FALSE.
        // Row must be filtered out!
        rows.ShouldBeEmpty();

        // And when the row filter matches raw data:
        var matchContext = CreateContext(metadata, new TenantId("tenant-a")) with
        {
            AccessDecision = TableAccessDecision.Allowed(
                metadata.Identifier,
                new Dictionary<string, ColumnAccessLevel>
                {
                    ["region"] = ColumnAccessLevel.Mask,
                    ["note"] = ColumnAccessLevel.Clear,
                    ["tenantId"] = ColumnAccessLevel.Clear
                },
                rowFilterSql: "region <> 'OTHER-REGION'")
        };

        var matchRows = await executor.ExecuteAsync(matchContext);

        // 2 rows kept, and the returned column is properly masked
        matchRows.Count.ShouldBe(2);
        matchRows[0]["region"].ShouldBe("MASKED[REDACT]");
    }
}
