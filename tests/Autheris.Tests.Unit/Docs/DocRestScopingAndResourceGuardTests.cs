#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Docs;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Domain.Security;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// REST documentation endpoints follow the MCP documentation tools: callers other than a canonical ClusterAdmin deposit tenant
/// documents (tenant bundle extraction is atomic and never replaces an existing official or tenant document), readers see official
/// documentation plus their own tenant, and no absolute server path is returned.
/// </summary>
public sealed class DocRestScopingAndResourceGuardTests
{
    private readonly InMemoryDocumentRepository _repository = new();
    private readonly InMemoryDocSearchService _search = new();
    private readonly GovernanceDocStore _store;

    public DocRestScopingAndResourceGuardTests()
    {
        _store = new GovernanceDocStore(_repository, _search, new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance);
    }

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

    [Fact]
    public async Task TenantBundle_StampsEveryDocument_ServesOnlyTheTenant_AndSkipsManifest()
    {
        using var zip = Zip(
            ("runbook/alpha.md", "# Zebraquartz Alpha\n\nAlpha procedure."),
            ("guide/beta.md", "# Zebraquartz Beta\n\nBeta procedure."),
            ("manifest.json", """{"appId":"tenant-app","name":"Spoofed"}"""));

        var result = await _store.ImportBundleAsync(new DocWriter("tenant-a", "S-1-5-21-1"), "tenant-app", zip);

        result.Success.ShouldBeTrue();

        var manifest = await _store.GetManifestAsync(DocAccessScope.ForTenant("tenant-a"), "tenant-app");
        manifest.ShouldBeNull();

        var alpha = await _repository.GetAsync(new DocKey("tenant-a", "tenant-app", DocCategory.Runbook, "alpha"));
        alpha.ShouldNotBeNull();
        alpha.AuthorSid.ShouldBe("S-1-5-21-1");

        var own = await _search.SearchAsync("zebraquartz", DocAccessScope.ForTenant("tenant-a"));
        own.Count.ShouldBeGreaterThanOrEqualTo(2);
        own.ShouldAllBe(r => r.TenantId == "tenant-a");

        (await _search.SearchAsync("zebraquartz", DocAccessScope.ForTenant("tenant-b"))).ShouldBeEmpty();
        (await _search.SearchAsync("zebraquartz", DocAccessScope.OfficialOnly)).ShouldBeEmpty();
        (await _search.SearchAsync("zebraquartz")).ShouldBeEmpty();
    }

    [Fact]
    public async Task TenantBundle_CannotOverwriteAnOfficialDocument_AndWritesNothingAtomically()
    {
        await _store.CreateAsync(new DocWriter(null, "S-ADMIN"), new DepositDocRequest("billing-service", DocCategory.Runbook, "Runbook", "# Official", DocId: "runbook"));

        using var zip = Zip(
            ("guide/harmless.md", "# Harmless"),
            ("runbook/runbook.md", "# Replaced by a tenant"));

        var result = await _store.ImportBundleAsync(new DocWriter("tenant-a", "S-1"), "billing-service", zip);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("DOC_ID_RESERVED");
        (await _repository.GetAsync(new DocKey(null, "billing-service", DocCategory.Runbook, "runbook")))!.Content.ShouldBe("# Official");
        (await _repository.GetAsync(new DocKey("tenant-a", "billing-service", DocCategory.Guide, "harmless"))).ShouldBeNull();
    }

    [Fact]
    public async Task TenantBundle_TwoTenantsWithSameDocId_StayIsolated()
    {
        using (var first = Zip(("runbook/shared.md", "# Tenant A secret procedure")))
        {
            (await _store.ImportBundleAsync(new DocWriter("tenant-a", "S-A"), "app", first)).Success.ShouldBeTrue();
        }

        using (var second = Zip(("runbook/shared.md", "# Tenant B own procedure")))
        {
            (await _store.ImportBundleAsync(new DocWriter("tenant-b", "S-B"), "app", second)).Success.ShouldBeTrue();
        }

        (await _repository.GetAsync(new DocKey("tenant-a", "app", DocCategory.Runbook, "shared")))!.Content.ShouldContain("Tenant A");
        (await _repository.GetAsync(new DocKey("tenant-b", "app", DocCategory.Runbook, "shared")))!.Content.ShouldContain("Tenant B");

        var searchA = await _search.SearchAsync("procedure", DocAccessScope.ForTenant("tenant-a"));
        searchA.ShouldHaveSingleItem();
        searchA[0].Summary.ShouldContain("Tenant A");

        var searchB = await _search.SearchAsync("procedure", DocAccessScope.ForTenant("tenant-b"));
        searchB.ShouldHaveSingleItem();
        searchB[0].Summary.ShouldContain("Tenant B");
    }

    [Fact]
    public async Task TenantBundle_ZipSlipEntry_IsRejectedBeforeAnythingIsWritten()
    {
        using var zip = Zip(
            ("runbook/legit.md", "# Legit"),
            ("../../evil.md", "# Evil"));

        var result = await _store.ImportBundleAsync(new DocWriter("tenant-a", "S-1"), "slip-app", zip);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ZIP_SLIP_ATTEMPT");
        (await _repository.GetAsync(new DocKey("tenant-a", "slip-app", DocCategory.Runbook, "legit"))).ShouldBeNull();
    }

    [Fact]
    public async Task TenantBundle_OversizedDocument_IsRejected()
    {
        using var zip = Zip(("runbook/huge.md", new string('x', 5 * 1024 * 1024 + 1)));

        var result = await _store.ImportBundleAsync(new DocWriter("tenant-a", "S-1"), "big-app", zip);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("DOC_TOO_LARGE");
    }

    [Fact]
    public async Task OfficialBundle_WithoutTenant_StaysOfficialAndVisibleToEveryone()
    {
        using var zip = Zip(("runbook/official.md", "# Quillbarrow Official\n\nOfficial procedure."));

        var result = await _store.ImportBundleAsync(new DocWriter(null, "S-ADMIN"), "off-app", zip);

        result.Success.ShouldBeTrue();

        var hits = await _search.SearchAsync("quillbarrow", DocAccessScope.ForTenant("tenant-b"));
        hits.ShouldNotBeEmpty();
        hits.ShouldAllBe(r => r.TenantId == null);
    }

    [Fact]
    public async Task Deposit_NeverExposesFilePathProperty()
    {
        var result = await _store.CreateAsync(new DocWriter(null, "S-ADMIN"), new DepositDocRequest("billing-service", DocCategory.Runbook, "Runbook", "# Text", DocId: "runbook"));

        result.Success.ShouldBeTrue();
        result.Version.ShouldBe(1);
        typeof(DepositDocResult).GetProperty("FilePath").ShouldBeNull();
    }

    private static DefaultHttpContext Http(string? tenant, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", "S-1-5-21-USER-1") };
        if (tenant is not null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
    }

    [Fact]
    public void Depositor_CanonicalClusterAdmin_DepositsOfficialDocumentation()
    {
        var ok = DocEndpointAccess.TryResolveDepositor(Http("tenant-a", "ClusterAdmin"), "autheris", out var tenantId, out var authorSid, out var denied);

        ok.ShouldBeTrue();
        denied.ShouldBeNull();
        tenantId.ShouldBeNull();
        authorSid.ShouldBe("S-1-5-21-USER-1");
    }

    [Fact]
    public void Depositor_GovernanceAdmin_DepositsForTheRequestTenant()
    {
        var ok = DocEndpointAccess.TryResolveDepositor(Http("tenant-a", "GovernanceAdmin"), "billing-service", out var tenantId, out var authorSid, out var denied);

        ok.ShouldBeTrue();
        denied.ShouldBeNull();
        tenantId.ShouldBe("tenant-a");
        authorSid.ShouldBe("S-1-5-21-USER-1");
    }

    [Theory]
    [InlineData("autheris")]
    [InlineData("GLOBAL")]
    [InlineData("system")]
    public void Depositor_GovernanceAdmin_CannotDepositForGlobalApplications(string appId)
    {
        var ok = DocEndpointAccess.TryResolveDepositor(Http("tenant-a", "GovernanceAdmin"), appId, out var tenantId, out _, out var denied);

        ok.ShouldBeFalse();
        tenantId.ShouldBeNull();
        denied!.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.GetValueOrDefault().ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void Depositor_NonClusterAdminWithoutTenant_IsForbidden()
    {
        var context = Http(null, "GovernanceAdmin");
        context.Items[SecurityPrincipalContext.TenantIdItemKey] = default(TenantId);

        var ok = DocEndpointAccess.TryResolveDepositor(context, "billing-service", out var tenantId, out _, out var denied);

        ok.ShouldBeFalse();
        tenantId.ShouldBeNull();
        denied!.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.GetValueOrDefault().ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void Depositor_MalformedTenantClaim_FailsClosed()
    {
        var ok = DocEndpointAccess.TryResolveDepositor(Http("bad tenant!", "GovernanceAdmin"), "billing-service", out var tenantId, out _, out var denied);

        ok.ShouldBeFalse();
        tenantId.ShouldBeNull();
        denied!.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.GetValueOrDefault().ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void ReadScope_IsOfficialPlusTheCallersTenant_AndOfficialOnlyWhenTheTenantIsUnusable()
    {
        DocEndpointAccess.ResolveReadScope(Http("tenant-a", "DataConsumer")).TenantId.ShouldBe("tenant-a");
        DocEndpointAccess.ResolveReadScope(Http("bad tenant!", "DataConsumer")).TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task ReadScope_HidesAnotherTenantsDocumentsFromRestReaders()
    {
        using var zip = Zip(("runbook/mine.md", "# Gryphonmoss Tenant Runbook\n\nTenant A only."));
        (await _store.ImportBundleAsync(new DocWriter("tenant-a", "S-A"), "app", zip)).Success.ShouldBeTrue();

        var asA = DocEndpointAccess.ResolveReadScope(Http("tenant-a", "DataConsumer"));
        var asB = DocEndpointAccess.ResolveReadScope(Http("tenant-b", "DataConsumer"));

        var visible = await _search.SearchAsync("gryphonmoss", asA);
        visible.ShouldNotBeEmpty();
        (await _search.SearchAsync("gryphonmoss", asB)).ShouldBeEmpty();
        (await _search.ListTopicsAsync(asB)).ShouldBeEmpty();
        (await _search.ListTopicsAsync(asA)).ShouldNotBeEmpty();
        (await _search.GetChunkAsync(visible[0].Id, asA)).ShouldNotBeNull();
        (await _search.GetChunkAsync(visible[0].Id, asB)).ShouldBeNull();
    }
}
