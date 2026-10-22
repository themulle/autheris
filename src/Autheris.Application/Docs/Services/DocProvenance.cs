namespace Autheris.Application.Docs.Services;

using System;
using System.IO;
using System.Text;

/// <summary>
/// Provenance of stored documents. A document deposited by a tenant carries a front-matter block naming the depositing tenant
/// and author at the very top of its markdown file, written in the same file write as the content, so a tenant document read
/// back from disk (startup sync) can never lose its owner and turn into official documentation. Official documentation has no
/// such block. The block is stripped before indexing, so it never reaches search results.
/// </summary>
public static class DocProvenance
{
    public const string TenantKey = "autheris-tenant";
    public const string AuthorKey = "autheris-author";

    /// <summary>
    /// Owner assigned when a provenance block names no usable tenant: the document then stays visible to no tenant (fail
    /// closed) instead of being treated as official documentation.
    /// </summary>
    public const string UnknownTenant = "__unknown__";

    private const string Fence = "---";

    /// <summary>
    /// The file content to store: <paramref name="content"/> preceded by the provenance block for tenant documents
    /// (<paramref name="tenantId"/> not null). A blank tenant id still writes the block, which then reads back as
    /// <see cref="UnknownTenant"/>, never as official documentation.
    /// </summary>
    public static string Compose(string content, string? tenantId, string? authorSid)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (tenantId is null)
        {
            return content;
        }

        var sb = new StringBuilder(content.Length + 128);
        sb.Append(Fence).Append('\n');
        sb.Append(TenantKey).Append(": ").Append(SanitizeValue(tenantId)).Append('\n');
        if (!string.IsNullOrWhiteSpace(authorSid))
        {
            sb.Append(AuthorKey).Append(": ").Append(SanitizeValue(authorSid)).Append('\n');
        }

        sb.Append(Fence).Append('\n');
        sb.Append(content);
        return sb.ToString();
    }

    /// <summary>
    /// Splits stored file content into the document body and its provenance. Only a leading front-matter block that names
    /// <see cref="TenantKey"/> is a provenance block; any other content (including ordinary YAML front matter of official
    /// documents) is returned unchanged with no tenant.
    /// </summary>
    public static (string Content, string? TenantId, string? AuthorSid) Parse(string fileContent)
    {
        if (string.IsNullOrEmpty(fileContent))
        {
            return (string.Empty, null, null);
        }

        var text = fileContent[0] == '﻿' ? fileContent[1..] : fileContent;
        var firstLineEnd = text.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineEnd < 0 || !string.Equals(text[..firstLineEnd].TrimEnd('\r').Trim(), Fence, StringComparison.Ordinal))
        {
            return (fileContent, null, null);
        }

        string? tenant = null;
        string? author = null;
        var hasTenantKey = false;
        var pos = firstLineEnd + 1;
        while (pos < text.Length)
        {
            var end = text.IndexOf('\n', pos);
            var line = (end < 0 ? text[pos..] : text[pos..end]).TrimEnd('\r');
            var next = end < 0 ? text.Length : end + 1;

            if (string.Equals(line.Trim(), Fence, StringComparison.Ordinal))
            {
                if (!hasTenantKey)
                {
                    return (fileContent, null, null);
                }

                return (text[next..], string.IsNullOrWhiteSpace(tenant) ? UnknownTenant : tenant, string.IsNullOrWhiteSpace(author) ? null : author);
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (string.Equals(key, TenantKey, StringComparison.OrdinalIgnoreCase))
                {
                    hasTenantKey = true;
                    tenant ??= value;
                }
                else if (string.Equals(key, AuthorKey, StringComparison.OrdinalIgnoreCase))
                {
                    author ??= value;
                }
            }

            pos = next;
        }

        // Unterminated block: keep the content as is, but never treat a document that names a tenant as official.
        return (fileContent, hasTenantKey ? UnknownTenant : null, null);
    }

    /// <summary>
    /// A source path that is safe to return to clients: relative, with forward slashes. Rooted paths and paths that climb
    /// out of their base ("..") are reduced to the file name, so no absolute server path is ever disclosed.
    /// </summary>
    public static string ToSafeSourcePath(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            return string.Empty;
        }

        var normalized = sourceFile.Trim().Replace('\\', '/');
        var isRooted = Path.IsPathRooted(sourceFile) ||
                       normalized.StartsWith('/') ||
                       (normalized.Length >= 2 && normalized[1] == ':');
        if (isRooted || normalized.Contains("..", StringComparison.Ordinal))
        {
            var lastSlash = normalized.LastIndexOf('/');
            var name = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
            var driveColon = name.LastIndexOf(':');
            return driveColon >= 0 ? name[(driveColon + 1)..] : name;
        }

        return normalized;
    }

    private static string SanitizeValue(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
}
