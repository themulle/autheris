namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Serializes <see cref="UsageReportResponse"/> to RFC 4180 CSV with spreadsheet formula neutralization
/// and per-unit-label cost row flattening.
/// </summary>
public static class UsageCsvWriter
{
    private const string MultiCountedHeaderComment = "# multi-counted: a user in N groups is counted in each group";

    private static readonly string[] HeaderColumns =
    [
        "key", "day", "user_key", "group_sid", "group_name", "billing_group", "data_source", "table_id",
        "transport", "application", "client_id", "grant_id", "requests", "queries", "rows_returned",
        "rows_measured", "bytes", "duration_ms", "analysis_cost", "source_compute_ms",
        "source_compute_measured", "source_cost", "source_cost_unit", "allow_count", "deny_access",
        "deny_rate_limit", "deny_cost", "deny_quota", "deny_other", "errors", "cost_units",
        "unit_label", "tariff_missing"
    ];

    public static string Write(UsageReportResponse report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();

        if (report.MultiCounted)
        {
            sb.Append(MultiCountedHeaderComment).Append("\r\n");
        }

        AppendRow(sb, HeaderColumns);
        foreach (var row in report.Rows)
        {
            if (row.Cost.Count == 0)
            {
                AppendReportRow(sb, row, "0", string.Empty);
                continue;
            }

            foreach (var bucket in row.Cost)
            {
                AppendReportRow(
                    sb,
                    row,
                    bucket.CostUnits.ToString("0.######", CultureInfo.InvariantCulture),
                    bucket.UnitLabel);
            }
        }

        return sb.ToString();
    }

    public static string BuildContentDisposition(string reportSlug, DateOnly fromUtc, DateOnly toUtc)
    {
        string safeSlug = SanitizeSlug(reportSlug);
        string fromStr = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string toStr = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"attachment; filename=\"usage-{safeSlug}-{fromStr}-{toStr}.csv\"";
    }

    /// <summary>
    /// Neutralizes spreadsheet formula triggers (=, +, -, @, tab, CR) before applying RFC 4180 quoting.
    /// </summary>
    public static string FormatCell(string? value)
    {
        string raw = value ?? string.Empty;
        if (raw.Length > 0 && raw[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            raw = "'" + raw;
        }

        if (raw.IndexOfAny([',', '"', '\n', '\r']) >= 0)
        {
            return "\"" + raw.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return raw;
    }

    private static void AppendReportRow(StringBuilder sb, UsageReportRowDto r, string costUnits, string unitLabel)
    {
        ReadOnlySpan<string?> cells =
        [
            r.Key,
            r.Day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            r.UserKey ?? string.Empty,
            r.GroupSid ?? string.Empty,
            r.GroupName ?? string.Empty,
            r.BillingGroup ?? string.Empty,
            r.DataSource ?? string.Empty,
            r.TableId ?? string.Empty,
            r.Transport ?? string.Empty,
            r.Application ?? string.Empty,
            r.ClientId ?? string.Empty,
            r.GrantId ?? string.Empty,
            r.Requests.ToString(CultureInfo.InvariantCulture),
            r.Queries.ToString(CultureInfo.InvariantCulture),
            r.RowsReturned.ToString(CultureInfo.InvariantCulture),
            r.RowsMeasured.ToString(CultureInfo.InvariantCulture),
            r.Bytes.ToString(CultureInfo.InvariantCulture),
            r.DurationMs.ToString(CultureInfo.InvariantCulture),
            r.AnalysisCost.ToString(CultureInfo.InvariantCulture),
            r.SourceComputeMs.ToString(CultureInfo.InvariantCulture),
            r.SourceComputeMeasured.ToString(CultureInfo.InvariantCulture),
            r.SourceCost?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty,
            r.SourceCostUnit ?? string.Empty,
            r.AllowCount.ToString(CultureInfo.InvariantCulture),
            r.DenyAccess.ToString(CultureInfo.InvariantCulture),
            r.DenyRateLimit.ToString(CultureInfo.InvariantCulture),
            r.DenyCost.ToString(CultureInfo.InvariantCulture),
            r.DenyQuota.ToString(CultureInfo.InvariantCulture),
            r.DenyOther.ToString(CultureInfo.InvariantCulture),
            r.Errors.ToString(CultureInfo.InvariantCulture),
            costUnits,
            unitLabel,
            r.TariffMissing ? "true" : "false"
        ];

        AppendRow(sb, cells);
    }

    private static void AppendRow(StringBuilder sb, ReadOnlySpan<string?> cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(FormatCell(cells[i]));
        }

        sb.Append("\r\n");
    }

    private static string SanitizeSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "report";
        }

        var sb = new StringBuilder(slug.Length);
        bool prevDash = true;
        foreach (char ch in slug)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevDash = false;
            }
            else if (!prevDash)
            {
                sb.Append('-');
                prevDash = true;
            }
        }

        while (sb.Length > 0 && sb[^1] == '-')
        {
            sb.Length--;
        }

        return sb.Length > 0 ? sb.ToString() : "report";
    }
}
