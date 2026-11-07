namespace Autheris.Api.Configuration.Validation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Application.Sql.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Why a data source is reported by <see cref="DataSourceTransportConfigurationReporter"/>.</summary>
public enum TransportConfigurationFindingKind
{
    /// <summary>Effective <c>AllowedTransports</c> is <see cref="DataSourceTransportFlags.None"/>: no transport may read the source.</summary>
    NoTransportAllowed,

    /// <summary>Every transport the source allows is switched off globally, so nothing can reach it.</summary>
    OnlyGloballyDisabledTransports
}

/// <summary>One misconfigured source (<c>Defaults</c> is reported under the name <see cref="DataSourceTransportConfigurationReporter.DefaultsName"/>).</summary>
public readonly record struct TransportConfigurationFinding(string Source, TransportConfigurationFindingKind Kind, DataSourceTransportFlags Allowed);

/// <summary>
/// Issue #447 (decision 11): with the transport gate enforcing<c>AllowedTransports</c>, a source that allows no reachable
/// transport is a locked-out source. This service logs one warning per such source on start and on every options reload, and one
/// warning that lists catalog sources without an explicit profile (they inherit <c>Defaults</c>, owner decision 1). It never fails the
/// start (<c>DataSourceOptionsValidator</c> stays fail-only) and never changes a decision of the gate.
/// </summary>
public sealed class DataSourceTransportConfigurationReporter : IHostedService, IDisposable
{
    /// <summary>The reporting name of the <c>Defaults</c> profile.</summary>
    public const string DefaultsName = "Defaults";

    private const int MaxListedSources = 20;
    private const int MaxNameLength = 128;

    private readonly IOptionsMonitor<DataSourceConfigurationOptions> _dataSources;
    private readonly Func<GatewayOptions> _gateway;
    private readonly Func<Action<GatewayOptions>, IDisposable?> _onGatewayChange;
    private readonly ILogger<DataSourceTransportConfigurationReporter> _logger;
    private readonly IServiceScopeFactory? _scopes;
    private readonly List<IDisposable> _subscriptions = [];

    public DataSourceTransportConfigurationReporter(
        IOptionsMonitor<DataSourceConfigurationOptions> dataSources,
        Func<GatewayOptions> currentGateway,
        Func<Action<GatewayOptions>, IDisposable?> onGatewayChange,
        ILogger<DataSourceTransportConfigurationReporter> logger,
        IServiceScopeFactory? scopes = null)
    {
        // The gateway options arrive as accessors (wired in DI) so this class stays a narrow consumer of the global switches only.
        _dataSources = dataSources ?? throw new ArgumentNullException(nameof(dataSources));
        _gateway = currentGateway ?? throw new ArgumentNullException(nameof(currentGateway));
        _onGatewayChange = onGatewayChange ?? throw new ArgumentNullException(nameof(onGatewayChange));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scopes = scopes;
    }

    /// <summary>Transports that are switched off globally. Trino shares the WebSQL switch; GraphQL and REST have no global switch.</summary>
    public static DataSourceTransportFlags GloballyDisabled(GatewayOptions gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var disabled = DataSourceTransportFlags.None;
        if (gateway.WebSql?.Enabled == false)
        {
            disabled |= DataSourceTransportFlags.WebSql | DataSourceTransportFlags.Trino;
        }

        if (gateway.Mcp?.Enabled == false)
        {
            disabled |= DataSourceTransportFlags.Mcp;
        }

        if (gateway.OData?.Enabled == false)
        {
            disabled |= DataSourceTransportFlags.OData;
        }

        if (gateway.Arrow?.Enabled == false)
        {
            disabled |= DataSourceTransportFlags.ArrowFlight;
        }

        return disabled;
    }

    /// <summary>
    /// Pure evaluation of one configuration snapshot: the effective transports of <c>Defaults</c> and of every configured profile
    /// (an unset profile value inherits <c>Defaults</c>, which falls back to <c>All</c>, decision 13). Computed from the snapshot
    /// itself so it never races the resolver's own rebuild on a reload.
    /// </summary>
    public static IReadOnlyList<TransportConfigurationFinding> Evaluate(DataSourceConfigurationOptions options, GatewayOptions gateway)
    {
        ArgumentNullException.ThrowIfNull(options);
        var disabled = GloballyDisabled(gateway);
        var defaults = options.Defaults?.AllowedTransports ?? DataSourceTransportFlags.All;
        var findings = new List<TransportConfigurationFinding>();
        Add(findings, DefaultsName, defaults, disabled);
        foreach (var (name, profile) in options.Profiles.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            Add(findings, name, profile?.AllowedTransports ?? defaults, disabled);
        }

        return findings;
    }

    private static void Add(List<TransportConfigurationFinding> findings, string name, DataSourceTransportFlags allowed, DataSourceTransportFlags disabled)
    {
        if (allowed == DataSourceTransportFlags.None)
        {
            findings.Add(new TransportConfigurationFinding(name, TransportConfigurationFindingKind.NoTransportAllowed, allowed));
        }
        else if ((allowed & ~disabled & DataSourceTransportFlags.All) == DataSourceTransportFlags.None)
        {
            findings.Add(new TransportConfigurationFinding(name, TransportConfigurationFindingKind.OnlyGloballyDisabledTransports, allowed));
        }
    }

    /// <summary>The catalog source names that have no explicit profile (they inherit <c>Defaults</c>), sanitised, de-duplicated and ordered.</summary>
    public static IReadOnlyList<string> UnprofiledSources(IEnumerable<string?> catalogSources, DataSourceConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalogSources);
        ArgumentNullException.ThrowIfNull(options);
        return catalogSources
            .Where(s => !string.IsNullOrWhiteSpace(s) && !options.Profiles.ContainsKey(s!))
            .Select(s => Sanitise(s!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Strips control characters and caps the length: a name comes from the catalog and is written to a log line.</summary>
    internal static string Sanitise(string name)
    {
        var builder = new StringBuilder(Math.Min(name.Length, MaxNameLength));
        foreach (var ch in name)
        {
            if (builder.Length >= MaxNameLength)
            {
                break;
            }

            builder.Append(char.IsControl(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // A reload re-reports synchronously from the new snapshot; the catalog read is start-up only (it needs a scope and the database).
        TrackChange(_dataSources.OnChange((options, _) => Report(options, _gateway())));
        TrackChange(_onGatewayChange(gateway => Report(_dataSources.CurrentValue, gateway)));

        Report(_dataSources.CurrentValue, _gateway());
        await ReportUnprofiledCatalogSourcesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private void TrackChange(IDisposable? subscription)
    {
        if (subscription is not null)
        {
            _subscriptions.Add(subscription);
        }
    }

    private void Report(DataSourceConfigurationOptions options, GatewayOptions gateway)
    {
        try
        {
            foreach (var finding in Evaluate(options, gateway))
            {
                var name = Sanitise(finding.Source);
                if (finding.Kind == TransportConfigurationFindingKind.NoTransportAllowed)
                {
                    _logger.LogWarning(
                        "Data source {DataSource} allows no transport (AllowedTransports is None): every request to it is denied.", name);
                }
                else
                {
                    _logger.LogWarning(
                        "Data source {DataSource} only allows transports that are disabled globally ({Allowed}): every request to it is denied.",
                        name, finding.Allowed);
                }
            }
        }
        catch (Exception ex)
        {
            // Reporting is advisory and must never break a reload or the start.
            _logger.LogDebug("Data source transport report failed ({ErrorType}).", ex.GetType().Name);
        }
    }

    private async Task ReportUnprofiledCatalogSourcesAsync(CancellationToken ct)
    {
        if (_scopes is null)
        {
            return;
        }

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetService<ITableMetadataRepository>();
            if (repository is null)
            {
                return;
            }

            var tables = await repository.GetAllTablesAsync(ct).ConfigureAwait(false);
            var unprofiled = UnprofiledSources(tables.Select(DataSourceAllowlistGuard.LogicalDataSource), _dataSources.CurrentValue);
            if (unprofiled.Count == 0)
            {
                return;
            }

            // Informational while Defaults is the permissive All (nothing is locked out); a warning once Defaults narrows the transports.
            var defaultAllowed = _dataSources.CurrentValue.Defaults?.AllowedTransports ?? DataSourceTransportFlags.All;
            _logger.Log(
                defaultAllowed == DataSourceTransportFlags.All ? LogLevel.Information : LogLevel.Warning,
                "{Count} catalog data source(s) have no explicit profile and inherit Defaults.AllowedTransports ({DefaultAllowed}): {DataSources}{More}",
                unprofiled.Count,
                defaultAllowed,
                string.Join(", ", unprofiled.Take(MaxListedSources)),
                unprofiled.Count > MaxListedSources ? ", ..." : string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The catalog may not be reachable at start; the check is advisory.
            _logger.LogDebug("Catalog data sources could not be checked for transport profiles ({ErrorType}).", ex.GetType().Name);
        }
    }
}
