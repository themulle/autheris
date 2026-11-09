namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Autheris.Infrastructure.Modules.Trust;
using Autheris.Tests.Unit.Support;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class FileHashVerifierTests : IDisposable
{
    private readonly string _tempRoot;

    public FileHashVerifierTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "autheris_fhv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
        }
    }

    private static void MakeFifo(string path)
    {
        using var process = System.Diagnostics.Process.Start("mkfifo", [path])!;
        process.WaitForExit();
        process.ExitCode.ShouldBe(0);
    }

    private ModuleManifest CreateManifest(params ModuleManifestFile[] files)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ModulePackageBuilder.CreateSignedManifest(key, files: files);
    }

    [RuntimeSkippableFact]
    [Trait("Category", "Security")]
    public async Task Fifo_in_module_root_is_rejected()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new RuntimeSkipException("FIFO handling is verified on Linux only.");
        }

        var dll = ModulePackageBuilder.GetValidModuleDllBytes();
        File.WriteAllBytes(Path.Combine(_tempRoot, "Sample.TestModule01.dll"), dll);
        MakeFifo(Path.Combine(_tempRoot, "pipe.bin"));
        var manifest = CreateManifest(
            new ModuleManifestFile("Sample.TestModule01.dll", IntegrityHashHelper.ComputeSha256HexLower(dll)));

        var attempt = Task.Run(() => Should.Throw<SecurityException>(
            () => FileHashVerifier.VerifyAndLoad(_tempRoot, manifest, verifyHashes: false)));

        // A timeout here means the FIFO was opened instead of being rejected.
        await attempt.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Oversized_file_is_rejected()
    {
        var dll = ModulePackageBuilder.GetValidModuleDllBytes();
        File.WriteAllBytes(Path.Combine(_tempRoot, "Sample.TestModule01.dll"), dll);
        using (var big = new FileStream(Path.Combine(_tempRoot, "big.bin"), FileMode.CreateNew))
        {
            big.SetLength(FileHashVerifier.MaxModuleFileBytes + 1);
        }

        var manifest = CreateManifest(
            new ModuleManifestFile("Sample.TestModule01.dll", IntegrityHashHelper.ComputeSha256HexLower(dll)));

        Should.Throw<SecurityException>(() => FileHashVerifier.VerifyAndLoad(_tempRoot, manifest, verifyHashes: false));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void File_growing_past_cap_during_read_is_rejected()
    {
        using var stream = new EndlessStream(reportedLength: 10);

        Should.Throw<SecurityException>(() => FileHashVerifier.ReadCapped(stream, "grow.bin", maxBytes: 4096));
        stream.TotalServed.ShouldBeLessThan(1024 * 1024);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Failed_or_untyped_stat_result_on_existing_path_is_unknown_kind()
    {
        UnixFileStat.ClassifyStatxResult(-1, 38, 0, 0).ShouldBe(UnixFileKind.Unknown);
        UnixFileStat.ClassifyStatxResult(-1, 1, 0, 0).ShouldBe(UnixFileKind.Unknown);
        UnixFileStat.ClassifyStatxResult(0, 0, 0x0, 0x8000).ShouldBe(UnixFileKind.Unknown);
        UnixFileStat.ClassifyStatxResult(-1, 2, 0, 0).ShouldBeNull();
        UnixFileStat.ClassifyStatxResult(0, 0, 0x1, 0x8000).ShouldBe(UnixFileKind.Regular);
        UnixFileStat.ClassifyStatxResult(0, 0, 0x1, 0x1000).ShouldBe(UnixFileKind.Fifo);
    }

    [Fact]
    public void Regular_file_within_cap_is_read_completely()
    {
        var content = RandomNumberGenerator.GetBytes(10_000);
        using var stream = new MemoryStream(content);

        FileHashVerifier.ReadCapped(stream, "ok.bin", maxBytes: 20_000).ShouldBe(content);
    }

    private sealed class EndlessStream(long reportedLength) : Stream
    {
        public long TotalServed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => reportedLength;

        public override long Position
        {
            get => TotalServed;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)1, offset, count);
            TotalServed += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
