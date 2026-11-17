namespace Autheris.Tests.Unit.Docs;

using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DocsTenantIsolationTests
{
    private readonly InMemoryDocumentRepository _repo = new();
    private readonly InMemoryDocSearchService _search = new();
    private readonly GovernanceDocStore _store;

    public DocsTenantIsolationTests()
    {
        _store = new GovernanceDocStore(_repo, _search, new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance);
    }

    [Fact]
    public async Task Cross_tenant_read_write_delete_denied()
    {
        var a = new DocWriter("tenant-a", "S-A");
        var b = new DocWriter("tenant-b", "S-B");
        (await _store.CreateAsync(a, new DepositDocRequest("app", DocCategory.Guide, "Mine", "# Isolatedtokenzeta", DocId: "mine"))).Success.ShouldBeTrue();

        // read: tenant B and official-only scopes never see tenant A content
        (await _search.SearchAsync("isolatedtokenzeta", DocAccessScope.ForTenant("tenant-b"))).ShouldBeEmpty();
        (await _search.SearchAsync("isolatedtokenzeta", DocAccessScope.OfficialOnly)).ShouldBeEmpty();

        // write: tenant B cannot replace tenant A's document (it addresses its own, absent, key)
        var replace = await _store.ReplaceAsync(b, new DepositDocRequest("app", DocCategory.Guide, "Takeover", "# Takeover", DocId: "mine"), 1);
        replace.Success.ShouldBeFalse();
        (await _repo.GetAsync(new DocKey("tenant-a", "app", DocCategory.Guide, "mine")))!.Content.ShouldBe("# Isolatedtokenzeta");

        // delete: a key addressed with another tenant id is refused
        (await _store.DeleteAsync(b, new DocKey("tenant-a", "app", DocCategory.Guide, "mine"))).ShouldBeFalse();
        (await _repo.GetAsync(new DocKey("tenant-a", "app", DocCategory.Guide, "mine")))!.IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Missing_tenant_never_addresses_official_store()
    {
        foreach (var governanceAdmin in new[] { false, true })
        {
            var caller = new DocCaller(false, governanceAdmin, null, "S-1", true, false);
            DocAccessPolicy.ResolveWriter(caller, "app", out var denial).ShouldBeNull();
            denial.Code.ShouldBe("FORBIDDEN");
        }

        // Even a resolved tenant writer can neither delete nor shadow an official document.
        var official = new DocWriter(null, "S-ADMIN");
        (await _store.CreateAsync(official, new DepositDocRequest("app", DocCategory.Guide, "Official", "# Official", DocId: "off"))).Success.ShouldBeTrue();
        (await _store.DeleteAsync(new DocWriter("tenant-a", "S-A"), new DocKey(null, "app", DocCategory.Guide, "off"))).ShouldBeFalse();
        (await _store.CreateAsync(new DocWriter("tenant-a", "S-A"), new DepositDocRequest("app", DocCategory.Guide, "Shadow", "# Shadow", DocId: "off"))).Success.ShouldBeFalse();
        (await _repo.GetAsync(new DocKey(null, "app", DocCategory.Guide, "off")))!.Content.ShouldBe("# Official");
    }
}
