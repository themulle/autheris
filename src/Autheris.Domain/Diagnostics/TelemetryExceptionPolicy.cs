namespace Autheris.Domain.Diagnostics;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Reflection;

public static class TelemetryExceptionPolicy
{
    public const string RedactedPlaceholder = "[redacted]";

    private static readonly FrozenSet<string> ValueFreeExceptionTypeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Autheris.Domain.Exceptions.GatewaySecurityException",
        "Autheris.Domain.Exceptions.GatewayForbiddenException",
        "Autheris.Domain.Exceptions.GatewayUnauthorizedException",
        "Autheris.Domain.Exceptions.TableNotFoundException",
        "Autheris.Domain.Exceptions.ConsentNotFoundException",
        "System.OperationCanceledException",
        "System.Threading.Tasks.TaskCanceledException",
        "System.TimeoutException"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static string ErrorType(Exception? ex)
    {
        if (ex == null)
        {
            return "unknown";
        }

        // Check if exception has an ErrorCode property
        var prop = ex.GetType().GetProperty("ErrorCode", BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.PropertyType == typeof(string))
        {
            var val = prop.GetValue(ex) as string;
            if (!string.IsNullOrWhiteSpace(val))
            {
                return val;
            }
        }

        return ex.GetType().Name;
    }

    public static string SafeMessage(Exception? ex)
    {
        if (ex == null)
        {
            return string.Empty;
        }

        var typeFullName = ex.GetType().FullName;
        if (typeFullName != null && ValueFreeExceptionTypeNames.Contains(typeFullName))
        {
            return ex.Message;
        }

        return RedactedPlaceholder;
    }

    public static int? GetDriverErrorCode(Exception? ex)
    {
        if (ex == null) return null;

        var prop = ex.GetType().GetProperty("Number", BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.PropertyType == typeof(int))
        {
            return (int?)prop.GetValue(ex);
        }

        if (ex is System.Data.Common.DbException dbEx && dbEx.ErrorCode != 0)
        {
            return dbEx.ErrorCode;
        }

        return null;
    }
}
