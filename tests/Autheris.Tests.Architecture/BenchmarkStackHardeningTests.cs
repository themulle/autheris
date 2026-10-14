using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>SEC2-09 / SR-D-08: the benchmark compose stack verifies the SQL Server certificate and hardens the reverse proxy.</summary>
[Trait("Category", "Security")]
public sealed partial class BenchmarkStackHardeningTests
{
    [Fact]
    public void Deploy_ContainsNoTrustServerCertificateTrue()
    {
        string deploy = Path.Combine(FindRepoRoot(), "deploy");
        var offenders = Directory.EnumerateFiles(deploy, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}certs{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}results{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.EndsWith("verify-hardening.sh", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("TrustServerCertificate=True", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(deploy, f))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void BenchmarkSqlServerConnectionStrings_SatisfyTlsPolicy_AndPinTheServerCertificate()
    {
        string root = FindRepoRoot();
        string compose = File.ReadAllText(Path.Combine(root, "deploy", "podman-compose.yaml"));
        string composeConn = ConnectionStringRegex().Match(compose).Groups[1].Value;
        using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "deploy", "containers", "gqlgateway-api", "appsettings.Benchmark.json")));
        string jsonConn = settings.RootElement.GetProperty("Gateway").GetProperty("DataSources").GetProperty("Connections")
            .GetProperty("crm").GetProperty("ConnectionString").GetString()!;

        foreach (string conn in new[] { composeConn, jsonConn })
        {
            conn.ShouldNotBeNullOrWhiteSpace();
            ConnectionTlsPolicy.Validate("SqlServer", conn.Replace("${MSSQL_CRM_PASSWORD:-${MSSQL_SA_PASSWORD:?set MSSQL_SA_PASSWORD in .env (see .env.example)}}", "pw", StringComparison.Ordinal)).ShouldBeNull();
            conn.ShouldContain("Encrypt=Mandatory");
            conn.ShouldContain("HostNameInCertificate=sqlserver");
            conn.ShouldContain("ServerCertificate=/certs/sqlserver.crt");
        }
    }

    [Fact]
    public void ReverseProxy_RunsReadOnlyWithTmpfsWritablePaths()
    {
        string compose = File.ReadAllText(Path.Combine(FindRepoRoot(), "deploy", "podman-compose.yaml"));
        string service = ServiceBlock(compose, "reverse-proxy");

        service.ShouldContain("read_only: true");
        service.ShouldContain("tmpfs:");
        foreach (string path in new[] { "/var/cache/nginx", "/var/run", "/tmp" })
        {
            service.ShouldContain($"- {path}");
        }
    }

    [Fact]
    public void SqlServer_ForcesEncryptionWithMountedCertificate()
    {
        string root = FindRepoRoot();
        string conf = File.ReadAllText(Path.Combine(root, "deploy", "containers", "sqlserver", "mssql.conf"));
        conf.ShouldContain("tlscert = /certs/sqlserver.crt");
        conf.ShouldContain("tlskey = /certs/sqlserver.key");
        conf.ShouldContain("forceencryption = 1");

        File.ReadAllText(Path.Combine(root, "deploy", "containers", "sqlserver", "Containerfile"))
            .ShouldContain("containers/sqlserver/mssql.conf");
        string composeText = File.ReadAllText(Path.Combine(root, "deploy", "podman-compose.yaml"));
        string sqlService = ServiceBlock(composeText, "sqlserver");
        sqlService.ShouldContain("./certs/sqlserver.crt:/certs/sqlserver.crt");
        sqlService.ShouldContain("./certs/sqlserver.key:/certs/sqlserver.key");
        ServiceBlock(composeText, "gqlgateway-api").ShouldContain("./certs/sqlserver.crt:/certs/sqlserver.crt");

        string gen = File.ReadAllText(Path.Combine(root, "deploy", "scripts", "generate-env.sh"));
        gen.ShouldContain("DNS:sqlserver");
        File.ReadAllText(Path.Combine(root, ".gitignore")).ShouldContain("deploy/certs/");
    }

    /// <summary>Text of one top-level compose service: from its key to the next two-space-indented key (order independent).</summary>
    private static string ServiceBlock(string compose, string service)
    {
        var match = Regex.Match(compose, $@"^  {Regex.Escape(service)}:\s*$(.*?)(?=^  [A-Za-z0-9_-]+:\s*$|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        match.Success.ShouldBeTrue($"compose service '{service}' not found");
        return match.Groups[1].Value;
    }

    [GeneratedRegex("Gateway__DataSources__Connections__crm__ConnectionString: \"([^\"]+)\"")]
    private static partial Regex ConnectionStringRegex();

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Autheris.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Autheris.sln not found");
    }
}
