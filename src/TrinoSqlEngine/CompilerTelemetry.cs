namespace TrinoSqlEngine;

using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using TrinoSqlEngine.Ast.Capabilities;

/// <summary>Span and counters of the governed compiler. They carry kinds and dialects only, never values (NFR-6, INV-16).</summary>
public static class CompilerTelemetry
{
    public const string ActivitySourceName = "TrinoSqlEngine.SqlCompiler";
    public const string MeterName = "Autheris.SqlCompiler";

    internal static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter MeterInstance = new(MeterName);
    private static readonly Counter<long> Rejected = MeterInstance.CreateCounter<long>("autheris.sql.compile.rejected");
    private static readonly Counter<long> LimitRejected = MeterInstance.CreateCounter<long>("autheris.sql.limit_rejected");

    internal static void RecordRejected(TargetSqlDialect dialect, Exception exception)
    {
        string dialectName = dialect.ToString();
        Rejected.Add(1, new KeyValuePair<string, object?>("dialect", dialectName), new KeyValuePair<string, object?>("reason", exception.GetType().Name));
        if (exception is SqlLimitExceededException limit)
        {
            LimitRejected.Add(1, new KeyValuePair<string, object?>("dialect", dialectName), new KeyValuePair<string, object?>("kind", limit.Kind.ToString()));
        }
    }
}
