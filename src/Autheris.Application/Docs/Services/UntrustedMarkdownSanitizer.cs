namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Result of sanitizing untrusted markdown content.
/// </summary>
public readonly record struct SanitizedMarkdown(string Text, bool Changed);

/// <summary>
/// Pure, deterministic sanitiser for untrusted Markdown documentation content, titles, and tags.
/// </summary>
public static partial class UntrustedMarkdownSanitizer
{
    [GeneratedRegex(@"<script\b[^>]*>(?:[\s\S]*?<\/script\s*>)?|<script\b[^>]*\/>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlockRegex();

    [GeneratedRegex(@"<style\b[^>]*>(?:[\s\S]*?<\/style\s*>)?|<style\b[^>]*\/>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleBlockRegex();

    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"<[\/!]?[a-zA-Z](?:'[^']*'|""[^""]*""|[^'"">])*>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"(?<image>!)?\[(?<text>(?:\\\]|[^\]])*)\]\((?<destination>\s*<?(?<url>(?:[^\s()<>]|\([^()]*\))+)>?(?:\s+(?:""[^""]*""|'[^']*'|\([^)]*\)))?\s*)\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"^[ \t]*\[(?<text>[^\]]+)\]:\s*<?(?<url>[^\s>]+)>?[ \t]*(?:""[^""]*""|'[^']*'|\([^)]*\))?[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex MarkdownReferenceLinkRegex();

    [GeneratedRegex(@"\A---\s*\n[\s\S]*?\n---\s*(?:\n|\z)")]
    private static partial Regex LeadingFrontMatterRegex();

    /// <summary>
    /// Sanitizes raw untrusted Markdown in the exact processing order required by the documentation security specification.
    /// Throws <see cref="DocValidationException"/> when the sanitized text is empty or exceeds <paramref name="maxBytes"/>.
    /// </summary>
    public static SanitizedMarkdown Sanitize(string markdown, int maxBytes)
    {
        if (markdown is null)
        {
            throw new DocValidationException("DOC_EMPTY", "Document content cannot be null.");
        }

        // Rule 1: Normalise line endings to \n
        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        // Rule 2: Unicode NFC normalisation
        if (!text.IsNormalized(NormalizationForm.FormC))
        {
            text = text.Normalize(NormalizationForm.FormC);
        }

        // Rules 3, 4, 5: Remove C0/C1 controls (except \n and \t), bidi controls, zero-width/invisible, tags, and cap combining marks at 4
        text = UntrustedTextRunes.FilterRunes(text, allowNewlinesAndTabs: true);

        // Rule 6: Remove HTML comments and raw HTML block/inline tags (keep text content; drop <script> and <style> content entirely)
        text = RemoveHtml(text);

        // Rule 7: Rewrite Markdown links/images whose URL scheme is not http, https, mailto or relative to their plain text
        text = RewriteLinks(text);

        // Rule 8: Drop a leading YAML front-matter block (--- ... ---)
        text = LeadingFrontMatterRegex().Replace(text, string.Empty);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DocValidationException("DOC_EMPTY", "Sanitized markdown is empty.");
        }

        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount > maxBytes)
        {
            throw new DocValidationException("DOC_TOO_LARGE", $"Sanitized markdown byte count ({byteCount}) exceeds maximum permitted size of {maxBytes} bytes.");
        }

        var changed = !string.Equals(markdown, text, StringComparison.Ordinal);
        return new SanitizedMarkdown(text, changed);
    }

    /// <summary>
    /// Sanitizes an untrusted title: applies NFC normalisation, rune rules, and caps at <paramref name="maxLength"/> on a rune boundary.
    /// </summary>
    public static string SanitizeTitle(string? title, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var text = title.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        if (!text.IsNormalized(NormalizationForm.FormC))
        {
            text = text.Normalize(NormalizationForm.FormC);
        }

        text = UntrustedTextRunes.FilterRunes(text, allowNewlinesAndTabs: false).Trim();

        return TruncateRunes(text, maxLength);
    }

    /// <summary>
    /// Sanitizes untrusted tags: applies NFC normalisation, rune rules, and caps each at <paramref name="maxTagLength"/>,
    /// returning at most <paramref name="maxTags"/> distinct non-empty tags.
    /// </summary>
    public static IReadOnlyList<string> SanitizeTags(IEnumerable<string>? tags, int maxTags = 20, int maxTagLength = 64)
    {
        if (tags is null)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var text = tag.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            if (!text.IsNormalized(NormalizationForm.FormC))
            {
                text = text.Normalize(NormalizationForm.FormC);
            }

            text = UntrustedTextRunes.FilterRunes(text, allowNewlinesAndTabs: false).Trim();
            text = TruncateRunes(text, maxTagLength);

            if (!string.IsNullOrEmpty(text) && !result.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(text);
                if (result.Count >= maxTags)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static string RemoveHtml(string text)
    {
        text = ScriptBlockRegex().Replace(text, string.Empty);
        text = StyleBlockRegex().Replace(text, string.Empty);
        text = HtmlCommentRegex().Replace(text, string.Empty);
        text = HtmlTagRegex().Replace(text, string.Empty);
        return text;
    }

    private static string RewriteLinks(string text)
    {
        text = MarkdownReferenceLinkRegex().Replace(text, match =>
        {
            var url = match.Groups["url"].Value;
            return IsAllowedUrl(url) ? match.Value : string.Empty;
        });

        text = MarkdownLinkRegex().Replace(text, match =>
        {
            var url = match.Groups["url"].Value;
            if (IsAllowedUrl(url))
            {
                return match.Value;
            }

            return match.Groups["text"].Value;
        });

        return text;
    }

    private static bool IsAllowedUrl(string url)
    {
        var trimmed = url.Trim().TrimStart('<').TrimEnd('>');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return true;
        }

        var colonIdx = trimmed.IndexOf(':');
        if (colonIdx < 0)
        {
            return true;
        }

        var slashIdx = trimmed.IndexOfAny(['/', '?', '#']);
        if (slashIdx >= 0 && slashIdx < colonIdx)
        {
            return true;
        }

        var scheme = trimmed[..colonIdx].Trim();
        return string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(scheme, "mailto", StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateRunes(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var builder = new StringBuilder(maxLength);
        foreach (var rune in text.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maxLength)
            {
                break;
            }

            builder.Append(rune);
        }

        return builder.ToString().TrimEnd();
    }
}
