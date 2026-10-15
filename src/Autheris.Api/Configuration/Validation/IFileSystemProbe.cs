using System.IO;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// WP-ARC-C-02: Abstracts filesystem existence checks for <see cref="Microsoft.Extensions.Options.IValidateOptions{TOptions}"/>
/// validators so option validation remains pure and deterministic in unit tests.
/// </summary>
public interface IFileSystemProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
}

public sealed class PhysicalFileSystemProbe : IFileSystemProbe
{
    public static readonly PhysicalFileSystemProbe Instance = new();

    public bool FileExists(string path) => File.Exists(System.IO.Path.GetFullPath(path));

    public bool DirectoryExists(string path) => Directory.Exists(System.IO.Path.GetFullPath(path));
}
