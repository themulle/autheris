namespace Autheris.Tests.Unit.Hosting;

using System;
using System.IO;
using System.Security.Cryptography;
using Autheris.Tools.ModuleSigner;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ModuleSignerVerifyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"autheris-modverify-{Guid.NewGuid():N}");

    public ModuleSignerVerifyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Verify_accepts_module_signed_with_pinned_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string dir = SignedModule(key);

        Verify(dir, PublicPem(key, "pub.pem")).ShouldBe(0);
    }

    [Fact]
    public void Verify_rejects_unsigned_module()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string dir = Path.Combine(_root, "unsigned");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Autheris.Modules.Sample.dll"), [1, 2, 3]);

        Verify(dir, PublicPem(key, "pub.pem")).ShouldBe(1);
    }

    [Fact]
    public void Verify_rejects_module_signed_by_foreign_key()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var pinned = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string dir = SignedModule(signer);

        Verify(dir, PublicPem(pinned, "pinned.pem")).ShouldBe(1);
    }

    [Fact]
    public void Verify_rejects_tampered_and_unlisted_files()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pub = PublicPem(key, "pub.pem");
        string tampered = SignedModule(key, "tampered");
        File.WriteAllBytes(Path.Combine(tampered, "Autheris.Modules.Sample.dll"), [9, 9, 9]);
        string extra = SignedModule(key, "extra");
        File.WriteAllText(Path.Combine(extra, "extra.dll"), "x");

        Verify(tampered, pub).ShouldBe(1);
        Verify(extra, pub).ShouldBe(1);
    }

    private static int Verify(string dir, string pubPath) =>
        ModuleSignerCli.Run(["verify", "--pub", pubPath, "--dir", dir], TextReader.Null, TextWriter.Null, TextWriter.Null);

    private string PublicPem(ECDsa key, string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, key.ExportSubjectPublicKeyInfoPem());
        return path;
    }

    private string SignedModule(ECDsa key, string name = "sample")
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Autheris.Modules.Sample.dll"), [0x4D, 0x5A, 0x90, 0x00]);
        ModuleSignerCli.SignDirectory(dir, "sample", "1.0.0", "1.0.0", "1.0.0", null, null, key);
        return dir;
    }
}
