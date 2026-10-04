namespace Autheris.Extensions.Backstage;

using System;
using System.IO;
using System.Text;
using Autheris.Domain.Model;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;

public static class BackstageYamlSerializer
{
    public static string Serialize(BackstageEntity entity)
    {
        var sb = new StringBuilder();
        sb.AppendLine("apiVersion: " + entity.ApiVersion);
        sb.AppendLine("kind: " + entity.Kind);
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: " + EscapeYamlString(entity.Metadata.Name));

        if (!string.IsNullOrWhiteSpace(entity.Metadata.Namespace))
            sb.AppendLine("  namespace: " + EscapeYamlString(entity.Metadata.Namespace));

        if (!string.IsNullOrWhiteSpace(entity.Metadata.Title))
            sb.AppendLine("  title: " + EscapeYamlString(entity.Metadata.Title));

        if (!string.IsNullOrWhiteSpace(entity.Metadata.Description))
            sb.AppendLine("  description: " + EscapeYamlString(entity.Metadata.Description));

        if (entity.Metadata.Tags.Count > 0)
        {
            sb.AppendLine("  tags:");
            foreach (var tag in entity.Metadata.Tags)
            {
                sb.AppendLine("    - " + EscapeYamlString(tag));
            }
        }

        if (entity.Metadata.Annotations.Count > 0)
        {
            sb.AppendLine("  annotations:");
            foreach (var (k, v) in entity.Metadata.Annotations)
            {
                sb.AppendLine($"    {EscapeYamlKey(k)}: {EscapeYamlString(v)}");
            }
        }

        if (entity.Metadata.Links.Count > 0)
        {
            sb.AppendLine("  links:");
            foreach (var link in entity.Metadata.Links)
            {
                sb.AppendLine("    - url: " + EscapeYamlString(link.Url));
                if (!string.IsNullOrWhiteSpace(link.Title))
                    sb.AppendLine("      title: " + EscapeYamlString(link.Title));
                if (!string.IsNullOrWhiteSpace(link.Icon))
                    sb.AppendLine("      icon: " + EscapeYamlString(link.Icon));
            }
        }

        sb.AppendLine("spec:");
        sb.AppendLine("  type: " + entity.Spec.Type);
        sb.AppendLine("  lifecycle: " + entity.Spec.Lifecycle);
        sb.AppendLine("  owner: " + EscapeYamlString(entity.Spec.Owner));

        if (!string.IsNullOrWhiteSpace(entity.Spec.System))
            sb.AppendLine("  system: " + EscapeYamlString(entity.Spec.System));

        if (!string.IsNullOrWhiteSpace(entity.Spec.Definition))
        {
            if (entity.Spec.Definition.Contains('\n'))
            {
                sb.AppendLine("  definition: |");
                using var reader = new StringReader(entity.Spec.Definition);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    sb.AppendLine("    " + line);
                }
            }
            else
            {
                sb.AppendLine("  definition: " + EscapeYamlString(entity.Spec.Definition));
            }
        }

        return sb.ToString();
    }

    private static string EscapeYamlString(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        // Check for YAML special characters or multiline
        if (value.Contains(':') || value.Contains('#') || value.Contains('\"') ||
            value.Contains('\'') || value.Contains('\n') || value.Contains('\r') ||
            value.StartsWith('@') || value.StartsWith('`') || value.StartsWith('-') ||
            value.StartsWith('[') || value.StartsWith('{') || value.StartsWith('*') ||
            value.StartsWith('&') || value.StartsWith('!') || value.StartsWith('|') ||
            value.StartsWith('>') || value.StartsWith('%'))
        {
            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "\"";
        }
        return value;
    }

    private static string EscapeYamlKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "\"\"";
        if (key.Contains('\"') || key.Contains('\'') || key.Contains('\n') || key.Contains('\r') || key.Contains(' ') || key.Contains(':'))
        {
            return "\"" + key
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "")
                .Replace("\n", "") + "\"";
        }
        return key;
    }
}
