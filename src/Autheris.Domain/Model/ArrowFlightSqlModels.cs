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
    string Signature,
    string UserSid = "",
    // Issue #447 (SEC-ATR-05): the ingress transport the ticket was issued over; part of the signature. A ticket without it (None) is never served.
    Autheris.Domain.Options.DataSources.DataSourceTransportFlags Ingress = Autheris.Domain.Options.DataSources.DataSourceTransportFlags.None
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
