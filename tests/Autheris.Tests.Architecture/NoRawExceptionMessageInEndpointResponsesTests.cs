using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SR-L-03 guard: endpoint code must not put raw exception text into client responses or audit details. The baseline is empty
/// (ARCH-MNT-08); any occurrence fails this test.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class NoRawExceptionMessageInEndpointResponsesTests
{
    private static readonly string[] ScannedDirectories = ["src/Autheris.Api/Endpoints", "src/Autheris.Api/Mcp"];

    [GeneratedRegex(@"\b(?:e\d*|err|error|inner|\w*ex\d*|\w*[Ee]xception\d*)\.(?:Message|InnerException|StackTrace|ToString\(\))(?!\s*\.\s*Contains)")]
    private static partial Regex ExceptionMessageAccess();

    [Fact]
    public void ArchTest_NoExceptionMessageInEndpoints_EmptyBaseline()
    {
        var found = new List<string>();
        var scannedFiles = new List<string>();
        foreach (var dir in ScannedDirectories)
        {
            foreach (var file in RepositoryScan.EnumerateFiles(dir, "*.cs"))
            {
                scannedFiles.Add(Path.GetFileName(file));
                found.AddRange(FindOccurrences(Path.GetFileName(file), RepositoryScan.ReadText(file)));
            }
        }

        // Non-vacuity (ARCH-MNT-08-SC-09): the scan must really cover the files that used to leak.
        scannedFiles.ShouldContain("GovernancePlanEndpoints.cs");
        scannedFiles.ShouldContain("VirtualFilterEndpoints.cs");
        scannedFiles.ShouldContain("IcebergRestCatalogEndpoints.cs");
        scannedFiles.ShouldContain("McpStdioRunner.cs");
        scannedFiles.Count.ShouldBeGreaterThan(30);

        found.ShouldBeEmpty(
            "Raw exception text must not reach client responses or audit details (SR-L-03, ARCH-MNT-08). Use ApiProblems with a fixed detail and log the exception instead:\n"
            + string.Join("\n", found));
    }

    [Fact]
    public void WidenedRegex_DetectsSexAndOtherExceptionVariables()
    {
        FindOccurrences("S.cs", "return Results.Json(new { error = sex.Message }, statusCode: 403);").Count.ShouldBe(1);
        FindOccurrences("S.cs", "reason = ex.InnerException,").Count.ShouldBe(1);
        FindOccurrences("S.cs", "var t = ex.ToString();").Count.ShouldBe(1);
        FindOccurrences("S.cs", "x = err.Message;").Count.ShouldBe(1);
        FindOccurrences("S.cs", "x = error.Message;").Count.ShouldBe(1);
        FindOccurrences("S.cs", "x = ex2.Message;").Count.ShouldBe(1);
        FindOccurrences("S.cs", "x = inner.Message;").Count.ShouldBe(1);
        FindOccurrences("S.cs", "reason = ex.GetType().Name,").ShouldBeEmpty();
        FindOccurrences("S.cs", "catch (ArgumentException ex) when (ex.Message.Contains(\"X\"))").ShouldBeEmpty();
    }

    [Fact]
    public void ResultsJson_ExceptionMessage_IsDetected()
    {
        var sample = "try { }\ncatch (Exception ex)\n{\n    return Results.Json(new { error = ex.Message }, statusCode: 500);\n}\n";

        FindOccurrences("Sample.cs", sample).ShouldBe(["Sample.cs|return Results.Json(new { error = ex.Message }, statusCode: 500);"]);
        FindOccurrences("Sample.cs", "return Results.Json(new { error = \"fixed\" }, statusCode: 500);").ShouldBeEmpty();
    }

    [Fact]
    public void JsonRpcError_ExceptionMessage_IsDetected()
    {
        var sample = "var errorResponse = $\"{{\\\"error\\\":{{\\\"code\\\":-32603,\\\"message\\\":{Serialize(ex.Message)}}}}}\";\r\n";

        FindOccurrences("Runner.cs", sample).Count.ShouldBe(1);
        FindOccurrences("Runner.cs", "var errorResponse = \"{\\\"code\\\":-32603,\\\"message\\\":\\\"Internal error\\\"}\";").ShouldBeEmpty();
    }

    private static List<string> FindOccurrences(string fileName, string text)
    {
        var found = new List<string>();
        foreach (var line in RepositoryScan.CodeLines(text))
        {
            if (line.Contains("logger", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ExceptionMessageAccess().IsMatch(line))
            {
                found.Add($"{fileName}|{line}");
            }
        }

        return found;
    }
}
