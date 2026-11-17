namespace Autheris.Tests.Unit.Docs;

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class UniversalDocStoreTests
{
    private static readonly DocWriter OfficialWriter = new(null, "cluster-admin");
    private static readonly DocWriter TenantAWriter = new("tenant-a", "user-a");

    private readonly InMemoryDocumentRepository _repository;
    private readonly InMemoryDocSearchService _searchService;
    private readonly IDocIngestor _ingestor;
    private readonly GovernanceDocStore _docStore;

    public UniversalDocStoreTests()
    {
        _repository = new InMemoryDocumentRepository();
        _searchService = new InMemoryDocSearchService();
        _ingestor = new MarkdownDocIngestor();
        _docStore = new GovernanceDocStore(
            _repository,
            _searchService,
            _ingestor,
            NullLogger<GovernanceDocStore>.Instance);
    }

    [Fact]
    public async Task CreateAsync_SafelyStoresAndIndexesDocument()
    {
        var request = new DepositDocRequest(
            AppId: "billing-service",
            Category: DocCategory.Runbook,
            Title: "Invoice Payment Gateway Timeout Resolution",
            Content: """
            # Invoice Payment Gateway Timeout Resolution

            ## Symptom
            Error code `PAY_TIMEOUT_504` occurs during high traffic bursts.

            ## Remediation
            Scale out the gateway connection pool:
            ```bash
            kubectl scale deployment payment-gw --replicas=5
            ```
            """,
            DocId: "payment-gw-timeout",
            ErrorCode: "PAY_TIMEOUT_504");

        var result = await _docStore.CreateAsync(OfficialWriter, request);

        result.Success.ShouldBeTrue();
        result.Version.ShouldBe(1);

        var persisted = await _repository.GetAsync(new DocKey(null, "billing-service", DocCategory.Runbook, "payment-gw-timeout"));
        persisted.ShouldNotBeNull();

        var searchHits = await _searchService.SearchAsync("PAY_TIMEOUT_504");
        searchHits.Count.ShouldBeGreaterThanOrEqualTo(1);
        searchHits[0].AppId.ShouldBe("billing-service");
        searchHits[0].Id.DocId.ShouldBe("payment-gw-timeout");
        searchHits.Any(h => h.Title.Contains("Invoice Payment Gateway") || h.Id.DocId == "payment-gw-timeout").ShouldBeTrue();

        var apps = await _docStore.ListApplicationsAsync(DocAccessScope.OfficialOnly);
        apps.Count.ShouldBe(1);
        apps[0].AppId.ShouldBe("billing-service");
        apps[0].DocumentCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReplaceAndDeleteAsync_EnforceOptimisticConcurrencyAndUpdateIndex()
    {
        var request = new DepositDocRequest(
            AppId: "billing-service",
            Category: DocCategory.Runbook,
            Title: "Failover Runbook",
            Content: "# Failover Runbook\n\n## LegacyStep\nUse old script.",
            DocId: "failover");

        var created = await _docStore.CreateAsync(TenantAWriter, request);
        created.Success.ShouldBeTrue();
        created.Version.ShouldBe(1);

        var wrongVersion = await _docStore.ReplaceAsync(
            TenantAWriter,
            request with { Content = "# Failover Runbook\n\n## NewStep\nUse new script." },
            expectedVersion: 99);
        wrongVersion.Success.ShouldBeFalse();
        wrongVersion.ErrorCode.ShouldBe("VERSION_MISMATCH");

        var replaced = await _docStore.ReplaceAsync(
            TenantAWriter,
            request with { Content = "# Failover Runbook\n\n## NewStep\nUse new script." },
            expectedVersion: 1);
        replaced.Success.ShouldBeTrue();
        replaced.Version.ShouldBe(2);

        var scopeA = DocAccessScope.ForTenant("tenant-a");
        (await _searchService.SearchAsync("LegacyStep", scopeA)).ShouldBeEmpty();
        (await _searchService.SearchAsync("NewStep", scopeA)).Count.ShouldBe(1);

        (await _docStore.DeleteAsync(new DocWriter("tenant-b", "user-b"), new DocKey("tenant-a", "billing-service", DocCategory.Runbook, "failover"))).ShouldBeFalse();
        (await _docStore.DeleteAsync(TenantAWriter, new DocKey("tenant-a", "billing-service", DocCategory.Runbook, "failover"))).ShouldBeTrue();
        (await _searchService.SearchAsync("NewStep", scopeA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ImportBundleAsync_DefendsAgainstZipSlipAttack()
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var goodEntry = archive.CreateEntry("runbooks/normal.md");
            using (var writer = new StreamWriter(goodEntry.Open()))
            {
                writer.WriteLine("# Normal Runbook");
            }

            var maliciousEntry = archive.CreateEntry("../../evil_system_file.txt");
            using (var writer = new StreamWriter(maliciousEntry.Open()))
            {
                writer.WriteLine("malicious payload");
            }
        }

        memoryStream.Position = 0;

        var result = await _docStore.ImportBundleAsync(OfficialWriter, "compromised-app", memoryStream);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ZIP_SLIP_ATTEMPT");
        result.Message.ShouldContain("Security violation");
    }

    [Fact]
    public async Task ImportBundleAsync_DefendsAgainstZipBomb()
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i < 1005; i++)
            {
                var entry = archive.CreateEntry($"docs/doc_{i}.md");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("# Small doc");
            }
        }

        memoryStream.Position = 0;
        var result = await _docStore.ImportBundleAsync(OfficialWriter, "huge-app", memoryStream);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ZIP_BOMB_DETECTED");
        result.Message.ShouldContain("Zip bomb");
    }

    [Fact]
    public async Task ImportBundleAsync_ExtractsValidArchiveAndIndexesMarkdown()
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                writer.WriteLine("""
                {
                  "appId": "auth-service",
                  "name": "Corporate Auth Service",
                  "version": "3.1.0",
                  "domain": "security",
                  "owner": "sec-ops@corp.local",
                  "governanceTier": "Tier-1",
                  "tags": ["OAuth2", "Security"]
                }
                """);
            }

            var runbookEntry = archive.CreateEntry("runbooks/mfa-recovery.md");
            using (var writer = new StreamWriter(runbookEntry.Open()))
            {
                writer.WriteLine("""
                # Emergency MFA Token Reset

                ## Procedure
                Execute reset tool with break-glass token:
                `autheris-admin reset-mfa --user user123`
                """);
            }
        }

        memoryStream.Position = 0;

        var result = await _docStore.ImportBundleAsync(OfficialWriter, "auth-service", memoryStream);

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("unpacked");

        var manifest = await _docStore.GetManifestAsync(DocAccessScope.OfficialOnly, "auth-service");
        manifest.ShouldNotBeNull();
        manifest.Name.ShouldBe("Corporate Auth Service");
        manifest.Domain.ShouldBe("security");

        var hits = await _searchService.SearchAsync("Emergency MFA");
        hits.Count.ShouldBeGreaterThanOrEqualTo(1);
        hits[0].AppId.ShouldBe("auth-service");
    }

    [Fact]
    public async Task ValidateIdentifier_RejectsPathTraversalInAppId()
    {
        var request = new DepositDocRequest(
            AppId: "../../../etc",
            Category: DocCategory.Runbook,
            Title: "Exploit",
            Content: "some text");

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await _docStore.CreateAsync(OfficialWriter, request);
        });
    }

    [Fact]
    public async Task McpTools_ListApplicationsAndDepositDocumentation_WorkCorrectly()
    {
        var depositArgs = """
        {
          "appId": "crm-system",
          "category": "Guide",
          "title": "Customer Data Sync Guide",
          "content": "# Customer Data Sync\n\nDaily sync runs at 02:00 UTC."
        }
        """;

        var depositJson = await McpDocTools.ExecuteDepositDocumentationAsync(_docStore, depositArgs, DocSession("ClusterAdmin"));
        depositJson.ShouldContain("\"success\": true");

        var listJson = await McpDocTools.ExecuteListApplicationsAsync(_docStore, null, DocSession("ClusterAdmin"));
        listJson.ShouldContain("\"total\": 1");
        listJson.ShouldContain("crm-system");
    }

    [Fact]
    public async Task RefreshIndexAsync_ReloadsPersistedDocumentsIntoFreshSearchService()
    {
        await _repository.CreateAsync(new DocRecord(
            TenantId: null,
            AppId: "inventory-service",
            Category: DocCategory.Runbook,
            DocId: "stock-alert",
            Title: "Stock Level Critical Alert",
            Content: "# Stock Level Critical Alert\n\n## Symptom\nError code `ERR_STOCK_LOW` occurs when items reach threshold.",
            ContentSha256: "abc",
            ByteCount: 95,
            Tags: [],
            ErrorCode: "ERR_STOCK_LOW",
            AuthorSid: "S-1",
            Version: 1,
            UpdatedAt: DateTimeOffset.UnixEpoch), new DocQuota(100, 1024 * 1024));

        var freshSearchService = new InMemoryDocSearchService();
        var freshStore = new GovernanceDocStore(
            _repository,
            freshSearchService,
            _ingestor,
            NullLogger<GovernanceDocStore>.Instance);

        var indexedCount = await freshStore.RefreshIndexAsync();

        indexedCount.ShouldBeGreaterThan(0);
        var searchHits = await freshSearchService.SearchAsync("ERR_STOCK_LOW");
        searchHits.Count.ShouldBeGreaterThanOrEqualTo(1);
        searchHits.ShouldContain(h => h.AppId == "inventory-service" && h.Id.DocId == "stock-alert" && h.Summary.Contains("ERR_STOCK_LOW"));
    }

    [Fact]
    public async Task PurgeTenantAsync_RemovesTenantDocumentsAndSearchEntries()
    {
        await _docStore.CreateAsync(TenantAWriter, new DepositDocRequest("app-1", DocCategory.Runbook, "A1", "# Tenant A\n\nQuorum purge sentinel", DocId: "a1"));
        await _docStore.CreateAsync(OfficialWriter, new DepositDocRequest("app-1", DocCategory.Runbook, "O1", "# Official\n\nOfficial purge sentinel", DocId: "o1"));

        var purged = await _docStore.PurgeTenantAsync("tenant-a");

        purged.ShouldBe(1);
        (await _searchService.SearchAsync("Quorum", DocAccessScope.ForTenant("tenant-a"))).ShouldBeEmpty();
        (await _searchService.SearchAsync("Official", DocAccessScope.ForTenant("tenant-a"))).Count.ShouldBe(1);
    }

    private static McpSessionContext DocSession(string role) =>
        new("s1", "user-1", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "user-1", Roles: [role]);

    [Fact]
    public async Task McpDeposit_ExistingDocWithoutReplaceVersion_IsRejected_WithReplaceVersionClusterAdminReplaces()
    {
        await _docStore.CreateAsync(OfficialWriter, new DepositDocRequest("billing-service", DocCategory.Runbook, "Runbook", "# Original", DocId: "runbook"));
        var overwrite = """{"appId":"billing-service","category":"Runbook","docId":"runbook","title":"Runbook","content":"# Replaced"}""";

        var denied = await McpDocTools.ExecuteDepositDocumentationAsync(_docStore, overwrite, DocSession("GovernanceAdmin"));
        denied.ShouldContain("DOC_ID_RESERVED");

        var replaceArgs = """{"appId":"billing-service","category":"Runbook","docId":"runbook","title":"Runbook","content":"# Replaced","replaceVersion":1}""";
        var allowed = await McpDocTools.ExecuteDepositDocumentationAsync(_docStore, replaceArgs, DocSession("ClusterAdmin"));

        allowed.ShouldContain("\"success\": true");
        var stored = await _repository.GetAsync(new DocKey(null, "billing-service", DocCategory.Runbook, "runbook"));
        stored.ShouldNotBeNull();
        stored.Content.ShouldBe("# Replaced");
        stored.Version.ShouldBe(2);
    }

    [Fact]
    public async Task McpDeposit_TenantGovernanceAdmin_MayAddANewDocument()
    {
        var args = """{"appId":"billing-service","category":"Runbook","docId":"new-runbook","title":"New","content":"# New"}""";

        var result = await McpDocTools.ExecuteDepositDocumentationAsync(_docStore, args, DocSession("GovernanceAdmin"));

        result.ShouldContain("\"success\": true");
        var stored = await _repository.GetAsync(new DocKey("tenant-a", "billing-service", DocCategory.Runbook, "new-runbook"));
        stored.ShouldNotBeNull();
    }
}
