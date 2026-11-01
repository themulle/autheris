namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>Request parameters for refreshing physical key and index directions of a datasource.</summary>
public sealed record PhysicalKeyRefreshRequest(
    bool DryRun = false,
    string? TableFilter = null);

/// <summary>Summary of a physical key and index refresh operation.</summary>
public sealed record PhysicalKeyRefreshResult(
    string DatasourceId,
    bool DryRun,
    int TablesScanned,
    int KeysUpdated,
    int IndexesUpdated,
    IReadOnlyList<TableKeyRefreshItem> Details);

/// <summary>Details of a table inspected during a physical key refresh.</summary>
public sealed record TableKeyRefreshItem(
    string Table,
    string Status,
    IReadOnlyList<string> Findings,
    string[]? PrimaryKeyDirections);
