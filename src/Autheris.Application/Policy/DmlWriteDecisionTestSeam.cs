namespace Autheris.Application.Policy;

using System.Threading;

/// <summary>
/// Test-only seam for the DML write decision, which denies every target. It is internal, has no configuration or option binding
/// and is flowed per async context, so one test enabling it never changes another test or any production path.
/// </summary>
internal static class DmlWriteDecisionTestSeam
{
    private static readonly AsyncLocal<bool> Permissive = new();

    internal static bool IsPermissive => Permissive.Value;

    internal static void EnablePermissiveWrite() => Permissive.Value = true;
}
