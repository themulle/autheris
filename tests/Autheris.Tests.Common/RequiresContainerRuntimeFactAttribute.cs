using System.Diagnostics;
using Xunit;

namespace Autheris.Tests.Common;

/// <summary>
/// SEC-SIMPL-50: Real-database tests using Testcontainers never silently skip in CI.
/// When <c>CI</c> or <c>GITHUB_ACTIONS</c> is truthy (<c>"true"</c> or <c>"1"</c>), a missing container
/// runtime never skips so the test executes and fails loudly. Locally, when no container runtime is
/// available, the test skips with an explicit local-only marker.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresContainerRuntimeFactAttribute : FactAttribute
{
    public const string LocalSkipReason = "Skipped: container runtime unavailable (local only)";

    private static readonly Lazy<bool> RuntimeProbe = new(ProbeContainerRuntime);

    public RequiresContainerRuntimeFactAttribute()
    {
        Skip = ResolveSkipReason();
    }

    public RequiresContainerRuntimeFactAttribute(
        Func<string, string?> getEnvironmentVariable,
        Func<bool> isContainerRuntimeAvailable)
    {
        Skip = ResolveSkipReason(getEnvironmentVariable, isContainerRuntimeAvailable);
    }

    public static string? ResolveSkipReason(
        Func<string, string?>? getEnvironmentVariable = null,
        Func<bool>? isContainerRuntimeAvailable = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        if (IsTruthy(env("CI")) || IsTruthy(env("GITHUB_ACTIONS")))
        {
            return null;
        }

        var probe = isContainerRuntimeAvailable ?? (() => RuntimeProbe.Value);
        return probe() ? null : LocalSkipReason;
    }

    internal static bool IsTruthy(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "1", StringComparison.Ordinal);

    private static bool ProbeContainerRuntime() =>
        CanRunRuntimeInfo("docker") || CanRunRuntimeInfo("podman");

    private static bool CanRunRuntimeInfo(string binary)
    {
        try
        {
            var info = new ProcessStartInfo(binary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            info.ArgumentList.Add("info");
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// SEC-SIMPL-50: Theory counterpart of <see cref="RequiresContainerRuntimeFactAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresContainerRuntimeTheoryAttribute : TheoryAttribute
{
    public RequiresContainerRuntimeTheoryAttribute()
    {
        Skip = RequiresContainerRuntimeFactAttribute.ResolveSkipReason();
    }

    public RequiresContainerRuntimeTheoryAttribute(
        Func<string, string?> getEnvironmentVariable,
        Func<bool> isContainerRuntimeAvailable)
    {
        Skip = RequiresContainerRuntimeFactAttribute.ResolveSkipReason(getEnvironmentVariable, isContainerRuntimeAvailable);
    }
}
