using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security;
using System.Security.Authentication;
using System.Security.Authentication.ExtendedProtection;
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

        // Process-wide identity guard (only for the real native environment; injected setters are test doubles).
        var registry = setEnvVar is null ? GssIdentityRegistry.Shared : null;
        if (registry is not null)
        {
            if (sspiContextProvider is null && !string.IsNullOrWhiteSpace(username))
            {
                registry.Claim(domain, username);
            }
            else if (sspiContextProvider is not null && registry.HasClaim)
            {
                // Managed NTLM keeps credentials per connection; never overwrite the identity a GSS source relies on.
                return;
            }
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
            int matching = 0;
            bool entryUnchanged = false;
            if (File.Exists(filePath))
            {
                foreach (var existing in File.ReadAllLines(filePath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(existing))
                    {
                        continue;
                    }

                    if (existing.StartsWith(entryKey, StringComparison.OrdinalIgnoreCase))
                    {
                        matching++;
                        entryUnchanged = string.Equals(existing, newLine, StringComparison.Ordinal);
                    }
                    else
                    {
                        lines.Add(existing);
                    }
                }
            }

            entryUnchanged &= matching == 1;
            lines.Add(newLine);
            string content = string.Join('\n', lines) + "\n";

            // Every connection open prepares the file; when the entry is already present and unchanged the file is not rewritten.
            if (entryUnchanged)
            {
                return filePath;
            }

            // Write atomically via temp file in the same directory with 0600 permissions on Unix
            string tempPath = Path.Combine(targetDir, $".{DefaultGeneratedNtlmFileName}.{Guid.NewGuid():N}.tmp");
            // The file is created with 0600 from the start (no window with umask permissions around the plaintext password).
            var createOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                createOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(tempPath, createOptions))
            {
                stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
            }

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
        var sqlDiag = ExtractSqlErrorDiagnostics(exception);
        int? sqlNumber = sqlDiag?.Number;
        int? sqlState = sqlDiag?.State;
        int? sqlClass = sqlDiag?.Class;

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

        // 3. SQL Server Login failed (Error 18452 / untrusted domain) - EPA Channel-Binding Token (CBT) / untrusted domain mismatch
        if (sqlNumber == 18452 ||
            (sqlNumber is null or not 18456 && msg.Contains("untrusted domain", StringComparison.OrdinalIgnoreCase)))
        {
            _ = SecretScrubber.Redact(msg.Trim());
            string diagnosticAdvice =
                "SQL Server Windows Authentication login failed with untrusted domain / EPA error (Error 18452). " +
                "In Linux container environments, managed NTLM requires TLS Channel-Binding Tokens (CBT) to satisfy Extended Protection. " +
                "If Microsoft.Data.SqlClient did not provide TLS Channel Binding and the target SQL Server enforces Extended Protection for Authentication (EPA / 'Erweiterter Schutz' = Required, or Allowed over TLS), " +
                "connections are rejected with Error 18452. Autheris extracts TLS session channel bindings from the underlying connection SslStream; " +
                "if channel binding extraction was unavailable (e.g. unencrypted connection or reflection mismatch), EPA rejection occurs. Actionable remediations: " +
                "(1) Verify that TLS encryption is active ('Encrypt=True' or 'Mandatory') so a TLS Channel-Binding Token is generated; " +
                "(2) Configure SQL Server Extended Protection (EPA) to 'Allowed' or 'Off' if channel binding cannot be passed; " +
                "(3) Use Kerberos authentication with a Keytab (F-AUTH-04); " +
                "(4) Use SQL Server native authentication (SqlPassword). " +
                "WARNING: Repeated failed authentication attempts can lock out the Active Directory account!";

            return new SqlServerWindowsAuthException(
                SqlServerWindowsAuthFailureCategory.LoginRejected,
                diagnosticAdvice,
                exception,
                sqlNumber ?? 18452, sqlState, sqlClass);
        }

        // 3b. SQL Server Login failed (Error 18456) - Bad credentials or login disabled
        if (sqlNumber == 18456 ||
            msg.Contains("Login failed for user", StringComparison.OrdinalIgnoreCase))
        {
            _ = SecretScrubber.Redact(msg.Trim());
            string diagnosticAdvice =
                "SQL Server Windows Authentication login was rejected by the server (Error 18456). " +
                "WARNING: Repeated failed Windows authentication attempts can lock out the Active Directory service account! " +
                "Verify the domain name, username, password secret, and that the AD account has CONNECT/LOGIN permission on the target SQL Server before retrying.";

            return new SqlServerWindowsAuthException(
                SqlServerWindowsAuthFailureCategory.LoginRejected,
                diagnosticAdvice,
                exception,
                sqlNumber ?? 18456, sqlState, sqlClass);
        }

        return exception;
    }

    private static (int? Number, int? State, int? Class, string Message)? ExtractSqlErrorDiagnostics(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                int num = sql.Number;
                int state = sql.State;
                int cls = sql.Class;
                if (sql.Errors.Count > 0 && sql.Errors[0] != null)
                {
                    num = sql.Errors[0].Number;
                    state = sql.Errors[0].State;
                    cls = sql.Errors[0].Class;
                }

                return (num, state, cls, sql.Message);
            }

            // Duck typing for test doubles and provider exceptions
            var type = current.GetType();
            var numProp = type.GetProperty("Number");
            if (numProp != null && numProp.PropertyType == typeof(int))
            {
                int num = (int)numProp.GetValue(current)!;
                int? state = type.GetProperty("State")?.GetValue(current) as int?
                    ?? (type.GetProperty("State")?.GetValue(current) is byte b ? (int)b : null);
                int? cls = type.GetProperty("Class")?.GetValue(current) as int?
                    ?? (type.GetProperty("Class")?.GetValue(current) is byte cb ? (int)cb : null);
                return (num, state, cls, current.Message);
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
/// Issue #230: Extracts the TLS session channel binding token (CBT) from Microsoft.Data.SqlClient's
/// internal managed SNI SslStream so .NET's <see cref="NegotiateAuthentication"/> can populate
/// the NTLM-AUTHENTICATE message with MsvAvChannelBindings (AV pair 10) under Extended Protection for Authentication (EPA).
/// </summary>
public static class SqlClientChannelBindingExtractor
{
    public static bool IsSupportedSqlClientVersion(Assembly? assembly)
    {
        if (assembly is null) return false;
        var version = assembly.GetName().Version;
        return version is not null && version.Major >= 7;
    }

    public static ChannelBindingExtractionResult ExtractChannelBinding(
        SspiContextProvider? provider,
        ILogger? logger = null)
    {
        if (provider is null)
        {
            return new ChannelBindingExtractionResult(null, null, "SspiContextProvider was null.", false, null);
        }

        var assembly = typeof(SspiContextProvider).Assembly;
        bool supportedVersion = IsSupportedSqlClientVersion(assembly);
        if (!supportedVersion)
        {
            logger?.LogWarning(
                "Microsoft.Data.SqlClient assembly version '{Version}' is not verified for managed NTLM TLS channel binding extraction. Attempting best-effort reflection.",
                assembly.GetName().Version);
        }

        try
        {
            // 1. Traverse SspiContextProvider._physicalStateObj
            var stateObjField = FindField(provider.GetType(), "_physicalStateObj")
                ?? FindField(typeof(SspiContextProvider), "_physicalStateObj");
            var stateObj = stateObjField?.GetValue(provider);
            if (stateObj is null)
            {
                return new ChannelBindingExtractionResult(
                    null, null, "SspiContextProvider._physicalStateObj was not initialized or null.", false, null);
            }

            // 2. Traverse TdsParserStateObjectManaged._sessionHandle (or SessionHandle property)
            var handle = FindField(stateObj.GetType(), "_sessionHandle")?.GetValue(stateObj)
                ?? FindProperty(stateObj.GetType(), "SessionHandle")?.GetValue(stateObj);

            // In test doubles, stateObj itself might expose _sslStream
            if (handle is null && (FindField(stateObj.GetType(), "_sslStream") != null || FindProperty(stateObj.GetType(), "SslStream") != null))
            {
                handle = stateObj;
            }

            if (handle is null)
            {
                return new ChannelBindingExtractionResult(
                    null, null, $"No session handle found on state object of type '{stateObj.GetType().FullName}'.", false, null);
            }

            // 3. If MARS handle (SniMarsHandle), unpack connection lower handle
            if (handle.GetType().Name.Contains("Mars", StringComparison.OrdinalIgnoreCase))
            {
                var conn = FindField(handle.GetType(), "_connection")?.GetValue(handle);
                if (conn is not null)
                {
                    var lower = FindField(conn.GetType(), "_lowerHandle")?.GetValue(conn);
                    if (lower is not null)
                    {
                        handle = lower;
                    }
                }
            }

            // Check if handle itself has _lowerHandle
            var directLower = FindField(handle.GetType(), "_lowerHandle")?.GetValue(handle);
            if (directLower is not null)
            {
                handle = directLower;
            }

            // 4. Retrieve _sslStream from handle
            var sslStreamObj = FindField(handle.GetType(), "_sslStream")?.GetValue(handle)
                ?? FindProperty(handle.GetType(), "SslStream")?.GetValue(handle);

            if (sslStreamObj is null)
            {
                return new ChannelBindingExtractionResult(
                    null, null, $"No _sslStream found on handle '{handle.GetType().FullName}'. Connection may be unencrypted.", false, null);
            }

            if (sslStreamObj is not SslStream sslStream)
            {
                return new ChannelBindingExtractionResult(
                    null, null, $"Handle _sslStream object '{sslStreamObj.GetType().FullName}' is not an SslStream.", false, null);
            }

            // 5. Query TransportContext from SslStream
            var transportContext = sslStream.TransportContext;
            if (transportContext is null)
            {
                return new ChannelBindingExtractionResult(
                    null, null, "SslStream.TransportContext returned null.", false, null);
            }

            // 6. Query ChannelBinding depending on TLS protocol
            ChannelBinding? binding = null;
            ChannelBindingKind? kindUsed = null;

            // TLS 1.3: tls-server-end-point (RFC 8446 forbids tls-unique)
            if (sslStream.SslProtocol == SslProtocols.Tls13)
            {
                try
                {
                    binding = transportContext.GetChannelBinding(ChannelBindingKind.Endpoint);
                    if (binding is not null && !binding.IsInvalid)
                    {
                        kindUsed = ChannelBindingKind.Endpoint;
                    }
                }
                catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException or InvalidOperationException)
                {
                    // Fallback below
                }
            }

            // TLS <= 1.2: tls-unique preferred
            if (binding is null || binding.IsInvalid)
            {
                try
                {
                    binding = transportContext.GetChannelBinding(ChannelBindingKind.Unique);
                    if (binding is not null && !binding.IsInvalid)
                    {
                        kindUsed = ChannelBindingKind.Unique;
                    }
                }
                catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException or InvalidOperationException)
                {
                    // Fallback to endpoint below
                }
            }

            // Fallback to Endpoint if Unique is null/invalid/unsupported
            if (binding is null || binding.IsInvalid)
            {
                try
                {
                    binding = transportContext.GetChannelBinding(ChannelBindingKind.Endpoint);
                    if (binding is not null && !binding.IsInvalid)
                    {
                        kindUsed = ChannelBindingKind.Endpoint;
                    }
                }
                catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException or InvalidOperationException)
                {
                    // Both kinds failed
                }
            }

            if (binding is null || binding.IsInvalid)
            {
                return new ChannelBindingExtractionResult(
                    null, null, "TransportContext did not provide a valid Endpoint or Unique channel binding.", false, null);
            }

            return new ChannelBindingExtractionResult(
                binding, kindUsed, $"Successfully extracted TLS Channel Binding ({kindUsed}).", true, null);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Unexpected error during managed NTLM TLS channel binding extraction.");
            return new ChannelBindingExtractionResult(
                null, null, $"Reflection error during channel binding extraction: {ex.Message}", false, ex);
        }
    }

    internal static FieldInfo? FindField(Type? type, string fieldName)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            var field = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field is not null) return field;
        }
        return null;
    }

    internal static PropertyInfo? FindProperty(Type? type, string propertyName)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            var prop = t.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (prop is not null) return prop;
        }
        return null;
    }
}

/// <summary>
/// Issue #230: Result of attempting to extract TLS Channel Binding from Microsoft.Data.SqlClient.
/// </summary>
public sealed record ChannelBindingExtractionResult(
    ChannelBinding? Binding,
    ChannelBindingKind? Kind,
    string? StatusMessage,
    bool Success,
    Exception? Error);

/// <summary>
/// Issue #102: Custom <see cref="SspiContextProvider"/> for <see cref="SqlConnection"/> that uses .NET's
/// <see cref="NegotiateAuthentication"/> with explicit per-datasource <see cref="NetworkCredential"/> and
/// <c>System.Net.Security.UseManagedNtlm = true</c>.
/// Issue #230: Under EPA, extracts the TLS session channel binding and populates <see cref="NegotiateAuthenticationClientOptions.Binding"/>.
/// </summary>
public sealed class ManagedNtlmSspiContextProvider : SspiContextProvider, IDisposable
{
    private readonly NetworkCredential _credential;
    private readonly string? _targetSpnOverride;
    private readonly string _package;
    private readonly Func<SspiContextProvider, ChannelBinding?>? _channelBindingAccessor;
    private NegotiateAuthentication? _negotiateAuth;
    private ChannelBinding? _channelBinding;
    private ManagedNtlmFailure? _lastFailure;
    private string? _channelBindingStatus;
    private ChannelBindingKind? _channelBindingKindUsed;

    public ManagedNtlmSspiContextProvider(
        NetworkCredential credential,
        string? targetSpnOverride = null,
        string package = "NTLM",
        Func<SspiContextProvider, ChannelBinding?>? channelBindingAccessor = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
        _targetSpnOverride = targetSpnOverride;
        _package = string.IsNullOrWhiteSpace(package) ? "NTLM" : package;
        _channelBindingAccessor = channelBindingAccessor;
        // SqlClient makes the SspiContextProvider part of the connection pool key and expects a stable identity per resource. A provider is
        // created per open, so equality is by credential identity: equal credentials share a pool, different accounts or passwords never do.
        _identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\u001f', credential.Domain, credential.UserName, credential.Password, targetSpnOverride, _package))));
        SqlServerWindowsAuthConfigurator.EnableManagedNtlmSwitch();
    }

    private readonly string _identity;

    public override bool Equals(object? obj) =>
        obj is ManagedNtlmSspiContextProvider other && string.Equals(_identity, other._identity, StringComparison.Ordinal);

    public override int GetHashCode() => _identity.GetHashCode(StringComparison.Ordinal);

    /// <summary>The most recent failure of <see cref="TryGenerateContext"/> (cleared when a new negotiation starts); never contains secrets.</summary>
    public ManagedNtlmFailure? LastFailure => Volatile.Read(ref _lastFailure);

    /// <summary>Issue #230: Status message describing TLS Channel Binding extraction.</summary>
    public string? ChannelBindingStatus => Volatile.Read(ref _channelBindingStatus);

    /// <summary>Issue #230: The TLS Channel Binding kind used for this session, if acquired.</summary>
    public ChannelBindingKind? ChannelBindingKindUsed => _channelBindingKindUsed;

    /// <summary>Issue #230: Indicates whether a valid TLS Channel Binding was acquired and assigned to NegotiateAuthentication.</summary>
    public bool ChannelBindingAcquired => _channelBindingKindUsed is not null;

    protected override bool GenerateContext(
        ReadOnlySpan<byte> incomingBlob,
        IBufferWriter<byte> outgoingBlobWriter,
        SspiAuthenticationParameters authParams) =>
        TryGenerateContext(incomingBlob, outgoingBlobWriter, authParams.Resource);

    /// <summary>
    /// Performs one negotiation step. An empty <paramref name="incomingBlob"/> starts a new negotiation (negotiate message);
    /// a non-empty one is the server challenge. Failures are recorded in <see cref="LastFailure"/> instead of being swallowed.
    /// Under EPA, the TLS session channel binding is extracted and passed to <see cref="NegotiateAuthenticationClientOptions.Binding"/>.
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
                _negotiateAuth = null;
                _channelBinding?.Dispose();
                _channelBinding = null;
                _channelBindingKindUsed = null;

                string? targetName = !string.IsNullOrWhiteSpace(_targetSpnOverride) ? _targetSpnOverride : resource;

                ChannelBinding? binding = null;
                if (_channelBindingAccessor is not null)
                {
                    binding = _channelBindingAccessor(this);
                    _channelBinding = binding;
                    if (binding is not null && !binding.IsInvalid)
                    {
                        _channelBindingKindUsed = ChannelBindingKind.Endpoint;
                        Volatile.Write(ref _channelBindingStatus, "Channel binding provided via custom accessor.");
                    }
                    else
                    {
                        Volatile.Write(ref _channelBindingStatus, "Custom channel binding accessor returned null or invalid binding.");
                    }
                }
                else
                {
                    var extraction = SqlClientChannelBindingExtractor.ExtractChannelBinding(this);
                    binding = extraction.Binding;
                    _channelBinding = binding;
                    _channelBindingKindUsed = extraction.Kind;
                    Volatile.Write(ref _channelBindingStatus, extraction.StatusMessage);
                }

                _negotiateAuth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
                {
                    Package = _package,
                    Credential = _credential,
                    TargetName = targetName,
                    Binding = binding
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
        _channelBinding?.Dispose();
        _channelBinding = null;
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
