namespace Autheris.Tests.Unit.Security;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Tests.Unit.Docs;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    // GHSA-p234-g3ph-gqh2 (details: private advisory).
    [Theory]
    [InlineData("same_doc_id_in_two_tenants_never_collides")]
    [InlineData("foreign_tenant_gets_no_existence_signal")]
    [InlineData("official_doc_id_cannot_be_shadowed")]
    [InlineData("document_count_quota_per_tenant")]
    [InlineData("byte_quota_per_tenant")]
    [InlineData("duplicate_create_is_refused_not_overwritten")]
    public async Task Ghsa_p234_gqh2_regression(string bullet)
    {
        var repo = new InMemoryDocumentRepository();
        var options = new DocsOptions { Quota = { MaxDocumentsPerTenant = 2, MaxBytesPerTenant = 4096 } };
        var store = new GovernanceDocStore(repo, new InMemoryDocSearchService(), new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance, Options.Create(options));
        var a = new DocWriter("tenant-a", "S-A");
        var b = new DocWriter("tenant-b", "S-B");
        DepositDocRequest Request(string id, string body = "# Body") => new("app", DocCategory.Guide, "Title " + id, body, DocId: id);

        switch (bullet)
        {
            case "same_doc_id_in_two_tenants_never_collides":
                (await store.CreateAsync(a, Request("shared"))).Success.ShouldBeTrue();
                (await store.CreateAsync(b, Request("shared", "# Other"))).Success.ShouldBeTrue();
                (await repo.GetAsync(new DocKey("tenant-a", "app", DocCategory.Guide, "shared")))!.Content.ShouldBe("# Body");
                (await repo.GetAsync(new DocKey("tenant-b", "app", DocCategory.Guide, "shared")))!.Content.ShouldBe("# Other");
                break;
            case "foreign_tenant_gets_no_existence_signal":
                (await store.CreateAsync(a, Request("secret-id"))).Success.ShouldBeTrue();
                var probe = await store.CreateAsync(b, Request("secret-id"));
                probe.Success.ShouldBeTrue();
                probe.ErrorCode.ShouldBeNull();
                break;
            case "official_doc_id_cannot_be_shadowed":
                (await store.CreateAsync(new DocWriter(null, "S-ADMIN"), Request("official"))).Success.ShouldBeTrue();
                var shadow = await store.CreateAsync(a, Request("official", "# Shadow"));
                shadow.Success.ShouldBeFalse();
                (await repo.GetAsync(new DocKey(null, "app", DocCategory.Guide, "official")))!.Content.ShouldBe("# Body");
                break;
            case "document_count_quota_per_tenant":
                (await store.CreateAsync(a, Request("one"))).Success.ShouldBeTrue();
                (await store.CreateAsync(a, Request("two"))).Success.ShouldBeTrue();
                var over = await store.CreateAsync(a, Request("three"));
                over.Success.ShouldBeFalse();
                over.ErrorCode.ShouldBe("DOC_QUOTA_EXCEEDED");
                (await store.CreateAsync(b, Request("one"))).Success.ShouldBeTrue();
                break;
            case "byte_quota_per_tenant":
                var big = await store.CreateAsync(a, Request("big", "# " + new string('x', 8192)));
                big.Success.ShouldBeFalse();
                (await repo.GetAsync(new DocKey("tenant-a", "app", DocCategory.Guide, "big"))).ShouldBeNull();
                break;
            case "duplicate_create_is_refused_not_overwritten":
                var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.CreateAsync(a, Request("race", "# Writer " + i))));
                results.Count(r => r.Success).ShouldBe(1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bullet), bullet, null);
        }
    }
}
