namespace Autheris.Tests.Unit.Docs;

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Docs.Services;
using Autheris.Infrastructure.Docs;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// .NET 11 validates the CRC32 of a ZIP entry while it is read. A bundle whose entry data does not match its recorded checksum
/// must never be unpacked as a success and never be indexed (fail-closed): corrupt or tampered documentation is rejected.
/// </summary>
[Trait("Category", "Security")]
public sealed class FileSystemUniversalDocStoreZipTests : IDisposable
{
    private const string Marker = "CORRUPTME-0123456789-CORRUPTME";

    private readonly string _root;
    private readonly InMemoryDocSearchService _search = new();
    private readonly FileSystemUniversalDocStore _store;

    public FileSystemUniversalDocStoreZipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "autheris_docstore_zip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new FileSystemUniversalDocStore(_root, _search, new MarkdownDocIngestor(), NullLogger<FileSystemUniversalDocStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup of the temp fixture
        }
    }

    private static byte[] StoredArchive(bool corrupt)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("runbooks/restore.md", CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("# Restore\n\n" + Marker + "\n");
        }

        var bytes = stream.ToArray();
        if (corrupt)
        {
            var needle = Encoding.UTF8.GetBytes(Marker);
            var at = bytes.AsSpan().IndexOf(needle);
            at.ShouldBeGreaterThan(0, "the stored entry data must be visible in the archive bytes");
            bytes[at + 3] ^= 0x20;
        }

        return bytes;
    }

    [Fact]
    public async Task ExtractBundleAsync_ValidStoredArchive_Succeeds()
    {
        using var stream = new MemoryStream(StoredArchive(corrupt: false));

        var result = await _store.ExtractBundleAsync("restore-app", stream);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public async Task ExtractBundleAsync_EntryWithCrcMismatch_IsNeverReportedAsSuccess()
    {
        using var stream = new MemoryStream(StoredArchive(corrupt: true));

        bool succeeded;
        try
        {
            var result = await _store.ExtractBundleAsync("restore-app", stream);
            succeeded = result.Success;
        }
        catch (InvalidDataException)
        {
            succeeded = false;
        }

        succeeded.ShouldBeFalse("a ZIP entry whose data does not match its CRC32 must be rejected");
    }
}
