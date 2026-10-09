namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;
using RequestContext = HotChocolate.Execution.RequestContext;

public sealed class DbtHealthExecutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IDbtHealthCircuitBreaker _circuitBreaker;

    public DbtHealthExecutionMiddleware(RequestDelegate next, IDbtHealthCircuitBreaker circuitBreaker)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        DocumentNode? doc = context.OperationDocumentInfo?.Document;
        if (doc == null && context.Request.Document is IOperationDocumentNodeProvider nodeProvider)
        {
            doc = nodeProvider.Document;
        }

        if (doc == null && context.Request.Document is not null)
        {
            try
            {
                var docStr = context.Request.Document.ToString();
                if (!string.IsNullOrWhiteSpace(docStr))
                {
                    doc = Utf8GraphQLParser.Parse(docStr);
                }
            }
            catch
            {
                // Ignore parse errors, let downstream pipeline handle them
            }
        }

        if (doc == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        Microsoft.AspNetCore.Http.HttpContext? httpContext = null;
        if (context.ContextData.TryGetValue("HttpContext", out var hcObj) && hcObj is Microsoft.AspNetCore.Http.HttpContext hc)
        {
            httpContext = hc;
        }
        else
        {
            var accessor = context.RequestServices.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
            httpContext = accessor?.HttpContext;
        }

        var principal = httpContext?.User ?? (context.ContextData.TryGetValue("ClaimsPrincipal", out var cpObj) && cpObj is System.Security.Claims.ClaimsPrincipal cp ? cp : null);

        // GQL-12: Do not disclose dbt quarantine status before authentication
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var accessResolver = context.RequestServices.GetService<ITableAccessResolver>();
        var tableRepo = context.RequestServices.GetService<ITableMetadataRepository>();

        // 1. Extract referenced models (field names, nested selections, and name arguments)
        var referencedModels = ExtractReferencedModels(doc);
        var degradedModels = new List<DbtHealthState>();

        // 2. Check health for each referenced model
        foreach (var modelName in referencedModels)
        {
            var tableId = new TableIdentifier("default", "default", modelName);
            var health = await _circuitBreaker.GetTableHealthAsync(tableId, context.RequestAborted).ConfigureAwait(false);

            if (health.Status == DbtModelHealthStatus.Quarantined || health.Status == DbtModelHealthStatus.Degraded)
            {
                bool isPrivileged = principal?.IsInRole("GovernanceAdmin") == true ||
                                    principal?.IsInRole("DataOwner") == true ||
                                    principal?.IsInRole("ClusterAdmin") == true;

                if (!isPrivileged)
                {
                    if (accessResolver == null)
                    {
                        // Do not disclose quarantine or degraded status if access cannot be verified; pass through to downstream
                        continue;
                    }

                    var targetTable = tableId;
                    if (tableRepo != null)
                    {
                        try
                        {
                            var allTables = await tableRepo.GetAllTablesAsync(context.RequestAborted).ConfigureAwait(false);
                            var match = allTables.FirstOrDefault(t =>
                                string.Equals(t.Identifier.TableName, modelName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(t.Identifier.ToString(), modelName, StringComparison.OrdinalIgnoreCase));
                            if (match != null)
                            {
                                targetTable = match.Identifier;
                            }
                        }
                        catch
                        {
                            // Ignore catalog lookup error
                        }
                    }

                    try
                    {
                        var access = await accessResolver.ResolveTableAccessAsync(principal, targetTable, null, null, context.RequestAborted).ConfigureAwait(false);
                        if (!access.Decision.IsAllowed)
                        {
                            // Do not disclose quarantine or degraded status to unauthorized callers; pass through to downstream pipeline
                            continue;
                        }
                    }
                    catch
                    {
                        // Table unknown or unresolvable; pass through to downstream
                        continue;
                    }
                }
            }

            if (health.Status == DbtModelHealthStatus.Quarantined)
            {
                bool isPrivileged = principal?.IsInRole("GovernanceAdmin") == true ||
                                    principal?.IsInRole("DataOwner") == true ||
                                    principal?.IsInRole("ClusterAdmin") == true;

                var errBuilder = ErrorBuilder.New()
                    .SetMessage($"The requested table/model '{modelName}' is quarantined due to failed dbt tests.")
                    .SetCode("TABLE_IN_QUARANTINE")
                    .SetExtension("table", modelName)
                    .SetExtension("dbtHealthStatus", "Quarantined");

                if (isPrivileged)
                {
                    errBuilder.SetExtension("activeFailures", health.ActiveFailures.Select(f => new
                    {
                        testName = f.TestName,
                        columnName = f.ColumnName,
                        severity = f.Severity,
                        message = f.Message,
                        failedRowsCount = f.FailedRowsCount
                    }).ToList());
                }

                context.Result = OperationResult.FromError(errBuilder.Build());
                return;
            }

            if (health.Status == DbtModelHealthStatus.Degraded)
            {
                degradedModels.Add(health);
            }
        }

        // 3. Execute downstream pipeline
        await _next(context).ConfigureAwait(false);

        // 4. Enrich result with health warnings if any models were degraded
        if (degradedModels.Count > 0 && context.Result is OperationResult opResult)
        {
            var healthWarning = new Dictionary<string, object?>
            {
                ["status"] = "DEGRADED",
                ["degradedModelsCount"] = degradedModels.Count,
                ["warnings"] = degradedModels.Select(d => new
                {
                    table = d.Table.TableName,
                    failures = d.ActiveFailures.Select(f => f.TestName).ToList()
                }).ToList()
            };

            var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
            opResult.Extensions = extensions.SetItem("dbt_health", healthWarning);
        }
    }

    private static HashSet<string> ExtractReferencedModels(DocumentNode document)
    {
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in document.Definitions)
        {
            if (definition is OperationDefinitionNode opDef)
            {
                CollectModelsFromSelections(opDef.SelectionSet.Selections, models);
            }
        }

        return models;
    }

    private static void CollectModelsFromSelections(IEnumerable<ISelectionNode> selections, HashSet<string> models)
    {
        foreach (var selection in selections)
        {
            if (selection is FieldNode fieldNode)
            {
                var name = fieldNode.Name.Value;
                if (!name.StartsWith("__", StringComparison.Ordinal))
                {
                    models.Add(name);

                    // Check arguments: if argument is "name" or "table"
                    foreach (var arg in fieldNode.Arguments)
                    {
                        if (string.Equals(arg.Name.Value, "name", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(arg.Name.Value, "table", StringComparison.OrdinalIgnoreCase))
                        {
                            if (arg.Value is StringValueNode strVal)
                            {
                                models.Add(strVal.Value);
                            }
                        }
                    }

                    // Traverse nested selections (e.g. finance { invoices { ... } })
                    if (fieldNode.SelectionSet != null)
                    {
                        CollectModelsFromSelections(fieldNode.SelectionSet.Selections, models);
                    }
                }
            }
        }
    }
}
