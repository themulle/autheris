namespace Autheris.Tests.Unit;

using System;
using System.IO;
using Autheris.Application.SqlEndpoints.Services;
using Shouldly;
using Xunit;

public sealed class SrM22SqlEndpointLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "srm22_" + Guid.NewGuid().ToString("N"));

    public SrM22SqlEndpointLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void SR_M_22_UnloadFile_UnregistersByPathWhenNameHeaderDiffersFromFileName()
    {
        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);
        string file = Path.Combine(_dir, "file_name.sql");
        File.WriteAllText(file, "-- @name: header_name\nSELECT 1");

        loader.LoadFile(file);
        registry.TryGet("header_name", out _).ShouldBeTrue();

        File.Delete(file);
        loader.UnloadFile(file);

        registry.TryGet("header_name", out _).ShouldBeFalse();
    }

    [Fact]
    public void SR_M_22_LoadFile_NameHeaderChange_DropsStaleRegistration()
    {
        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);
        string file = Path.Combine(_dir, "ep.sql");
        File.WriteAllText(file, "-- @name: first\nSELECT 1");
        loader.LoadFile(file);

        File.WriteAllText(file, "-- @name: second\nSELECT 1");
        loader.LoadFile(file);

        registry.TryGet("first", out _).ShouldBeFalse();
        registry.TryGet("second", out _).ShouldBeTrue();
    }

    [Fact]
    public void SR_M_22_LoadFromDirectory_DoesNotFollowDirectorySymlinks()
    {
        string outside = Path.Combine(Path.GetTempPath(), "srm22_out_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "leak.sql"), "-- @name: leaked\nSELECT 1");
            File.WriteAllText(Path.Combine(_dir, "ok.sql"), "-- @name: ok\nSELECT 1");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(_dir, "link"), outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return; // symlink creation not permitted on this host
            }

            var registry = new InMemorySqlEndpointRegistry();
            using var loader = new SqlEndpointLoader(registry);

            loader.LoadFromDirectory(_dir, enableHotReload: false).ShouldBe(1);
            registry.TryGet("leaked", out _).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
