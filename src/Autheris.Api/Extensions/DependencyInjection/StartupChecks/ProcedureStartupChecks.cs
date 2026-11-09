namespace Autheris.Api.Extensions.DependencyInjection.StartupChecks;

using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autheris.Application.Procedures.Services;

/// <summary>Startup checks of the governed stored procedure endpoints.</summary>
internal static class ProcedureStartupChecks
{
    /// <summary>
    /// with the switch on, the procedure connection and every allowed data source must resolve to SQL Server or
    /// PostgreSQL; Oracle, Databricks, an unresolvable dialect and a missing connection fail host start. The message names
    /// the data source, never the connection string.
    /// </summary>
    internal static void DialectAdmission(StartupCheckContext context)
    {
        var settings = context.Options.SqlEndpoints.Procedures;
        if (!settings.Enabled)
        {
            return;
        }

        foreach (string name in new[] { settings.ConnectionName }.Concat(settings.AllowedDataSources).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Kind is checked per definition by the loader; startup only admits the data source.
            string? rejection = ProcedureConnectionProvider.CheckAdmission(context.Options, name);
            if (rejection != null)
            {
                throw new ValidationException(rejection);
            }
        }
    }
}
