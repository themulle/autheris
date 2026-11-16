using System.Diagnostics;
using System.Text.RegularExpressions;
using Autheris.Domain.Diagnostics;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

public class OpenTelemetryPiiScannerTests
{
    private static readonly Regex EmailRegex = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,7}\b", RegexOptions.Compiled);
    private static readonly Regex IbanRegex = new(@"\b[A-Z]{2}[0-9]{2}[A-Z0-9]{4}[0-9]{7}([A-Z0-9]?){0,16}\b", RegexOptions.Compiled);
    // A card number is a standalone 16-digit group that passes the Luhn check; digit runs inside a longer hyphenated identifier
    // (a GUID such as 'audit-entry-0000-0000-0000-0000-...') are not card numbers.
    private static readonly Regex CreditCardRegex = new(@"(?<![\w-])(?:\d{4}[ -]?){3}\d{4}(?![\w-])", RegexOptions.Compiled);

    private static bool ContainsCardNumber(string value)
    {
        foreach (Match match in CreditCardRegex.Matches(value))
        {
            var sum = 0;
            var alternate = false;
            for (var i = match.Value.Length - 1; i >= 0; i--)
            {
                var c = match.Value[i];
                if (!char.IsDigit(c))
                {
                    continue;
                }

                var digit = c - '0';
                if (alternate)
                {
                    digit *= 2;
                    if (digit > 9)
                    {
                        digit -= 9;
                    }
                }

                sum += digit;
                alternate = !alternate;
            }

            if (sum % 10 == 0)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void SetSafeTag_AllowsWhitelistedKeysOnly()
    {
        using var source = new ActivitySource("Test.Source");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("Governance.EvaluateConsent");
        activity.ShouldNotBeNull();

        // 1. Whitelisted keys should be set
        GatewayDiagnostics.SetSafeTag(activity, "Governance.EvaluateConsent", "user.sid", "S-1-5-21-test");
        GatewayDiagnostics.SetSafeTag(activity, "Governance.EvaluateConsent", "tenant.id", "tenant-eu-1");

        activity.GetTagItem("user.sid").ShouldBe("S-1-5-21-test");
        activity.GetTagItem("tenant.id").ShouldBe("tenant-eu-1");

        // 2. Non-whitelisted or PII keys should be rejected/ignored
        GatewayDiagnostics.SetSafeTag(activity, "Governance.EvaluateConsent", "user.email", "admin@corp.local");
        GatewayDiagnostics.SetSafeTag(activity, "Governance.EvaluateConsent", "iban", "DE89370400440532013000");
        GatewayDiagnostics.SetSafeTag(activity, "Governance.EvaluateConsent", "credit_card", "4111-2222-3333-4444");

        activity.GetTagItem("user.email").ShouldBeNull();
        activity.GetTagItem("iban").ShouldBeNull();
        activity.GetTagItem("credit_card").ShouldBeNull();
    }

    [Fact]
    public void PiiRegexGuardrail_VerifiesNoPiiInExportedActivityTags()
    {
        using var source = new ActivitySource("Test.PiiGuardrail");
        var exportedTags = new List<KeyValuePair<string, string?>>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Test.PiiGuardrail",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = act =>
            {
                lock (exportedTags)
                {
                    foreach (var tag in act.Tags)
                    {
                        exportedTags.Add(tag);
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        using (var act = source.StartActivity("AuditLog.AppendHmacEntry"))
        {
            act.ShouldNotBeNull();
            GatewayDiagnostics.SetSafeTag(act, "AuditLog.AppendHmacEntry", "audit.entry_id", "audit-entry-00000000-0000-0000-0000-000000000001");
            GatewayDiagnostics.SetSafeTag(act, "AuditLog.AppendHmacEntry", "tenant.id", "tenant-100");
            GatewayDiagnostics.SetSafeTag(act, "AuditLog.AppendHmacEntry", "chain.height", 42L);

            // Attempt to inject PII through SetSafeTag - should be rejected by allow list
            GatewayDiagnostics.SetSafeTag(act, "AuditLog.AppendHmacEntry", "email", "sensitive.user@domain.com");
        }

        // Run PII Regex scanner over all recorded tag values
        List<KeyValuePair<string, string?>> snapshot;
        lock (exportedTags)
        {
            snapshot = exportedTags.ToList();
        }

        foreach (var tag in snapshot)
        {
            var strVal = tag.Value?.ToString() ?? string.Empty;
            EmailRegex.IsMatch(strVal).ShouldBeFalse($"Tag '{tag.Key}' with value '{strVal}' contains an email address!");
            IbanRegex.IsMatch(strVal).ShouldBeFalse($"Tag '{tag.Key}' with value '{strVal}' contains an IBAN!");
            ContainsCardNumber(strVal).ShouldBeFalse($"Tag '{tag.Key}' with value '{strVal}' contains a Credit Card!");
        }
    }
}
