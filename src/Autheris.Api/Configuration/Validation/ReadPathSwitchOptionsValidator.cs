using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// PLAN-SEC-115 A-8 / C-115-2: fail-closed start-up validation of the read-path switches. A switch the operator wrote to CLOSE a path
/// must never be silently ignored, so the raw configuration is checked, not only the bound options: unknown keys under the switch
/// nodes, a scalar where a section is expected, null/empty switch values, anything but a strict <c>true</c>/<c>false</c>, a scalar
/// or malformed <c>DisabledTools</c>, blank elements and unknown tool names. All checks run regardless of <c>Mcp:Enabled</c>.
/// Messages name key paths only; the one echoed value is an unknown tool name.
/// </summary>
public sealed class ReadPathSwitchOptionsValidator : IValidateOptions<GatewayOptions>
{
    private static readonly string[] ToolGroupKeys = ["Catalog", "Query", "Api", "Access"];
    private static readonly string[] DataEndpointKeys = ["Enabled"];
    private static readonly string[] BulkStreamKeys = ["Enabled", "SigningKey"];

    private readonly IConfiguration? _configuration;

    public ReadPathSwitchOptionsValidator(IConfiguration? configuration = null)
    {
        _configuration = configuration;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateToolNames(options.Mcp?.DisabledTools, options.Mcp?.AllowedOperations, failures);
        if (_configuration is not null)
        {
            ValidateRawConfiguration(_configuration, failures);
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void ValidateToolNames(IReadOnlyList<string>? disabled, IReadOnlyList<string>? allowedOperations, List<string> failures)
    {
        if (disabled is null) return;

        var derived = (allowedOperations ?? [])
            .Where(o => o is not null)
            .Select(o => o.ToLowerInvariant().Replace(' ', '_'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in disabled)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                failures.Add("Gateway:Mcp:DisabledTools contains a blank entry.");
                continue;
            }

            var trimmed = entry.Trim();
            if (!McpToolGroups.IsKnownBuiltIn(trimmed) && !derived.Contains(trimmed))
            {
                failures.Add(
                    $"Gateway:Mcp:DisabledTools contains the unknown tool name '{entry}'. " +
                    $"Valid built-in names: {string.Join(", ", McpToolGroups.BuiltInNames.Order(StringComparer.Ordinal))}. " +
                    "Names of GraphQL-discovered tools cannot be listed; use Gateway:Mcp:Tools:Query instead.");
            }
        }
    }

    private static void ValidateRawConfiguration(IConfiguration configuration, List<string> failures)
    {
        var root = GatewayOptions.SectionName;

        CheckParentForTypos(configuration.GetSection(root), root, typeof(GatewayOptions), failures);
        CheckParentForTypos(configuration.GetSection($"{root}:Mcp"), $"{root}:Mcp", typeof(McpOptions), failures);

        var restApi = configuration.GetSection($"{root}:RestApi");
        CheckNode(restApi, $"{root}:RestApi", ["DataEndpoint", "BulkStream"], failures);
        var dataEndpoint = restApi.GetSection("DataEndpoint");
        CheckNode(dataEndpoint, $"{root}:RestApi:DataEndpoint", DataEndpointKeys, failures);
        CheckSwitch(dataEndpoint, "Enabled", $"{root}:RestApi:DataEndpoint:Enabled", failures);

        var bulkStream = restApi.GetSection("BulkStream");
        if (bulkStream.Exists())
        {
            CheckNode(bulkStream, $"{root}:RestApi:BulkStream", BulkStreamKeys, failures);
            CheckSwitch(bulkStream, "Enabled", $"{root}:RestApi:BulkStream:Enabled", failures);
        }

        var tools = configuration.GetSection($"{root}:Mcp:Tools");
        CheckNode(tools, $"{root}:Mcp:Tools", ToolGroupKeys, failures);
        foreach (var key in ToolGroupKeys)
        {
            CheckSwitch(tools, key, $"{root}:Mcp:Tools:{key}", failures);
        }

        CheckDisabledToolsShape(configuration.GetSection($"{root}:Mcp:DisabledTools"), $"{root}:Mcp:DisabledTools", failures);
    }

    private static readonly string[] SwitchNodeNames = ["Tools", "DisabledTools", "RestApi", "DataEndpoint", "BulkStream"];

    /// <summary>
    /// A misspelt parent key (<c>Gateway:Mcp:Tool</c>, <c>Gateway:Mcp:DisabledTool</c>, <c>Gateway:RestApis</c>) is ignored by the binder, so
    /// a closing switch would silently stay open. A child that is no property of the options type but is a near miss of a switch
    /// node name fails the start.
    /// </summary>
    private static void CheckParentForTypos(IConfigurationSection parent, string path, Type optionsType, List<string> failures)
    {
        var known = optionsType.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var child in parent.GetChildren())
        {
            if (known.Contains(child.Key)) continue;
            var near = SwitchNodeNames.FirstOrDefault(n => IsNearMiss(child.Key, n));
            if (near is not null)
            {
                failures.Add($"{path} contains the unknown key '{child.Key}', which looks like a misspelling of '{near}'.");
            }
        }
    }

    private static bool IsNearMiss(string key, string target)
    {
        if (string.Equals(key, target, StringComparison.OrdinalIgnoreCase)) return false;
        if (key.Length >= 4 &&
            (target.Contains(key, StringComparison.OrdinalIgnoreCase) || key.Contains(target, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return EditDistance(key.ToLowerInvariant(), target.ToLowerInvariant()) <= 2;
    }

    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    /// <summary>A node must be a section: no scalar value of its own, only keys from the closed set (case-insensitive).</summary>
    private static void CheckNode(IConfigurationSection node, string path, string[] allowedKeys, List<string> failures)
    {
        if (!string.IsNullOrEmpty(node.Value))
        {
            failures.Add($"{path} must be a configuration section, not a scalar value.");
        }

        foreach (var child in node.GetChildren())
        {
            if (!allowedKeys.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"{path} contains the unknown key '{child.Key}'. Valid keys: {string.Join(", ", allowedKeys)}.");
            }
        }
    }

    private static void CheckSwitch(IConfigurationSection parent, string key, string path, List<string> failures)
    {
        foreach (var child in parent.GetChildren().Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            var value = child.Value;
            var isBoolean = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            if (!isBoolean)
            {
                failures.Add($"{path} must be exactly true or false (it is null, empty or not a boolean).");
            }
        }
    }

    private static void CheckDisabledToolsShape(IConfigurationSection node, string path, List<string> failures)
    {
        // An empty list (JSON [] or an empty environment value) is "nothing disabled"; any other scalar is an operator
        // mistake (for example GATEWAY__MCP__DISABLEDTOOLS=query_sql) that would silently disable nothing.
        if (!string.IsNullOrEmpty(node.Value))
        {
            failures.Add($"{path} must be a list of tool names, not a scalar value (use {path}:0, {path}:1, ... or a JSON array).");
        }

        foreach (var child in node.GetChildren())
        {
            if (!int.TryParse(child.Key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                failures.Add($"{path} contains the non-index key '{child.Key}'; it must be a list.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(child.Value))
            {
                failures.Add($"{path}:{child.Key} is null or blank.");
            }

            if (child.GetChildren().Any())
            {
                failures.Add($"{path}:{child.Key} must be a tool name, not a section.");
            }
        }
    }
}
