namespace Autheris.Tests.Unit.Docs;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Docs.Services;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

internal sealed class DocsCapturingAuditWriter : IAuditWriter
{
    public bool ShouldFail { get; set; }
    public List<AuditLogEntry> Entries { get; } = [];

    public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        if (ShouldFail)
        {
            throw new AuditUnavailableException("Simulated audit failure.");
        }

        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);

    public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);
}

internal sealed class DocsCapturingLogger : ILogger
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception) + exception);
}

[Trait("Category", "Security")]
public sealed class DocsAuditTests
{
    private const string Title = "SecretTitleMarker";
    private const string Body = "SecretBodyMarker";

    private static McpSessionContext Session() =>
        new("s1", "c1", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-5-21-A", Roles: ["GovernanceAdmin"]);

    private static Task<string> Deposit(GovernanceDocStore store, IAuditWriter audit, string appId, string docId, long? replaceVersion = null)
    {
        var args = JsonSerializer.Serialize(new { appId, category = "Runbook", docId, title = Title, content = "# H\n" + Body, replaceVersion });
        return McpDocTools.ExecuteDocToolAsync(McpDocTools.DepositDocumentation, args, new InMemoryDocSearchService(), store, Session(), audit, indexReadiness: null, CancellationToken.None);
    }

    [Fact]
    public async Task Content_never_logged_or_audited()
    {
        var logger = new DocsCapturingLogger();
        var repo = new InMemoryDocumentRepository();
        var store = new GovernanceDocStore(repo, new InMemoryDocSearchService(), new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance);
        var audit = new DocsCapturingAuditWriter();

        await Deposit(store, audit, "app", "one");
        await Deposit(store, audit, "app", "one"); // conflict -> denied audit

        audit.Entries.Count.ShouldBe(2);
        foreach (var entry in audit.Entries)
        {
            entry.DetailsJson.ShouldNotContain(Title);
            entry.DetailsJson.ShouldNotContain(Body);
            entry.TargetTable.ShouldNotContain(Body);
        }

        using var corrupt = new System.IO.MemoryStream(Encoding_Corrupt());
        DocBundleReader.Read(new DocWriter("tenant-a", "S-1"), "app", corrupt, new Autheris.Domain.Options.DocsOptions(), logger);
        logger.Messages.ShouldNotContain(m => m.Contains(Body, StringComparison.Ordinal));
    }

    private static byte[] Encoding_Corrupt()
    {
        using var ms = new System.IO.MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("guide/x.md", System.IO.Compression.CompressionLevel.NoCompression);
            using var w = new System.IO.StreamWriter(entry.Open());
            w.Write("# X\n" + Body);
        }

        var bytes = ms.ToArray();
        var needle = System.Text.Encoding.UTF8.GetBytes(Body);
        bytes[bytes.AsSpan().IndexOf(needle) + 2] ^= 0x20;
        return bytes;
    }

    [Fact]
    public async Task McpDeposit_Denied_IsAudited_WithErrorCode()
    {
        var store = new GovernanceDocStore(new InMemoryDocumentRepository(), new InMemoryDocSearchService(), new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance);
        var audit = new DocsCapturingAuditWriter();

        await Deposit(store, audit, "app", "dup");
        var denied = await Deposit(store, audit, "app", "dup");
        var stale = await Deposit(store, audit, "app", "dup", replaceVersion: 99);

        denied.ShouldContain("DOC_EXISTS");
        stale.ShouldContain("VERSION_MISMATCH");
        audit.Entries.Count.ShouldBe(3);
        audit.Entries[0].Decision.ShouldBe("ALLOW");
        audit.Entries[1].Decision.ShouldBe("DENY");
        audit.Entries[1].EventType.ShouldBe(AuditEventTypes.DocIngestionDocumentApplied);
        audit.Entries[1].DetailsJson.ShouldContain("DOC_EXISTS");
        audit.Entries[2].Decision.ShouldBe("DENY");
        audit.Entries[2].DetailsJson.ShouldContain("VERSION_MISMATCH");
    }

    [Fact]
    public async Task McpDeposit_Success_AuditCarriesHashAndByteCount_NoContent()
    {
        var repo = new InMemoryDocumentRepository();
        var store = new GovernanceDocStore(repo, new InMemoryDocSearchService(), new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance);
        var audit = new DocsCapturingAuditWriter();

        await Deposit(store, audit, "app", "ok");

        var saved = await repo.GetAsync(new Autheris.Application.Docs.Interfaces.DocKey("tenant-a", "app", Autheris.Domain.Model.DocCategory.Runbook, "ok"));
        using var details = JsonDocument.Parse(audit.Entries[0].DetailsJson!);
        details.RootElement.GetProperty("contentSha256").GetString().ShouldBe(saved!.ContentSha256);
        details.RootElement.GetProperty("byteCount").GetInt32().ShouldBe(saved.ByteCount);
        audit.Entries[0].DetailsJson.ShouldNotContain(Body);
        audit.Entries[0].DetailsJson.ShouldNotContain(Title);
    }

    [Fact]
    public async Task McpDeposit_AuditRunsAfterCommit_AndAuditFailureFailsClosed()
    {
        var repo = new InMemoryDocumentRepository();
        var store = new GovernanceDocStore(repo, new InMemoryDocSearchService(), new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance);
        var audit = new DocsCapturingAuditWriter();

        await Deposit(store, audit, "app", "first");
        (await repo.GetAsync(new Autheris.Application.Docs.Interfaces.DocKey("tenant-a", "app", Autheris.Domain.Model.DocCategory.Runbook, "first"))).ShouldNotBeNull();

        // Same as the REST write routes: the document is committed first, the audit follows, and a failing audit surfaces as an error.
        audit.ShouldFail = true;
        await Should.ThrowAsync<AuditUnavailableException>(() => Deposit(store, audit, "app", "second"));
        (await repo.GetAsync(new Autheris.Application.Docs.Interfaces.DocKey("tenant-a", "app", Autheris.Domain.Model.DocCategory.Runbook, "second"))).ShouldNotBeNull();
    }
}
