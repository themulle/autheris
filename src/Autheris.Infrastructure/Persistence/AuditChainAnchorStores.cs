using System.Text.Json;

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
        Volatile.Write(ref _anchor, anchor);
    }
}
