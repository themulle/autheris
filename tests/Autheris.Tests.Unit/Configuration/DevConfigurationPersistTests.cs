namespace Autheris.Tests.Unit.Configuration;

using System.Collections.Generic;
using Autheris.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

public sealed class DevConfigurationPersistTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Persist_enabled_never_writes_sqlite_governance_connection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autheris-devpersist-" + Guid.NewGuid().ToString("N"));
        var cfg = new ConfigurationManager();
        cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Dev:Persist:Enabled"] = "true",
            ["Gateway:Dev:Persist:Directory"] = dir,
        });

        var report = DevConfiguration.Apply(cfg, new Env());

        cfg["Gateway:GovernanceDb:ConnectionString"].ShouldBeNullOrEmpty();
        report.Applied.Keys.ShouldNotContain(k => k.EndsWith("GovernanceDb:ConnectionString", StringComparison.OrdinalIgnoreCase));
        Directory.Exists(dir).ShouldBeFalse();
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
