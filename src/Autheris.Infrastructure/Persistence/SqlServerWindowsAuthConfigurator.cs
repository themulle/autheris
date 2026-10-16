using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Security;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Issue #102: Configures and validates SQL Server Windows Authentication (Managed NTLM via
/// <c>System.Net.Security.UseManagedNtlm</c> + <see cref="SspiContextProvider"/>, NTLM via <c>gss-ntlmssp</c>,
/// and Kerberos via <c>libgssapi-krb5-2</c>) for Linux/Podman/Docker containers and non-domain hosts.
/// <para>
/// Security guarantees:
/// <list type="bullet">
///   <item>Never logs or includes plaintext passwords in exception messages or connection strings.</item>
///   <item>Detects and rejects the common anti-pattern of putting <c>User ID=DOMAIN\user</c> without <c>Integrated Security=true</c>,
///   which would send an AD credential as a SQL login and risk locking the Active Directory account after failed attempts.</item>
///   <item>Supports .NET's managed NTLM (<c>System.Net.Security.UseManagedNtlm</c>) via a per-connection <see cref="SspiContextProvider"/>
///   as well as pre-mounted <c>NTLM_USER_FILE</c> (<c>DOMAIN:USER:PASSWORD</c>, mode <c>0400</c>/<c>0600</c>) and
///   per-datasource <see cref="SqlServerWindowsAuthOptions"/> backed by <see cref="SecretReferenceResolver"/> (<c>file:/run/secrets/...</c> or Key Vault).</item>
///   <item>Classifies runtime <see cref="SqlException"/> / GSSAPI / SSPI failures into actionable operator diagnostics.</item>
/// </list>
/// </para>
/// </summary>
public static class SqlServerWindowsAuthConfigurator
{
    public const string UseManagedNtlmSwitchName = "System.Net.Security.UseManagedNtlm";
    public const string DefaultGssStateDirectory = "/app/data/gss";
    public const string DefaultGeneratedNtlmFileName = "ntlm_users";
    public const string GssMechConfigPath = "/etc/gss/mech.d/mech.ntlmssp.conf";
    public const string LegacyGssMechPath = "/etc/gss/mech";

    private static readonly Lock SyncLock = new();

    /// <summary>
    /// Enables .NET's built-in managed NTLM implementation (<c>System.Net.Security.UseManagedNtlm</c>) so
    /// <see cref="NegotiateAuthentication"/> can generate NTLMSSP tokens directly in managed code on Linux/containers.
    /// </summary>
    public static void EnableManagedNtlmSwitch()
    {
        AppContext.SetSwitch(UseManagedNtlmSwitchName, true);
    }

    /// <summary>
    /// Prepares the SQL Server connection string, optional per-connection <see cref="SspiContextProvider"/> (using
    /// <c>System.Net.Security.UseManagedNtlm</c>), and GSSAPI environment for Windows Authentication (NTLM / Kerberos).
    /// </summary>
    public static string PrepareConnection(
        DataSourceConnectionOptions options,
        IKeyVaultSecretProvider? secrets,
        IHostEnvironment? environment,
        out SspiContextProvider? sspiContextProvider,
        ILogger? logger = null,
        string? gssStateDirectory = null,
        Func<string, string?>? getEnvVar = null,
        Action<string, string>? setEnvVar = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        sspiContextProvider = null;

        var getEnvironmentVariable = getEnvVar ?? Environment.GetEnvironmentVariable;
        var setEnvironmentVariable = setEnvVar ?? NativeEnvironment.Set;

        var builder = new SqlConnectionStringBuilder(options.ConnectionString);
        var winAuth = options.WindowsAuth;
        bool hasWindowsAuthConfig = winAuth is not null && winAuth.Mode != SqlServerWindowsAuthMode.None;

        // Guard against the classic AD lockout trap: "User ID=DOMAIN\user" without Integrated Security=true
        // sends a Windows account to SQL Server as a SQL Login, which fails and increments the AD badPwdCount!
        if (!builder.IntegratedSecurity &&
            !string.IsNullOrWhiteSpace(builder.UserID) &&
            builder.UserID.Contains('\\', StringComparison.Ordinal))
        {
            throw new SecurityException(
                "SQL Server connection string contains a domain-qualified 'User ID' (DOMAIN\\user) without 'Integrated Security=true'. " +
                "Microsoft.Data.SqlClient does not accept Windows domain credentials via 'User ID'/'Password' and would send them as a SQL Server login, " +
                "which can lock out the Active Directory account. Use 'Integrated Security=true' with NTLM_USER_FILE or DataSources:Connections:<name>:WindowsAuth.");
        }

        if (!hasWindowsAuthConfig && !builder.IntegratedSecurity)
        {
            return options.ConnectionString;
        }

        if (hasWindowsAuthConfig)
        {
            // Enforce Integrated Security=true and strip any User ID / Password from the connection string so
            // SqlClient uses SSPI/GSSAPI instead of SQL authentication.
            builder.IntegratedSecurity = true;
            builder.Remove("User ID");
            builder.Remove("UID");
            builder.Remove("Password");
            builder.Remove("PWD");

            ConfigureGssEnvironmentAndSspiProvider(
                winAuth!,
                secrets,
                environment,
                out sspiContextProvider,
                logger,
                gssStateDirectory,
                getEnvironmentVariable,
                setEnvironmentVariable);
        }
        else if (builder.IntegratedSecurity)
        {
            // Integrated Security=true without explicit WindowsAuth section: check ambient NTLM_USER_FILE if set.
            var ambientNtlmFile = getEnvironmentVariable("NTLM_USER_FILE");
            if (!string.IsNullOrWhiteSpace(ambientNtlmFile))
            {
                ValidateNtlmUserFile(ambientNtlmFile, logger, out var ambientDomain, out var ambientUser, out var ambientPassword);
                if (!string.IsNullOrWhiteSpace(ambientUser) && !string.IsNullOrWhiteSpace(ambientPassword) &&
                    ManagedNtlmPolicy.Resolve(null, getEnvironmentVariable))
                {
                    EnableManagedNtlmSwitch();
                    sspiContextProvider = new ManagedNtlmSspiContextProvider(
                        new NetworkCredential(ambientUser, ambientPassword, ambientDomain ?? string.Empty),
                        targetSpnOverride: null,
                        package: "NTLM");
                }
            }
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Backward-compatible overload of <see cref="PrepareConnection(DataSourceConnectionOptions, IKeyVaultSecretProvider?, IHostEnvironment?, out SspiContextProvider?, ILogger?, string?, Func{string, string?}?, Action{string, string}?)"/>.
    /// </summary>
    public static string PrepareConnection(
        DataSourceConnectionOptions options,
        IKeyVaultSecretProvider? secrets,
        IHostEnvironment? environment,
        ILogger? logger = null,
        string? gssStateDirectory = null,
        Func<string, string?>? getEnvVar = null,
        Action<string, string>? setEnvVar = null) =>
        PrepareConnection(options, secrets, environment, out _, logger, gssStateDirectory, getEnvVar, setEnvVar);

    /// <summary>
    /// Configures the GSSAPI environment variables (<c>NTLM_USER_FILE</c>, <c>NTLMUSER</c>, <c>NETBIOS_DOMAIN_NAME</c>,
    /// <c>KRB5_CONFIG</c>, <c>KRB5_CLIENT_KTNAME</c>, <c>KRB5CCNAME</c>) and, when <see cref="SqlServerWindowsAuthOptions.UseManagedNtlm"/>
    /// is enabled, creates a per-connection <see cref="SspiContextProvider"/> backed by <c>System.Net.Security.UseManagedNtlm</c>.
    /// </summary>
    public static void ConfigureGssEnvironment(
        SqlServerWindowsAuthOptions winAuth,
        IKeyVaultSecretProvider? secrets,
        IHostEnvironment? environment,
        ILogger? logger = null,
        string? gssStateDirectory = null,
        Func<string, string?>? getEnvVar = null,
        Action<string, string>? setEnvVar = null) =>
        ConfigureGssEnvironmentAndSspiProvider(
            winAuth,
            secrets,
            environment,
            out _,
            logger,
            gssStateDirectory,
            getEnvVar,
            setEnvVar);

    private static void ConfigureGssEnvironmentAndSspiProvider(
        SqlServerWindowsAuthOptions winAuth,
        IKeyVaultSecretProvider? secrets,
        IHostEnvironment? environment,
        out SspiContextProvider? sspiContextProvider,
        ILogger? logger = null,
        string? gssStateDirectory = null,
        Func<string, string?>? getEnvVar = null,
        Action<string, string>? setEnvVar = null)
    {
        ArgumentNullException.ThrowIfNull(winAuth);
        sspiContextProvider = null;

        var getEnvironmentVariable = getEnvVar ?? Environment.GetEnvironmentVariable;
        var setEnvironmentVariable = setEnvVar ?? NativeEnvironment.Set;

        var effectiveMode = ResolveEffectiveMode(winAuth, getEnvironmentVariable);
        if (effectiveMode == SqlServerWindowsAuthMode.None)
        {
            return;
        }

        if (effectiveMode == SqlServerWindowsAuthMode.Kerberos)
        {
            if (!string.IsNullOrWhiteSpace(winAuth.KerberosConfigPath))
            {
                EnsureFileExists(winAuth.KerberosConfigPath, "Kerberos configuration file (KerberosConfigPath)");
                setEnvironmentVariable("KRB5_CONFIG", Path.GetFullPath(winAuth.KerberosConfigPath));
            }

            if (!string.IsNullOrWhiteSpace(winAuth.KerberosKeytabPath))
            {
                EnsureFileExists(winAuth.KerberosKeytabPath, "Kerberos keytab file (KerberosKeytabPath)");
                CheckRestrictedFilePermissions(winAuth.KerberosKeytabPath, "Kerberos keytab file", logger);
                setEnvironmentVariable("KRB5_CLIENT_KTNAME", Path.GetFullPath(winAuth.KerberosKeytabPath));
            }

            if (!string.IsNullOrWhiteSpace(winAuth.KerberosCachePath))
            {
                setEnvironmentVariable("KRB5CCNAME", Path.GetFullPath(winAuth.KerberosCachePath));
            }

            return;
        }

        // NTLM mode (or Auto resolved to NTLM)
        string? ntlmFilePath = winAuth.NtlmUserFilePath;
        if (string.IsNullOrWhiteSpace(ntlmFilePath))
        {
            ntlmFilePath = getEnvironmentVariable("NTLM_USER_FILE");
        }

        var (domain, username) = NormalizeDomainAndUser(winAuth.Domain, winAuth.Username);
        string? resolvedPassword = null;

        if (!string.IsNullOrWhiteSpace(winAuth.PasswordRef))
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new SecurityException(
                    "SQL Server WindowsAuth with PasswordRef requires Username (and Domain) to be configured.");
            }

            resolvedPassword = SecretReferenceResolver.Resolve(
                secrets,
                winAuth.PasswordRef,
                environment,
                allowPlaintextInDevelopment: true,
                logger: logger,
                secretDescription: "SQL Server WindowsAuth AD password")
                ?? throw new SecurityException("The SQL Server WindowsAuth password could not be resolved (fail-closed).");

            if (resolvedPassword.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                throw new SecurityException("The resolved SQL Server WindowsAuth password contains invalid newline characters.");
            }

            ntlmFilePath = MaterializeNtlmUserFile(domain ?? string.Empty, username, resolvedPassword, gssStateDirectory);
        }
        else if (!string.IsNullOrWhiteSpace(ntlmFilePath))
        {
            ValidateNtlmUserFile(ntlmFilePath, logger, out var fileDomain, out var fileUser, out var filePassword);
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = fileDomain;
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                username = fileUser;
            }

            resolvedPassword = filePassword;
        }
        else
        {
            throw new SecurityException(
                "SQL Server WindowsAuth (NTLM) requires either (Domain, Username, PasswordRef) or an existing NTLM_USER_FILE / NtlmUserFilePath.");
        }

        if (ManagedNtlmPolicy.Resolve(winAuth.UseManagedNtlm, getEnvironmentVariable) && !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(resolvedPassword))
        {
            EnableManagedNtlmSwitch();
            sspiContextProvider = new ManagedNtlmSspiContextProvider(
                new NetworkCredential(username, resolvedPassword, domain ?? string.Empty),
                winAuth.ServicePrincipalName,
                package: "NTLM");
        }

        lock (SyncLock)
        {
            setEnvironmentVariable("NTLM_USER_FILE", Path.GetFullPath(ntlmFilePath));
            if (!string.IsNullOrWhiteSpace(username))
            {
                // gss-ntlmssp looks up getenv("NTLMUSER") (falling back to getenv("USER")) when SqlClient calls
                // gss_init_sec_context with GSS_C_NO_CREDENTIAL in a non-root container where UID 10001 has no matching /etc/passwd user.
                setEnvironmentVariable("NTLMUSER", username);
            }

            if (!string.IsNullOrWhiteSpace(domain))
            {
                setEnvironmentVariable("NETBIOS_DOMAIN_NAME", domain);
            }
        }
    }

    /// <summary>
    /// Materializes or updates a restricted (<c>0600</c>) <c>NTLM_USER_FILE</c> entry (<c>DOMAIN:USER:PASSWORD</c>)
    /// in the private GSS state directory so <c>gss-ntlmssp</c> can authenticate multiple AD accounts or a single service account.
    /// </summary>
    public static string MaterializeNtlmUserFile(string domain, string username, string password, string? gssStateDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (domain.AsSpan().IndexOfAny(':', '\r', '\n') >= 0)
        {
            throw new ArgumentException("Domain name must not contain ':' or newline characters.", nameof(domain));
        }

        if (username.AsSpan().IndexOfAny(':', '\r', '\n') >= 0)
        {
            throw new ArgumentException("Username must not contain ':' or newline characters.", nameof(username));
        }

        string targetDir = ResolveWritableGssDirectory(gssStateDirectory);
        string filePath = Path.Combine(targetDir, DefaultGeneratedNtlmFileName);
        string entryKey = $"{domain}:{username}:";
        string newLine = $"{domain}:{username}:{password}";

        lock (SyncLock)
        {
            Directory.CreateDirectory(targetDir);
            TrySetUnixDirectoryPermissions(targetDir);

            var lines = new List<string>();
            if (File.Exists(filePath))
            {
                foreach (var existing in File.ReadAllLines(filePath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(existing))
                    {
                        continue;
                    }

                    if (!existing.StartsWith(entryKey, StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add(existing);
                    }
                }
            }

            lines.Add(newLine);
            string content = string.Join('\n', lines) + "\n";

            // Write atomically via temp file in the same directory with 0600 permissions on Unix
            string tempPath = Path.Combine(targetDir, $".{DefaultGeneratedNtlmFileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            TrySetUnixFilePermissions(tempPath);
            File.Move(tempPath, filePath, overwrite: true);
            TrySetUnixFilePermissions(filePath);
        }

        return filePath;
    }

    /// <summary>
    /// Validates that an <c>NTLM_USER_FILE</c> exists, is non-empty, and has valid <c>DOMAIN:USER:PASSWORD</c> lines.
    /// Never includes file contents or passwords in exception messages or logs.
    /// </summary>
    public static void ValidateNtlmUserFile(string filePath, ILogger? logger = null) =>
        ValidateNtlmUserFile(filePath, logger, out _, out _, out _);

    public static void ValidateNtlmUserFile(
        string filePath,
        ILogger? logger,
        out string? firstDomain,
        out string? firstUsername) =>
        ValidateNtlmUserFile(filePath, logger, out firstDomain, out firstUsername, out _);

    public static void ValidateNtlmUserFile(
        string filePath,
        ILogger? logger,
        out string? firstDomain,
        out string? firstUsername,
        out string? firstPassword)
    {
        firstDomain = null;
        firstUsername = null;
        firstPassword = null;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new SecurityException("NTLM_USER_FILE path is empty.");
        }

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new SecurityException($"Configured NTLM_USER_FILE '{fullPath}' does not exist.");
        }

        CheckRestrictedFilePermissions(fullPath, "NTLM_USER_FILE", logger);

        string[] lines = File.ReadAllLines(fullPath, Encoding.UTF8);
        int validEntries = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            // Expected gss-ntlmssp format: DOMAIN:USER:PASSWORD (password itself may contain colons)
            int firstColon = line.IndexOf(':', StringComparison.Ordinal);
            int secondColon = firstColon >= 0 ? line.IndexOf(':', firstColon + 1) : -1;
            if (firstColon < 0 || secondColon <= firstColon + 1 || secondColon == line.Length - 1)
            {
                throw new SecurityException(
                    $"Invalid format in NTLM_USER_FILE '{fullPath}' on line {i + 1}: expected 'DOMAIN:USER:PASSWORD' with non-empty user and password.");
            }

            string domainPart = line[..firstColon].Trim();
            string userPart = line[(firstColon + 1)..secondColon].Trim();
            string passwordPart = line[(secondColon + 1)..];
            if (string.IsNullOrWhiteSpace(userPart))
            {
                throw new SecurityException(
                    $"Invalid format in NTLM_USER_FILE '{fullPath}' on line {i + 1}: username segment in 'DOMAIN:USER:PASSWORD' must not be empty.");
            }

            if (validEntries == 0)
            {
                firstDomain = domainPart;
                firstUsername = userPart;
                firstPassword = passwordPart;
            }

            validEntries++;
        }

        if (validEntries == 0)
        {
            throw new SecurityException($"NTLM_USER_FILE '{fullPath}' contains no valid 'DOMAIN:USER:PASSWORD' entries.");
        }
    }

    /// <summary>
    /// Names the authentication path a data source uses: <c>SqlPassword</c>, <c>Integrated-ManagedNtlm</c>,
    /// <c>Integrated-GSSAPI</c> (gss-ntlmssp / ambient GSSAPI) or <c>Integrated-Kerberos</c>. Never throws, never reads secrets.
    /// </summary>
    public static string DescribeAuthPath(DataSourceConnectionOptions options, Func<string, string?>? getEnvVar = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var getEnvironmentVariable = getEnvVar ?? Environment.GetEnvironmentVariable;
        var winAuth = options.WindowsAuth;
        if (winAuth is { Mode: not SqlServerWindowsAuthMode.None })
        {
            if (ResolveEffectiveMode(winAuth, getEnvironmentVariable) == SqlServerWindowsAuthMode.Kerberos)
            {
                return "Integrated-Kerberos";
            }

            return ManagedNtlmPolicy.Resolve(winAuth.UseManagedNtlm, getEnvironmentVariable)
                ? "Integrated-ManagedNtlm"
                : "Integrated-GSSAPI";
        }

        bool integrated = false;
        try
        {
            integrated = new SqlConnectionStringBuilder(options.ConnectionString).IntegratedSecurity;
        }
        catch (ArgumentException)
        {
            // malformed connection string: treated as not integrated
        }

        if (!integrated)
        {
            return "SqlPassword";
        }

        return !string.IsNullOrWhiteSpace(getEnvironmentVariable("NTLM_USER_FILE")) && ManagedNtlmPolicy.Resolve(null, getEnvironmentVariable)
            ? "Integrated-ManagedNtlm"
            : "Integrated-GSSAPI";
    }

    /// <summary>
    /// Classifies an exception thrown during <see cref="SqlConnection.OpenAsync(CancellationToken)"/> when <c>Integrated Security=true</c>
    /// is active and wraps it in a <see cref="SqlServerWindowsAuthException"/> with a clear, actionable, credential-free message.
    /// </summary>
    /// <param name="managedNtlmUsed">True when the connection used the managed NTLM <see cref="SspiContextProvider"/>.</param>
    /// <param name="managedNtlmFailure">The failure recorded by that provider, when any.</param>
    public static Exception ClassifyOpenException(
        Exception exception,
        DataSourceConnectionOptions options,
        Func<string, string?>? getEnvVar = null,
        bool managedNtlmUsed = false,
        ManagedNtlmFailure? managedNtlmFailure = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(options);

        bool isIntegrated = options.WindowsAuth is { Mode: not SqlServerWindowsAuthMode.None };
        if (!isIntegrated && !string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            try
            {
                isIntegrated = new SqlConnectionStringBuilder(options.ConnectionString).IntegratedSecurity;
            }
            catch
            {
                // Ignore malformed connection string here
            }
        }

        if (!isIntegrated)
        {
            return exception;
        }

        var getEnvironmentVariable = getEnvVar ?? Environment.GetEnvironmentVariable;
        string msg = FullExceptionText(exception);
        var sqlEx = FindSqlException(exception);
        int? sqlNumber = sqlEx?.Number;
        int? sqlState = sqlEx is null ? null : sqlEx.State;
        int? sqlClass = sqlEx is null ? null : sqlEx.Class;

        // 1. Missing GSSAPI / gss-ntlmssp native library or mechanism
        if (msg.Contains("libgssapi_krb5", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("gssntlmssp", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("GSSAPI operation failed", StringComparison.OrdinalIgnoreCase) &&
            (msg.Contains("No credentials were supplied", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("An unsupported mechanism was requested", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("Mechanismitched", StringComparison.OrdinalIgnoreCase)))
        {
            string ntlmFile = getEnvironmentVariable("NTLM_USER_FILE") ?? options.WindowsAuth?.NtlmUserFilePath ?? "<not set>";
            return new SqlServerWindowsAuthException(
                SqlServerWindowsAuthFailureCategory.GssapiUnavailable,
                "SQL Server Windows Authentication (GSSAPI/NTLM) failed to acquire client credentials in container/Linux environment. " +
                $"Ensure 'libgssapi-krb5-2' and 'gss-ntlmssp' are installed, '{GssMechConfigPath}' is readable by the container user, " +
                $"and NTLM credentials are provided via NTLM_USER_FILE ('{ntlmFile}', format 'DOMAIN:USER:PASSWORD') or DataSources:Connections:<name>:WindowsAuth.",
                exception,
                sqlNumber, sqlState, sqlClass);
        }

        // 2. SSPI context failure. With managed NTLM this is an NTLM problem (the managed provider returned false), not Kerberos/SPN.
        bool sspiContextFailure = msg.Contains("Cannot generate SSPI context", StringComparison.OrdinalIgnoreCase);
        if (managedNtlmUsed && sspiContextFailure)
        {
            string detail = managedNtlmFailure is null
                ? "no failure details were recorded"
                : $"step '{managedNtlmFailure.Step}'" +
                  (managedNtlmFailure.StatusCode is not null ? $", NegotiateAuthentication status '{managedNtlmFailure.StatusCode}'" : string.Empty) +
                  (managedNtlmFailure.ExceptionType is not null ? $", exception '{managedNtlmFailure.ExceptionType}'" : string.Empty);
            return new SqlServerWindowsAuthException(
                SqlServerWindowsAuthFailureCategory.NtlmNegotiation,
                $"SQL Server Windows Authentication (managed NTLM) failed during NTLM negotiation ({detail}). " +
                "This is not a Kerberos/SPN problem. Verify the domain, username and password secret, that the SQL Server accepts NTLM from this host, " +
                "or set WindowsAuth.UseManagedNtlm=false to use gss-ntlmssp instead.",
                exception,
                sqlNumber, sqlState, sqlClass,
                managedNtlmFailure?.StatusCode,
                managedNtlmFailure?.Step,
                managedNtlmFailure?.ExceptionType);
        }

        // 2b. Kerberos SPN / KDC failure
        if (sspiContextFailure ||
            msg.Contains("Server not found in Kerberos database", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("KRB5", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("SPN", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlServerWindowsAuthException(
                SqlServerWindowsAuthFailureCategory.KerberosNegotiation,
                "SQL Server Windows Authentication failed during SSPI/Kerberos negotiation ('Cannot generate SSPI context' / SPN error). " +
                "Verify that the SQL Server Service Principal Name (MSSQLSvc/<fqdn>:<port>@REALM) is registered in Active Directory, " +
                "or use NTLM authentication via 'System.Net.Security.UseManagedNtlm' / 'gss-ntlmssp' (WindowsAuth.Mode = 'Ntlm').",
                exception,
                sqlNumber, sqlState, sqlClass);
        }

        // 3. SQL Server Login failed (Error 18456 / 18452) - warn about AD account lockout!
        if (sqlNumber is 18456 or 18452 ||
            msg.Contains("Login failed for user", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("untrusted domain", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlServerWindowsAuthException(
                SqlServerWindowsAuthFailureCategory.LoginRejected,
                "SQL Server Windows Authentication login was rejected by the server (Error 18456/18452). " +
                "WARNING: Repeated failed Windows authentication attempts can lock out the Active Directory service account! " +
                "Verify the domain name, username, password secret, and that the AD account has CONNECT/LOGIN permission on the target SQL Server before retrying.",
                exception,
                sqlNumber, sqlState, sqlClass);
        }

        return exception;
    }

    private static SqlException? FindSqlException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql;
            }
        }

        return null;
    }

    public static SqlServerWindowsAuthMode ResolveEffectiveMode(
        SqlServerWindowsAuthOptions winAuth,
        Func<string, string?>? getEnvVar = null)
    {
        ArgumentNullException.ThrowIfNull(winAuth);
        if (winAuth.Mode != SqlServerWindowsAuthMode.Auto)
        {
            return winAuth.Mode;
        }

        var getEnvironmentVariable = getEnvVar ?? Environment.GetEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(winAuth.KerberosKeytabPath) ||
            !string.IsNullOrWhiteSpace(winAuth.KerberosCachePath) ||
            (!string.IsNullOrWhiteSpace(winAuth.KerberosConfigPath) &&
             string.IsNullOrWhiteSpace(winAuth.PasswordRef) &&
             string.IsNullOrWhiteSpace(winAuth.NtlmUserFilePath) &&
             string.IsNullOrWhiteSpace(getEnvironmentVariable("NTLM_USER_FILE"))))
        {
            return SqlServerWindowsAuthMode.Kerberos;
        }

        return SqlServerWindowsAuthMode.Ntlm;
    }

    public static (string? Domain, string? Username) NormalizeDomainAndUser(string? domain, string? username)
    {
        string? cleanDomain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        string? cleanUser = string.IsNullOrWhiteSpace(username) ? null : username.Trim();

        if (!string.IsNullOrWhiteSpace(cleanUser) && cleanUser.Contains('\\', StringComparison.Ordinal))
        {
            var parts = cleanUser.Split('\\', 2);
            if (string.IsNullOrWhiteSpace(cleanDomain) && !string.IsNullOrWhiteSpace(parts[0]))
            {
                cleanDomain = parts[0].Trim();
            }

            cleanUser = parts[1].Trim();
        }

        return (cleanDomain, cleanUser);
    }

    /// <summary>
    /// Checks whether the Linux GSSAPI NTLM mechanism configuration (<c>/etc/gss/mech.d/mech.ntlmssp.conf</c> or <c>/etc/gss/mech</c>)
    /// is present on disk.
    /// </summary>
    public static bool IsLinuxGssNtlmMechanismPresent() =>
        File.Exists(GssMechConfigPath) || File.Exists(LegacyGssMechPath);

    private static string ResolveWritableGssDirectory(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory);
        }

        try
        {
            if (Directory.Exists("/app/data"))
            {
                return DefaultGssStateDirectory;
            }
        }
        catch
        {
            // Fall back to temp directory
        }

        return Path.Combine(Path.GetTempPath(), $"autheris-gss-{Environment.ProcessId}");
    }

    private static void EnsureFileExists(string path, string description)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new SecurityException($"Configured {description} '{fullPath}' does not exist.");
        }
    }

    private static void CheckRestrictedFilePermissions(string fullPath, string description, ILogger? logger)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(fullPath);
            const UnixFileMode groupOrOtherMask =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

            if ((mode & groupOrOtherMask) != 0)
            {
                logger?.LogWarning(
                    "Security warning: {Description} '{FilePath}' has overly permissive Unix file mode ({FileMode}). Recommended permissions are 0400 or 0600.",
                    description,
                    fullPath,
                    mode);
            }
        }
        catch
        {
            // Ignore on filesystems that do not support UnixFileMode
        }
    }

    private static void TrySetUnixDirectoryPermissions(string directoryPath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                directoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // Best-effort on mounted volumes
        }
    }

    private static void TrySetUnixFilePermissions(string filePath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort on mounted volumes
        }
    }

    private static string FullExceptionText(Exception ex)
    {
        var sb = new StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            sb.Append(current.Message).Append(' ');
        }

        return sb.ToString();
    }
}

/// <summary>
/// Issue #102: Custom <see cref="SspiContextProvider"/> for <see cref="SqlConnection"/> that uses .NET's
/// <see cref="NegotiateAuthentication"/> with explicit per-datasource <see cref="NetworkCredential"/> and
/// <c>System.Net.Security.UseManagedNtlm = true</c>.
/// </summary>
public sealed class ManagedNtlmSspiContextProvider : SspiContextProvider, IDisposable
{
    private readonly NetworkCredential _credential;
    private readonly string? _targetSpnOverride;
    private readonly string _package;
    private NegotiateAuthentication? _negotiateAuth;
    private ManagedNtlmFailure? _lastFailure;

    public ManagedNtlmSspiContextProvider(
        NetworkCredential credential,
        string? targetSpnOverride = null,
        string package = "NTLM")
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
        _targetSpnOverride = targetSpnOverride;
        _package = string.IsNullOrWhiteSpace(package) ? "NTLM" : package;
        SqlServerWindowsAuthConfigurator.EnableManagedNtlmSwitch();
    }

    /// <summary>The most recent failure of <see cref="TryGenerateContext"/> (cleared when a new negotiation starts); never contains secrets.</summary>
    public ManagedNtlmFailure? LastFailure => Volatile.Read(ref _lastFailure);

    protected override bool GenerateContext(
        ReadOnlySpan<byte> incomingBlob,
        IBufferWriter<byte> outgoingBlobWriter,
        SspiAuthenticationParameters authParams) =>
        TryGenerateContext(incomingBlob, outgoingBlobWriter, authParams.Resource);

    /// <summary>
    /// Performs one negotiation step. An empty <paramref name="incomingBlob"/> starts a new negotiation (negotiate message);
    /// a non-empty one is the server challenge. Failures are recorded in <see cref="LastFailure"/> instead of being swallowed.
    /// </summary>
    public bool TryGenerateContext(ReadOnlySpan<byte> incomingBlob, IBufferWriter<byte> outgoingBlobWriter, string? resource)
    {
        string step = incomingBlob.IsEmpty ? ManagedNtlmFailure.NegotiateStep : ManagedNtlmFailure.ChallengeStep;
        try
        {
            if (incomingBlob.IsEmpty)
            {
                Volatile.Write(ref _lastFailure, null);
                _negotiateAuth?.Dispose();
                string? targetName = !string.IsNullOrWhiteSpace(_targetSpnOverride) ? _targetSpnOverride : resource;

                _negotiateAuth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
                {
                    Package = _package,
                    Credential = _credential,
                    TargetName = targetName
                });
            }

            if (_negotiateAuth is null)
            {
                Record(step, null, "InvalidOperationException", "No negotiation was started before the server challenge.");
                return false;
            }

            byte[]? sendBuff = _negotiateAuth.GetOutgoingBlob(incomingBlob, out var statusCode);
            if (statusCode is not NegotiateAuthenticationStatusCode.Completed and not NegotiateAuthenticationStatusCode.ContinueNeeded)
            {
                Record(step, statusCode.ToString(), null, null);
                return false;
            }

            if (sendBuff is { Length: > 0 })
            {
                outgoingBlobWriter.Write(sendBuff);
            }

            return true;
        }
        catch (Exception ex)
        {
            // NegotiateAuthentication messages carry no credentials; the type and a bounded message are kept for diagnostics.
            string message = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            Record(step, null, ex.GetType().Name, message);
            return false;
        }
    }

    private void Record(string step, string? statusCode, string? exceptionType, string? exceptionMessage) =>
        Volatile.Write(ref _lastFailure, new ManagedNtlmFailure(step, statusCode, exceptionType, exceptionMessage));

    public void Dispose()
    {
        _negotiateAuth?.Dispose();
        _negotiateAuth = null;
    }
}

/// <summary>Why a managed NTLM negotiation step failed. Contains no credentials.</summary>
/// <param name="Step"><c>negotiate</c> or <c>challenge</c>.</param>
/// <param name="StatusCode">Name of the <see cref="NegotiateAuthenticationStatusCode"/>, when the step returned one.</param>
/// <param name="ExceptionType">Exception type name, when the step threw.</param>
/// <param name="ExceptionMessage">Bounded exception message, when the step threw.</param>
public sealed record ManagedNtlmFailure(string Step, string? StatusCode, string? ExceptionType, string? ExceptionMessage)
{
    public const string NegotiateStep = "negotiate";
    public const string ChallengeStep = "challenge";
}
