using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// SEC H-17: Default anchor store. Keeps the signed audit chain end anchor in a JSON file outside of the
/// governance database. For real tamper resistance the file should live on a different volume / identity
/// than the database (or be mirrored to WORM storage, see AuditWormExportService manifest).
/// </summary>
public sealed class FileAuditChainAnchorStore : IAuditChainAnchorStore
{
    private readonly string _path;
    private readonly object _ioLock = new();

    public FileAuditChainAnchorStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public string FilePath => _path;

    public AuditChainAnchor? Load()
    {
        lock (_ioLock)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                // An empty anchor file is never a valid state; surface it as an invalid anchor.
                return new AuditChainAnchor(-1, string.Empty, DateTimeOffset.MinValue, string.Empty);
            }

            try
            {
                return JsonSerializer.Deserialize<AuditChainAnchor>(json)
                       ?? new AuditChainAnchor(-1, string.Empty, DateTimeOffset.MinValue, string.Empty);
            }
            catch (JsonException)
            {
                return new AuditChainAnchor(-1, string.Empty, DateTimeOffset.MinValue, string.Empty);
            }
        }
    }

    /// <summary>
    /// SR-L-07: the anchor never moves backwards. A lower sequence than the one already on disk is ignored. Read-compare-write
    /// is serialised in-process by <c>_ioLock</c> and across replicas sharing the path by an exclusive sibling <c>.lock</c> file.
    /// </summary>
    public void Save(AuditChainAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        lock (_ioLock)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var processLock = AcquireProcessLock();

            var current = ReadCurrentSequence();
            if (current.HasValue && current.Value > anchor.Sequence)
            {
                return;
            }

            // Unique temp file: replicas sharing one anchor path must not write the same temp file.
            var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(anchor));
                File.Move(tempPath, _path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch (IOException) { }
                throw;
            }
        }
    }

    private long? ReadCurrentSequence()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            // An unparsable or invalid (negative sequence) file does not block a valid anchor from replacing it.
            var existing = JsonSerializer.Deserialize<AuditChainAnchor>(json);
            return existing is { Sequence: >= 0 } ? existing.Sequence : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private FileStream AcquireProcessLock()
    {
        var lockPath = _path + ".lock";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
        }
    }
}

/// <summary>
/// Anchor store for in-memory governance databases (tests / development only). The anchor lives as long as
/// the repository instance; it offers no protection across restarts and is never used for file databases.
/// </summary>
public sealed class InMemoryAuditChainAnchorStore : IAuditChainAnchorStore
{
    private AuditChainAnchor? _anchor;

    public AuditChainAnchor? Load() => Volatile.Read(ref _anchor);

    public void Save(AuditChainAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        // SR-L-07: monotonic, lock-free compare-and-swap; a lower sequence never replaces a higher one.
        while (true)
        {
            var current = Volatile.Read(ref _anchor);
            if (current != null && current.Sequence > anchor.Sequence)
            {
                return;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _anchor, anchor, current), current))
            {
                return;
            }
        }
    }
}

/// <summary>
/// SEC E-11: Append-only anchor store for separate / WORM storage. Every anchor becomes a new file that is created
/// exclusively (never overwritten) and marked read-only; <see cref="Load"/> returns the newest one. Point the
/// directory at a mount that enforces immutability (object lock, WORM NAS) to also stop an attacker with write
/// access to the host from deleting history.
/// </summary>
public sealed class WormDirectoryAuditChainAnchorStore : IAuditChainAnchorStore
{
    private static readonly AuditChainAnchor Invalid = new(-1, string.Empty, DateTimeOffset.MinValue, string.Empty);
    private readonly string _directory;
    private readonly Func<AuditChainAnchor, bool>? _isValidlySigned;
    private readonly ILogger? _logger;

    /// <param name="directory">Append-only anchor directory.</param>
    /// <param name="isValidlySigned">
    /// Optional signature check (e.g. the asymmetric KMS signature). Files that fail it are skipped like unparsable files,
    /// so a planted newest file cannot permanently disable anchor verification.
    /// </param>
    /// <param name="logger">Receives a critical alert for every skipped (poisoned) file.</param>
    public WormDirectoryAuditChainAnchorStore(string directory, Func<AuditChainAnchor, bool>? isValidlySigned = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _isValidlySigned = isValidlySigned;
        _logger = logger;
    }

    public string DirectoryPath => _directory;

    public AuditChainAnchor? Load()
    {
        if (!Directory.Exists(_directory))
        {
            return null;
        }

        // File names start with the zero padded sequence, so the ordinal order is the chain order.
        var files = Directory.GetFiles(_directory, "anchor-*.json");
        if (files.Length == 0)
        {
            return null;
        }

        Array.Sort(files, StringComparer.Ordinal);
        AuditChainAnchor? best = null;
        var skipped = 0;
        foreach (var file in files.Reverse().Take(8))
        {
            // Review G5: an invalid newest file (planted or corrupted) is skipped with a critical alert; the newest
            // valid anchor is used instead of failing permanently.
            AuditChainAnchor? anchor;
            try
            {
                anchor = JsonSerializer.Deserialize<AuditChainAnchor>(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                anchor = null;
            }

            if (anchor == null || anchor.Sequence < 0 || string.IsNullOrEmpty(anchor.EntryHash) ||
                (_isValidlySigned != null && !_isValidlySigned(anchor)))
            {
                skipped++;
                _logger?.LogCritical(
                    "CRITICAL: WORM audit anchor file '{File}' is invalid or not validly signed and was skipped; falling back to the newest valid anchor. Investigate possible tampering of the anchor directory.",
                    Path.GetFileName(file));
                continue;
            }

            if (best == null)
            {
                best = anchor;
            }
            else if (anchor.Sequence == best.Sequence && !string.Equals(anchor.EntryHash, best.EntryHash, StringComparison.Ordinal))
            {
                // Two different valid anchors for one sequence: history was rewritten or the chain forked.
                return Invalid;
            }
        }

        if (best == null && skipped > 0)
        {
            return Invalid;
        }

        return best;
    }

    public void Save(AuditChainAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        Directory.CreateDirectory(_directory);
        var name = string.Create(CultureInfo.InvariantCulture, $"anchor-{anchor.Sequence:D20}-{anchor.UpdatedAt.UtcTicks:D20}.json");
        var path = Path.Combine(_directory, name);
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(anchor));
                stream.Flush(true);
            }

            try { File.SetAttributes(path, FileAttributes.ReadOnly); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        }
        catch (IOException) when (File.Exists(path))
        {
            // Same sequence and timestamp already archived (replica race): immutable history is not touched.
        }
    }
}

/// <summary>
/// SEC E-11: Fans the anchor out to several stores (e.g. local file + WORM directory + mirror on another volume).
/// <see cref="Load"/> returns the newest anchor of all stores, so deleting or rolling back one copy is detected as
/// soon as another copy is ahead of the database; conflicting anchors for one sequence count as invalid.
/// </summary>
public sealed class CompositeAuditChainAnchorStore : IAuditChainAnchorStore
{
    private static readonly AuditChainAnchor Invalid = new(-1, string.Empty, DateTimeOffset.MinValue, string.Empty);
    private readonly IReadOnlyList<IAuditChainAnchorStore> _stores;

    public CompositeAuditChainAnchorStore(IEnumerable<IAuditChainAnchorStore> stores)
    {
        _stores = stores.ToArray();
        if (_stores.Count == 0)
        {
            throw new ArgumentException("At least one anchor store is required.", nameof(stores));
        }
    }

    public IReadOnlyList<IAuditChainAnchorStore> Stores => _stores;

    public AuditChainAnchor? Load()
    {
        AuditChainAnchor? best = null;
        foreach (var store in _stores)
        {
            var anchor = store.Load();
            if (anchor == null)
            {
                continue;
            }

            if (anchor.Sequence < 0)
            {
                return Invalid;
            }

            if (best == null || anchor.Sequence > best.Sequence)
            {
                best = anchor;
            }
            else if (anchor.Sequence == best.Sequence && !string.Equals(anchor.EntryHash, best.EntryHash, StringComparison.Ordinal))
            {
                return Invalid;
            }
        }

        return best;
    }

    public void Save(AuditChainAnchor anchor)
    {
        List<Exception>? errors = null;
        foreach (var store in _stores)
        {
            try
            {
                store.Save(anchor);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors != null)
        {
            throw new AggregateException("Failed to persist the audit chain anchor to at least one store.", errors);
        }
    }
}

/// <summary>
/// SEC E-11: Decorator that adds an asymmetric (KMS/HSM) signature to every saved anchor and requires a valid one on
/// load. A forged, unsigned or HMAC-only anchor is returned as invalid (fail-closed).
/// </summary>
public sealed class SigningAuditChainAnchorStore : IAuditChainAnchorStore
{
    private static readonly AuditChainAnchor Invalid = new(-1, string.Empty, DateTimeOffset.MinValue, string.Empty);
    private readonly IAuditChainAnchorStore _inner;
    private readonly IAuditAnchorSigner _signer;

    public SigningAuditChainAnchorStore(IAuditChainAnchorStore inner, IAuditAnchorSigner signer)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    }

    public IAuditChainAnchorStore Inner => _inner;

    internal static byte[] Payload(AuditChainAnchor a) =>
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"anchor-ext-v1|{a.Sequence}|{a.EntryHash}|{a.UpdatedAt.ToUniversalTime():O}|{a.Signature}"));

    public AuditChainAnchor? Load()
    {
        var anchor = _inner.Load();
        if (anchor == null || anchor.Sequence < 0)
        {
            return anchor;
        }

        if (string.IsNullOrEmpty(anchor.ExternalSignature) || !_signer.Verify(Payload(anchor), anchor.ExternalSignature))
        {
            return Invalid;
        }

        return anchor;
    }

    public void Save(AuditChainAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        _inner.Save(anchor with { ExternalSignature = _signer.Sign(Payload(anchor)) });
    }
}

/// <summary>
/// SEC E-11: Signer backed by an asymmetric key (ECDSA or RSA) whose PEM is fetched from the Key Vault / KMS
/// secret provider, never from the governance database. Replace with a remote-KMS implementation of
/// <see cref="IAuditAnchorSigner"/> when the private key must not leave the HSM.
/// </summary>
public sealed class AsymmetricAuditAnchorSigner : IAuditAnchorSigner
{
    private readonly string? _privatePem;
    private readonly string _publicPem;

    public AsymmetricAuditAnchorSigner(byte[]? privateKeyPem, byte[]? publicKeyPem = null)
    {
        if ((privateKeyPem == null || privateKeyPem.Length == 0) && (publicKeyPem == null || publicKeyPem.Length == 0))
        {
            throw new ArgumentException("A private or a public anchor key is required.");
        }

        _privatePem = privateKeyPem is { Length: > 0 } ? Encoding.UTF8.GetString(privateKeyPem) : null;
        _publicPem = publicKeyPem is { Length: > 0 } ? Encoding.UTF8.GetString(publicKeyPem) : DerivePublic(_privatePem!);
    }

    private static string DerivePublic(string privatePem)
    {
        using var key = FromPem(privatePem);
        return key switch
        {
            ECDsa e => e.ExportSubjectPublicKeyInfoPem(),
            RSA r => r.ExportSubjectPublicKeyInfoPem(),
            _ => throw new CryptographicException("Unsupported anchor key type.")
        };
    }

    private static AsymmetricAlgorithm FromPem(string pem)
    {
        var ec = ECDsa.Create();
        try
        {
            ec.ImportFromPem(pem);
            return ec;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            ec.Dispose();
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return rsa;
        }
    }

    public string Sign(ReadOnlySpan<byte> data)
    {
        if (_privatePem == null)
        {
            throw new InvalidOperationException("This anchor signer has no private key (verify-only).");
        }

        using var key = FromPem(_privatePem);
        return key switch
        {
            ECDsa e => Convert.ToHexString(e.SignData(data, HashAlgorithmName.SHA256)),
            RSA r => Convert.ToHexString(r.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)),
            _ => throw new CryptographicException("Unsupported anchor key type.")
        };
    }

    public bool Verify(ReadOnlySpan<byte> data, string signature)
    {
        try
        {
            var sig = Convert.FromHexString(signature);
            using var key = FromPem(_publicPem);
            return key switch
            {
                ECDsa e => e.VerifyData(data, sig, HashAlgorithmName.SHA256),
                RSA r => r.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
                _ => false
            };
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>SEC E-11: Builds the effective anchor store chain from <see cref="AuditOptions"/>.</summary>
public static class AuditChainAnchorStoreFactory
{
    /// <param name="primary">Local anchor store (file or in-memory) as before.</param>
    public static IAuditChainAnchorStore Create(
        AuditOptions? options,
        IAuditChainAnchorStore primary,
        IKeyVaultSecretProvider? secrets,
        IAuditAnchorSigner? signer = null,
        ILogger? logger = null)
    {
        signer ??= CreateSigner(options, secrets);
        Func<AuditChainAnchor, bool>? verify = signer == null
            ? null
            : a => !string.IsNullOrEmpty(a.ExternalSignature) && signer.Verify(SigningAuditChainAnchorStore.Payload(a), a.ExternalSignature);
        var stores = new List<IAuditChainAnchorStore> { primary };
        if (options != null)
        {
            foreach (var mirror in options.ChainAnchorMirrorPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                stores.Add(new FileAuditChainAnchorStore(mirror));
            }

            if (!string.IsNullOrWhiteSpace(options.ChainAnchorWormDirectory))
            {
                stores.Add(new WormDirectoryAuditChainAnchorStore(options.ChainAnchorWormDirectory, verify, logger));
            }
        }

        IAuditChainAnchorStore result = stores.Count == 1 ? primary : new CompositeAuditChainAnchorStore(stores);

        return signer == null ? result : new SigningAuditChainAnchorStore(result, signer);
    }

    /// <summary>
    /// Builds the anchor store of the governance repository for the configured provider (AU-01/AU-03, ARC-I-13). Outside
    /// Development/Test a persistent anchor (file, WORM directory or KMS signer) is mandatory; the provider defaults are the SQLite
    /// <c>&lt;db&gt;.audit-anchor.json</c> file or, for PostgreSQL and SQL Server, <c>Audit:ChainAnchorPath</c>.
    /// </summary>
    public static IAuditChainAnchorStore CreateForGovernance(
        GatewayOptions options,
        Microsoft.Extensions.Hosting.IHostEnvironment environment,
        IKeyVaultSecretProvider secrets,
        IAuditAnchorSigner? signer,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(secrets);

        var audit = options.Audit;
        var isDevOrTest = string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(environment.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase);

        // AU-01 & AU-03: fail closed outside Dev/Test without a persistent anchor path or KMS signer.
        if (!isDevOrTest &&
            string.IsNullOrWhiteSpace(audit?.ChainAnchorPath) &&
            string.IsNullOrWhiteSpace(audit?.ChainAnchorWormDirectory) &&
            string.IsNullOrWhiteSpace(audit?.ChainAnchorSignerKeyVaultRef) &&
            signer == null)
        {
            throw new InvalidOperationException(
                "CRITICAL AUDIT MISCONFIGURATION (AU-01/AU-03): In production environments, " +
                "a persistent audit anchor store (ChainAnchorPath, ChainAnchorWormDirectory, or KMS Signer) " +
                "is mandatory. In-memory anchor stores are strictly prohibited outside Development/Test.");
        }

        var provider = options.GovernanceDb?.Provider;
        var primary = DataSourceProvider.Is(provider, DatabaseDialect.PostgreSql) || DataSourceProvider.Is(provider, DatabaseDialect.SqlServer)
            ? (!string.IsNullOrWhiteSpace(audit?.ChainAnchorPath)
                ? new FileAuditChainAnchorStore(audit.ChainAnchorPath)
                : (IAuditChainAnchorStore)new InMemoryAuditChainAnchorStore())
            : CreateSqliteDefaultStore(options.GovernanceDb?.ConnectionString, audit?.ChainAnchorPath);

        return Create(audit, primary, secrets, signer, logger);
    }

    private static IAuditChainAnchorStore CreateSqliteDefaultStore(string? connectionString, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return new FileAuditChainAnchorStore(configuredPath);
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new InMemoryAuditChainAnchorStore();
        }

        string dataSource;
        bool isMemory;
        try
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
            dataSource = builder.DataSource;
            isMemory = builder.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory;
        }
        catch
        {
            dataSource = connectionString;
            isMemory = false;
        }

        if (isMemory || string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
        {
            return new InMemoryAuditChainAnchorStore();
        }

        return new FileAuditChainAnchorStore(dataSource + ".audit-anchor.json");
    }

    private static IAuditAnchorSigner? CreateSigner(AuditOptions? options, IKeyVaultSecretProvider? secrets)
    {
        if (options == null || string.IsNullOrWhiteSpace(options.ChainAnchorSignerKeyVaultRef))
        {
            return null;
        }

        if (secrets == null)
        {
            throw new InvalidOperationException("Audit:ChainAnchorSignerKeyVaultRef is configured but no IKeyVaultSecretProvider is available.");
        }

        var priv = secrets.GetSecretBytes(options.ChainAnchorSignerKeyVaultRef);
        var pub = string.IsNullOrWhiteSpace(options.ChainAnchorVerifyKeyVaultRef)
            ? null
            : secrets.GetSecretBytes(options.ChainAnchorVerifyKeyVaultRef);
        return new AsymmetricAuditAnchorSigner(priv, pub);
    }
}
