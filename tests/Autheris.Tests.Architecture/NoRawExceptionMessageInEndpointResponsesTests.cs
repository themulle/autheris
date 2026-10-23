using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SR-L-03 guard: endpoint code must not put raw exception text into client responses. Sites that predate the guard
/// are baselined below (file name plus trimmed source line) and are retired by the central ProblemDetails work;
/// any new occurrence fails this test.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class NoRawExceptionMessageInEndpointResponsesTests
{
    // Entries are "<file name>|<trimmed source line>". Identical lines in one file are listed once per occurrence.
    private static readonly string[] Baseline =
    [
        "CatalogApiEndpoints.cs|return Results.BadRequest(new { error = ex.Message });",
        "DataContractEndpoints.cs|return Results.BadRequest(new { error = ex.Message });",
        "DuckDbOlapEndpoints.cs|await httpContext.Response.WriteAsJsonAsync(new { error = \"Analytical query execution failed.\", details = ex.Message }, ct);",
        "DuckDbOlapEndpoints.cs|await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);",
        "DuckDbOlapEndpoints.cs|await httpContext.Response.WriteAsJsonAsync(new { error = isDev ? ex.Message : \"OLAP query result exceeded maximum allowed memory size.\" }, ct);",
        "GovernanceEndpoints.Audit.cs|error = ex.Message,",
        "GovernanceEndpoints.Access.cs|isDevelopment ? $\"Invalid row filter syntax: {ex.Message}\" : \"The row filter predicate is not valid SQL.\",",
        "GovernanceEndpoints.Access.cs|reason = ex.Message,",
        "GovernanceEndpoints.Access.cs|reason = ex.Message,",
        "GovernancePlanEndpoints.cs|return Results.BadRequest(new { error = ex.Message });",
        "GovernancePlanEndpoints.cs|return Results.BadRequest(new { error = ex.Message });",
        "IcebergRestCatalogEndpoints.cs|ArgumentException => Results.Problem(detail: isProduction ? \"Access denied.\" : ex.Message, statusCode: StatusCodes.Status400BadRequest),",
        "IcebergRestCatalogEndpoints.cs|NotSupportedException => Results.Problem(detail: isProduction ? \"Not implemented.\" : ex.Message, statusCode: StatusCodes.Status501NotImplemented),",
        "IcebergRestCatalogEndpoints.cs|SecurityException => Results.Problem(detail: isProduction ? \"Access denied.\" : ex.Message, statusCode: StatusCodes.Status403Forbidden),",
        "IcebergRestCatalogEndpoints.cs|_ => Results.Problem(detail: isProduction ? \"An unexpected error occurred.\" : ex.Message, statusCode: StatusCodes.Status500InternalServerError)",
        "ProcedureEndpointRoutes.cs|await WriteErrorAsync(http, StatusCodes.Status400BadRequest, ex.Message, ct).ConfigureAwait(false);",
        "ProcedureEndpointRoutes.cs|await WriteErrorAsync(http, StatusCodes.Status422UnprocessableEntity, ex.Message, ct).ConfigureAwait(false);",
        "RebacEndpoints.cs|DetailsJson = JsonSerializer.Serialize(new { error = ex.Message, tenant = tenantId })",
        "VirtualFilterEndpoints.cs|return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);",
        "VirtualFilterEndpoints.cs|return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status404NotFound);",
        "VirtualFilterEndpoints.cs|return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);",
        "VirtualFilterEndpoints.cs|return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status400BadRequest);",
        "VirtualFilterEndpoints.cs|var message = isDev ? ex.Message : \"The requested virtual filter operation is invalid.\";"
    ];

    [GeneratedRegex(@"\b(ex|e|exception)\.Message\b(?!\s*\.\s*Contains)")]
    private static partial Regex ExceptionMessageAccess();

    [Fact]
    public void EndpointSources_DoNotExposeNewRawExceptionMessages()
    {
        var endpointsDir = FindEndpointsDirectory();
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(endpointsDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.Contains("logger", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ExceptionMessageAccess().IsMatch(line))
                {
                    found.Add($"{name}|{line}");
                }
            }
        }

        var allowed = Baseline.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var violations = new List<string>();
        foreach (var group in found.GroupBy(x => x))
        {
            var allowedCount = allowed.GetValueOrDefault(group.Key);
            if (group.Count() > allowedCount)
            {
                violations.Add(group.Key);
            }
        }

        violations.ShouldBeEmpty(
            "Raw exception text must not reach client responses (SR-L-03). Use SafeErrorResults with a fixed message and log the exception instead:\n"
            + string.Join("\n", violations));
    }

    private static string FindEndpointsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Autheris.Api", "Endpoints");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/Autheris.Api/Endpoints from the test output directory.");
    }
}
