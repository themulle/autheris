namespace Autheris.Tests.Unit.Docs;

using System.Threading.Tasks;
using Autheris.Application.Docs.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class MarkdownDocIngestorTests
{
    private const string SampleMarkdown = """
    # Operations Runbook

    This runbook covers operational emergency procedures for the gateway.

    ## Break-Glass Access Procedure

    In emergency incidents, operators can activate break-glass mode:

    ```bash
    curl -X POST https://gateway.internal/api/v1/governance/break-glass \
      -H "Authorization: Bearer $EMERGENCY_TOKEN"
    ```

    If the token is expired, the system returns ERR_TOKEN_EXPIRED and HTTP 401.

    ### Consent Revocation

    To immediately revoke a compromised consent:

    ```sql
    UPDATE consents SET is_revoked = 1 WHERE consent_id = 'xxx';
    ```

    Failure code: ERR_REVOCATION_FAILED.
    """;

    [Fact]
    public async Task IngestMarkdownContentAsync_ProducesChunksAcrossHeadings()
    {
        var ingestor = new MarkdownDocIngestor();

        var chunks = await ingestor.IngestMarkdownContentAsync(
            markdownContent: SampleMarkdown,
            docId: "operations-runbook",
            appId: "autheris",
            category: DocCategory.Runbook,
            sourceFile: "docs/operations-runbook.md");

        chunks.Count.ShouldBe(3);

        // Chunk 1: Root / Main title
        chunks[0].Title.ShouldBe("Operations Runbook");
        chunks[0].Id.ToString().ShouldBe("autheris::Runbook::operations-runbook#operations-runbook");

        // Chunk 2: Break-Glass Section
        chunks[1].Title.ShouldBe("Break-Glass Access Procedure");
        chunks[1].Content.ShouldContain("curl -X POST");
        chunks[1].Content.ShouldContain("Authorization: Bearer");
        chunks[1].ErrorCodes.ShouldContain("ERR_TOKEN_EXPIRED");

        // Chunk 3: Consent Revocation Section
        chunks[2].Title.ShouldBe("Consent Revocation");
        chunks[2].Content.ShouldContain("UPDATE consents SET is_revoked");
        chunks[2].ErrorCodes.ShouldContain("ERR_REVOCATION_FAILED");
    }

    [Fact]
    public async Task IngestMarkdownFileAsync_RejectsPathTraversalSequences()
    {
        var ingestor = new MarkdownDocIngestor();

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await ingestor.IngestMarkdownFileAsync("../../etc/passwd", "test-app", DocCategory.Runbook);
        });
    }
}
