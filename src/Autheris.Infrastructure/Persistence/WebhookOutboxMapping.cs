namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>Dialect-neutral helpers shared by the three <c>IWebhookOutbox</c> repository partials.</summary>
internal static class WebhookOutboxMapping
{
    /// <summary>Column order read by <see cref="ReadMessage"/> (RETURNING / OUTPUT / SELECT lists must follow it).</summary>
    public const string Columns =
        "id, source, subscription_id, owner_tenant_id, event_tenant_id, event_id, event_type, event_source, event_time_ms, payload_json, attempt, max_attempts, state, next_attempt_ms, lease_until_ms, lease_owner, last_status_code, last_error_code, created_ms";

    /// <summary>PostgreSQL <c>RETURNING</c> list (alias <c>o</c>).</summary>
    public static readonly string ReturningAliasO = Prefixed("o.");

    /// <summary>SQL Server <c>OUTPUT</c> list.</summary>
    public static readonly string OutputInserted = Prefixed("inserted.");

    private static string Prefixed(string prefix) =>
        string.Join(", ", Columns.Split(", ", StringSplitOptions.None).Select(c => prefix + c));

    public static long Ms(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    public static int ClampLimit(int limit) => Math.Clamp(limit, 1, 1000);

    /// <summary>Fail-closed scope check: a tenant scope needs a tenant id; cluster scope must not carry one. No all-tenants listing exists.</summary>
    public static void ValidateScope(WebhookOwnerScope scope)
    {
        if (scope.ClusterScope)
        {
            if (!string.IsNullOrWhiteSpace(scope.TenantId))
            {
                throw new ArgumentException("A cluster scope must not carry a tenant id.", nameof(scope));
            }
        }
        else if (string.IsNullOrWhiteSpace(scope.TenantId))
        {
            throw new ArgumentException("A tenant id is required unless the cluster scope is requested.", nameof(scope));
        }
    }

    public static WebhookOutboxMessage ReadMessage(DbDataReader r)
    {
        return new WebhookOutboxMessage(
            Id: r.GetString(0),
            Source: (WebhookSubscriptionSource)Int(r, 1),
            SubscriptionId: r.GetString(2),
            OwnerTenantId: Str(r, 3),
            EventTenantId: Str(r, 4),
            EventId: r.GetString(5),
            EventType: r.GetString(6),
            EventSource: r.GetString(7),
            EventTime: FromMs(Long(r, 8)),
            PayloadJson: r.GetString(9),
            Attempt: Int(r, 10),
            MaxAttempts: Int(r, 11),
            State: (WebhookOutboxState)Int(r, 12),
            NextAttemptAt: FromMs(Long(r, 13)),
            LeaseUntil: r.IsDBNull(14) ? null : FromMs(Long(r, 14)),
            LeaseOwner: Str(r, 15),
            LastStatusCode: r.IsDBNull(16) ? null : Int(r, 16),
            LastErrorCode: Str(r, 17),
            CreatedAt: FromMs(Long(r, 18)));
    }

    public static WebhookDeadLetter ReadDeadLetter(DbDataReader r) => new(
        Id: r.GetString(0),
        SubscriptionId: r.GetString(1),
        EventId: r.GetString(2),
        EventType: r.GetString(3),
        Attempt: Int(r, 4),
        LastStatusCode: r.IsDBNull(5) ? null : Int(r, 5),
        LastErrorCode: Str(r, 6),
        CreatedAt: FromMs(Long(r, 7)));

    public const string DeadLetterColumns = "id, subscription_id, event_id, event_type, attempt, last_status_code, last_error_code, created_ms";

    /// <summary>RETURNING / OUTPUT order is not guaranteed in any dialect.</summary>
    public static IReadOnlyList<WebhookOutboxMessage> SortByNextAttempt(List<WebhookOutboxMessage> rows)
    {
        rows.Sort(static (a, b) => a.NextAttemptAt.CompareTo(b.NextAttemptAt));
        return rows;
    }

    private static int Int(DbDataReader r, int i) => Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);

    private static long Long(DbDataReader r, int i) => Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);

    private static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
