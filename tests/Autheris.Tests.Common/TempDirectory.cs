namespace Autheris.Tests.Common;

/// <summary>A uniquely named temporary directory that is deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "autheris-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of test scratch space.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of test scratch space.
        }
    }
}
