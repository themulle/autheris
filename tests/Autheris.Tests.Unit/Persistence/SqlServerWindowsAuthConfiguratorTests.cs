using System.ComponentModel.DataAnnotations;
using System.Security;
using System.Text;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Autheris.Tests.Unit.Persistence;

public sealed class SqlServerWindowsAuthConfiguratorTests : IDisposable
{
    private readonly string _tempDir;

    public SqlServerWindowsAuthConfiguratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"autheris-ntlm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void PrepareConnection_RejectsDomainUserIdWithoutIntegratedSecurity_ToPreventAdLockout()
    {
        var options = new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=10.160.2.94,1433;Database=Reporting;User ID=LWEW2K\\lwesvcbidbreportread;Password=Secret;Encrypt=True;TrustServerCertificate=True;"
        };

        var ex = Assert.Throws<SecurityException>(() => SqlServerWindowsAuthConfigurator.PrepareConnection(
            options,
            secrets: null,
            environment: new FakeHostEnvironment("Production"),
            gssStateDirectory: _tempDir));

        Assert.Contains("domain-qualified 'User ID'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Integrated Security=true", ex.Message, StringComparison.Ordinal);
        Assert.Contains("lock out the Active Directory account", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareConnection_WithWindowsAuthOptions_MaterializesRestrictedNtlmUserFileAndSetsGssEnvVars()
    {
        const string secretPassword = "Super:Secret!Password#2026";
        var secrets = new FakeSecretProvider(new Dictionary<string, string>
        {
            ["file:/run/secrets/lwe_bi_password"] = secretPassword
        });

        var envVars = new Dictionary<string, string?>(StringComparer.Ordinal);
        var options = new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=10.160.2.94,1433;Database=Reporting;Encrypt=True;TrustServerCertificate=True;",
            WindowsAuth = new SqlServerWindowsAuthOptions
            {
                Mode = SqlServerWindowsAuthMode.Ntlm,
                Domain = "LWEW2K",
                Username = "lwesvcbidbreportread",
                PasswordRef = "file:/run/secrets/lwe_bi_password"
            }
        };

        string preparedConnStr = SqlServerWindowsAuthConfigurator.PrepareConnection(
            options,
            secrets,
            new FakeHostEnvironment("Production"),
            gssStateDirectory: _tempDir,
            getEnvVar: k => envVars.GetValueOrDefault(k),
            setEnvVar: (k, v) => envVars[k] = v);

        var builder = new SqlConnectionStringBuilder(preparedConnStr);
        Assert.True(builder.IntegratedSecurity);
        Assert.True(string.IsNullOrEmpty(builder.UserID));
        Assert.True(string.IsNullOrEmpty(builder.Password));
        Assert.DoesNotContain(secretPassword, preparedConnStr, StringComparison.Ordinal);

        Assert.True(envVars.ContainsKey("NTLM_USER_FILE"));
        Assert.Equal("lwesvcbidbreportread", envVars["NTLMUSER"]);
        Assert.Equal("LWEW2K", envVars["NETBIOS_DOMAIN_NAME"]);

        string ntlmFile = envVars["NTLM_USER_FILE"]!;
        Assert.True(File.Exists(ntlmFile));
        Assert.Equal($"LWEW2K:lwesvcbidbreportread:{secretPassword}", File.ReadAllText(ntlmFile, Encoding.UTF8).Trim());

        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(ntlmFile);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public void PrepareConnection_WithPreMountedNtlmUserFile_ValidatesAndPopulatesNtlmUserAndDomain()
    {
        string ntlmFile = Path.Combine(_tempDir, "ntlm_users");
        File.WriteAllText(ntlmFile, "# Comment line\nLWEW2K:lwesvcbidbreportread:MySecretPass\n", Encoding.UTF8);

        var envVars = new Dictionary<string, string?>(StringComparer.Ordinal);
        var options = new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=10.160.2.94,1433;Database=Reporting;Integrated Security=true;Encrypt=True;TrustServerCertificate=True;",
            WindowsAuth = new SqlServerWindowsAuthOptions
            {
                Mode = SqlServerWindowsAuthMode.Ntlm,
                NtlmUserFilePath = ntlmFile
            }
        };

        SqlServerWindowsAuthConfigurator.PrepareConnection(
            options,
            secrets: null,
            environment: new FakeHostEnvironment("Production"),
            gssStateDirectory: _tempDir,
            getEnvVar: k => envVars.GetValueOrDefault(k),
            setEnvVar: (k, v) => envVars[k] = v);

        Assert.Equal(Path.GetFullPath(ntlmFile), envVars["NTLM_USER_FILE"]);
        Assert.Equal("lwesvcbidbreportread", envVars["NTLMUSER"]);
        Assert.Equal("LWEW2K", envVars["NETBIOS_DOMAIN_NAME"]);
    }

    [Theory]
    [InlineData("invalid_line_without_colons")]
    [InlineData("LWEW2K::password_without_user")]
    [InlineData("LWEW2K:user_without_password:")]
    public void ValidateNtlmUserFile_RejectsMalformedLinesWithoutLeakingContent(string malformedLine)
    {
        string ntlmFile = Path.Combine(_tempDir, "bad_ntlm_users");
        File.WriteAllText(ntlmFile, malformedLine + "\n", Encoding.UTF8);

        var ex = Assert.Throws<SecurityException>(() => SqlServerWindowsAuthConfigurator.ValidateNtlmUserFile(ntlmFile));
        Assert.Contains("DOMAIN:USER:PASSWORD", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password_without_user", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyOpenException_ClassifiesGssApiSspiAndLoginFailuresClearly()
    {
        var options = new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=10.160.2.94,1433;Database=Reporting;Integrated Security=true;",
            WindowsAuth = new SqlServerWindowsAuthOptions { Mode = SqlServerWindowsAuthMode.Ntlm }
        };

        var gssEx = new InvalidOperationException("GSSAPI operation failed with error - An unsupported mechanism was requested.");
        var classifiedGss = SqlServerWindowsAuthConfigurator.ClassifyOpenException(gssEx, options);
        Assert.Contains("gss-ntlmssp", classifiedGss.Message, StringComparison.Ordinal);
        Assert.Contains("libgssapi-krb5-2", classifiedGss.Message, StringComparison.Ordinal);

        var sspiEx = new InvalidOperationException("The target principal name is incorrect. Cannot generate SSPI context.");
        var classifiedSspi = SqlServerWindowsAuthConfigurator.ClassifyOpenException(sspiEx, options);
        Assert.Contains("SSPI/Kerberos", classifiedSspi.Message, StringComparison.Ordinal);
        Assert.Contains("MSSQLSvc/", classifiedSspi.Message, StringComparison.Ordinal);

        var loginEx = new InvalidOperationException("Login failed for user 'LWEW2K\\lwesvcbidbreportread'.");
        var classifiedLogin = SqlServerWindowsAuthConfigurator.ClassifyOpenException(loginEx, options);
        Assert.Contains("lock out the Active Directory service account", classifiedLogin.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSqlServerWindowsAuth_AtStartup_RejectsDomainUserIdWithoutIntegratedSecurity_WithoutNetworkConnect()
    {
        var gatewayOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["LweBi"] = new DataSourceConnectionOptions
                    {
                        Provider = "SqlServer",
                        ConnectionString = "Server=10.160.2.94,1433;Database=Reporting;User ID=LWEW2K\\lwesvcbidbreportread;Password=bad;"
                    }
                }
            }
        };

        var ex = Assert.Throws<ValidationException>(() => GatewayStartupValidator.ValidateSqlServerWindowsAuth(
            gatewayOptions,
            new FakeHostEnvironment("Production")));

        Assert.Contains("LweBi", ex.Message, StringComparison.Ordinal);
        Assert.Contains("domain-qualified 'User ID'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Integrated Security=true", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FakeSecretProvider(IReadOnlyDictionary<string, string> secrets) : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) =>
            secrets.TryGetValue(secretRef, out var val)
                ? Encoding.UTF8.GetBytes(val)
                : throw new InvalidOperationException("Secret not found");

        public void SetSecret(string secretRef, byte[] secretBytes)
        {
        }
    }

    private sealed class FakeHostEnvironment(string envName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = envName;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
