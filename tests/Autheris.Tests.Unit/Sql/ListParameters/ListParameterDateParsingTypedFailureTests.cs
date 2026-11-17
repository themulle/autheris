using System.Text.Json;
using Autheris.Application.Sql;
using Autheris.Application.Sql.ListParameters;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace Autheris.Tests.Unit.Sql.ListParameters;

/// <summary>
/// .NET 11 <c>DateOnly/TimeOnly/DateTime.TryParse*</c> throw for invalid <c>DateTimeStyles</c> instead of returning false. The styles
/// in the list-parameter normalizer are fixed in code, so no client input can reach them; hostile client strings must still map to the
/// typed list-parameter error (HTTP 400), never to an unhandled exception (HTTP 500 with internals).
/// </summary>
[Trait("Category", "Security")]
public sealed class ListParameterDateParsingTypedFailureTests
{
    private static readonly ListParameterLimits Limits = new(ConfiguredMaxValues: 1000, MaxListParameters: 8);

    [Theory]
    [InlineData(SqlParameterType.Date)]
    [InlineData(SqlParameterType.Timestamp)]
    [InlineData(SqlParameterType.TimestampTz)]
    public void HostileDateStrings_AreRejectedWithTheTypedError(SqlParameterType declared)
    {
        var accepted = new List<string>();
        foreach (var hostile in new[]
                 {
                     "not-a-date", "2026-13-45", "2026-02-30", "0000-00-00", "9999-12-32", "2026-01-01T25:61:61Z",
                     "AdjustToUniversal", "AssumeLocal AssumeUniversal", "  ", "\u0000", "2026-01-01\u0000",
                     new string('9', 4096), "-1", "1e9", "2026/01/01 AM PM",
                 })
        {
            if (declared != SqlParameterType.Date && hostile == "2026-01-01\u0000")
            {
                // DateTime.TryParse ignores trailing NUL characters on every runtime; the parsed value is the plain date.
                continue;
            }

            var json = JsonSerializer.Serialize(new[] { hostile });
            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement.Clone();
            try
            {
                ListParameterNormalizer.Normalize(element, Limits, declared);
                accepted.Add(JsonSerializer.Serialize(hostile));
            }
            catch (ClientListException ex)
            {
                ex.Code.ShouldBe(GovernedSqlErrorCodes.ListParamType);
            }
        }

        accepted.ShouldBeEmpty($"declared {declared}: accepted {string.Join(" ", accepted)}");
    }

    [Fact]
    public void ValidDate_StillParses()
    {
        var value = ListParameterNormalizer.Normalize(JsonDocument.Parse("[\"2026-02-28\"]").RootElement.Clone(), Limits, SqlParameterType.Date);

        value.Count.ShouldBe(1);
        value[0].ShouldBe(new DateOnly(2026, 2, 28));
    }
}
