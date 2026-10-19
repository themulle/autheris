using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Security;
using System.Security.Authentication.ExtendedProtection;
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

[Collection("ManagedNtlmSwitch")]
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
            out var sspiContextProvider,
            gssStateDirectory: _tempDir,
            getEnvVar: k => envVars.GetValueOrDefault(k),
            setEnvVar: (k, v) => envVars[k] = v);

        var builder = new SqlConnectionStringBuilder(preparedConnStr);
        Assert.True(builder.IntegratedSecurity);
        Assert.True(string.IsNullOrEmpty(builder.UserID));
        Assert.True(string.IsNullOrEmpty(builder.Password));
        Assert.DoesNotContain(secretPassword, preparedConnStr, StringComparison.Ordinal);

        Assert.True(AppContext.TryGetSwitch(SqlServerWindowsAuthConfigurator.UseManagedNtlmSwitchName, out bool managedNtlmEnabled) && managedNtlmEnabled);
        Assert.IsType<ManagedNtlmSspiContextProvider>(sspiContextProvider);

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

    private static DataSourceConnectionOptions NtlmOptions(bool? useManagedNtlm = null) => new()
    {
        Provider = "SqlServer",
        ConnectionString = "Server=10.160.2.94,1433;Database=Reporting;Integrated Security=true;",
        WindowsAuth = new SqlServerWindowsAuthOptions { Mode = SqlServerWindowsAuthMode.Ntlm, UseManagedNtlm = useManagedNtlm }
    };

    [Fact]
    public void ClassifyOpenException_GssapiMissing_ReturnsGssapiUnavailableCategory()
    {
        var result = SqlServerWindowsAuthConfigurator.ClassifyOpenException(
            new InvalidOperationException("GSSAPI operation failed with error - An unsupported mechanism was requested."), NtlmOptions());

        var ex = Assert.IsType<SqlServerWindowsAuthException>(result);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal(SqlServerWindowsAuthFailureCategory.GssapiUnavailable, ex.Category);
        Assert.Equal("InvalidOperationException", ex.InnerExceptionType);
    }

    [Fact]
    public void ClassifyOpenException_SspiContextWithoutManagedNtlm_ReturnsKerberosNegotiationCategory()
    {
        var result = SqlServerWindowsAuthConfigurator.ClassifyOpenException(
            new InvalidOperationException("Cannot generate SSPI context."), NtlmOptions());

        Assert.Equal(SqlServerWindowsAuthFailureCategory.KerberosNegotiation, Assert.IsType<SqlServerWindowsAuthException>(result).Category);
    }

    [Fact]
    public void ClassifyOpenException_SspiContextWithManagedNtlm_ReturnsNtlmNegotiationWithRecordedStatus()
    {
        var failure = new ManagedNtlmFailure(ManagedNtlmFailure.ChallengeStep, "InvalidToken", null, null);

        var result = SqlServerWindowsAuthConfigurator.ClassifyOpenException(
            new InvalidOperationException("Cannot generate SSPI context."), NtlmOptions(), managedNtlmUsed: true, managedNtlmFailure: failure);

        var ex = Assert.IsType<SqlServerWindowsAuthException>(result);
        Assert.Equal(SqlServerWindowsAuthFailureCategory.NtlmNegotiation, ex.Category);
        Assert.Equal("InvalidToken", ex.NtlmStatus);
        Assert.Equal("challenge", ex.NtlmStep);
        Assert.Contains("InvalidToken", ex.Message, StringComparison.Ordinal);
        Assert.Contains("challenge", ex.Message, StringComparison.Ordinal);
        Assert.Equal("InvalidToken", ex.DiagnosticFields["NtlmStatus"]);
        Assert.Equal("challenge", ex.DiagnosticFields["NtlmStep"]);
    }

    [Fact]
    public void ClassifyOpenException_LoginFailed_ReturnsLoginRejectedCategory()
    {
        var result = SqlServerWindowsAuthConfigurator.ClassifyOpenException(
            new InvalidOperationException("Login failed for user 'LWEW2K\\svc'."), NtlmOptions());

        var ex = Assert.IsType<SqlServerWindowsAuthException>(result);
        Assert.Equal(SqlServerWindowsAuthFailureCategory.LoginRejected, ex.Category);
        Assert.DoesNotContain("LWEW2K", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyOpenException_SqlPasswordConnection_IsReturnedUnchanged()
    {
        var original = new InvalidOperationException("Cannot generate SSPI context.");
        var options = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=h;User ID=sa;Password=x;" };

        Assert.Same(original, SqlServerWindowsAuthConfigurator.ClassifyOpenException(original, options));
    }

    [Fact]
    public void ManagedNtlmProvider_InvalidChallengeBlob_ReturnsFalseAndRecordsFailure()
    {
        using var provider = new ManagedNtlmSspiContextProvider(new NetworkCredential("svc", "Secret-Pw-123", "DOM"), "MSSQLSvc/host:1433", "NTLM");
        var writer = new ArrayBufferWriter<byte>();

        Assert.True(provider.TryGenerateContext(ReadOnlySpan<byte>.Empty, writer, "MSSQLSvc/host:1433"));
        Assert.Null(provider.LastFailure);
        Assert.True(writer.WrittenCount > 0);

        bool ok = provider.TryGenerateContext(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, new ArrayBufferWriter<byte>(), "MSSQLSvc/host:1433");

        Assert.False(ok);
        var failure = provider.LastFailure;
        Assert.NotNull(failure);
        Assert.Equal(ManagedNtlmFailure.ChallengeStep, failure!.Step);
        Assert.True(failure.StatusCode is not null || failure.ExceptionType is not null);
        Assert.DoesNotContain("Secret-Pw-123", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareConnection_UseManagedNtlmFalse_CreatesNoProviderAndDoesNotSetSwitch()
    {
        AppContext.SetSwitch(SqlServerWindowsAuthConfigurator.UseManagedNtlmSwitchName, false);
        var envVars = new Dictionary<string, string?>(StringComparer.Ordinal);
        var options = new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=h,1433;Database=D;",
            WindowsAuth = new SqlServerWindowsAuthOptions
            {
                Mode = SqlServerWindowsAuthMode.Ntlm,
                Domain = "DOM",
                Username = "svc",
                PasswordRef = "file:/run/secrets/pw",
                UseManagedNtlm = false
            }
        };

        SqlServerWindowsAuthConfigurator.PrepareConnection(
            options,
            new FakeSecretProvider(new Dictionary<string, string> { ["file:/run/secrets/pw"] = "pw-123" }),
            new FakeHostEnvironment("Production"),
            out var provider,
            gssStateDirectory: _tempDir,
            getEnvVar: k => envVars.GetValueOrDefault(k),
            setEnvVar: (k, v) => envVars[k] = v);

        Assert.Null(provider);
        Assert.True(AppContext.TryGetSwitch(SqlServerWindowsAuthConfigurator.UseManagedNtlmSwitchName, out bool enabled) && !enabled);
        Assert.True(envVars.ContainsKey("NTLM_USER_FILE"));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "false", false)]
    [InlineData(null, "0", false)]
    [InlineData(null, "true", true)]
    [InlineData(true, "false", true)]
    [InlineData(false, "true", false)]
    public void ManagedNtlmPolicy_ExplicitOptionWinsOverEnvironmentVariable(bool? configured, string? env, bool expected)
    {
        Assert.Equal(expected, ManagedNtlmPolicy.Resolve(configured, _ => env));
    }

    [Fact]
    public void PrepareConnection_OptionUnsetAndEnvironmentFalse_UsesGssPath()
    {
        var envVars = new Dictionary<string, string?>(StringComparer.Ordinal) { [ManagedNtlmPolicy.EnvironmentVariableName] = "false" };
        var options = new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=h,1433;Database=D;",
            WindowsAuth = new SqlServerWindowsAuthOptions
            {
                Mode = SqlServerWindowsAuthMode.Ntlm,
                Domain = "DOM",
                Username = "svc",
                PasswordRef = "file:/run/secrets/pw"
            }
        };

        SqlServerWindowsAuthConfigurator.PrepareConnection(
            options,
            new FakeSecretProvider(new Dictionary<string, string> { ["file:/run/secrets/pw"] = "pw-123" }),
            new FakeHostEnvironment("Production"),
            out var provider,
            gssStateDirectory: _tempDir,
            getEnvVar: k => envVars.GetValueOrDefault(k),
            setEnvVar: (k, v) => envVars[k] = v);

        Assert.Null(provider);
        Assert.Equal("Integrated-GSSAPI", SqlServerWindowsAuthConfigurator.DescribeAuthPath(options, k => envVars.GetValueOrDefault(k)));
        Assert.Equal("Integrated-ManagedNtlm", SqlServerWindowsAuthConfigurator.DescribeAuthPath(options, _ => null));
    }

    [Fact]
    public void DescribeAuthPath_ReportsSqlPasswordAndKerberos()
    {
        var sqlLogin = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=h;User ID=sa;Password=x;" };
        var kerberos = new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=h;Integrated Security=true;",
            WindowsAuth = new SqlServerWindowsAuthOptions { Mode = SqlServerWindowsAuthMode.Kerberos }
        };

        Assert.Equal("SqlPassword", SqlServerWindowsAuthConfigurator.DescribeAuthPath(sqlLogin, _ => null));
        Assert.Equal("Integrated-Kerberos", SqlServerWindowsAuthConfigurator.DescribeAuthPath(kerberos, _ => null));
    }

    [Theory]
    [InlineData("Server=10.160.2.94,1433;Database=R;User ID=u;Password=p;", "10.160.2.94", 1433)]
    [InlineData("Data Source=tcp:sql.corp.local,14330;Integrated Security=true", "sql.corp.local", 14330)]
    [InlineData("Server=sqlhost\\INST1;Integrated Security=true", "sqlhost", null)]
    public void SqlSourceDescriber_ExtractsHostAndPortWithoutSecrets(string connectionString, string host, int? port)
    {
        var description = new SqlSourceDescriber().Describe(new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = connectionString });

        Assert.Equal(host, description.Host);
        Assert.Equal(port, description.Port);
        Assert.DoesNotContain("Password", description.Host, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeEnvironment_Set_IsVisibleToNativeGetenv_OnUnix()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // native environ is only separate from the managed copy on Unix; the P/Invoke below targets glibc
        }

        string name = $"AUTHERIS_TEST_NATIVE_ENV_{Guid.NewGuid():N}";
        System.Diagnostics.Debug.Assert(NativeGetenv(name) == IntPtr.Zero);

        // Managed-only set is invisible to native code (documents the bug this helper fixes).
        Environment.SetEnvironmentVariable(name, "managed-only");
        Assert.Equal(IntPtr.Zero, NativeGetenv(name));

        NativeEnvironmentAccess.Set(name, "native-visible");

        var ptr = NativeGetenv(name);
        Assert.NotEqual(IntPtr.Zero, ptr);
        Assert.Equal("native-visible", System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr));
        Assert.Equal("native-visible", Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void SqlClientChannelBindingExtractor_IsSupportedSqlClientVersion_DetectsVersion7AndHigher()
    {
        Assert.True(SqlClientChannelBindingExtractor.IsSupportedSqlClientVersion(typeof(SspiContextProvider).Assembly));
        Assert.False(SqlClientChannelBindingExtractor.IsSupportedSqlClientVersion(null));
    }

    [Fact]
    public void SqlClientChannelBindingExtractor_NullProvider_ReturnsGracefulFailureResult()
    {
        var result = SqlClientChannelBindingExtractor.ExtractChannelBinding(null);

        Assert.False(result.Success);
        Assert.Null(result.Binding);
        Assert.Null(result.Kind);
        Assert.Contains("SspiContextProvider was null", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlClientChannelBindingExtractor_UninitializedStateObject_ReturnsGracefulFailure()
    {
        using var provider = new ManagedNtlmSspiContextProvider(new NetworkCredential("svc", "secret", "DOM"));
        var result = SqlClientChannelBindingExtractor.ExtractChannelBinding(provider);

        Assert.False(result.Success);
        Assert.Null(result.Binding);
        Assert.Null(result.Kind);
        Assert.Contains("_physicalStateObj was not initialized", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedNtlmSspiContextProvider_WithCustomChannelBindingAccessor_AssignsBindingAndAcquires()
    {
        using var testBinding = new TestChannelBinding(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        using var provider = new ManagedNtlmSspiContextProvider(
            new NetworkCredential("svc", "SecretPass123", "CORP"),
            targetSpnOverride: "MSSQLSvc/db01.corp.local:1433",
            package: "NTLM",
            channelBindingAccessor: _ => testBinding);

        var writer = new ArrayBufferWriter<byte>();
        bool negotiateSuccess = provider.TryGenerateContext(ReadOnlySpan<byte>.Empty, writer, "MSSQLSvc/db01.corp.local:1433");

        Assert.True(negotiateSuccess);
        Assert.True(provider.ChannelBindingAcquired);
        Assert.Equal(ChannelBindingKind.Endpoint, provider.ChannelBindingKindUsed);
        Assert.Contains("custom accessor", provider.ChannelBindingStatus, StringComparison.Ordinal);
        Assert.Null(provider.LastFailure);
        Assert.True(writer.WrittenCount > 0);
    }

    [Fact]
    public void ManagedNtlmSspiContextProvider_Dispose_CleansUpChannelBindingAndNegotiation()
    {
        using var testBinding = new TestChannelBinding(new byte[] { 1, 2, 3, 4 });
        var provider = new ManagedNtlmSspiContextProvider(
            new NetworkCredential("svc", "SecretPass123", "CORP"),
            channelBindingAccessor: _ => testBinding);

        var writer = new ArrayBufferWriter<byte>();
        Assert.True(provider.TryGenerateContext(ReadOnlySpan<byte>.Empty, writer, "resource"));
        Assert.True(provider.ChannelBindingAcquired);

        provider.Dispose();
        // Provider disposed without exception
    }

    [Fact]
    public void SqlClientChannelBindingExtractor_WithSimulatedObjectGraph_DetectsNonSslStreamGracefully()
    {
        var dummyTcpHandle = new DummyTcpHandle(new object()); // not an SslStream
        var dummyMarsConnection = new DummyMarsConnection(dummyTcpHandle);
        var dummyMarsHandle = new DummyMarsHandle(dummyMarsConnection);
        var dummyStateObject = new DummyStateObject(dummyMarsHandle);

        var simulatedProvider = new SimulatedSspiContextProvider(dummyStateObject);
        var result = SqlClientChannelBindingExtractor.ExtractChannelBinding(simulatedProvider);

        Assert.False(result.Success);
        Assert.Null(result.Binding);
        Assert.Contains("is not an SslStream", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlClientChannelBindingExtractor_WithUnencryptedHandle_ReportsNoSslStream()
    {
        var dummyTcpHandle = new DummyTcpHandle(null); // unencrypted connection
        var dummyStateObject = new DummyStateObject(dummyTcpHandle);

        var simulatedProvider = new SimulatedSspiContextProvider(dummyStateObject);
        var result = SqlClientChannelBindingExtractor.ExtractChannelBinding(simulatedProvider);

        Assert.False(result.Success);
        Assert.Null(result.Binding);
        Assert.Contains("No _sslStream found", result.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("unencrypted", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestChannelBinding : ChannelBinding
    {
        private readonly IntPtr _ptr;

        public TestChannelBinding(byte[] data)
        {
            _ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length);
            System.Runtime.InteropServices.Marshal.Copy(data, 0, _ptr, data.Length);
            SetHandle(_ptr);
            Size = data.Length;
        }

        public override int Size { get; }

        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(handle);
                handle = IntPtr.Zero;
            }
            return true;
        }
    }

    private sealed class SimulatedSspiContextProvider : SspiContextProvider
    {
        private readonly object _physicalStateObj;

        public SimulatedSspiContextProvider(object stateObj)
        {
            _physicalStateObj = stateObj;
        }

        protected override bool GenerateContext(
            ReadOnlySpan<byte> incomingBlob,
            IBufferWriter<byte> outgoingBlobWriter,
            SspiAuthenticationParameters authParams) => true;
    }

    private sealed class DummyStateObject(object sessionHandle)
    {
        private readonly object _sessionHandle = sessionHandle;
    }

    private sealed class DummyMarsHandle(object connection)
    {
        private readonly object _connection = connection;
    }

    private sealed class DummyMarsConnection(object lowerHandle)
    {
        private readonly object _lowerHandle = lowerHandle;
    }

    private sealed class DummyTcpHandle(object? sslStream)
    {
        private readonly object? _sslStream = sslStream;
    }

#pragma warning disable SYSLIB1054, CA5392, CA2101 // test-only probe of the native environment; the name is marshalled explicitly as UTF-8
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getenv")]
    private static extern IntPtr NativeGetenv([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string name);
#pragma warning restore SYSLIB1054, CA5392, CA2101

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

/// <summary>Test access to the internal <c>NativeEnvironment</c> helper.</summary>
internal static class NativeEnvironmentAccess
{
    public static void Set(string name, string value) => Autheris.Infrastructure.Persistence.NativeEnvironment.Set(name, value);
}
