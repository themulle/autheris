namespace Autheris.Tests.Unit.Mcp;

using System.Text.Json;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Application.Sql;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>The data source of a query_sql call: tables resolve against the source the executor will use; a source the caller may not use answers like HTTP.</summary>
[Trait("Category", "Security")]
public sealed class McpQuerySqlDataSourceGuardrailTests
{
    private const string Sql = """{"sql":"SELECT id FROM public.orders","dataSource":"DS"}""";

    private static TableMetadata Orders(string source) => new()
    {
        Identifier = new TableIdentifier(source, "public", "orders"),
        Table = new Table { TableName = "orders", SchemaName = "public", SourceName = source, IsActive = true }
    };

    private static GatewayOptions Options() => new()
    {
        Mcp = new McpOptions { Enabled = true },
        WebSql = new WebSqlOptions
        {
            Enabled = true,
            DefaultDataSourceName = "sales",
            AllowedDataSources = ["sales", "other", "restricted"],
            TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant-a"] = ["sales", "other"]
            }
        }
    };

    private static (McpErrorTestRig Rig, List<TableIdentifier> Decided) Rig()
    {
        var decided = new List<TableIdentifier>();
        var rig = McpErrorTestRig.Create(
            execute: (_, _, _) => """{"columns":[],"rows":[],"rowCount":0,"truncated":false}""",
            decide: request =>
            {
                decided.Add(request.Object.Identifier);
                return TableAccessDecision.Allowed(request.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
            },
            tables: [Orders("sales"), Orders("other"), Orders("restricted")],
            options: Options());
        return (rig, decided);
    }

    private static async Task<McpToolCallResult> CallAsync(McpErrorTestRig rig, string dataSource) =>
        await rig.Call("query_sql", Sql.Replace("DS", dataSource));

    [Fact]
    public async Task DataSource_ResolvedNameUsedForTablesAndAbac()
    {
        var (rig, decided) = Rig();

        var result = await CallAsync(rig, "OTHER");

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        decided.ShouldNotBeEmpty();
        decided.ShouldAllBe(t => t.Domain == "other", "the canonical configured name decides, never the raw input");
    }

    [Fact]
    public async Task DataSource_TablesResolvedAgainstNamedSource()
    {
        var (rig, decided) = Rig();

        await rig.Call("query_sql", """{"sql":"SELECT id FROM public.orders"}""");
        await CallAsync(rig, "other");

        decided.Select(t => t.Domain).Distinct().Order().ShouldBe(["other", "sales"]);
    }

    [Fact]
    public async Task DataSource_UnknownDisallowedTenantDisallowed_AreByteIdentical_PolicyDenied()
    {
        var (rig, decided) = Rig();
        // "restricted" is globally allowed but not for tenant-a; "nowhere" is unknown; "Sales2" is not configured.
        var unknown = await CallAsync(rig, "nowhere");
        var tenantDisallowed = await CallAsync(rig, "restricted");
        var notListed = await CallAsync(rig, "Sales2");

        foreach (var denied in new[] { unknown, tenantDisallowed, notListed })
        {
            denied.IsSuccess.ShouldBeFalse();
            McpErrorEnvelopeAssert.Code(denied.ContentJson).ShouldBe(GovernedSqlErrorCodes.PolicyViolation);
        }

        tenantDisallowed.ContentJson.ShouldBe(unknown.ContentJson);
        notListed.ContentJson.ShouldBe(unknown.ContentJson);
        // The same bytes POST /api/v1/sql answers for the same request.
        unknown.ContentJson.ShouldBe(McpToolError.FailSql(new WebSqlPolicyException("The requested data source is not enabled for WebSQL.")).ContentJson);
        rig.Executor.Calls.ShouldBeEmpty();
        decided.ShouldBeEmpty();
    }

    [Fact]
    public async Task DataSource_NotAllowed_SameBytesAsUnknownTable()
    {
        var (rig, _) = Rig();

        var disallowed = await CallAsync(rig, "restricted");
        var unknownTable = await rig.Call("query_sql", """{"sql":"SELECT id FROM public.ghost"}""");

        // Both are denials with a fixed message; neither names the source or the table.
        McpErrorEnvelopeAssert.Message(disallowed.ContentJson).ShouldBe(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.PolicyViolation));
        disallowed.ContentJson.ShouldNotContain("restricted");
        unknownTable.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task DataSource_Denial_AuditAndLogsDoNotContainSuppliedName()
    {
        var (rig, _) = Rig();

        var result = await CallAsync(rig, "supplied-secret-source");

        result.IsSuccess.ShouldBeFalse();
        result.ContentJson.ShouldNotContain("supplied-secret-source");
        JsonSerializer.Serialize(rig.Audit.Entries).ShouldNotContain("supplied-secret-source");
    }

    [Fact]
    public async Task DataSource_NonStringOrBlank_InvalidParamsOrDefault()
    {
        var (rig, decided) = Rig();

        foreach (var blank in new[] { """{"sql":"SELECT id FROM public.orders","dataSource":""}""", """{"sql":"SELECT id FROM public.orders","dataSource":"  "}""", """{"sql":"SELECT id FROM public.orders","dataSource":null}""" })
        {
            (await rig.Call("query_sql", blank)).IsSuccess.ShouldBeTrue();
        }

        decided.ShouldAllBe(t => t.Domain == "sales");
        var querySql = McpDatasetTools.Definitions().Single(t => t.Name == McpDatasetTools.QuerySql);
        using var doc = JsonDocument.Parse("""{"sql":"SELECT 1","dataSource":5}""");
        new PersistedToolValidator().ValidateToolInvocation(querySql, doc.RootElement, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task DataSource_TransportNotAllowed_OnlyAfterSourceAllowed()
    {
        var (rig, decided) = Rig();

        // A source the caller may not use is refused before any table decision (so no transport or access decision can separate it from an unknown source).
        (await CallAsync(rig, "restricted")).IsSuccess.ShouldBeFalse();
        decided.ShouldBeEmpty();

        (await CallAsync(rig, "other")).IsSuccess.ShouldBeTrue();
        decided.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"query":"SELECT id FROM public.orders"}""")]
    [InlineData("""{"sql":"   "}""")]
    [InlineData("""{"sql":5}""")]
    public async Task MissingSqlOrOnlyQuery_IsInvalidParams_NoTableResolution(string args)
    {
        var (rig, decided) = Rig();

        var result = await rig.Call("query_sql", args);

        result.IsSuccess.ShouldBeFalse();
        McpErrorEnvelopeAssert.Code(result.ContentJson).ShouldBe("INVALID_PARAMS");
        decided.ShouldBeEmpty();
        rig.Executor.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingSql_IsInvalidParams()
    {
        var (rig, _) = Rig();

        var result = await rig.Call("query_sql", "{}");

        McpErrorEnvelopeAssert.Code(result.ContentJson).ShouldBe("INVALID_PARAMS");
        result.ContentJson.ShouldContain("'sql'");
    }
}
