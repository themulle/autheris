namespace Autheris.Tests.Unit.Security;

using System;
using System.Data.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>
/// Security conditions SEC-SEC210-01 and SEC-SEC210-02 (REQ-SEC-210):
/// SQL Server Windows Auth NTLM/EPA error classification, numeric diagnostics, and circuit breaker protection.
/// </summary>
[Trait("Category", "Security")]
public sealed class SqlServerWindowsAuthSecurityTests
{
    private sealed class FakeSqlExceptionWithDiagnostics(int number, byte state, byte cls, string message) : DbException(message)
    {
        public int Number { get; } = number;
        public byte State { get; } = state;
        public byte Class { get; } = cls;
    }

    private static DataSourceConnectionOptions IntegratedOptions() => new()
    {
        Provider = "SqlServer",
        ConnectionString = "Server=10.160.2.94,1433;Database=Reporting;Integrated Security=true;Encrypt=True;TrustServerCertificate=True;",
        WindowsAuth = new SqlServerWindowsAuthOptions
        {
            Mode = SqlServerWindowsAuthMode.Ntlm,
            Domain = "CORP",
            Username = "svc-autheris"
        }
    };

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_SEC210_01_ClassifyOpenException_Error18452_DiagnosesEPA_AndSurfacesRemediations()
    {
        // SEC-SEC210-01: On SqlNumber 18452, classify as EPA Channel-Binding Token (CBT) mismatch and provide actionable remediations.
        var fakeEx = new FakeSqlExceptionWithDiagnostics(
            18452,
            state: 132,
            cls: 14,
            message: "Login failed. The login is from an untrusted domain and cannot be used with Integrated authentication.");

        var classified = SqlServerWindowsAuthConfigurator.ClassifyOpenException(fakeEx, IntegratedOptions());

        var authEx = classified.ShouldBeOfType<SqlServerWindowsAuthException>();
        authEx.Category.ShouldBe(SqlServerWindowsAuthFailureCategory.LoginRejected);
        authEx.SqlNumber.ShouldBe(18452);
        authEx.SqlState.ShouldBe(132);
        authEx.SqlClass.ShouldBe(14);

        // Actionable guidance for EPA and containerized NTLM
        authEx.Message.ShouldContain("Extended Protection for Authentication (EPA");
        authEx.Message.ShouldContain("Channel-Binding Token", Case.Insensitive);
        authEx.Message.ShouldContain("Allowed");
        authEx.Message.ShouldContain("Kerberos", Case.Insensitive);
        authEx.Message.ShouldContain("SqlPassword", Case.Insensitive);
        authEx.Message.ShouldContain("lock out the Active Directory account", Case.Insensitive);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_SEC210_01_ClassifyOpenException_Error18456_DifferentiatedFrom18452()
    {
        // SEC-SEC210-01: Error 18456 is classified distinctly from 18452 (no EPA false positives).
        var fakeEx = new FakeSqlExceptionWithDiagnostics(
            18456,
            state: 8,
            cls: 14,
            message: "Login failed for user 'CORP\\svc-autheris'.");

        var classified = SqlServerWindowsAuthConfigurator.ClassifyOpenException(fakeEx, IntegratedOptions());

        var authEx = classified.ShouldBeOfType<SqlServerWindowsAuthException>();
        authEx.Category.ShouldBe(SqlServerWindowsAuthFailureCategory.LoginRejected);
        authEx.SqlNumber.ShouldBe(18456);
        authEx.SqlState.ShouldBe(8);
        authEx.SqlClass.ShouldBe(14);

        authEx.Message.ShouldContain("Error 18456");
        authEx.Message.ShouldNotContain("Extended Protection");
        authEx.Message.ShouldNotContain("Channel-Binding Token");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_SEC210_02_ClassifyOpenException_CapturesNumericDiagnostics_StateNumberClass()
    {
        // SEC-SEC210-02: Numeric diagnostic fields (SqlNumber, SqlState, SqlClass) are preserved in DiagnosticFields.
        var fakeEx = new FakeSqlExceptionWithDiagnostics(
            18452,
            state: 142,
            cls: 14,
            message: "Login failed. The login is from an untrusted domain and cannot be used with Integrated authentication.");

        var classified = SqlServerWindowsAuthConfigurator.ClassifyOpenException(fakeEx, IntegratedOptions());

        var authEx = classified.ShouldBeOfType<SqlServerWindowsAuthException>();
        authEx.DiagnosticFields.ShouldContainKey("SqlNumber");
        authEx.DiagnosticFields["SqlNumber"].ShouldBe("18452");
        authEx.DiagnosticFields.ShouldContainKey("SqlState");
        authEx.DiagnosticFields["SqlState"].ShouldBe("142");
        authEx.DiagnosticFields.ShouldContainKey("SqlClass");
        authEx.DiagnosticFields["SqlClass"].ShouldBe("14");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_SEC210_02_ClassifyOpenException_SanitizesSensitiveClientMessage()
    {
        // SEC-SEC210-02: Sanitizes client messages to prevent credential leakage.
        const string sensitiveRaw = "Login failed. The login is from an untrusted domain. Password=SuperSecretP@ssw0rd!;User ID=admin;";
        var fakeEx = new FakeSqlExceptionWithDiagnostics(18452, state: 1, cls: 14, message: sensitiveRaw);

        var classified = SqlServerWindowsAuthConfigurator.ClassifyOpenException(fakeEx, IntegratedOptions());

        var authEx = classified.ShouldBeOfType<SqlServerWindowsAuthException>();
        authEx.Message.ShouldNotContain("SuperSecretP@ssw0rd!");
        foreach (var (_, value) in authEx.DiagnosticFields)
        {
            value.ShouldNotContain("SuperSecretP@ssw0rd!");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_SEC210_02_SqlLoginCircuitBreaker_TripsOnBoth18452And18456()
    {
        // SEC-SEC210-02: Maintains SqlFailureCategory.LoginRejected on both 18452 and 18456 to trigger circuit breaker.
        var ex18452 = SqlServerWindowsAuthConfigurator.ClassifyOpenException(
            new FakeSqlExceptionWithDiagnostics(18452, 132, 14, "untrusted domain"), IntegratedOptions());
        var ex18456 = SqlServerWindowsAuthConfigurator.ClassifyOpenException(
            new FakeSqlExceptionWithDiagnostics(18456, 8, 14, "Login failed for user"), IntegratedOptions());

        SqlLoginCircuitBreaker.IsLoginFailure(ex18452, out var reason18452).ShouldBeTrue();
        reason18452.ShouldBe("LoginRejected, SqlNumber=18452");

        SqlLoginCircuitBreaker.IsLoginFailure(ex18456, out var reason18456).ShouldBeTrue();
        reason18456.ShouldBe("LoginRejected, SqlNumber=18456");

        var breaker = new SqlLoginCircuitBreaker();
        var options = IntegratedOptions();

        breaker.RecordFailure(options, ex18452).ShouldBeTrue();
        var blocked = Should.Throw<SqlSourceLoginBlockedException>(() => breaker.ThrowIfOpen(options));
        blocked.Remaining.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_SEC230_01_ClassifyOpenException_Error18452_ClarifiesSqlClientTlsChannelBindingAndEPA()
    {
        // SEC-SEC230-01 & SEC-SEC230-04: On SqlNumber 18452, clarify that SqlClient did not provide TLS Channel Binding and server enforces EPA.
        var fakeEx = new FakeSqlExceptionWithDiagnostics(
            18452,
            state: 1,
            cls: 14,
            message: "Login failed. The login is from an untrusted domain and cannot be used with Integrated authentication.");

        var classified = SqlServerWindowsAuthConfigurator.ClassifyOpenException(fakeEx, IntegratedOptions());

        var authEx = classified.ShouldBeOfType<SqlServerWindowsAuthException>();
        authEx.Category.ShouldBe(SqlServerWindowsAuthFailureCategory.LoginRejected);
        authEx.SqlNumber.ShouldBe(18452);
        authEx.SqlState.ShouldBe(1);
        authEx.SqlClass.ShouldBe(14);

        // Required Issue #230 clarification: SqlClient did not provide TLS Channel Binding
        authEx.Message.ShouldContain("Microsoft.Data.SqlClient did not provide TLS Channel Binding and the target SQL Server enforces Extended Protection", Case.Insensitive);
        authEx.Message.ShouldContain("Extended Protection for Authentication (EPA");
        authEx.Message.ShouldContain("Channel-Binding Token", Case.Insensitive);
        authEx.Message.ShouldContain("Encrypt=True", Case.Insensitive);
        authEx.Message.ShouldContain("Allowed");
        authEx.Message.ShouldContain("Kerberos", Case.Insensitive);
        authEx.Message.ShouldContain("SqlPassword", Case.Insensitive);
        authEx.Message.ShouldContain("lock out the Active Directory account", Case.Insensitive);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_SEC230_02_ChannelBindingExtraction_DoesNotLeakSecrets()
    {
        // SEC-SEC230-01: Neither ChannelBindingStatus nor failure records leak passwords or credentials.
        const string secretPassword = "SuperSensitivePassword#2026";
        using var provider = new ManagedNtlmSspiContextProvider(
            new System.Net.NetworkCredential("svc-app", secretPassword, "CORP"));

        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        provider.TryGenerateContext(ReadOnlySpan<byte>.Empty, writer, "MSSQLSvc/host:1433");

        provider.ChannelBindingStatus.ShouldNotBeNull();
        provider.ChannelBindingStatus!.ShouldNotContain(secretPassword);
        provider.LastFailure?.ToString().ShouldNotContain(secretPassword);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_SEC230_03_ReflectionFailSafe_DoesNotThrowAndRecordsStatus()
    {
        // SEC-SEC230-03: Reflection extraction failures on unexpected driver types degrade gracefully without throwing.
        var unexpectedProvider = new DummyFailingSspiContextProvider();
        var result = SqlClientChannelBindingExtractor.ExtractChannelBinding(unexpectedProvider);

        result.Success.ShouldBeFalse();
        result.Binding.ShouldBeNull();
        result.StatusMessage.ShouldNotBeNullOrWhiteSpace();
        result.StatusMessage.ShouldContain("was not initialized or null");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_SEC230_05_ChannelBindingLifecycle_SafeDisposal()
    {
        // SEC-SEC230-02: ManagedNtlmSspiContextProvider disposes resources without leaking handles.
        var provider = new ManagedNtlmSspiContextProvider(
            new System.Net.NetworkCredential("svc", "pass", "DOM"));

        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        provider.TryGenerateContext(ReadOnlySpan<byte>.Empty, writer, "MSSQLSvc/db:1433");

        // Dispose provider
        provider.Dispose();
        // Subsequent dispose is idempotent and safe
        provider.Dispose();
    }

    private sealed class DummyFailingSspiContextProvider : Microsoft.Data.SqlClient.SspiContextProvider
    {
        protected override bool GenerateContext(
            ReadOnlySpan<byte> incomingBlob,
            System.Buffers.IBufferWriter<byte> outgoingBlobWriter,
            Microsoft.Data.SqlClient.SspiAuthenticationParameters authParams) => true;
    }
}
