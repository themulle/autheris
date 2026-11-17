namespace Autheris.Tests.Unit.Docs;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Docs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class LegacyDocFileImporterTests : IDisposable
{
    private readonly string _source = Path.Combine(Path.GetTempPath(), "legacy-docs-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryDocumentRepository _repo = new();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public LegacyDocFileImporterTests() => Directory.CreateDirectory(_source);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_source, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LegacyDocFileImporter CreateImporter(string? sourcePath, DocsOptions? baseOptions = null)
    {
        var options = baseOptions ?? new DocsOptions();
        options.LegacyImport.SourcePath = sourcePath;
        var wrapped = Options.Create(options);
        var store = new GovernanceDocStore(_repo, new InMemoryDocSearchService(), new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance, wrapped);
        return new LegacyDocFileImporter(store, _repo, _audit, wrapped, NullLogger<LegacyDocFileImporter>.Instance);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string TenantFile(string tenant, string body) => $"---\nautheris-tenant: {tenant}\nautheris-author: S-1-5-21-1\n---\n{body}";

    [Fact]
    public async Task Imports_official_and_tenant_files_by_provenance()
    {
        Write("autheris/guide/official-guide.md", "# Official\n\nUse the console.");
        Write("payments/runbook/reset.md", TenantFile("tenant-a", "# Reset\n\nRestart the gateway."));

        var result = await CreateImporter(_source).RunAsync(CancellationToken.None);

        result.ShouldBe(new LegacyDocImportResult(2, 0, 0, 0));
        var official = await _repo.GetAsync(new DocKey(null, "autheris", DocCategory.Guide, "official-guide"));
        official.ShouldNotBeNull();
        var tenant = await _repo.GetAsync(new DocKey("tenant-a", "payments", DocCategory.Runbook, "reset"));
        tenant.ShouldNotBeNull();
        tenant.AuthorSid.ShouldBe("S-1-5-21-1");
        tenant.Content.ShouldNotContain("autheris-tenant");
        (await _repo.GetAsync(new DocKey(null, "payments", DocCategory.Runbook, "reset"))).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Skips_unknown_tenant_files()
    {
        Write("app/guide/blank.md", "---\nautheris-tenant:  \n---\nbody");
        Write("app/guide/unterminated.md", "---\nautheris-tenant: tenant-a\nno closing fence");
        Write("app/guide/invalid.md", TenantFile("bad tenant/../x", "body"));

        var result = await CreateImporter(_source).RunAsync(CancellationToken.None);

        result.ShouldBe(new LegacyDocImportResult(0, 3, 0, 0));
        (await _repo.ReadChangedSinceAsync(null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Second_run_is_idempotent_and_never_overwrites()
    {
        var changed = Write("app/guide/changed.md", "# Changed\n\nfirst");
        Write("app/guide/stable.md", "# Stable\n\nsame");
        var importer = CreateImporter(_source);
        (await importer.RunAsync(CancellationToken.None)).ShouldBe(new LegacyDocImportResult(2, 0, 0, 0));

        File.WriteAllText(changed, "# Changed\n\nsecond");
        var second = await importer.RunAsync(CancellationToken.None);

        second.ShouldBe(new LegacyDocImportResult(0, 0, 1, 1));
        var stored = await _repo.GetAsync(new DocKey(null, "app", DocCategory.Guide, "changed"));
        stored!.Content.ShouldContain("first");
        stored.Version.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Import_respects_quota_and_sanitiser()
    {
        Write("app/guide/a.md", TenantFile("tenant-a", "# A\n\nvisible<!-- hidden note -->text‮end"));
        Write("app/guide/b.md", TenantFile("tenant-a", "# B"));
        Write("app/guide/huge.md", "x" + new string('y', 3000));
        var options = new DocsOptions { MaxDocumentBytes = 1000, Quota = { MaxDocumentsPerTenant = 1 } };

        var result = await CreateImporter(_source, options).RunAsync(CancellationToken.None);

        result!.Imported.ShouldBe(1);
        result.Rejected.ShouldBe(2);
        var all = await _repo.ReadChangedSinceAsync(null);
        all.Count.ShouldBe(1);
        all[0].Content.ShouldNotContain("hidden note");
        all[0].Content.ShouldNotContain("‮");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Traversal_and_symlink_entries_are_skipped()
    {
        var outside = Path.Combine(Path.GetTempPath(), "legacy-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outside, "guide"));
        try
        {
            File.WriteAllText(Path.Combine(outside, "guide", "secret.md"), "# Outside");
            Directory.CreateDirectory(Path.Combine(_source, "app", "guide"));
            Directory.CreateSymbolicLink(Path.Combine(_source, "linked-app"), outside);
            Directory.CreateSymbolicLink(Path.Combine(_source, "app", "linked-category"), Path.Combine(outside, "guide"));
            File.CreateSymbolicLink(Path.Combine(_source, "app", "guide", "linked-file.md"), Path.Combine(outside, "guide", "secret.md"));
            Write("app/guide/Bad Name.md", "# Bad identifier");
            Write("app/guide/notes.txt", "ignored");
            Write("app/guide/ok.md", "# Ok");

            var result = await CreateImporter(_source).RunAsync(CancellationToken.None);

            result.ShouldBe(new LegacyDocImportResult(1, 3, 1, 0));
            (await _repo.ReadChangedSinceAsync(null)).Select(r => r.DocId).ShouldBe(["ok"]);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Audit_contains_counts_only_no_content()
    {
        Write("app/guide/secret-title.md", "# Confidential Heading\n\nconfidential body text");
        Write("app/guide/skipme.md", "---\nautheris-tenant: __unknown__\n---\nbody");
        AuditLogEntry? captured = null;
        await _audit.RecordAsync(Arg.Do<AuditLogEntry>(e => captured = e), Arg.Any<CancellationToken>());

        await CreateImporter(_source).RunAsync(CancellationToken.None);

        await _audit.Received(1).RecordAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        captured.ShouldNotBeNull();
        captured.EventType.ShouldBe(AuditEventTypes.DocLegacyImportCompleted);
        captured.DetailsJson.ShouldBe("""{"imported":1,"skipped":1,"rejected":0,"alreadyPresent":0}""");
        captured.DetailsJson.ShouldNotContain("Confidential");
        captured.DetailsJson.ShouldNotContain("secret-title");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Not_configured_does_nothing(string? sourcePath)
    {
        Write("app/guide/a.md", "# A");

        var result = await CreateImporter(sourcePath).RunAsync(CancellationToken.None);

        result.ShouldBeNull();
        (await _repo.ReadChangedSinceAsync(null)).ShouldBeEmpty();
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }
}
