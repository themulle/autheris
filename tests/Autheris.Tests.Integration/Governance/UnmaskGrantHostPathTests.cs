namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// #1009 A-3 through the real host: a classified column is masked on default access and an active per-object unmask grant returns
/// it in clear, while a <c>CREDENTIALS</c> column stays masked even when the grant names it. The grant is read through the registered
/// consent repository (the attribute-validating decorator), so a decorator that drops the unmask read fails here.
/// Run: dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~UnmaskGrantHostPathTests"
/// </summary>
[Trait("Category", "Security")]
public sealed class UnmaskGrantHostPathTests : IClassFixture<UnmaskGrantHostPathTests.UnmaskFixture>
{
    private static readonly TableIdentifier Air1Id = new("default", "main", "air1");

    /// <summary>The rows the David consent filter releases (ids 5 to 10) and their clear <c>value</c>.</summary>
    private static readonly long[] ClearIds = [5, 6, 7, 8, 9, 10];
    private static readonly double[] ClearValues = [3.0, 3.5, 4.0, 4.5, 5.0, 5.5];

    private readonly UnmaskFixture _fixture;

    public UnmaskGrantHostPathTests(UnmaskFixture fixture) => _fixture = fixture;

    /// <summary><c>air1.value</c> is PII and <c>air1.id</c> is CREDENTIALS; David holds a base data consent and an unmask grant naming both.</summary>
    public sealed class UnmaskFixture : RowFilterChannelParityTests.Fixture
    {
        protected override async Task SeedExtraGovernanceAsync(IGovernanceRepository repo)
        {
            var air1 = (await repo.GetTableMetadataAsync(Air1Id))!;
            var credentials = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = [new ColumnClassification { TableColumnId = "id", Code = ClassificationScopes.CredentialsCode, Source = "TEST" }]
            };
            await repo.UpsertTableMetadataAsync(air1 with
            {
                ColumnClassifications = ClassificationMetadata.Merge(
                    ClassificationMetadata.Merge(air1.ColumnClassifications, TableMetadataClassificationTestExtensions.PiiFor("value")),
                    credentials)
            });

            await repo.CreateConsentAsync(new Consent
            {
                Id = Guid.NewGuid(),
                TableId = air1.Table.Id,
                TableIdentifier = Air1Id,
                TenantId = new TenantId(Tenant),
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = new Sid(DavidSid),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                GrantKind = PermissionGrantKind.Unmask,
                TargetColumns = ["value", "id"]
            });
        }
    }

    [Theory]
    [InlineData("websql")]
    [InlineData("trino")]
    public async Task Unmask_grant_returns_classified_column_in_clear_but_never_credentials(string channel)
    {
        _fixture.CallerSid = RowFilterChannelParityTests.Fixture.DavidSid;
        var (ids, values) = await QueryAsync(channel);

        values.ShouldBe(ClearValues, ignoreOrder: true, customMessage: $"unmasked PII column, channel '{channel}'");
        values.Count.ShouldBe(ClearIds.Length);
        ids.ShouldNotContain(id => id != null && ClearIds.Contains(id.Value), $"CREDENTIALS column, channel '{channel}'");
    }

    private async Task<(IReadOnlyList<long?> Ids, IReadOnlyList<double> Values)> QueryAsync(string channel)
    {
        const string sql = "SELECT id, value FROM default.main.air1";
        JsonElement rows;
        JsonDocument doc;
        if (channel == "websql")
        {
            var r = await _fixture.Client().PostAsJsonAsync("/api/v1/sql", new { sql });
            doc = await JsonOrFailAsync(r);
            rows = doc.RootElement.GetProperty("rows");
        }
        else
        {
            var c = _fixture.Client();
            c.DefaultRequestHeaders.Add("X-Trino-User", "test-user");
            c.DefaultRequestHeaders.Add("X-Trino-Wait-Timeout", "30s");
            var r = await c.PostAsync("/v1/statement", new StringContent(sql, Encoding.UTF8, "text/plain"));
            doc = await JsonOrFailAsync(r);
            doc.RootElement.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FINISHED", doc.RootElement.ToString());
            rows = doc.RootElement.GetProperty("data");
        }

        using (doc)
        {
            var ids = new List<long?>();
            var values = new List<double>();
            foreach (var row in rows.EnumerateArray())
            {
                var id = row.ValueKind == JsonValueKind.Array ? row[0] : row.GetProperty("id");
                var value = row.ValueKind == JsonValueKind.Array ? row[1] : row.GetProperty("value");
                ids.Add(Number(id) is { } n ? (long)n : null);
                if (Number(value) is { } v)
                {
                    values.Add(v);
                }
            }

            return (ids, values);
        }
    }

    private static async Task<JsonDocument> JsonOrFailAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new ShouldAssertException($"HTTP {(int)response.StatusCode}: {body}");
        }

        return JsonDocument.Parse(body);
    }

    private static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };
}
