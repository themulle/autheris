namespace Autheris.Tests.Integration;

using System;

/// <summary>
/// Policy for a database container that does not start. A developer machine without Docker skips the database tests silently;
/// under <c>CI=true</c> (or <c>SQLGATE_REQUIRE_DATABASES=1</c>) a failed start is an error, so a green run always means the tests ran.
/// </summary>
internal static class ContainerStartup
{
    public static bool IsRequired(Func<string, string?> environment) =>
        string.Equals(environment("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || environment("SQLGATE_REQUIRE_DATABASES") == "1";

    public static bool IsRequired() => IsRequired(Environment.GetEnvironmentVariable);

    /// <summary>Rethrows <paramref name="failure"/> as an error when the run requires databases; returns (a skip) otherwise.</summary>
    public static void FailIfRequired(Exception failure, string what, bool? required = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (required ?? IsRequired())
        {
            throw new InvalidOperationException(
                $"{what} did not start and the run requires its databases (CI=true or SQLGATE_REQUIRE_DATABASES=1): {failure.GetType().Name}: {failure.Message}",
                failure);
        }
    }
}
