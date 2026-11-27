using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// The <c>detail</c> of an MCP INVALID_PARAMS or INVALID_QUERY error is a fixed text of the server. A producer call with one of these codes
/// must pass a string literal, a constant of the allowlist or <c>McpErrorCodes.FixedMessage(...)</c>; a variable, an interpolated string or
/// an exception text would let request input reach the client. The runtime registry of server texts is the second layer.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class McpErrorDetailArchitectureTests
{
    /// <summary>Constants (and the one forwarding parameter) known to hold fixed texts.</summary>
    private static readonly string[] AllowedIdentifiers =
    [
        "InvalidArgumentsCode", "PersistedToolValidator.InvalidArgumentsCode", "InvalidFilterMessage", "CatalogAccessStatus.InvalidFilterMessage",
        "Autheris.Application.Catalog.Discovery.CatalogAccessStatus.InvalidFilterMessage", "InvalidSearchQueryMessage",
        "McpSqlParameters.InvalidMessage", "McpSqlParameters.InvalidValueMessage", "string.Empty", "\"\"",
        // GatewayMcpQueryExecutor.Invalid(string message): every caller of that local function passes a literal.
        "message"
    ];

    [GeneratedRegex(@"(?:McpToolError\.(?:Fail|Serialize)|CreateErrorResult|CreateMcpError|new\s+McpToolErrorBody)\s*\(")]
    private static partial Regex Call();

    [GeneratedRegex(@"InvalidParams|INVALID_PARAMS|InvalidQuery|INVALID_QUERY")]
    private static partial Regex InvalidCode();

    [GeneratedRegex(@"^(?:@?""(?:[^""\\]|\\.)*""\s*\+?\s*)+$")]
    private static partial Regex Literal();

    [GeneratedRegex(@"^""""""[\s\S]*""""""$")]
    private static partial Regex RawLiteral();

    private static List<string> Violations(IEnumerable<(string Path, string Text)> files)
    {
        var problems = new List<string>();
        foreach (var (path, text) in files)
        {
            foreach (Match call in Call().Matches(text))
            {
                var args = SplitArguments(text, call.Index + call.Length);
                if (args is null)
                {
                    continue;
                }

                var head = call.Value;
                int codeIndex, textIndex;
                if (head.Contains("CreateErrorResult") || head.Contains("CreateMcpError"))
                {
                    (codeIndex, textIndex) = (2, 3);
                }
                else
                {
                    (codeIndex, textIndex) = (0, 1);
                }

                if (args.Count <= textIndex || !InvalidCode().IsMatch(args[codeIndex]))
                {
                    continue;
                }

                var detail = args[textIndex].Trim();
                if (!IsFixedText(detail))
                {
                    var line = text[..call.Index].Count(c => c == '\n') + 1;
                    problems.Add($"{path}:{line}: INVALID_PARAMS/INVALID_QUERY detail must be a literal or a known constant, found `{detail}`");
                }
            }
        }

        return problems;
    }

    private static bool IsFixedText(string argument) =>
        Literal().IsMatch(argument) || RawLiteral().IsMatch(argument) || AllowedIdentifiers.Contains(argument, StringComparer.Ordinal)
        || argument.StartsWith("McpErrorCodes.FixedMessage(", StringComparison.Ordinal);

    /// <summary>The top-level arguments of the call whose opening parenthesis precedes <paramref name="start"/>; null when it cannot be parsed.</summary>
    private static List<string>? SplitArguments(string text, int start)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        int depth = 1;
        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                // A string literal (regular, verbatim, interpolated or raw): copy it whole.
                int quotes = 1;
                while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;
                if (quotes >= 3)
                {
                    var end = text.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                    if (end < 0) return null;
                    current.Append(text, i, end + quotes - i);
                    i = end + quotes - 1;
                    continue;
                }

                int j = i + 1;
                while (j < text.Length && text[j] != '"')
                {
                    if (text[j] == '\\') j++;
                    j++;
                }

                if (j >= text.Length) return null;
                current.Append(text, i, j - i + 1);
                i = j;
                continue;
            }

            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']')
            {
                depth--;
                if (depth == 0)
                {
                    if (current.ToString().Trim().Length > 0) args.Add(current.ToString().Trim());
                    return args;
                }
            }
            else if (c == ',' && depth == 1)
            {
                args.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        return null;
    }

    [Fact]
    public void InvalidParamsAndInvalidQueryDetails_AreConstantText()
    {
        var files = RepositoryScan.ProductionFiles;
        files.Count.ShouldBeGreaterThan(500, "the scan must cover the production tree");

        var problems = Violations(files.Select(f => (f.RelativePath, f.Text)));

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Guard_Detects_Planted_NonConstantDetails()
    {
        string[] planted =
        [
            "return McpToolError.Fail(McpErrorCodes.InvalidParams, ex.Message);",
            "return McpToolError.Serialize(\"INVALID_PARAMS\", $\"bad {name}\");",
            "return CreateErrorResult(tenant, tool.Name, McpErrorCodes.InvalidQuery,\n    detail);",
            "return CreateMcpError(t, tool, InvalidParamsCode, \"a\" + name);",
            "return McpToolError.Serialize(new McpToolErrorBody(McpErrorCodes.InvalidParams, text));"
        ];
        foreach (var text in planted)
        {
            Violations([("src/Planted.cs", text)]).Count.ShouldBe(1, text);
        }

        string[] allowed =
        [
            "return McpToolError.Fail(McpErrorCodes.InvalidParams, \"Fixed text, with a comma.\");",
            "return McpToolError.Serialize(\"INVALID_PARAMS\", McpErrorCodes.FixedMessage(code));",
            "return CreateMcpError(t, tool, InvalidParamsCode, CatalogAccessStatus.InvalidFilterMessage);",
            "return McpToolError.Fail(McpErrorCodes.DatasetNotFound, ex.Message);"
        ];
        foreach (var text in allowed)
        {
            Violations([("src/Planted.cs", text)]).ShouldBeEmpty(text);
        }
    }
}
