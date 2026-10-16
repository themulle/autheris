using Autheris.Application.Interfaces;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Failure category of SQL Server Windows authentication.</summary>
public enum SqlServerWindowsAuthFailureCategory
{
    /// <summary>The native GSSAPI library or the gss-ntlmssp mechanism is missing or unusable.</summary>
    GssapiUnavailable = 0,

    /// <summary>Kerberos / SPN / KDC negotiation failed.</summary>
    KerberosNegotiation = 1,

    /// <summary>NTLM negotiation failed (managed NTLM or gss-ntlmssp) before the server accepted the credentials.</summary>
    NtlmNegotiation = 2,

    /// <summary>The server rejected the login (error 18456 / 18452).</summary>
    LoginRejected = 3
}

/// <summary>
/// Classified SQL Server Windows authentication failure. Derives from <see cref="InvalidOperationException"/> for compatibility.
/// The message and <see cref="DiagnosticFields"/> never contain passwords, connection strings or raw driver messages.
/// </summary>
public sealed class SqlServerWindowsAuthException : InvalidOperationException, ISqlSourceDiagnosticException
{
    public SqlServerWindowsAuthException(
        SqlServerWindowsAuthFailureCategory category,
        string message,
        Exception? innerException = null,
        int? sqlNumber = null,
        int? sqlState = null,
        int? sqlClass = null,
        string? ntlmStatus = null,
        string? ntlmStep = null,
        string? ntlmExceptionType = null)
        : base(message, innerException)
    {
        Category = category;
        SqlNumber = sqlNumber;
        SqlState = sqlState;
        SqlClass = sqlClass;
        NtlmStatus = ntlmStatus;
        NtlmStep = ntlmStep;
        NtlmExceptionType = ntlmExceptionType;
        InnerExceptionType = innerException?.GetType().Name;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sqlNumber is { } n) fields["SqlNumber"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (sqlState is { } st) fields["SqlState"] = st.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (sqlClass is { } c) fields["SqlClass"] = c.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ntlmStatus is not null) fields["NtlmStatus"] = ntlmStatus;
        if (ntlmStep is not null) fields["NtlmStep"] = ntlmStep;
        if (ntlmExceptionType is not null) fields["NtlmExceptionType"] = ntlmExceptionType;
        if (InnerExceptionType is not null) fields["InnerExceptionType"] = InnerExceptionType;
        DiagnosticFields = fields;
    }

    public SqlServerWindowsAuthFailureCategory Category { get; }

    public int? SqlNumber { get; }

    public int? SqlState { get; }

    public int? SqlClass { get; }

    /// <summary>Managed NTLM <c>NegotiateAuthenticationStatusCode</c> name, when known.</summary>
    public string? NtlmStatus { get; }

    /// <summary>Managed NTLM step: <c>negotiate</c> (first token) or <c>challenge</c> (processing the server challenge).</summary>
    public string? NtlmStep { get; }

    public string? NtlmExceptionType { get; }

    public string? InnerExceptionType { get; }

    public string CategoryName => Category.ToString();

    public IReadOnlyDictionary<string, string> DiagnosticFields { get; }
}
