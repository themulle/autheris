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
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class GovernanceDocStoreTests
{
    private const string Dirty = "# Title\n\n<script>alert('x')</script>\nBody <!-- hidden comment -->";

    private static MemoryStream Zip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        ms.Position = 0;
        return ms;
    }

    private static GovernanceDocStore NewStore(InMemoryDocumentRepository repo, DocsOptions? options = null)
        => new(repo, new InMemoryDocSearchService(), new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance, Options.Create(options ?? new DocsOptions()));

    private static void AssertSanitised(DocRecord? record)
    {
        record.ShouldNotBeNull();
        record.Content.ShouldNotContain("<script");
        record.Content.ShouldNotContain("alert(");
        record.Content.ShouldNotContain("hidden comment");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Bundle_is_all_or_nothing_when_quota_is_exceeded()
    {
        var repo = new InMemoryDocumentRepository();
        var options = new DocsOptions { Quota = { MaxDocumentsPerTenant = 2 } };
        var store = NewStore(repo, options);
        using var zip = Zip(("guide/a.md", "# A"), ("guide/b.md", "# B"), ("guide/c.md", "# C"));

        var result = await store.ImportBundleAsync(new DocWriter("tenant-a", "S-1"), "app", zip);

        result.Success.ShouldBeFalse();
        foreach (var id in new[] { "a", "b", "c" })
        {
            (await repo.GetAsync(new DocKey("tenant-a", "app", DocCategory.Guide, id))).ShouldBeNull();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Every_write_path_sanitises_before_repository()
    {
        var repo = new InMemoryDocumentRepository();
        var store = NewStore(repo);
        var writer = new DocWriter("tenant-a", "S-1");

        // Deposit path shared by REST PUT and MCP: the store sanitises before the repository sees the record.
        (await store.CreateAsync(writer, new DepositDocRequest("app", DocCategory.Guide, "Put", Dirty, DocId: "put"))).Success.ShouldBeTrue();
        AssertSanitised(await repo.GetAsync(new DocKey("tenant-a", "app", DocCategory.Guide, "put")));

        using var zip = Zip(("guide/bundled.md", Dirty));
        (await store.ImportBundleAsync(writer, "app", zip)).Success.ShouldBeTrue();
        AssertSanitised(await repo.GetAsync(new DocKey("tenant-a", "app", DocCategory.Guide, "bundled")));

        var session = new McpSessionContext("s1", "c1", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1", Roles: ["GovernanceAdmin"]);
        var json = System.Text.Json.JsonSerializer.Serialize(new { appId = "app", category = "Guide", docId = "mcp", title = "Mcp", content = Dirty });
        await McpDocTools.ExecuteDocToolAsync(McpDocTools.DepositDocumentation, json, new InMemoryDocSearchService(), store, session, null, indexReadiness: null, default);
        AssertSanitised(await repo.GetAsync(new DocKey("tenant-a", "app", DocCategory.Guide, "mcp")));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task OfficialBundle_MidBundleFailure_LeavesNoPartialWrites()
    {
        var repo = new InMemoryDocumentRepository { FailUpsertAfter = 1 };
        var store = NewStore(repo);
        using var zip = Zip(("guide/a.md", "# A"), ("guide/b.md", "# B"), ("manifest.json", """{"appId":"app","name":"Official"}"""));

        var result = await store.ImportBundleAsync(new DocWriter(null, "S-ADMIN"), "app", zip);

        result.Success.ShouldBeFalse();
        (await repo.GetAsync(new DocKey(null, "app", DocCategory.Guide, "a"))).ShouldBeNull();
        (await repo.GetAsync(new DocKey(null, "app", DocCategory.Guide, "b"))).ShouldBeNull();
        (await store.GetManifestAsync(DocAccessScope.OfficialOnly, "app")).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Read_DuplicateResolvedKey_RejectsBundle()
    {
        using var zip = Zip(("guide/x.md", "# One"), ("guides/x.md", "# Two"));

        var parsed = DocBundleReader.Read(new DocWriter("tenant-a", "S-1"), "app", zip, new DocsOptions(), NullLogger.Instance);

        parsed.IsFailure.ShouldBeTrue();
        parsed.Failure!.ErrorCode.ShouldBe("INVALID_PARAMS");
    }
}
