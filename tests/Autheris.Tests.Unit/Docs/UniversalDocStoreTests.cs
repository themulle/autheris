namespace Autheris.Tests.Unit.Docs;

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class UniversalDocStoreTests : IDisposable
{
    private readonly string _testRootDir;
    private readonly IDocSearchService _searchService;
    private readonly IDocIngestor _ingestor;
    private readonly FileSystemUniversalDocStore _docStore;

    public UniversalDocStoreTests()
    {
        _testRootDir = Path.Combine(Path.GetTempPath(), "autheris_docstore_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRootDir);

        _searchService = new InMemoryDocSearchService();
        _ingestor = new MarkdownDocIngestor();
        _docStore = new FileSystemUniversalDocStore(
            _testRootDir,
            _searchService,
            _ingestor,
            NullLogger<FileSystemUniversalDocStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRootDir))
            {
                Directory.Delete(_testRootDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact]
    public async Task DepositDocumentAsync_SafelyStoresAndIndexesDocument()
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

        var result = await _docStore.DepositDocumentAsync(request);

        result.Success.ShouldBeTrue();
        // The result carries a store-relative path only (never the absolute server path).
        result.FilePath.ShouldNotBeNull();
        Path.IsPathRooted(result.FilePath).ShouldBeFalse();
        result.FilePath.ShouldBe("billing-service/runbook/payment-gw-timeout.md");
        File.Exists(Path.Combine(_testRootDir, result.FilePath)).ShouldBeTrue();

        // Verify instant discoverability via IDocSearchService
        var searchHits = await _searchService.SearchAsync("PAY_TIMEOUT_504");
        searchHits.Count.ShouldBeGreaterThanOrEqualTo(1);
        searchHits[0].AppId.ShouldBe("billing-service");
        searchHits[0].Id.DocId.ShouldBe("payment-gw-timeout");
        searchHits.Any(h => h.Title.Contains("Invoice Payment Gateway") || h.Id.DocId == "payment-gw-timeout").ShouldBeTrue();

        // Verify listing applications
        var apps = await _docStore.ListApplicationsAsync();
        apps.Count.ShouldBe(1);
        apps[0].AppId.ShouldBe("billing-service");
        apps[0].DocumentCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExtractBundleAsync_DefendsAgainstZipSlipAttack()
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Legitimate entry
            var goodEntry = archive.CreateEntry("runbooks/normal.md");
            using (var writer = new StreamWriter(goodEntry.Open()))
            {
                writer.WriteLine("# Normal Runbook");
            }

            // Malicious traversal entry trying to escape target dir
            var maliciousEntry = archive.CreateEntry("../../evil_system_file.txt");
            using (var writer = new StreamWriter(maliciousEntry.Open()))
            {
                writer.WriteLine("malicious payload");
            }
        }

        memoryStream.Position = 0;

        var result = await _docStore.ExtractBundleAsync("compromised-app", memoryStream);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ZIP_SLIP_ATTEMPT");
        result.Message.ShouldContain("Security violation");
    }

    [Fact]
    public async Task ExtractBundleAsync_DefendsAgainstZipBomb()
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Create entries that exceed maximum entry limit or huge uncompressed size
            for (int i = 0; i < 1005; i++)
            {
                var entry = archive.CreateEntry($"docs/doc_{i}.md");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("# Small doc");
            }
        }

        memoryStream.Position = 0;
        var result = await _docStore.ExtractBundleAsync("huge-app", memoryStream);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ZIP_BOMB_DETECTED");
        result.Message.ShouldContain("Zip bomb");
    }

    [Fact]
    public async Task ExtractBundleAsync_ExtractsValidArchiveAndIndexesMarkdown()
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

        var result = await _docStore.ExtractBundleAsync("auth-service", memoryStream);

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("unpacked");

        // Verify manifest parsed
        var manifest = await _docStore.GetManifestAsync("auth-service");
        manifest.ShouldNotBeNull();
        manifest.Name.ShouldBe("Corporate Auth Service");
        manifest.Domain.ShouldBe("security");

        // Verify search hit
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
            await _docStore.DepositDocumentAsync(request);
        });
    }

    [Fact]
    public async Task McpTools_ListApplicationsAndDepositDocumentation_WorkCorrectly()
    {
        // 1. Deposit via MCP execution handler
        var depositArgs = """
        {
          "appId": "crm-system",
          "category": "Guide",
          "title": "Customer Data Sync Guide",
          "content": "# Customer Data Sync\n\nDaily sync runs at 02:00 UTC."
        }
        """;

        var depositJson = await McpDocTools.ExecuteDepositDocumentationAsync(_docStore, depositArgs);
        depositJson.ShouldContain("\"success\": true");

        // 2. List via MCP execution handler
        var listJson = await McpDocTools.ExecuteListApplicationsAsync(_docStore, null);
        listJson.ShouldContain("\"total\": 1");
        listJson.ShouldContain("crm-system");
    }

    [Fact]
    public async Task SyncExistingDocumentsAsync_IndexesMarkdownFilesOnDisk()
    {
        // Arrange: manually create a file structure on disk (simulating mounted shared-docs volume)
        var appDir = Path.Combine(_testRootDir, "inventory-service", "runbooks");
        Directory.CreateDirectory(appDir);
        var filePath = Path.Combine(appDir, "stock-alert.md");
        await File.WriteAllTextAsync(filePath, """
        # Stock Level Critical Alert

        ## Symptom
        Error code `ERR_STOCK_LOW` occurs when items reach threshold.
        """);

        // Act: Sync existing documents into fresh search service
        var freshSearchService = new InMemoryDocSearchService();
        var store = new FileSystemUniversalDocStore(
            _testRootDir,
            freshSearchService,
            _ingestor,
            NullLogger<FileSystemUniversalDocStore>.Instance);

        var indexedCount = await store.SyncExistingDocumentsAsync();

        // Assert
        indexedCount.ShouldBeGreaterThan(0);
        var searchHits = await freshSearchService.SearchAsync("ERR_STOCK_LOW");
        searchHits.Count.ShouldBeGreaterThanOrEqualTo(1);
        searchHits.ShouldContain(h => h.AppId == "inventory-service" && h.Id.DocId == "stock-alert" && h.Summary.Contains("ERR_STOCK_LOW"));
    }

    [Fact]
    public async Task DocumentExistsAsync_SeesExactlyTheFileADepositWrites()
    {
        var request = new DepositDocRequest("billing-service", DocCategory.Runbook, "Payment Timeout", "# Payment Timeout", DocId: "payment-timeout");

        (await _docStore.DocumentExistsAsync(request)).ShouldBeFalse();
        await _docStore.DepositDocumentAsync(request);

        (await _docStore.DocumentExistsAsync(request)).ShouldBeTrue();
        // Same document id without DocId (slug of the title) and in another category.
        (await _docStore.DocumentExistsAsync(request with { DocId = null, Title = "payment-timeout" })).ShouldBeTrue();
        (await _docStore.DocumentExistsAsync(request with { Category = DocCategory.Guide })).ShouldBeFalse();
    }

    private static McpSessionContext DocSession(string role) =>
        new("s1", "user-1", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "user-1", Roles: [role]);

    [Fact]
    public async Task McpDeposit_TenantGovernanceAdmin_CannotOverwriteAnExistingDocument_ClusterAdminCan()
    {
        await _docStore.DepositDocumentAsync(new DepositDocRequest("billing-service", DocCategory.Runbook, "Runbook", "# Original", DocId: "runbook"));
        var filePath = Path.Combine(_testRootDir, "billing-service", "runbook", "runbook.md");
        var overwrite = """{"appId":"billing-service","category":"Runbook","docId":"runbook","title":"Runbook","content":"# Replaced"}""";

        var denied = await McpDocTools.ExecuteDepositDocumentationAsync(_docStore, overwrite, DocSession("GovernanceAdmin"));

        denied.ShouldContain("Forbidden");
        (await File.ReadAllTextAsync(filePath)).ShouldBe("# Original");

        var allowed = await McpDocTools.ExecuteDepositDocumentationAsync(_docStore, overwrite, DocSession("ClusterAdmin"));

        allowed.ShouldContain("\"success\": true");
        (await File.ReadAllTextAsync(filePath)).ShouldBe("# Replaced");
    }

    [Fact]
    public async Task McpDeposit_TenantGovernanceAdmin_MayAddANewDocument()
    {
        var args = """{"appId":"billing-service","category":"Runbook","docId":"new-runbook","title":"New","content":"# New"}""";

        var result = await McpDocTools.ExecuteDepositDocumentationAsync(_docStore, args, DocSession("GovernanceAdmin"));

        result.ShouldContain("\"success\": true");
        File.Exists(Path.Combine(_testRootDir, "billing-service", "runbook", "new-runbook.md")).ShouldBeTrue();
    }
}
