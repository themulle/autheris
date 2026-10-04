namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// F-DATA-04-B: Arrow Flight SQL Domain contracts for high-speed JDBC/ODBC and Data Science streaming.
/// </summary>
public sealed record FlightSqlTicket(
    string TicketId,
    string TenantId,
    string Query,
    DateTimeOffset CreatedAtUtc,
    string Signature
);

public sealed record FlightSqlInfo(
    string Query,
    string SchemaJson,
    long EstimatedRowCount,
    FlightSqlTicket Ticket,
    IReadOnlyList<string> Columns
);

public sealed record FlightSqlTableInfo(
    string Catalog,
    string Schema,
    string TableName,
    string TableType
);
