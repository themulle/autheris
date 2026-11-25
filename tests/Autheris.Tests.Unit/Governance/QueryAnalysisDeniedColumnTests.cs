namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Governance.Analysis;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Ensures denied columns on static query analysis are indistinguishable from nonexistent columns
/// across SQL, GraphQL, federation, and read-only token callers.
/// </summary>
[Trait("Category", "Security")]
public sealed class QueryAnalysisDeniedColumnTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier TableId = new("demo", "main", "t");
    private static readonly TableIdentifier SecondTableId = new("hr", "main", "e");

    private static ClaimsPrincipal Caller(string sid = "S-1-USER", bool readOnly = false, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sid),
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", TenantA.Value)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        if (readOnly)
        {
            TokenAccessScope.MarkReadOnly(identity);
        }

        return new ClaimsPrincipal(identity);
    }

    private static TableMetadata DemoTable(bool deprecateSecret = false, bool catalogDenySecret = false)
    {
        var secretMeta = deprecateSecret
            ? new Dictionary<string, string>
            {
                ["deprecated_since"] = "2026-01-01T00:00:00Z",
                ["sunset_date"] = "2026-06-01T00:00:00Z",
                ["replacement_field"] = "public_col",
                ["deprecation_reason"] = "Deprecated secret column"
            }
            : new Dictionary<string, string>();

        var masking = catalogDenySecret
            ? new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["secret_col"] = new MaskingRule { RuleType = "DENY" }
            }
            : new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

        return new TableMetadata
        {
            Identifier = TableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "demo",
                SchemaName = "main",
                TableName = "t",
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "region", DataType = "text" },
                new TableColumn { ColumnName = "masked_col", DataType = "text" },
                new TableColumn { ColumnName = "secret_col", DataType = "text", Meta = secretMeta }
            ],
            PrimaryKeyColumns = ["id"],
            ColumnMaskingRules = masking
        };
    }

    private static TableMetadata SecondTable() => new()
    {
        Identifier = SecondTableId,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "hr",
            SchemaName = "main",
            TableName = "e",
            SourceType = "SqlServer",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "dept", DataType = "text" },
            new TableColumn { ColumnName = "secret_on_e", DataType = "text" }
        ],
        PrimaryKeyColumns = ["id"]
    };

    private static Consent AllowWithSecretDenied(TableIdentifier? tableId = null, bool explicitDeny = true, string sid = "S-1-USER")
    {
        var rules = new List<ConsentColumnRule>
        {
            new() { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
            new() { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
            new() { ColumnName = "masked_col", AccessLevel = ColumnAccessLevel.Mask }
        };
        if (explicitDeny)
        {
            rules.Add(new ConsentColumnRule { ColumnName = "secret_col", AccessLevel = ColumnAccessLevel.Deny });
        }

        return new Consent
        {
            TenantId = TenantA,
            TableIdentifier = tableId ?? TableId,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(sid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(5),
            ColumnRules = rules
        };
    }

    private static QueryAnalysisService CreateService(
        TableMetadata[] tables,
        Consent[] consents,
        InMemoryContainerOwnership? ownership = null)
    {
        return new QueryAnalysisService(
            new InMemoryTableMetadataRepository(tables),
            new InMemoryConsentRepository(consents),
            new InMemoryAuditLogRepository(),
            dataOwnershipRepository: ownership);
    }

    private static void AssertEquivalentOracleResults(
        QueryAnalysisResult deniedResult,
        QueryAnalysisResult nonexistentResult,
        string deniedRawName,
        string nonexistentRawName)
    {
        deniedResult.ValidationReport.IsValid.ShouldBe(nonexistentResult.ValidationReport.IsValid);
        deniedResult.ValidationReport.OutputColumns.ShouldBe(nonexistentResult.ValidationReport.OutputColumns);
        deniedResult.ValidationReport.DeprecatedFields.ShouldBe(nonexistentResult.ValidationReport.DeprecatedFields);
        deniedResult.AccessRequirements.MissingConsents.ShouldBe(nonexistentResult.AccessRequirements.MissingConsents);

        var deniedDiags = deniedResult.ValidationReport.Diagnostics;
        var nopeDiags = nonexistentResult.ValidationReport.Diagnostics;
        deniedDiags.Count.ShouldBe(nopeDiags.Count);

        for (var i = 0; i < deniedDiags.Count; i++)
        {
            deniedDiags[i].Code.ShouldBe(nopeDiags[i].Code);
            deniedDiags[i].QueryName.ShouldBe(nopeDiags[i].QueryName);
            deniedDiags[i].Dataset.ShouldBe(nopeDiags[i].Dataset);
            deniedDiags[i].Column.ShouldBe(deniedRawName);
            nopeDiags[i].Column.ShouldBe(nonexistentRawName);
            deniedDiags[i].Message.Replace(deniedRawName, "<COL>", StringComparison.Ordinal)
                .ShouldBe(nopeDiags[i].Message.Replace(nonexistentRawName, "<COL>", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(true, false, "Secret_Col", "Nope_Col")]
    [InlineData(false, false, "SECRET_COL", "NOPE_COL")]
    [InlineData(false, true, "secret_col", "nope_col")]
    public async Task DeniedColumn_SqlSelect_MatchesNonexistentColumnFieldForField(
        bool explicitDenyRule,
        bool catalogDenyRule,
        string deniedToken,
        string unknownToken)
    {
        var service = CreateService(
            [DemoTable(deprecateSecret: true, catalogDenySecret: catalogDenyRule)],
            [AllowWithSecretDenied(explicitDeny: explicitDenyRule)]);
        var user = Caller(readOnly: true);

        var deniedResult = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql($"SELECT region, {deniedToken} FROM demo.main.t", name: "q")],
            user);
        var nopeResult = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql($"SELECT region, {unknownToken} FROM demo.main.t", name: "q")],
            user);

        AssertEquivalentOracleResults(deniedResult, nopeResult, deniedToken, unknownToken);
        deniedResult.ValidationReport.IsValid.ShouldBeFalse();
        deniedResult.ValidationReport.OutputColumns.Select(c => c.Column).ShouldBe(["region"]);
        deniedResult.ValidationReport.Diagnostics.ShouldContain(d => d.Code == QueryDiagnosticCodes.UnknownColumn && d.Column == deniedToken);
        deniedResult.ValidationReport.DeprecatedFields.ShouldBeEmpty();
        deniedResult.AccessRequirements.MissingConsents.ShouldBeEmpty();

        var json = JsonSerializer.Serialize(deniedResult);
        if (!string.Equals(deniedToken, "secret_col", StringComparison.Ordinal))
        {
            json.ShouldNotContain("secret_col", Case.Sensitive);
        }
        json.ShouldNotContain("Deprecated secret column");
        json.ShouldNotContain("\"EffectiveAccessLevel\":0");
    }

    [Fact]
    public async Task DeniedColumn_SelectStar_OmitsColumnFromOutputsConsentsAndDeprecations()
    {
        var service = CreateService(
            [DemoTable(deprecateSecret: true)],
            [AllowWithSecretDenied(explicitDeny: true)]);
        var user = Caller(readOnly: true);

        var starResult = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT * FROM demo.main.t", name: "q_star")],
            user);
        var qualifiedStar = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT tbl.* FROM demo.main.t tbl", name: "q_qstar")],
            user);

        foreach (var result in new[] { starResult, qualifiedStar })
        {
            result.ValidationReport.IsValid.ShouldBeTrue();
            result.ValidationReport.Diagnostics.ShouldBeEmpty();
            result.ValidationReport.OutputColumns.Select(c => c.Column).ShouldBe(["id", "region", "masked_col"], ignoreOrder: true);
            result.ValidationReport.OutputColumns.ShouldNotContain(c => c.Column == "secret_col");
            result.ValidationReport.DeprecatedFields.ShouldBeEmpty();
            result.AccessRequirements.MissingConsents.Select(m => m.Column).ShouldBe(["masked_col"]);
        }

        var starPlusDenied = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT *, Secret_Col FROM demo.main.t", name: "q")],
            user);
        var starPlusUnknown = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT *, Nope_Col FROM demo.main.t", name: "q")],
            user);

        AssertEquivalentOracleResults(starPlusDenied, starPlusUnknown, "Secret_Col", "Nope_Col");
    }

    [Theory]
    [InlineData("SELECT region, {0} AS harmless_alias FROM demo.main.t")]
    [InlineData("SELECT region, UPPER({0}) AS upper_val FROM demo.main.t")]
    [InlineData("SELECT region FROM demo.main.t WHERE {0} = 'abc'")]
    [InlineData("SELECT a.id FROM demo.main.t a JOIN demo.main.t b ON a.{0} = b.id")]
    [InlineData("SELECT region FROM demo.main.t GROUP BY region, {0}")]
    [InlineData("SELECT region FROM demo.main.t ORDER BY {0}")]
    [InlineData("SELECT region, COUNT(*) FROM demo.main.t GROUP BY region HAVING MAX({0}) > '0'")]
    [InlineData("SELECT region, ROW_NUMBER() OVER (PARTITION BY {0} ORDER BY id) FROM demo.main.t")]
    public async Task DeniedColumn_AllSqlClauses_NoExistenceTypeOrDeprecationOracle(string sqlTemplate)
    {
        var service = CreateService(
            [DemoTable(deprecateSecret: true)],
            [AllowWithSecretDenied(explicitDeny: true)]);
        var user = Caller(readOnly: true);

        var deniedSql = string.Format(sqlTemplate, "Secret_Col");
        var unknownSql = string.Format(sqlTemplate, "Nope_Col");

        var deniedResult = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(deniedSql, name: "q")], user);
        var unknownResult = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(unknownSql, name: "q")], user);

        AssertEquivalentOracleResults(deniedResult, unknownResult, "Secret_Col", "Nope_Col");
        deniedResult.ValidationReport.Diagnostics.ShouldContain(d => d.Code == QueryDiagnosticCodes.UnknownColumn && d.Column == "Secret_Col");
        deniedResult.ValidationReport.Diagnostics.ShouldNotContain(d => d.Code == QueryDiagnosticCodes.MaskedColumnInFilter);
        deniedResult.ValidationReport.DeprecatedFields.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeniedColumn_MultiTableUnqualified_DoesNotLeakSourceTableOrExistence()
    {
        var t2 = SecondTable();
        var consentedOnBoth = CreateService(
            [DemoTable(), t2],
            [
                AllowWithSecretDenied(),
                new Consent
                {
                    TenantId = TenantA,
                    TableIdentifier = SecondTableId,
                    Effect = ConsentEffect.Allow,
                    GranteeType = GranteeType.User,
                    GranteeSid = new Sid("S-1-USER"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(5),
                    ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
                }
            ]);

        var user = Caller(readOnly: true);
        var deniedSelect = await consentedOnBoth.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT t1.id, Secret_On_E FROM demo.main.t t1 JOIN hr.main.e t2 ON t1.id = t2.id", name: "q")],
            user);
        var unknownSelect = await consentedOnBoth.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT t1.id, Nope_Col FROM demo.main.t t1 JOIN hr.main.e t2 ON t1.id = t2.id", name: "q")],
            user);

        AssertEquivalentOracleResults(deniedSelect, unknownSelect, "Secret_On_E", "Nope_Col");

        var unconsentedT2 = CreateService([DemoTable(), t2], [AllowWithSecretDenied()]);
        var unconsentedDenied = await unconsentedT2.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT t1.id, Secret_On_E FROM demo.main.t t1 JOIN hr.main.e t2 ON t1.id = t2.id", name: "q")],
            user);
        var unconsentedUnknown = await unconsentedT2.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT t1.id, Nope_Col FROM demo.main.t t1 JOIN hr.main.e t2 ON t1.id = t2.id", name: "q")],
            user);

        AssertEquivalentOracleResults(unconsentedDenied, unconsentedUnknown, "Secret_On_E", "Nope_Col");
    }

    [Fact]
    public async Task DeniedColumn_CrossSourceFederation_NoResidualOrKeySetLeak()
    {
        var service = CreateService(
            [DemoTable(), SecondTable()],
            [
                AllowWithSecretDenied(),
                new Consent
                {
                    TenantId = TenantA,
                    TableIdentifier = SecondTableId,
                    Effect = ConsentEffect.Allow,
                    GranteeType = GranteeType.User,
                    GranteeSid = new Sid("S-1-USER"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(5),
                    ColumnRules =
                    [
                        new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                        new ConsentColumnRule { ColumnName = "dept", AccessLevel = ColumnAccessLevel.Clear }
                    ]
                }
            ]);

        var user = Caller(readOnly: true);

        var unqualifiedDenied = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT t1.id, t2.dept FROM demo.main.t t1 JOIN hr.main.e t2 ON t1.id = t2.id WHERE Secret_Col = 'x'", name: "q")],
            user);
        var unqualifiedNope = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT t1.id, t2.dept FROM demo.main.t t1 JOIN hr.main.e t2 ON t1.id = t2.id WHERE Nope_Col = 'x'", name: "q")],
            user);

        AssertEquivalentOracleResults(unqualifiedDenied, unqualifiedNope, "Secret_Col", "Nope_Col");
        unqualifiedDenied.Federation.ShouldNotBeNull();
        unqualifiedNope.Federation.ShouldNotBeNull();
        unqualifiedDenied.Federation.Tables.SelectMany(t => t.ResidualPredicates)
            .Select(r => (r.Column, r.Reason))
            .ShouldBe(unqualifiedNope.Federation.Tables.SelectMany(t => t.ResidualPredicates).Select(r => (r.Column, r.Reason)));
        unqualifiedDenied.Federation.Tables.SelectMany(t => t.ResidualPredicates)
            .ShouldNotContain(r => string.Equals(r.Column, "secret_col", StringComparison.OrdinalIgnoreCase));

        var multiTableDenied = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT t1.id FROM demo.main.t t1 JOIN hr.main.e t2 ON t1.id = t2.id WHERE t1.Secret_Col = t2.dept", name: "q")],
            user);
        var multiTableNope = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT t1.id FROM demo.main.t t1 JOIN hr.main.e t2 ON t1.id = t2.id WHERE t1.Nope_Col = t2.dept", name: "q")],
            user);

        AssertEquivalentOracleResults(multiTableDenied, multiTableNope, "Secret_Col", "Nope_Col");
        multiTableDenied.Federation!.Tables.SelectMany(t => t.ResidualPredicates)
            .Select(r => (r.Column, r.Reason))
            .ShouldBe(multiTableNope.Federation!.Tables.SelectMany(t => t.ResidualPredicates).Select(r => (r.Column, r.Reason)));
    }

    [Fact]
    public async Task DeniedColumn_GraphQlAndDeclaredProcedure_NoOracle()
    {
        var service = CreateService([DemoTable(deprecateSecret: true)], [AllowWithSecretDenied()]);
        var user = Caller(readOnly: true);

        var gqlDenied = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromGraphQl("query { t(where: { Secret_Col_eq: \"x\" }) { region Secret_Col } }", name: "gql")],
            user);
        var gqlNope = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromGraphQl("query { t(where: { Nope_Col_eq: \"x\" }) { region Nope_Col } }", name: "gql")],
            user);

        AssertEquivalentOracleResults(gqlDenied, gqlNope, "Secret_Col", "Nope_Col");

        var procImplicit = new ProcedureDefinition(
            Name: "sp_implicit",
            Summary: "proc",
            ProcedureName: "main.sp_implicit",
            Mode: ProcedureMode.Read,
            DataSource: "demo",
            Parameters: [],
            ContextBindings: [],
            RlsMode: ProcedureRlsMode.SessionContext,
            ResultTable: "demo.main.t",
            ClearedResultColumns: [],
            RequiredRoles: [],
            TimeoutSeconds: 30)
        {
            ReferencedTables = ["demo.main.t"]
        };

        var procExplicitDenied = procImplicit with { Name = "sp_denied", DeclaredOutputs = ["region", "Secret_Col"] };
        var procExplicitNope = procImplicit with { Name = "sp_nope", DeclaredOutputs = ["region", "Nope_Col"] };

        var implicitResult = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromProcedure(procImplicit)], user);
        implicitResult.ValidationReport.OutputColumns.Select(c => c.Column).ShouldBe(["id", "region", "masked_col"], ignoreOrder: true);

        var deniedProcResult = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromProcedure(procExplicitDenied, name: "q")], user);
        var nopeProcResult = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromProcedure(procExplicitNope, name: "q")], user);
        AssertEquivalentOracleResults(deniedProcResult, nopeProcResult, "Secret_Col", "Nope_Col");
    }

    [Fact]
    public async Task ReadOnlyToken_AdminAndOwner_DoNotBypassDeniedColumnsOrTableGrant()
    {
        var ownership = new InMemoryContainerOwnership()
            .AssignOwner(TableId, new ContainerOwnerEntry { AdSid = new Sid("S-1-OWNER"), DisplayName = "Owner", IsActive = true });

        var service = CreateService(
            [DemoTable()],
            [AllowWithSecretDenied(sid: "S-1-ADMIN"), AllowWithSecretDenied(sid: "S-1-OWNER")],
            ownership);

        var roAdmin = Caller("S-1-ADMIN", readOnly: true, "GovernanceAdmin");
        var roOwner = Caller("S-1-OWNER", readOnly: true);
        var rwAdmin = Caller("S-1-ADMIN", readOnly: false, "GovernanceAdmin");

        // Read-only admin and read-only owner see denied columns as UNKNOWN_COLUMN, just like any consent caller.
        foreach (var roPrincipal in new[] { roAdmin, roOwner })
        {
            var deniedRes = await service.AnalyzeQueriesAsync(
                [QueryAnalysisItem.FromSql("SELECT region, secret_col FROM demo.main.t", name: "q")],
                roPrincipal);
            var starRes = await service.AnalyzeQueriesAsync(
                [QueryAnalysisItem.FromSql("SELECT * FROM demo.main.t", name: "q")],
                roPrincipal);

            deniedRes.ValidationReport.OutputColumns.Select(c => c.Column).ShouldBe(["region"]);
            deniedRes.ValidationReport.Diagnostics.ShouldContain(d => d.Code == QueryDiagnosticCodes.UnknownColumn && d.Column == "secret_col");
            starRes.ValidationReport.OutputColumns.Select(c => c.Column).ShouldNotContain("secret_col");
        }

        // Control: full-privilege GovernanceAdmin evaluating itself retains admin semantics.
        var fullAdminRes = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT region, secret_col FROM demo.main.t", name: "q")],
            rwAdmin);
        fullAdminRes.ValidationReport.IsValid.ShouldBeTrue();
        fullAdminRes.ValidationReport.OutputColumns.Select(c => c.Column).ShouldBe(["region", "secret_col"], ignoreOrder: true);
    }

    [Fact]
    public async Task HttpEndpoint_AnalyzeQueries_DeniedColumn_OmitsColumnMetadataFromJson()
    {
        var service = CreateService([DemoTable()], [AllowWithSecretDenied()]);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            User = Caller(readOnly: true)
        };
        httpContext.Response.Body = new MemoryStream();

        var result = await QueryAnalysisAndProfilingEndpoints.HandleAnalyzeQueriesAsync(
            new AnalyzeQueriesHttpRequest([QueryAnalysisItem.FromSql("SELECT region, secret_col FROM main.t")]),
            httpContext,
            service,
            CancellationToken.None);

        await result.ExecuteAsync(httpContext);
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        var body = await reader.ReadToEndAsync();

        using var doc = JsonDocument.Parse(body);
        var report = doc.RootElement.GetProperty("validationReport");
        report.GetProperty("isValid").GetBoolean().ShouldBeFalse();
        var outputs = report.GetProperty("outputColumns").EnumerateArray().ToList();
        outputs.Count.ShouldBe(1);
        outputs[0].GetProperty("column").GetString().ShouldBe("region");

        var diag = report.GetProperty("diagnostics").EnumerateArray().Single();
        diag.GetProperty("code").GetString().ShouldBe(QueryDiagnosticCodes.UnknownColumn);
        diag.GetProperty("column").GetString().ShouldBe("secret_col");
    }
}
