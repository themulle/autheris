using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Autheris.Tests.Common;

/// <summary>Plain <see cref="IHostEnvironment"/> for tests that only need an environment name.</summary>
public sealed class TestHostEnvironment(string environmentName = "Development") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "Autheris.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
