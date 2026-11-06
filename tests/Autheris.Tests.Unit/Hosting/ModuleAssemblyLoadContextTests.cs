namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Autheris.Api.Hosting;
using Autheris.Infrastructure.Modules.Trust;
using Parquet;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ModuleAssemblyLoadContextTests : IDisposable
{
    private readonly string _tempRoot;

    public ModuleAssemblyLoadContextTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "autheris_alc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        ModulePackageBuilder.RestoreWritablePermissionsRecursively(_tempRoot);
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static byte[] CreatePatchedPrivateDependencyDllBytes()
    {
        var path = typeof(ModulePackageBuilder).Assembly.Location;
        var bytes = File.ReadAllBytes(path);

        // "Autheris.Tests.Unit\0" (20 bytes) -> "PrivateDep.TestLib1\0" (20 bytes)
        ReplaceBytesInPlace(
            bytes,
            Encoding.UTF8.GetBytes("Autheris.Tests.Unit\0"),
            Encoding.UTF8.GetBytes("PrivateDep.TestLib1\0"));

        ReplaceBytesInPlace(
            bytes,
            Encoding.UTF8.GetBytes("Autheris.Tests.Unit.dll\0"),
            Encoding.UTF8.GetBytes("PrivateDep.TestLib1.dll\0"));

        return bytes;
    }

    private static void ReplaceBytesInPlace(byte[] buffer, ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> replacement)
    {
        for (int i = 0; i <= buffer.Length - pattern.Length; i++)
        {
            if (buffer.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                replacement.CopyTo(buffer.AsSpan(i, replacement.Length));
            }
        }
    }

    private static byte[] GetSampleNativeSharedLibraryBytes()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var candidates = new[]
        {
            "libSystem.Globalization.Native.so",
            "libSystem.Native.so",
            "System.Globalization.Native.dll",
            "libSystem.Globalization.Native.dylib",
            "libSystem.Native.dylib"
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.Combine(runtimeDir, candidate);
            if (File.Exists(fullPath))
            {
                return File.ReadAllBytes(fullPath);
            }
        }

        var firstNative = Directory.EnumerateFiles(runtimeDir, "*Native*.*")
            .FirstOrDefault(f => f.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        if (firstNative is not null)
        {
            return File.ReadAllBytes(firstNative);
        }

        throw new InvalidOperationException($"Could not locate a sample native library in '{runtimeDir}'.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Private_managed_dependency_resolves_from_module_folder()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = Path.Combine(_tempRoot, "sample");
        Directory.CreateDirectory(moduleDir);

        var entryBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        var privateDepBytes = CreatePatchedPrivateDependencyDllBytes();

        File.WriteAllBytes(Path.Combine(moduleDir, "Sample.TestModule01.dll"), entryBytes);
        File.WriteAllBytes(Path.Combine(moduleDir, "PrivateDep.TestLib1.dll"), privateDepBytes);

        var manifest = ModulePackageBuilder.CreateSignedManifest(
            key,
            moduleId: "sample",
            entryAssembly: "Sample.TestModule01.dll",
            files:
            [
                new ModuleManifestFile("PrivateDep.TestLib1.dll", IntegrityHashHelper.ComputeSha256HexLower(privateDepBytes)),
                new ModuleManifestFile("Sample.TestModule01.dll", IntegrityHashHelper.ComputeSha256HexLower(entryBytes))
            ]);
        File.WriteAllText(Path.Combine(moduleDir, "module.json"), ModulePackageBuilder.SerializeManifestJson(manifest));

        var image = FileHashVerifier.VerifyAndLoad(moduleDir, manifest);
        var alc = new ModuleAssemblyLoadContext(image);
        alc.EnsureNoHostAssemblyCollisions();

        var entryAssembly = alc.LoadEntryAssembly();
        var privateDepAssembly = alc.LoadFromAssemblyName(new AssemblyName("PrivateDep.TestLib1"));

        entryAssembly.GetName().Name.ShouldBe("Sample.TestModule01");
        privateDepAssembly.GetName().Name.ShouldBe("PrivateDep.TestLib1");
        AssemblyLoadContext.GetLoadContext(privateDepAssembly).ShouldBe(alc);
        AssemblyLoadContext.GetLoadContext(privateDepAssembly).ShouldNotBe(AssemblyLoadContext.Default);
        AssemblyLoadContext.Default.Assemblies.Any(a => string.Equals(a.GetName().Name, "PrivateDep.TestLib1", StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Private_native_library_resolves_from_runtimes_folder()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = Path.Combine(_tempRoot, "native-mod");
        var rid = RuntimeInformation.RuntimeIdentifier;
        var nativeFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "autheris_native_stub.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "autheris_native_stub.dylib"
                : "libautheris_native_stub.so";
        var nativeRelativePath = $"runtimes/{rid}/native/{nativeFileName}";
        var nativeFullPath = Path.Combine(moduleDir, nativeRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(nativeFullPath)!);

        var entryBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        var nativeBytes = GetSampleNativeSharedLibraryBytes();

        File.WriteAllBytes(Path.Combine(moduleDir, "Sample.TestModule01.dll"), entryBytes);
        File.WriteAllBytes(nativeFullPath, nativeBytes);

        var manifest = ModulePackageBuilder.CreateSignedManifest(
            key,
            moduleId: "native-mod",
            entryAssembly: "Sample.TestModule01.dll",
            files:
            [
                new ModuleManifestFile("Sample.TestModule01.dll", IntegrityHashHelper.ComputeSha256HexLower(entryBytes)),
                new ModuleManifestFile(nativeRelativePath, IntegrityHashHelper.ComputeSha256HexLower(nativeBytes))
            ]);
        File.WriteAllText(Path.Combine(moduleDir, "module.json"), ModulePackageBuilder.SerializeManifestJson(manifest));

        var image = FileHashVerifier.VerifyAndLoad(moduleDir, manifest);
        var alc = new ModuleAssemblyLoadContext(image);
        alc.EnsureNoHostAssemblyCollisions();

        var entryAssembly = alc.LoadEntryAssembly();
        var handle = NativeLibrary.Load("autheris_native_stub", entryAssembly, null);
        handle.ShouldNotBe(IntPtr.Zero);
        NativeLibrary.Free(handle);

        // Tampering with the native library on disk after startup verification must fail closed
        var tamperedBytes = (byte[])nativeBytes.Clone();
        tamperedBytes[^1] ^= 0xFF;
        File.WriteAllBytes(nativeFullPath, tamperedBytes);

        var loadUnmanagedMethod = typeof(ModuleAssemblyLoadContext)
            .GetMethod("LoadUnmanagedDll", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var ex = Should.Throw<TargetInvocationException>(() =>
            loadUnmanagedMethod.Invoke(alc, ["autheris_native_stub"]));
        ex.InnerException.ShouldBeOfType<ModuleLoadException>();
        ((ModuleLoadException)ex.InnerException!).Reason.ShouldContain("integrity check");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Shared_host_assembly_Parquet_resolves_from_default_context()
    {
        var hostParquetAssembly = typeof(ParquetReader).Assembly;
        AssemblyLoadContext.GetLoadContext(hostParquetAssembly).ShouldBe(AssemblyLoadContext.Default);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var moduleDir = Path.Combine(_tempRoot, "lakehouse");
        Directory.CreateDirectory(moduleDir);

        var entryBytes = ModulePackageBuilder.GetValidModuleDllBytes();
        File.WriteAllBytes(Path.Combine(moduleDir, "Sample.TestModule01.dll"), entryBytes);

        var manifest = ModulePackageBuilder.CreateSignedManifest(
            key,
            moduleId: "lakehouse",
            entryAssembly: "Sample.TestModule01.dll",
            files:
            [
                new ModuleManifestFile("Sample.TestModule01.dll", IntegrityHashHelper.ComputeSha256HexLower(entryBytes))
            ]);
        File.WriteAllText(Path.Combine(moduleDir, "module.json"), ModulePackageBuilder.SerializeManifestJson(manifest));

        var image = FileHashVerifier.VerifyAndLoad(moduleDir, manifest);
        var alc = new ModuleAssemblyLoadContext(image);
        alc.EnsureNoHostAssemblyCollisions();

        var resolvedParquet = alc.LoadFromAssemblyName(hostParquetAssembly.GetName());
        resolvedParquet.ShouldBeSameAs(hostParquetAssembly);
        AssemblyLoadContext.GetLoadContext(resolvedParquet).ShouldBe(AssemblyLoadContext.Default);

        // Shipping a duplicate Parquet.dll inside a module image must be rejected by collision enforcement
        var collidingImage = new VerifiedModuleImage(
            "lakehouse",
            moduleDir,
            manifest,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["Sample.TestModule01.dll"] = entryBytes,
                [hostParquetAssembly.GetName().Name + ".dll"] = File.ReadAllBytes(hostParquetAssembly.Location)
            });
        var collidingAlc = new ModuleAssemblyLoadContext(collidingImage);
        Should.Throw<ModuleLoadException>(() => collidingAlc.EnsureNoHostAssemblyCollisions([hostParquetAssembly]));
    }
}
