namespace Autheris.Application.Sql.Services;

using System;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>
/// SEC-ADG-23, X1.2: the keyed links between an audit record and the statement. Two values, each under its own HKDF sub-key (own info
/// string) of the audit key, so neither can be brute-forced without the key and neither contains a value:
/// <list type="bullet">
/// <item><c>sql.compiled_digest = HMAC-SHA256(key, CompilerVersion || Dialect || CompiledSql.Sql || parameter shape)</c>
/// (<see cref="CompiledSqlDigest"/>), the statement that actually executed. The shape is (ordinal, type, origin) only.</item>
/// <item><c>originalSqlHash = HMAC-SHA256(key, redacted original text)</c> (M-A3), the statement as the client wrote it, hashed over the text
/// with every literal replaced by <c>@p_redacted</c>: two statements that differ only in their values share the hash, and no value is derivable.</item>
/// </list>
/// The audit key is resolved like the audit hash chain of the repositories (L-A1): <c>GovernanceDb:AuditHmacKeyVaultRef</c> first, otherwise the
/// <c>Autheris:AuditChain:v1</c> HKDF key of <c>DataMasking:HmacSecretKeyVaultRef</c>; the minimum length is enforced outside Development. Outside
/// Development and Test there is no ephemeral fallback: a missing or unreadable key makes <see cref="Compute"/> and <see cref="Anonymize"/> throw,
/// so the governed statement fails closed (no audit record without a verifiable link). The key is read once and cached; instances are registered as
/// singletons, so there is no vault read per request.
/// </summary>
public sealed class CompiledSqlAuditDigester
{
    private static readonly byte[] DigestKeyInfo = "Autheris:SqlCompiledDigest:v1"u8.ToArray();
    private static readonly byte[] OriginalHashKeyInfo = "Autheris:SqlOriginalHash:v1"u8.ToArray();
    private static readonly byte[] AuditChainInfo = "Autheris:AuditChain:v1"u8.ToArray();
    private static readonly Lazy<byte[]> EphemeralKey = new(static () => RandomNumberGenerator.GetBytes(32));

    private readonly IOptions<GatewayOptions> _options;
    private readonly IKeyVaultSecretProvider? _secrets;
    private readonly IHostEnvironment? _environment;
    private readonly object _gate = new();
    private volatile Keys? _keys;

    private sealed record Keys(byte[] Digest, byte[] OriginalHash);

    public CompiledSqlAuditDigester(IOptions<GatewayOptions> options, IKeyVaultSecretProvider? secretProvider = null, IHostEnvironment? environment = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secrets = secretProvider;
        _environment = environment;
    }

    /// <summary>The HKDF sub-key of the compiled-statement digest for an audit key (exposed for the tests that recompute the digest).</summary>
    public static byte[] DeriveKey(byte[] auditKey)
    {
        ArgumentNullException.ThrowIfNull(auditKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, auditKey, 32, info: DigestKeyInfo);
    }

    /// <summary>The audit key that the audit hash chain derives from a master secret (same HKDF info as the audit repositories).</summary>
    public static byte[] DeriveAuditKeyFromMaster(byte[] masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: AuditChainInfo);
    }

    /// <exception cref="InvalidOperationException">No usable audit key outside Development and Test (fail closed).</exception>
    public string Compute(CompiledSql compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        return CompiledSqlDigest.Compute(Resolved().Digest, compiled);
    }

    /// <summary>
    /// The audit text of a client statement: every literal replaced by <c>@p_redacted</c> (cut to <paramref name="maxLength"/>), and the keyed
    /// hash over the full redacted text. Never a hash over the raw text.
    /// </summary>
    /// <exception cref="InvalidOperationException">No usable audit key outside Development and Test (fail closed).</exception>
    public (string RedactedSql, string OriginalSqlHash) Anonymize(string? sql, int maxLength = AstSecurityVisitor.DefaultMaxAuditSqlLength)
    {
        var key = Resolved().OriginalHash;
        var redacted = AstSecurityVisitor.RedactSqlLiterals(sql);
        var hash = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(redacted)));
        return (AstSecurityVisitor.TruncateForAudit(redacted, maxLength), hash);
    }

    private Keys Resolved()
    {
        if (_keys is { } cached)
        {
            return cached;
        }

        lock (_gate)
        {
            // A failure is not cached: nothing is derived from a key that could not be read, and the next call asks again.
            return _keys ??= Derive(ResolveAuditKey());
        }
    }

    private static Keys Derive(byte[] auditKey) => new(
        HKDF.DeriveKey(HashAlgorithmName.SHA256, auditKey, 32, info: DigestKeyInfo),
        HKDF.DeriveKey(HashAlgorithmName.SHA256, auditKey, 32, info: OriginalHashKeyInfo));

    private byte[] ResolveAuditKey()
    {
        var envName = _environment?.EnvironmentName ??
                      Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        bool isDev = string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase);
        bool isDevOrTest = isDev || string.Equals(envName, "Test", StringComparison.OrdinalIgnoreCase);

        byte[]? key = null;
        var auditRef = _options.Value.GovernanceDb?.AuditHmacKeyVaultRef;
        if (_secrets is not null && !string.IsNullOrWhiteSpace(auditRef))
        {
            try
            {
                key = _secrets.GetSecretBytes(auditRef);
            }
            catch (Exception)
            {
                if (!isDev)
                {
                    throw new InvalidOperationException("Security critical: Failed to load AuditHmacKeyVaultRef from Key Vault in non-development environment (SQL audit digest).");
                }
            }
        }

        if (key is { Length: > 0 })
        {
            SecretKeyRequirements.EnsureMinimumLength(key, "The audit HMAC key (AuditHmacKeyVaultRef)", isDevOrTest);
            return key;
        }

        var masterRef = _options.Value.DataMasking?.HmacSecretKeyVaultRef;
        if (_secrets is not null && !string.IsNullOrWhiteSpace(masterRef))
        {
            byte[]? master = null;
            try
            {
                master = _secrets.GetSecretBytes(masterRef);
            }
            catch (Exception)
            {
                // handled below: outside Development and Test a missing key is fatal for the statement
            }

            if (master is { Length: > 0 })
            {
                SecretKeyRequirements.EnsureMinimumLength(master, "The HMAC master key (HmacSecretKeyVaultRef)", isDevOrTest);
                return DeriveAuditKeyFromMaster(master);
            }
        }

        if (!isDevOrTest)
        {
            throw new InvalidOperationException(
                "Security critical: No AuditHmacKeyVaultRef or HmacSecretKeyVaultRef is usable in a non-development environment. The SQL audit digest cannot be keyed, so the statement is refused.");
        }

        return EphemeralKey.Value;
    }
}
