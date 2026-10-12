namespace Autheris.Application.Sql.Services;

using System;
using System.Security.Cryptography;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// SEC-ADG-23, X1.2: the tamper-evident link between an audit record and the statement that actually executed,
/// <c>sql.compiled_digest = HMAC-SHA256(auditKey, CompilerVersion || Dialect || CompiledSql.Sql || parameter shape)</c>
/// (<see cref="CompiledSqlDigest"/>). The shape is (ordinal, type, origin) only, so the digest never contains a value or a value hash.
/// The key is an HKDF sub-key of the gateway HMAC master secret (the one that anchors the audit hash chain), separated from the
/// masking and chain keys by its own info string. Without a configured master secret (Development and tests; the audit repositories
/// refuse to start without one elsewhere) a process-ephemeral key is used.
/// </summary>
public sealed class CompiledSqlAuditDigester
{
    private static readonly byte[] KeyInfo = "Autheris:SqlCompiledDigest:v1"u8.ToArray();
    private static readonly Lazy<byte[]> EphemeralKey = new(static () => RandomNumberGenerator.GetBytes(32));

    private readonly IOptions<GatewayOptions> _options;
    private readonly IKeyVaultSecretProvider? _secrets;
    private readonly object _gate = new();
    private byte[]? _key;

    public CompiledSqlAuditDigester(IOptions<GatewayOptions> options, IKeyVaultSecretProvider? secretProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secrets = secretProvider;
    }

    /// <summary>The HKDF sub-key of a master secret (exposed for the tests that recompute the digest).</summary>
    public static byte[] DeriveKey(byte[] masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: KeyInfo);
    }

    public string Compute(CompiledSql compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        return CompiledSqlDigest.Compute(Key(), compiled);
    }

    private byte[] Key()
    {
        if (_key is { } cached)
        {
            return cached;
        }

        lock (_gate)
        {
            return _key ??= Resolve();
        }
    }

    private byte[] Resolve()
    {
        string? secretRef = _options.Value.DataMasking?.HmacSecretKeyVaultRef;
        if (_secrets is not null && !string.IsNullOrWhiteSpace(secretRef))
        {
            try
            {
                var master = _secrets.GetSecretBytes(secretRef);
                if (master is { Length: > 0 })
                {
                    return DeriveKey(master);
                }
            }
            catch (Exception)
            {
                // fall through to the process key: an audit digest must never make a governed statement unanswerable
            }
        }

        return EphemeralKey.Value;
    }
}
