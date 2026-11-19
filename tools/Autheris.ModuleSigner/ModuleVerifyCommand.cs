namespace Autheris.Tools.ModuleSigner;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autheris.Infrastructure.Modules.Trust;

/// <summary>
/// <c>verify --pub &lt;public-key.pem&gt; --dir &lt;moduleDir&gt;</c>: offline check used by the image build. The public key is read from
/// a file only (never from an environment variable or the command line text); the module must carry a manifest signed by exactly
/// that key, every listed file must match its hash, and no unlisted file may exist.
/// </summary>
public static class ModuleVerifyCommand
{
    public const string ManifestFileName = "module.json";

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? pubPath = null;
        string? dir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pub" when i + 1 < args.Length:
                    pubPath = args[++i];
                    break;
                case "--dir" when i + 1 < args.Length:
                    dir = args[++i];
                    break;
                default:
                    stderr.WriteLine($"Error: Unknown or incomplete argument '{args[i]}'. Usage: verify --pub <public-key.pem> --dir <moduleDir>");
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(pubPath) || string.IsNullOrWhiteSpace(dir))
        {
            stderr.WriteLine("Error: verify requires --pub <public-key.pem> and --dir <moduleDir>.");
            return 2;
        }

        try
        {
            using ECDsa publicKey = ECDsa.Create();
            publicKey.ImportFromPem(File.ReadAllText(pubPath).AsSpan().Trim());
            if (publicKey.KeySize != 256)
            {
                stderr.WriteLine("Error: Public key must be an ECDSA P-256 key.");
                return 2;
            }

            string fullDir = Path.GetFullPath(dir);
            string manifestPath = Path.Combine(fullDir, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                stderr.WriteLine($"Error: Module '{fullDir}' is unsigned (no {ManifestFileName}).");
                return 1;
            }

            var manifest = ModuleManifest.Parse(File.ReadAllText(manifestPath));
            if (!ManifestSignatureVerifier.VerifyManifest(manifest, publicKey, out string? failure))
            {
                stderr.WriteLine($"Error: Module '{manifest.Id}' signature rejected: {failure}");
                return 1;
            }

            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                listed.Add(file.Path);
                string path = Path.Combine(fullDir, file.Path);
                if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null)
                {
                    stderr.WriteLine($"Error: Module '{manifest.Id}' file '{file.Path}' is missing or a symlink.");
                    return 1;
                }

                if (!string.Equals(IntegrityHashHelper.ComputeFileSha256HexLower(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    stderr.WriteLine($"Error: Module '{manifest.Id}' file '{file.Path}' does not match its signed hash.");
                    return 1;
                }
            }

            foreach (string path in Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(fullDir, path).Replace('\\', '/');
                if (!string.Equals(relative, ManifestFileName, StringComparison.Ordinal) && !listed.Contains(relative))
                {
                    stderr.WriteLine($"Error: Module '{manifest.Id}' contains unlisted file '{relative}'.");
                    return 1;
                }
            }

            stdout.WriteLine($"Verified module '{manifest.Id}' v{manifest.Version} ({manifest.Files.Count} files, keyId={manifest.KeyId}).");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or IOException or UnauthorizedAccessException
                                       or FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
