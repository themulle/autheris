namespace Autheris.Application.Sql.Services;

using System;

/// <summary>
/// Internal signal thrown during SQL governance when a query cannot be executed via single-source pushdown
/// and must be routed to the federated cross-source execution engine (INV-18).
/// </summary>
internal sealed class CrossSourceRoutingException : Exception
{
    public CrossSourceRoutingException()
        : base("Query must be routed to federated cross-source execution.")
    {
    }
}
