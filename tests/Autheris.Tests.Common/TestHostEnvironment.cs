using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Autheris.Tests.Common;

/// <summary>Plain <see cref="IHostEnvironment"/> for tests that only need an environment name; other members default to empty, like a substitute.</summary>
public sealed class TestHostEnvironment(string environmentName = "Development") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = string.Empty;

    public string ContentRootPath { get; set; } = string.Empty;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
