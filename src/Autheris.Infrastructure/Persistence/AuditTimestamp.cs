namespace Autheris.Infrastructure.Persistence;

using System;
using System.Globalization;

/// <summary>
/// PERS-01: the one format of audit timestamps in text columns. Stored values and the values compared against them (since, from, to) are
/// always UTC ISO-8601 strings, so the lexicographic comparison in SQL matches the instant comparison, and the hash input of a row is
/// the same string that is stored.
/// </summary>
internal static class AuditTimestamp
{
    internal static string ToStorage(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
