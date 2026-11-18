namespace Autheris.Application.Docs.Services;

using System;
using System.IO;

/// <summary>Source path helpers for documents returned to clients.</summary>
public static class DocSourcePath
{
    /// <summary>
    /// A source path that is safe to return to clients: relative, with forward slashes. Rooted paths and paths that climb
    /// out of their base ("..") are reduced to the file name, so no absolute server path is ever disclosed.
    /// </summary>
    public static string ToSafe(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            return string.Empty;
        }

        var normalized = sourceFile.Trim().Replace('\\', '/');
        var isRooted = Path.IsPathRooted(sourceFile) ||
                       normalized.StartsWith('/', StringComparison.Ordinal) ||
                       (normalized.Length >= 2 && normalized[1] == ':');
        if (isRooted || normalized.Contains("..", StringComparison.Ordinal))
        {
            var lastSlash = normalized.LastIndexOf('/', StringComparison.Ordinal);
            var name = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
            var driveColon = name.LastIndexOf(':', StringComparison.Ordinal);
            return driveColon >= 0 ? name[(driveColon + 1)..] : name;
        }

        return normalized;
    }
}
