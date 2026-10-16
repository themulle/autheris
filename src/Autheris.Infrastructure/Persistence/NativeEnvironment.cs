using System.Runtime.InteropServices;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// On Linux/macOS <see cref="Environment.SetEnvironmentVariable(string, string?)"/> only changes .NET's managed copy of the environment.
/// Native libraries (libgssapi_krb5, gss-ntlmssp, krb5) read the process <c>environ</c> via <c>getenv()</c> and never see it.
/// This helper sets both copies so GSSAPI sees <c>NTLM_USER_FILE</c>, <c>NTLMUSER</c>, <c>KRB5_CONFIG</c>, and so on.
/// </summary>
internal static partial class NativeEnvironment
{
    private static readonly Lock Sync = new();

    /// <summary>Sets <paramref name="name"/> in the managed and (on Unix) the native environment. Unchanged values are not rewritten.</summary>
    public static void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        lock (Sync)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(name), value, StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                return;
            }

            if (string.Equals(GetNative(name), value, StringComparison.Ordinal))
            {
                return;
            }

            if (NativeSetEnv(name, value, 1) != 0)
            {
                throw new InvalidOperationException($"setenv failed for environment variable '{name}' (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
    }

    /// <summary>Reads the variable from the native <c>environ</c> (Unix); on other platforms the managed value.</summary>
    public static string? GetNative(string name)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return Environment.GetEnvironmentVariable(name);
        }

        nint ptr = NativeGetEnv(name);
        return ptr == 0 ? null : Marshal.PtrToStringUTF8(ptr);
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int NativeSetEnv(string name, string value, int overwrite);

    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NativeGetEnv(string name);
}
