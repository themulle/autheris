namespace Autheris.Api.Security;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Konscious.Security.Cryptography;

/// <summary>
/// Cryptographic password hashing and verification utility supporting Argon2id and PBKDF2.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultPbkdf2Iterations = 600_000; // OWASP 2023 for PBKDF2-HMAC-SHA256
    public const int MinPbkdf2Iterations = 10_000;
    public const int MaxPbkdf2Iterations = 10_000_000;

    public const int DefaultArgon2MemorySizeKb = 65_536; // 64 MB
    public const int MinArgon2MemorySizeKb = 8_192;      // 8 MB
    public const int MaxArgon2MemorySizeKb = 262_144;    // 256 MB

    public const int DefaultArgon2Iterations = 3;
    public const int MinArgon2Iterations = 1;
    public const int MaxArgon2Iterations = 10;

    public const int DefaultArgon2Parallelism = 1;
    public const int MinArgon2Parallelism = 1;
    public const int MaxArgon2Parallelism = 8;

    public const int SaltLength = 16;
    public const int HashLength = 32;

    private static readonly Regex Argon2ParamsRegex = new(
        @"^m=(\d+),t=(\d+),p=(\d+)$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Hashes a password using Argon2id with modular crypt format ($argon2id$v=19$m=...,t=...,p=...$salt$hash).
    /// </summary>
    public static string HashPasswordArgon2id(
        string password,
        int memorySizeKb = DefaultArgon2MemorySizeKb,
        int iterations = DefaultArgon2Iterations,
        int parallelism = DefaultArgon2Parallelism)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        memorySizeKb = Math.Clamp(memorySizeKb, MinArgon2MemorySizeKb, MaxArgon2MemorySizeKb);
        iterations = Math.Clamp(iterations, MinArgon2Iterations, MaxArgon2Iterations);
        parallelism = Math.Clamp(parallelism, MinArgon2Parallelism, MaxArgon2Parallelism);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] hash = ComputeArgon2idHash(password, salt, memorySizeKb, iterations, parallelism, HashLength);

        string saltB64 = Convert.ToBase64String(salt);
        string hashB64 = Convert.ToBase64String(hash);

        return $"$argon2id$v=19$m={memorySizeKb},t={iterations},p={parallelism}${saltB64}${hashB64}";
    }

    /// <summary>
    /// Hashes a password using PBKDF2-HMAC-SHA256 ($pbkdf2$iterations$salt$hash).
    /// </summary>
    public static string HashPasswordPbkdf2(
        string password,
        int iterations = DefaultPbkdf2Iterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        iterations = Math.Clamp(iterations, MinPbkdf2Iterations, MaxPbkdf2Iterations);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);

        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashLength);

        string saltB64 = Convert.ToBase64String(salt);
        string hashB64 = Convert.ToBase64String(hash);

        return $"$pbkdf2${iterations}${saltB64}${hashB64}";
    }

    /// <summary>
    /// Verifies an input password against a stored hash (Argon2id, PBKDF2, or dev plaintext/hex).
    /// </summary>
    public static bool VerifyPassword(
        string inputPassword,
        string storedHash,
        string username,
        bool isDevelopment,
        Action<string>? logError = null)
    {
        ArgumentNullException.ThrowIfNull(inputPassword);
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        // 1. Argon2id: $argon2id$v=19$m=65536,t=3,p=1$salt$hash
        if (storedHash.StartsWith("$argon2id$", StringComparison.OrdinalIgnoreCase))
        {
            return VerifyArgon2id(inputPassword, storedHash, username, logError);
        }

        // 2. PBKDF2: $pbkdf2$iterations$salt$hash
        if (storedHash.StartsWith("$pbkdf2$", StringComparison.OrdinalIgnoreCase))
        {
            return VerifyPbkdf2(inputPassword, storedHash, username, logError);
        }

        // 3. Plaintext or unsalted SHA-256 (strictly Development only)
        if (!isDevelopment)
        {
            string userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username)))[..12];
            logError?.Invoke($"Basic authentication rejected user (hash: {userHash}): Plaintext or unsalted passwords are strictly prohibited outside of Development.");
            return false;
        }

        byte[] userPasswordBytes = Encoding.UTF8.GetBytes(storedHash);
        byte[] inputPasswordBytes = Encoding.UTF8.GetBytes(inputPassword);

        if (CryptographicOperations.FixedTimeEquals(userPasswordBytes, inputPasswordBytes))
        {
            return true;
        }

        string inputHex = Convert.ToHexString(SHA256.HashData(inputPasswordBytes));
        if (storedHash.Length == inputHex.Length)
        {
            byte[] userHexBytes = Encoding.ASCII.GetBytes(storedHash.ToUpperInvariant());
            byte[] inputHexBytes = Encoding.ASCII.GetBytes(inputHex);
            if (CryptographicOperations.FixedTimeEquals(userHexBytes, inputHexBytes))
            {
                return true;
            }
        }

        return false;
    }

    private static bool VerifyArgon2id(
        string inputPassword,
        string storedHash,
        string username,
        Action<string>? logError)
    {
        // Format: empty, "argon2id", "v=19", "m=...,t=...,p=...", salt, hash
        string[] parts = storedHash.Split('$');
        if (parts.Length != 6)
        {
            return false;
        }

        var match = Argon2ParamsRegex.Match(parts[3]);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out int memoryKb) ||
            !int.TryParse(match.Groups[2].Value, out int iterations) ||
            !int.TryParse(match.Groups[3].Value, out int parallelism))
        {
            return false;
        }

        if (memoryKb < MinArgon2MemorySizeKb || memoryKb > MaxArgon2MemorySizeKb ||
            iterations < MinArgon2Iterations || iterations > MaxArgon2Iterations ||
            parallelism < MinArgon2Parallelism || parallelism > MaxArgon2Parallelism)
        {
            string userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username)))[..12];
            logError?.Invoke($"Basic authentication rejected user (hash: {userHash}): Argon2id cost parameters outside permitted bounds (m={memoryKb}, t={iterations}, p={parallelism}).");
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[4]);
            byte[] expectedHash = Convert.FromBase64String(parts[5]);

            byte[] computedHash = ComputeArgon2idHash(inputPassword, salt, memoryKb, iterations, parallelism, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(expectedHash, computedHash);
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifyPbkdf2(
        string inputPassword,
        string storedHash,
        string username,
        Action<string>? logError)
    {
        // Format: empty, "pbkdf2", iterations, salt_b64, hash_b64
        string[] parts = storedHash.Split('$');
        if (parts.Length != 5 || !int.TryParse(parts[2], out int iterations))
        {
            return false;
        }

        if (iterations < MinPbkdf2Iterations || iterations > MaxPbkdf2Iterations)
        {
            string userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username)))[..12];
            logError?.Invoke($"Basic authentication rejected user (hash: {userHash}): PBKDF2 iteration count {iterations} outside permitted range.");
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[3]);
            byte[] expectedHash = Convert.FromBase64String(parts[4]);

            byte[] computedHash = Rfc2898DeriveBytes.Pbkdf2(
                inputPassword,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(expectedHash, computedHash);
        }
        catch
        {
            return false;
        }
    }

    public static byte[] ComputeArgon2idHash(
        string password,
        byte[] salt,
        int memorySizeKb,
        int iterations,
        int parallelism,
        int hashLength)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memorySizeKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };

        return argon2.GetBytes(hashLength);
    }
}
