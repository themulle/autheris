namespace Autheris.Tests.Unit.Docs;

using System.IO;
using System.IO.Compression;
using System.Text;
using Autheris.Application.Docs.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// .NET 11 validates the CRC32 of a ZIP entry while it is read. A bundle whose entry data does not match its recorded checksum
/// must never be unpacked as a success and never be indexed (fail-closed): corrupt or tampered documentation is rejected.
/// </summary>
[Trait("Category", "Security")]
public sealed class DocBundleReaderZipTests
{
    private const string Marker = "CORRUPTME-0123456789-CORRUPTME";

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

    private static ParsedDocBundle Read(byte[] zip)
    {
        using var stream = new MemoryStream(zip);
        return DocBundleReader.Read(new DocWriter("tenant-a", "S-1"), "restore-app", stream, new DocsOptions(), NullLogger.Instance);
    }

    [Fact]
    public void Read_ValidStoredArchive_Succeeds()
    {
        var parsed = Read(StoredArchive(corrupt: false));

        parsed.IsFailure.ShouldBeFalse(parsed.Failure?.Message);
        parsed.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public void Read_EntryWithCrcMismatch_IsNeverReportedAsSuccess()
    {
        var parsed = Read(StoredArchive(corrupt: true));

        parsed.IsFailure.ShouldBeTrue("a ZIP entry whose data does not match its CRC32 must be rejected");
        parsed.Entries.ShouldBeEmpty();
    }
}
