namespace Autheris.Application.Events.Services;

using System;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Table and operation filter matching shared by every <c>ICloudEventSubscriptionStore</c> implementation.</summary>
public static class CloudEventSubscriptionMatcher
{
    public static bool Matches(CloudEventWebhookSubscription subscription, TableIdentifier table, CdcOperation operation)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return subscription.IsEnabled
               && MatchesTableFilter(subscription.FilterTable, table)
               && MatchesOperation(subscription, operation);
    }

    /// <summary>Overload for table names that are not a parsable <see cref="TableIdentifier"/>: plain case-insensitive name comparison.</summary>
    public static bool Matches(CloudEventWebhookSubscription subscription, string tableName, CdcOperation operation)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (TableIdentifier.TryParse(tableName, out var table))
        {
            return Matches(subscription, table, operation);
        }

        return subscription.IsEnabled
               && (subscription.FilterTable == "*" || string.Equals(subscription.FilterTable, tableName, StringComparison.OrdinalIgnoreCase))
               && MatchesOperation(subscription, operation);
    }

    private static bool MatchesOperation(CloudEventWebhookSubscription subscription, CdcOperation operation) =>
        subscription.FilterOperations.Count == 0 || subscription.FilterOperations.Contains(operation);

    private static bool MatchesTableFilter(string filterTable, TableIdentifier table)
    {
        if (filterTable == "*") return true;
        if (string.Equals(filterTable, table.TableName, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(filterTable, table.ToQualifiedName(), StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(filterTable, table.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
