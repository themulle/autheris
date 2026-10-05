namespace Autheris.Api.Security;

using System;

/// <summary>
/// CLI utility for generating secure Argon2id and PBKDF2 password hashes for appsettings.json.
/// </summary>
public static class PasswordHashCli
{
    public static int Run(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1] is "--help" or "-h")
        {
            Console.WriteLine("Autheris Password Hasher CLI");
            Console.WriteLine("============================");
            Console.WriteLine("Usage: dotnet run --project src/Autheris.Api/Autheris.Api.csproj -- hash-password <password> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --type <argon2id|pbkdf2>   Algorithm to use (default: both)");
            Console.WriteLine("  --memory <kb>              Argon2id memory in KB (default: 65536 = 64 MB)");
            Console.WriteLine("  --iterations <count>       Iteration count (default: 3 for Argon2id, 100000 for PBKDF2)");
            Console.WriteLine("  --parallelism <threads>    Argon2id parallelism (default: 1)");
            return 1;
        }

        string password = args[1];
        string type = "both";
        int memoryKb = PasswordHasher.DefaultArgon2MemorySizeKb;
        int argonIterations = PasswordHasher.DefaultArgon2Iterations;
        int pbkdf2Iterations = PasswordHasher.DefaultPbkdf2Iterations;
        int parallelism = PasswordHasher.DefaultArgon2Parallelism;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--type" && i + 1 < args.Length)
            {
                type = args[++i].ToLowerInvariant();
            }
            else if (args[i] == "--memory" && i + 1 < args.Length && int.TryParse(args[++i], out int m))
            {
                memoryKb = m;
            }
            else if (args[i] == "--iterations" && i + 1 < args.Length && int.TryParse(args[++i], out int iters))
            {
                argonIterations = iters;
                pbkdf2Iterations = iters;
            }
            else if (args[i] == "--parallelism" && i + 1 < args.Length && int.TryParse(args[++i], out int p))
            {
                parallelism = p;
            }
        }

        Console.WriteLine("Autheris Password Hash Generator");
        Console.WriteLine("--------------------------------");

        if (type is "both" or "argon2id")
        {
            string argon2Hash = PasswordHasher.HashPasswordArgon2id(password, memoryKb, argonIterations, parallelism);
            Console.WriteLine("Algorithm: Argon2id (OWASP & BSI recommended)");
            Console.WriteLine($"Hash:      {argon2Hash}");
            Console.WriteLine();
        }

        if (type is "both" or "pbkdf2")
        {
            string pbkdf2Hash = PasswordHasher.HashPasswordPbkdf2(password, pbkdf2Iterations);
            Console.WriteLine("Algorithm: PBKDF2-HMAC-SHA256 (NIST compliant)");
            Console.WriteLine($"Hash:      {pbkdf2Hash}");
            Console.WriteLine();
        }

        Console.WriteLine("Copy the generated hash into your appsettings.json or environment variable under:");
        Console.WriteLine("Gateway:Authentication:BasicAuth:Users:[n]:Password");

        return 0;
    }
}
