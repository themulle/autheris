namespace Autheris.GraphQL.Catalog;

using System;
using System.Threading;
using HotChocolate.Configuration;
using HotChocolate.Types.Descriptors.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Emits throttled progress and memory/GC metrics during slow (&gt;= 5 s) HotChocolate schema builds
/// so large-catalog startups are observable without logging any schema, table, or column names.
/// </summary>
public sealed partial class CatalogSchemaBuildProgressTypeInterceptor : TypeInterceptor
{
    internal static readonly TimeSpan SlowBuildThreshold = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(10);

    private ILogger<CatalogGraphQlTypeModule>? _logger;
    private TimeProvider _timeProvider = TimeProvider.System;
    private long _startTimestamp;
    private long _lastLogTimestamp;
    private int _totalTypes;
    private int _completedTypes;

    public override void OnBeforeDiscoverTypes()
    {
        Volatile.Write(ref _startTimestamp, 0);
        Volatile.Write(ref _lastLogTimestamp, 0);
        Volatile.Write(ref _totalTypes, 0);
        Volatile.Write(ref _completedTypes, 0);
        _logger = null;
    }

    public override void OnBeforeInitialize(ITypeDiscoveryContext discoveryContext) =>
        EnsureInitialized(discoveryContext.Services);

    public override void OnAfterInitialize(ITypeDiscoveryContext discoveryContext, TypeSystemConfiguration configuration) =>
        Interlocked.Increment(ref _totalTypes);

    public override void OnAfterDiscoverTypes() => LogPhaseFinishedIfSlow("discover-types");

    public override void OnAfterMergeTypeExtensions() => LogPhaseFinishedIfSlow("merge-type-extensions");

    public override void OnBeforeCompleteType(ITypeCompletionContext completionContext, TypeSystemConfiguration configuration)
    {
        EnsureInitialized(completionContext.Services);
        var completed = Interlocked.Increment(ref _completedTypes);
        if (_logger is null)
        {
            return;
        }

        var now = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(_startTimestamp, now);
        if (elapsed < SlowBuildThreshold)
        {
            return;
        }

        var previousLog = Volatile.Read(ref _lastLogTimestamp);
        if (_timeProvider.GetElapsedTime(previousLog, now) < ProgressLogInterval ||
            Interlocked.CompareExchange(ref _lastLogTimestamp, now, previousLog) != previousLog)
        {
            return;
        }

        LogCompletionProgress(_logger, completed, Volatile.Read(ref _totalTypes), (long)elapsed.TotalMilliseconds,
            GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024), GC.CollectionCount(2));
    }

    public override void OnAfterCompleteTypes() => LogPhaseFinishedIfSlow("complete-types");

    public override void OnAfterMakeExecutable() => LogPhaseFinishedIfSlow("make-executable");

    private void EnsureInitialized(IServiceProvider services)
    {
        if (Volatile.Read(ref _startTimestamp) != 0)
        {
            return;
        }

        var rootServices = services;
        try
        {
            rootServices = services.GetRootServiceProvider();
        }
        catch (InvalidOperationException)
        {
            rootServices = services;
        }

        _logger ??= services.GetService<ILogger<CatalogGraphQlTypeModule>>()
            ?? rootServices.GetService<ILogger<CatalogGraphQlTypeModule>>();
        _timeProvider = services.GetService<TimeProvider>()
            ?? rootServices.GetService<TimeProvider>()
            ?? TimeProvider.System;
        var now = _timeProvider.GetTimestamp();
        if (Interlocked.CompareExchange(ref _startTimestamp, now, 0) == 0)
        {
            Volatile.Write(ref _lastLogTimestamp, now);
        }
    }

    private void LogPhaseFinishedIfSlow(string phase)
    {
        var start = Volatile.Read(ref _startTimestamp);
        if (_logger is null || start == 0)
        {
            return;
        }

        var now = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(start, now);
        if (elapsed < SlowBuildThreshold)
        {
            return;
        }

        Volatile.Write(ref _lastLogTimestamp, now);
        LogPhaseFinished(_logger, phase, Volatile.Read(ref _totalTypes), (long)elapsed.TotalMilliseconds,
            GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024), GC.CollectionCount(2));
    }

    [LoggerMessage(EventId = 15141, Level = LogLevel.Information,
        Message = "GraphQL schema compilation phase finished: phase={Phase} totalTypes={TotalTypes} elapsedMs={ElapsedMs} managedMemoryMb={ManagedMemoryMb} gen2Collections={Gen2Collections}")]
    private static partial void LogPhaseFinished(ILogger logger, string phase, int totalTypes, long elapsedMs, long managedMemoryMb, int gen2Collections);

    [LoggerMessage(EventId = 15142, Level = LogLevel.Information,
        Message = "GraphQL schema compilation progress: phase=complete-types completedTypes={CompletedTypes} totalTypes={TotalTypes} elapsedMs={ElapsedMs} managedMemoryMb={ManagedMemoryMb} gen2Collections={Gen2Collections}")]
    private static partial void LogCompletionProgress(ILogger logger, int completedTypes, int totalTypes, long elapsedMs, long managedMemoryMb, int gen2Collections);
}
