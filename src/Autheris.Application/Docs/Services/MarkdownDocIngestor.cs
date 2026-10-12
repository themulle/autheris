namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;

public sealed partial class MarkdownDocIngestor : IDocIngestor
{
    [GeneratedRegex(@"\b[A-Z]{3,}_[A-Z0-9_]{3,}\b")]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex(@"^#{1,3}\s+(.+)$")]
    private static partial Regex HeadingRegex();

    public async Task<IReadOnlyList<DocChunk>> IngestMarkdownFileAsync(
        string filePath,
        string appId,
        DocCategory category,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Markdown file not found: {filePath}", filePath);
        }

        var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var docId = Path.GetFileNameWithoutExtension(filePath);
        return await IngestMarkdownContentAsync(content, docId, appId, category, filePath, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DocChunk>> IngestMarkdownContentAsync(
        string markdownContent,
        string docId,
        string appId,
        DocCategory category,
        string sourceFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        if (string.IsNullOrWhiteSpace(markdownContent))
        {
            return Task.FromResult<IReadOnlyList<DocChunk>>(Array.Empty<DocChunk>());
        }

        var lines = markdownContent.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var chunks = new List<DocChunk>();

        var currentTitle = docId;
        var currentSectionId = "root";
        var currentLines = new List<string>();
        var inCodeBlock = false;

        void FlushChunk()
        {
            if (currentLines.Count == 0) return;

            var sectionBody = string.Join("\n", currentLines).Trim();
            if (string.IsNullOrWhiteSpace(sectionBody))
            {
                currentLines.Clear();
                return;
            }

            var chunkId = new DocChunkIdentifier(appId, category.ToString(), docId, currentSectionId);
            var summary = ExtractSummary(sectionBody);
            var keywords = ExtractKeywords(sectionBody);
            var errorCodes = ExtractErrorCodes(sectionBody);

            chunks.Add(new DocChunk(
                Id: chunkId,
                AppId: appId,
                Category: category,
                Title: currentTitle,
                Content: sectionBody,
                Summary: summary,
                Keywords: keywords,
                ErrorCodes: errorCodes,
                SourceFile: sourceFile,
                AnchorId: currentSectionId,
                UpdatedAt: DateTimeOffset.UtcNow
            ));

            currentLines.Clear();
        }

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                currentLines.Add(line);
                continue;
            }

            if (!inCodeBlock)
            {
                var match = HeadingRegex().Match(line);
                if (match.Success)
                {
                    FlushChunk();
                    currentTitle = match.Groups[1].Value.Trim();
                    currentSectionId = SanitizeAnchor(currentTitle);
                    currentLines.Add(line);
                    continue;
                }
            }

            currentLines.Add(line);
        }

        FlushChunk();

        return Task.FromResult<IReadOnlyList<DocChunk>>(chunks);
    }

    private static string ExtractSummary(string content)
    {
        var firstParagraph = content.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);
        if (firstParagraph.Length == 0) return "";
        var candidate = firstParagraph[0].Replace('#', ' ').Trim();
        return candidate.Length > 200 ? string.Concat(candidate.AsSpan(0, 197), "...") : candidate;
    }

    private static IReadOnlyList<string> ExtractKeywords(string content)
    {
        var words = content.Split([' ', '\t', '\n', '\r', '`', '*', '(', ')', '[', ']', '{', '}', ':', '.', ','],
            StringSplitOptions.RemoveEmptyEntries);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in words)
        {
            if (w.Length >= 4 && char.IsLetter(w[0]))
            {
                set.Add(w.ToLowerInvariant());
            }
        }

        var list = new List<string>(set);
        return list.Count > 20 ? list.GetRange(0, 20) : list;
    }

    private static IReadOnlyList<string> ExtractErrorCodes(string content)
    {
        var matches = ErrorCodeRegex().Matches(content);
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            codes.Add(m.Value);
        }
        return new List<string>(codes);
    }

    private static string SanitizeAnchor(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (c is ' ' or '-' or '_')
            {
                sb.Append('-');
            }
        }
        var res = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(res) ? "section" : res;
    }
}
