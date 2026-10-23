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
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// REST documentation endpoints follow the MCP documentation tools: callers other than a canonical ClusterAdmin deposit tenant
/// documents (tenant bundle extraction never replaces an existing file), readers see official documentation plus their own tenant,
/// and no absolute server path is returned. The stdio protocol handler reads semantic resources through the guardrail.
/// </summary>
public sealed class DocRestScopingAndResourceGuardTests : IDisposable
{
    private readonly string _root;
    private readonly InMemoryDocSearchService _search = new();
    private readonly FileSystemUniversalDocStore _store;

    public DocRestScopingAndResourceGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "autheris_doc_rest_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new FileSystemUniversalDocStore(_root, _search, new MarkdownDocIngestor(), NullLogger<FileSystemUniversalDocStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
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

    // ---- Tenant bundle extraction ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TenantBundle_StampsEveryDocument_ServesOnlyTheTenant_AndSkipsNonMarkdown()
    {
        using var zip = Zip(
            ("runbook/alpha.md", "# Zebraquartz Alpha\n\nAlpha procedure."),
            ("guide/beta.md", "# Zebraquartz Beta\n\nBeta procedure."),
            ("manifest.json", """{"appId":"tenant-app","name":"Spoofed"}"""));

        var result = await _store.ExtractBundleAsync("tenant-app", zip, "tenant-a", "S-1-5-21-1");

        result.Success.ShouldBeTrue();
        result.FilePath.ShouldNotBeNull();
        Path.IsPathRooted(result.FilePath).ShouldBeFalse();

        foreach (var rel in new[] { "runbook/alpha.md", "guide/beta.md" })
        {
            var text = await File.ReadAllTextAsync(Path.Combine(_root, "tenant-app", rel));
            text.ShouldStartWith("---\n" + DocProvenance.TenantKey + ": tenant-a\n");
            text.ShouldContain(DocProvenance.AuthorKey + ": S-1-5-21-1");
        }

        File.Exists(Path.Combine(_root, "tenant-app", "manifest.json")).ShouldBeFalse();

        var own = await _search.SearchAsync("zebraquartz", DocAccessScope.ForTenant("tenant-a"));
        own.Count.ShouldBeGreaterThanOrEqualTo(2);
        own.ShouldAllBe(r => r.TenantId == "tenant-a");

        (await _search.SearchAsync("zebraquartz", DocAccessScope.ForTenant("tenant-b"))).ShouldBeEmpty();
        (await _search.SearchAsync("zebraquartz", DocAccessScope.OfficialOnly)).ShouldBeEmpty();
        (await _search.SearchAsync("zebraquartz")).ShouldBeEmpty();
    }

    [Fact]
    public async Task TenantBundle_CannotOverwriteAnOfficialDocument_AndWritesNothing()
    {
        await _store.DepositDocumentAsync(new DepositDocRequest("billing-service", DocCategory.Runbook, "Runbook", "# Official", DocId: "runbook"));
        var officialPath = Path.Combine(_root, "billing-service", "runbook", "runbook.md");

        using var zip = Zip(
            ("guide/harmless.md", "# Harmless"),
            ("runbook/runbook.md", "# Replaced by a tenant"));

        var result = await _store.ExtractBundleAsync("billing-service", zip, "tenant-a", "S-1");

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("DOC_OVERWRITE_FORBIDDEN");
        (await File.ReadAllTextAsync(officialPath)).ShouldBe("# Official");
        File.Exists(Path.Combine(_root, "billing-service", "guide", "harmless.md")).ShouldBeFalse();
    }

    [Fact]
    public async Task TenantBundle_CannotOverwriteAnotherTenantsDocument()
    {
        using (var first = Zip(("runbook/shared.md", "# Tenant A secret procedure")))
        {
            (await _store.ExtractBundleAsync("app", first, "tenant-a", "S-A")).Success.ShouldBeTrue();
        }

        using var second = Zip(("runbook/shared.md", "# Tenant B replacement"));
        var result = await _store.ExtractBundleAsync("app", second, "tenant-b", "S-B");

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("DOC_OVERWRITE_FORBIDDEN");
        (await File.ReadAllTextAsync(Path.Combine(_root, "app", "runbook", "shared.md"))).ShouldContain("Tenant A secret procedure");
    }

    [Fact]
    public async Task TenantBundle_ZipSlipEntry_IsRejectedBeforeAnythingIsWritten()
    {
        using var zip = Zip(
            ("runbook/legit.md", "# Legit"),
            ("../../evil.md", "# Evil"));

        var result = await _store.ExtractBundleAsync("slip-app", zip, "tenant-a", "S-1");

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ZIP_SLIP_ATTEMPT");
        File.Exists(Path.Combine(_root, "slip-app", "runbook", "legit.md")).ShouldBeFalse();
    }

    [Fact]
    public async Task TenantBundle_OversizedDocument_IsRejected()
    {
        using var zip = Zip(("runbook/huge.md", new string('x', 5 * 1024 * 1024 + 1)));

        var result = await _store.ExtractBundleAsync("big-app", zip, "tenant-a", "S-1");

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("DOC_TOO_LARGE");
    }

    [Fact]
    public async Task OfficialBundle_WithoutTenant_StaysOfficialAndVisibleToEveryone()
    {
        using var zip = Zip(("runbook/official.md", "# Quillbarrow Official\n\nOfficial procedure."));

        var result = await _store.ExtractBundleAsync("off-app", zip, null, null);

        result.Success.ShouldBeTrue();
        result.FilePath.ShouldNotBeNull();
        Path.IsPathRooted(result.FilePath).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(_root, "off-app", "runbook", "official.md"))).ShouldNotContain(DocProvenance.TenantKey);

        var hits = await _search.SearchAsync("quillbarrow", DocAccessScope.ForTenant("tenant-b"));
        hits.ShouldNotBeEmpty();
        hits.ShouldAllBe(r => r.TenantId == null);
    }

    [Fact]
    public async Task Deposit_ReturnsAStoreRelativePath()
    {
        var result = await _store.DepositDocumentAsync(new DepositDocRequest("billing-service", DocCategory.Runbook, "Runbook", "# Text", DocId: "runbook"));

        result.FilePath.ShouldBe("billing-service/runbook/runbook.md");
        Path.IsPathRooted(result.FilePath).ShouldBeFalse();
    }

    // ---- REST endpoint scoping helpers --------------------------------------------------------------------------------------------

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
        context.Items[TenantResolutionMiddleware.TenantIdItemKey] = default(TenantId);

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
        (await _store.ExtractBundleAsync("app", zip, "tenant-a", "S-A")).Success.ShouldBeTrue();

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

    [Fact]
    public void Scrub_NeverReturnsAnAbsolutePath()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "shared-docs", "billing-service", "runbook", "x.md");

        var scrubbed = DocEndpointAccess.Scrub(new DepositDocResult(true, null, absolute, "ok"));
        var relative = DocEndpointAccess.Scrub(new DepositDocResult(true, null, "billing-service/runbook/x.md", "ok"));
        var none = DocEndpointAccess.Scrub(new DepositDocResult(false, null, null, "no"));

        scrubbed.FilePath.ShouldBe("x.md");
        relative.FilePath.ShouldBe("billing-service/runbook/x.md");
        none.FilePath.ShouldBeNull();
    }

    // ---- stdio protocol handler: semantic resources through the guardrail ---------------------------------------------------------

    private static (McpProtocolHandler Handler, IAiDataGuardrailService Guardrail, ISemanticMcpCompiler Compiler, string SessionId) Handler()
    {
        var guardrail = Substitute.For<IAiDataGuardrailService>();
        var compiler = Substitute.For<ISemanticMcpCompiler>();
        compiler.ReadSemanticResourceAsync(Arg.Any<ClaimsPrincipal?>(), "glossary://finance/mrr", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<McpResourceItem?>(
                new McpResourceItem("glossary://finance/mrr", "mrr", "Monthly recurring revenue", "text/markdown", "RAW contact jane@example.com")));
        var handler = new McpProtocolHandler(
            new McpSessionStore(NullLogger<McpSessionStore>.Instance),
            Substitute.For<IMcpToolRegistry>(),
            guardrail,
            NullLogger<McpProtocolHandler>.Instance,
            compiler);
        return (handler, guardrail, compiler, handler.CreateSession("agent", "tenant-a").SessionId);
    }

    private static string Read(string uri) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 7, method = "resources/read", @params = new { uri } });

    [Fact]
    public async Task ResourcesRead_ServesTheGuardrailOutput_NotTheRawResourceText()
    {
        var (handler, guardrail, _, sid) = Handler();
        IReadOnlyList<TableIdentifier>? targets = null;
        guardrail.ReadResourceWithGuardrailAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<TableIdentifier>>(), Arg.Any<Func<CancellationToken, Task<string>>>(),
                Arg.Any<McpSessionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                targets = ci.ArgAt<IReadOnlyList<TableIdentifier>>(1);
                return RunGuardedRead(ci.ArgAt<Func<CancellationToken, Task<string>>>(2), raw => raw.Replace("jane@example.com", "[MASKED]", StringComparison.Ordinal));
            });

        var response = await handler.HandleMessageAsync(sid, Read("glossary://finance/mrr"));

        using var doc = JsonDocument.Parse(response);
        var text = doc.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString();
        text.ShouldBe("RAW contact [MASKED]");
        targets.ShouldNotBeNull();
        targets!.Count.ShouldBe(1);
        targets[0].ShouldBe(McpSemanticTools.SemanticCatalogTable);
    }

    private static async ValueTask<McpToolCallResult> RunGuardedRead(Func<CancellationToken, Task<string>> read, Func<string, string> scrub)
    {
        try
        {
            return new McpToolCallResult(IsSuccess: true, ContentJson: scrub(await read(CancellationToken.None)));
        }
        catch (McpResourceNotFoundException)
        {
            return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: "not found");
        }
    }

    [Fact]
    public async Task ResourcesRead_DeniedByTheGuardrail_AnswersLikeAnUnknownResource()
    {
        var (handler, guardrail, _, sid) = Handler();
        guardrail.ReadResourceWithGuardrailAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<TableIdentifier>>(), Arg.Any<Func<CancellationToken, Task<string>>>(),
                Arg.Any<McpSessionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: "denied"));

        var denied = await handler.HandleMessageAsync(sid, Read("glossary://finance/mrr"));

        using var doc = JsonDocument.Parse(denied);
        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32004);
        denied.ShouldNotContain("RAW contact");
        denied.ShouldNotContain("jane@example.com");
        denied.ShouldNotContain("denied");
    }

    [Fact]
    public async Task ResourcesRead_UnknownUri_AnswersNotFound_ThroughTheGuardrail()
    {
        var (handler, guardrail, _, sid) = Handler();
        guardrail.ReadResourceWithGuardrailAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<TableIdentifier>>(), Arg.Any<Func<CancellationToken, Task<string>>>(),
                Arg.Any<McpSessionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => RunGuardedRead(ci.ArgAt<Func<CancellationToken, Task<string>>>(2), raw => raw));

        var response = await handler.HandleMessageAsync(sid, Read("glossary://nothing/here"));

        using var doc = JsonDocument.Parse(response);
        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32004);
        await guardrail.Received(1).ReadResourceWithGuardrailAsync(
            "glossary://nothing/here", Arg.Any<IReadOnlyList<TableIdentifier>>(), Arg.Any<Func<CancellationToken, Task<string>>>(),
            Arg.Any<McpSessionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResourcesRead_NeverReadsTheCompilerOutsideTheGuardrail()
    {
        var (handler, guardrail, compiler, sid) = Handler();
        // A guardrail that denies without running the read delegate: the compiler must not have been consulted by the handler itself.
        guardrail.ReadResourceWithGuardrailAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<TableIdentifier>>(), Arg.Any<Func<CancellationToken, Task<string>>>(),
                Arg.Any<McpSessionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new McpToolCallResult(IsSuccess: false, ContentJson: "{}"));

        await handler.HandleMessageAsync(sid, Read("glossary://finance/mrr"));

        await compiler.DidNotReceive().ReadSemanticResourceAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
