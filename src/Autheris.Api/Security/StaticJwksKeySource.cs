namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

/// <summary>The static JWKS file is unusable (missing, oversize, malformed or it holds a key the gateway refuses). The message never contains key material.</summary>
public sealed class StaticJwksException(string message) : Exception(message);

/// <summary>
/// Signing keys of a GenericOidc realm read from a local JWKS file, with no outbound request. Only public RSA (at least 2048 bits) and
/// EC P-256 / P-384 keys are accepted; private, symmetric and <c>none</c> keys, a missing or duplicate <c>kid</c>, more than
/// <see cref="MaxKeys"/> keys and a file over <see cref="MaxFileBytes"/> fail the load. The file is read at construction (a failure fails
/// startup) and re-read lazily once the reload interval has passed or, at most every <see cref="UnknownKidReloadInterval"/>, for an
/// unknown <c>kid</c>; a failed re-read keeps the last good key set.
/// </summary>
public sealed class StaticJwksKeySource
{
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxKeys = 16;
    public const int MinRsaKeyBits = 2048;
    public static readonly TimeSpan UnknownKidReloadInterval = TimeSpan.FromSeconds(30);

    /// <summary>Signature algorithms a realm with a static JWKS accepts (asymmetric only; never none or HMAC).</summary>
    public static readonly string[] AllowedAlgorithms = ["RS256", "RS384", "RS512", "PS256", "ES256", "ES384"];

    private static readonly string[] PrivateMembers = ["d", "p", "q", "dp", "dq", "qi", "oth", "k"];

    private readonly string _path;
    private readonly TimeSpan _reloadInterval;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private volatile IReadOnlyDictionary<string, SecurityKey[]> _keys;
    private DateTimeOffset _lastReload;
    private DateTimeOffset _lastUnknownKidReload = DateTimeOffset.MinValue;

    public StaticJwksKeySource(string path, TimeSpan reloadInterval, TimeProvider time, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new StaticJwksException("The static JWKS path must be absolute.");
        }

        _path = path;
        _reloadInterval = reloadInterval;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keys = Index(LoadFile(_path));
        _lastReload = _time.GetUtcNow();
    }

    /// <summary>Number of keys of the current set.</summary>
    public int KeyCount => _keys.Count;

    /// <summary>The signing keys for a token <c>kid</c>; empty when the <c>kid</c> is missing or unknown.</summary>
    public IReadOnlyList<SecurityKey> ResolveKeys(string? kid)
    {
        var now = _time.GetUtcNow();
        if (now - _lastReload >= _reloadInterval)
        {
            Reload(now, unknownKid: false);
        }

        if (!string.IsNullOrEmpty(kid) && !_keys.ContainsKey(kid) && now - _lastUnknownKidReload >= UnknownKidReloadInterval)
        {
            Reload(now, unknownKid: true);
        }

        return !string.IsNullOrEmpty(kid) && _keys.TryGetValue(kid, out var keys) ? keys : [];
    }

    private static Dictionary<string, SecurityKey[]> Index(IReadOnlyDictionary<string, JsonWebKey> parsed)
    {
        var indexed = new Dictionary<string, SecurityKey[]>(parsed.Count, StringComparer.Ordinal);
        foreach (var (kid, key) in parsed)
        {
            indexed[kid] = [key];
        }

        return indexed;
    }

    /// <summary>Parses and validates a JWKS document; throws <see cref="StaticJwksException"/> on any violation.</summary>
    public static IReadOnlyDictionary<string, JsonWebKey> Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > MaxFileBytes)
        {
            throw new StaticJwksException("The static JWKS is empty or larger than the allowed size.");
        }

        using var document = ParseDocument(utf8Json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("keys", out var keys)
            || keys.ValueKind != JsonValueKind.Array)
        {
            throw new StaticJwksException("The static JWKS has no keys array.");
        }

        var count = keys.GetArrayLength();
        if (count == 0 || count > MaxKeys)
        {
            throw new StaticJwksException($"The static JWKS must hold 1 to {MaxKeys} keys.");
        }

        var result = new Dictionary<string, JsonWebKey>(count, StringComparer.Ordinal);
        foreach (var element in keys.EnumerateArray())
        {
            var key = ValidateKey(element);
            if (!result.TryAdd(key.Kid, key))
            {
                throw new StaticJwksException("The static JWKS contains a duplicate kid.");
            }
        }

        return result;
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException)
        {
            throw new StaticJwksException("The static JWKS is not valid JSON.");
        }
    }

    private static JsonWebKey ValidateKey(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new StaticJwksException("A static JWKS key is not an object.");
        }

        foreach (var member in PrivateMembers)
        {
            if (element.TryGetProperty(member, out _))
            {
                throw new StaticJwksException("The static JWKS holds private or symmetric key material.");
            }
        }

        var kty = StringMember(element, "kty");
        var kid = StringMember(element, "kid");
        var alg = StringMember(element, "alg");
        var use = StringMember(element, "use");
        if (string.IsNullOrEmpty(kid))
        {
            throw new StaticJwksException("A static JWKS key has no kid.");
        }

        if (use is not null && !string.Equals(use, "sig", StringComparison.Ordinal))
        {
            throw new StaticJwksException("A static JWKS key is not a signature key.");
        }

        if (alg is not null && Array.IndexOf(AllowedAlgorithms, alg) < 0)
        {
            throw new StaticJwksException("A static JWKS key declares a disallowed algorithm.");
        }

        switch (kty)
        {
            case "RSA":
                if (alg is not null && alg.StartsWith("ES", StringComparison.Ordinal))
                {
                    throw new StaticJwksException("A static JWKS key algorithm does not match its key type.");
                }

                if (StringMember(element, "n") is not { Length: > 0 } modulus || Base64UrlEncoder.DecodeBytes(modulus) is not { Length: > 0 } bytes
                    || StringMember(element, "e") is not { Length: > 0 })
                {
                    throw new StaticJwksException("A static JWKS RSA key is incomplete.");
                }

                var leadingZeros = 0;
                while (leadingZeros < bytes.Length - 1 && bytes[leadingZeros] == 0)
                {
                    leadingZeros++;
                }

                if ((bytes.Length - leadingZeros) * 8 < MinRsaKeyBits)
                {
                    throw new StaticJwksException("A static JWKS RSA key is shorter than 2048 bits.");
                }

                break;
            case "EC":
                if (alg is not null && !alg.StartsWith("ES", StringComparison.Ordinal))
                {
                    throw new StaticJwksException("A static JWKS key algorithm does not match its key type.");
                }

                var crv = StringMember(element, "crv");
                if (crv is not ("P-256" or "P-384") || string.IsNullOrEmpty(StringMember(element, "x")) || string.IsNullOrEmpty(StringMember(element, "y")))
                {
                    throw new StaticJwksException("A static JWKS EC key is not a complete P-256 or P-384 key.");
                }

                break;
            default:
                throw new StaticJwksException("A static JWKS key has a disallowed key type.");
        }

        try
        {
            return new JsonWebKey(element.GetRawText());
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            throw new StaticJwksException("A static JWKS key could not be parsed.");
        }
    }

    private static string? StringMember(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyDictionary<string, JsonWebKey> LoadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new StaticJwksException("The static JWKS file does not exist.");
            }

            if (info.Length > MaxFileBytes)
            {
                throw new StaticJwksException("The static JWKS file is larger than the allowed size.");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[(int)Math.Min(info.Length, MaxFileBytes) + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0)
            {
                read += n;
            }

            return Parse(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StaticJwksException("The static JWKS file cannot be read.");
        }
    }

    private void Reload(DateTimeOffset now, bool unknownKid)
    {
        lock (_gate)
        {
            if (unknownKid)
            {
                if (now - _lastUnknownKidReload < UnknownKidReloadInterval)
                {
                    return;
                }

                _lastUnknownKidReload = now;
            }
            else if (now - _lastReload < _reloadInterval)
            {
                return;
            }

            _lastReload = now;
            try
            {
                _keys = Index(LoadFile(_path));
            }
            catch (StaticJwksException)
            {
                // The last good key set stays in force; the reason text carries no key material.
                _logger.LogWarning("The static JWKS file could not be reloaded; the last good key set stays in use.");
            }
        }
    }
}
