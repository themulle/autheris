namespace Autheris.Tools.ModuleSigner;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Infrastructure.Modules.Trust;

public static class Program
{
    public static int Main(string[] args) =>
        ModuleSignerCli.Run(args, Console.In, Console.Out, Console.Error);
}

public sealed record ModuleSignResult(ModuleManifest Manifest, string ManifestPath);

public static class ModuleSignerCli
{
    public const string SigningKeyEnvVar = "AUTHERIS_MODULE_SIGNING_KEY_PEM";
    public const string DefaultHostVersion = "1.0.0";
    public const string DefaultAbstractionsVersion = "1.0.0";
    private const int ManifestSchemaVersion = 1;
    private const int MaxModuleIdLength = 64;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly string[] ForbiddenKeyFlags =
    [
        "-k",
        "--key",
        "--private-key",
        "--signing-key",
        "--pem",
    ];

    public static int Run(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (ContainsCliKeyArgument(args))
        {
            stderr.WriteLine(
                $"Error: Passing signing keys via command-line arguments is prohibited. Use {SigningKeyEnvVar} or --key-stdin.");
            return 2;
        }

        if (!TryParseArgs(args, out var parsed, out bool showHelp, out var parseError))
        {
            stderr.WriteLine($"Error: {parseError}");
            return 2;
        }

        if (showHelp)
        {
            WriteUsage(stdout);
            return 0;
        }

        string? pemText = ReadSigningKeyPem(parsed!.KeyFromStdin, stdin, getEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pemText))
        {
            stderr.WriteLine(parsed.KeyFromStdin
                ? "Error: No signing private key PEM received on stdin."
                : $"Error: Environment variable {SigningKeyEnvVar} is not set.");
            return 2;
        }

        using ECDsa signingKey = ECDsa.Create();
        try
        {
            signingKey.ImportFromPem(pemText.AsSpan().Trim());
            if (signingKey.KeySize != 256)
            {
                stderr.WriteLine("Error: Module signing key must be an ECDSA P-256 key.");
                return 2;
            }

            // Ensure private key parameters are actually present (ImportFromPem accepts public-only PEMs).
            _ = signingKey.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or PlatformNotSupportedException)
        {
            stderr.WriteLine("Error: Failed to import ECDSA P-256 private key PEM.");
            return 2;
        }

        try
        {
            var result = SignDirectory(
                parsed.ModuleDirectory,
                parsed.Id,
                parsed.Version,
                parsed.HostVersion,
                parsed.AbstractionsVersion,
                parsed.EntryAssembly,
                parsed.EntryType,
                signingKey);

            stdout.WriteLine(
                $"Signed module '{result.Manifest.Id}' v{result.Manifest.Version} ({result.Manifest.Files.Count} files, keyId={result.Manifest.KeyId}) -> {result.ManifestPath}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static ModuleSignResult SignDirectory(
        string moduleDirectory,
        string id,
        string version,
        string hostVersion,
        string abstractionsVersion,
        string? entryAssembly,
        string? entryType,
        ECDsa signingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        ValidateModuleId(id);
        ValidateSemVerInput(version, nameof(version));
        ValidateSemVerInput(hostVersion, nameof(hostVersion));
        ValidateSemVerInput(abstractionsVersion, nameof(abstractionsVersion));
        ArgumentNullException.ThrowIfNull(signingKey);

        string fullDir = Path.GetFullPath(moduleDirectory);
        if (!Directory.Exists(fullDir))
        {
            throw new DirectoryNotFoundException($"Module directory '{fullDir}' does not exist.");
        }

        var dirInfo = new DirectoryInfo(fullDir);
        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || dirInfo.LinkTarget is not null)
        {
            throw new InvalidOperationException("Module directory must not be a symbolic link.");
        }

        string pascalId = ToPascalModuleName(id);
        string resolvedEntryAssembly = string.IsNullOrWhiteSpace(entryAssembly)
            ? $"Autheris.Modules.{pascalId}.dll"
            : entryAssembly.Trim();
        string resolvedEntryType = string.IsNullOrWhiteSpace(entryType)
            ? $"Autheris.Modules.{pascalId}.{pascalId}Module"
            : entryType.Trim();

        if (!ModuleManifest.IsValidRelativeManifestPath(resolvedEntryAssembly))
        {
            throw new ArgumentException("Invalid entry assembly relative path.", nameof(entryAssembly));
        }

        var manifestFiles = CollectManifestFiles(fullDir);
        if (manifestFiles.Count == 0)
        {
            throw new InvalidOperationException("Module directory contains no files to sign.");
        }

        if (!manifestFiles.Any(f => string.Equals(f.Path, resolvedEntryAssembly, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Entry assembly '{resolvedEntryAssembly}' was not found in module directory.");
        }

        string keyId = ManifestSignatureVerifier.ComputeKeyId(signingKey);
        var unsigned = new ModuleManifest(
            ManifestSchemaVersion,
            id,
            version.Trim(),
            hostVersion.Trim(),
            abstractionsVersion.Trim(),
            resolvedEntryAssembly,
            resolvedEntryType,
            manifestFiles,
            keyId,
            string.Empty);

        byte[] canonicalBytes = ManifestCanonicalizer.ToCanonicalBytes(unsigned);
        string signature = ManifestSignatureVerifier.Sign(canonicalBytes, signingKey);
        var signed = unsigned with { Signature = signature };

        if (!ManifestSignatureVerifier.VerifyManifest(signed, signingKey, out var failureReason))
        {
            throw new InvalidOperationException($"Self-verification failed: {failureReason}");
        }

        string json = JsonSerializer.Serialize(signed, ManifestJsonOptions);
        _ = ModuleManifest.Parse(json);

        string manifestPath = Path.Combine(fullDir, "module.json");
        File.WriteAllText(manifestPath, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new ModuleSignResult(signed, manifestPath);
    }

    private static List<ModuleManifestFile> CollectManifestFiles(string fullDir)
    {
        EnsureNoReparsePointsInTree(fullDir);

        var files = new List<ModuleManifestFile>();
        foreach (string filePath in Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(fullDir, filePath)
                .Replace('\\', '/');

            if (string.Equals(relativePath, "module.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ModuleManifest.IsValidRelativeManifestPath(relativePath))
            {
                throw new InvalidOperationException($"Invalid module file relative path '{relativePath}'.");
            }

            string sha256 = IntegrityHashHelper.ComputeFileSha256HexLower(filePath);
            files.Add(new ModuleManifestFile(relativePath, sha256));
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return files;
    }

    private static void EnsureNoReparsePointsInTree(string rootDir)
    {
        foreach (string dir in Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories))
        {
            var info = new DirectoryInfo(dir);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            {
                throw new InvalidOperationException("Module directory must not contain symbolic links.");
            }
        }

        foreach (string file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            {
                throw new InvalidOperationException("Module directory must not contain symbolic links.");
            }
        }
    }

    private static bool ContainsCliKeyArgument(string[] args)
    {
        foreach (string rawArg in args)
        {
            if (rawArg is null)
            {
                continue;
            }

            string arg = rawArg.Trim();
            foreach (string flag in ForbiddenKeyFlags)
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (arg.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase) ||
                arg.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseArgs(
        string[] args,
        out ParsedArgs? parsed,
        out bool showHelp,
        out string? error)
    {
        parsed = null;
        showHelp = false;
        error = null;

        string? dir = null;
        string? id = null;
        string? version = null;
        string hostVersion = DefaultHostVersion;
        string abstractionsVersion = DefaultAbstractionsVersion;
        string? entryAssembly = null;
        string? entryType = null;
        bool keyStdin = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    return true;
                case "--key-stdin":
                    keyStdin = true;
                    break;
                case "--dir":
                case "--module-dir":
                    if (!TryConsumeValue(args, ref i, out dir))
                    {
                        error = "Missing value for --dir.";
                        return false;
                    }
                    break;
                case "--id":
                    if (!TryConsumeValue(args, ref i, out id))
                    {
                        error = "Missing value for --id.";
                        return false;
                    }
                    break;
                case "--version":
                    if (!TryConsumeValue(args, ref i, out version))
                    {
                        error = "Missing value for --version.";
                        return false;
                    }
                    break;
                case "--host-version":
                    if (!TryConsumeValue(args, ref i, out string? parsedHost))
                    {
                        error = "Missing value for --host-version.";
                        return false;
                    }
                    hostVersion = parsedHost!;
                    break;
                case "--abstractions-version":
                    if (!TryConsumeValue(args, ref i, out string? parsedAbs))
                    {
                        error = "Missing value for --abstractions-version.";
                        return false;
                    }
                    abstractionsVersion = parsedAbs!;
                    break;
                case "--entry-assembly":
                    if (!TryConsumeValue(args, ref i, out entryAssembly))
                    {
                        error = "Missing value for --entry-assembly.";
                        return false;
                    }
                    break;
                case "--entry-type":
                    if (!TryConsumeValue(args, ref i, out entryType))
                    {
                        error = "Missing value for --entry-type.";
                        return false;
                    }
                    break;
                default:
                    // Do not echo unrecognized argument text in case sensitive data was passed on CLI.
                    error = "Unknown or invalid command-line argument.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            error = "Missing required argument --dir.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Missing required argument --id.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            error = "Missing required argument --version.";
            return false;
        }

        parsed = new ParsedArgs(
            dir,
            id,
            version,
            hostVersion,
            abstractionsVersion,
            entryAssembly,
            entryType,
            keyStdin);
        return true;
    }

    private static bool TryConsumeValue(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            return false;
        }

        value = args[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? ReadSigningKeyPem(
        bool fromStdin,
        TextReader stdin,
        Func<string, string?>? getEnvironmentVariable)
    {
        if (fromStdin)
        {
            return stdin.ReadToEnd();
        }

        return (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(SigningKeyEnvVar);
    }

    private static void ValidateModuleId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxModuleIdLength)
        {
            throw new ArgumentException("Module id must be 1-64 characters.", nameof(id));
        }

        foreach (char c in id)
        {
            bool valid = (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c is '-' or '_';
            if (!valid)
            {
                throw new ArgumentException($"Module id '{id}' contains invalid character '{c}'.", nameof(id));
            }
        }
    }

    private static void ValidateSemVerInput(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Version cannot be empty.", paramName);
        }

        string trimmed = value.Trim();
        int sepIdx = trimmed.IndexOfAny(['-', '+']);
        string core = sepIdx >= 0 ? trimmed[..sepIdx] : trimmed;
        string[] parts = core.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) || major < 0 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) || minor < 0 ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int patch) || patch < 0)
        {
            throw new ArgumentException($"Invalid semantic version '{value}'.", paramName);
        }
    }

    private static string ToPascalModuleName(string moduleId) =>
        string.Concat(moduleId.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static void WriteUsage(TextWriter stdout)
    {
        stdout.WriteLine(
            $"Usage: Autheris.ModuleSigner --dir <moduleDir> --id <moduleId> --version <semver> [--host-version <semver>] [--abstractions-version <semver>] [--entry-assembly <dll>] [--entry-type <type>] [--key-stdin]\n" +
            $"Private key PEM is read strictly from {SigningKeyEnvVar} or stdin (--key-stdin).");
    }

    private sealed record ParsedArgs(
        string ModuleDirectory,
        string Id,
        string Version,
        string HostVersion,
        string AbstractionsVersion,
        string? EntryAssembly,
        string? EntryType,
        bool KeyFromStdin);
}
