namespace Autheris.Api.Middleware;

using System;
using System.Text.RegularExpressions;
using Autheris.Domain.Diagnostics;

internal static partial class AuditExceptionCode
{
    [GeneratedRegex(@"^[A-Za-z0-9_.:-]{1,64}$")]
    private static partial Regex CodePattern();

    public static string For(Exception ex)
    {
        if (ex == null)
        {
            return "UNCLASSIFIED";
        }

        try
        {
            var code = TelemetryExceptionPolicy.ErrorType(ex);
            if (!string.IsNullOrEmpty(code) && CodePattern().IsMatch(code))
            {
                return code;
            }

            return ex.GetType().Name;
        }
        catch
        {
            return "UNCLASSIFIED";
        }
    }
}
