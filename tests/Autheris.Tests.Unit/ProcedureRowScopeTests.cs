using System.Security;
using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// A consent row filter on the result table of a procedure is enforced by a key match against the database
/// (<c>row_scope_key</c>); without a key it still denies the call.
/// </summary>
public class ProcedureRowScopeTests
{
    private const string Header = """
        -- @name get_cranes
        -- @procedure tem.usp_Cranes
        -- @result-table md.crane
        -- @row-scope-key serial_number
        """;

    private static readonly TenantId Tenant = new("tenant-a");

    // ---------- parser ----------

    [Fact]
    public void Parse_Header_ReadsCompositeKey()
    {
        var def = ProcedureDefinitionParser.Parse(Header.Replace("serial_number", "client_id, serial_number"), "x", false, 60);
        def.RowScopeKey.ShouldBe(["client_id", "serial_number"]);
    }

    [Fact]
    public void ParseYaml_AcceptsSingleValueAndList()
    {
        const string single = "procedure: tem.usp_X\nresult_table: md.crane\nrow_scope_key: serial_number\n";
        const string list = "procedure: tem.usp_X\nresult_table: md.crane\nrow_scope_key: [client_id, serial_number]\n";

        ProcedureDefinitionParser.ParseYaml(single, "x", false, 60).RowScopeKey.ShouldBe(["serial_number"]);
        ProcedureDefinitionParser.ParseYaml(list, "x", false, 60).RowScopeKey.ShouldBe(["client_id", "serial_number"]);
    }

    [Fact]
    public void ParseYaml_KeyMapping_MapsResultColumnToTableColumn()
    {
        const string yaml = "procedure: tem.usp_X\nresult_table: md.crane\nrow_scope_key: [\"crane_serial_number=serial_number\", client_id]\n";

        var def = ProcedureDefinitionParser.ParseYaml(yaml, "x", false, 60);

        def.RowScopeKey.ShouldBe(["crane_serial_number", "client_id"]);
        def.RowScopeKeyTable.ShouldBe(["serial_number", "client_id"]);
    }

    [Fact]
    public async Task Execute_MappedKey_ReadsResultColumnAndMatchesTableColumn()
    {
        var f = Active(Header.Replace("-- @row-scope-key serial_number", "-- @row-scope-key serial=serial_number"));
        f.Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(
                ["serial", "name"],
                [new object?[] { "100", "delivered" }, new object?[] { "200", "active" }],
                false));
        f.RowScope.GetAllowedKeysAsync(
                Arg.Any<ProcedureDefinition>(), Arg.Any<TableMetadata>(), Arg.Any<TableAccessDecision>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyList<object?[]>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { RowScopeKeys.Normalize(["200"])! }));

        var result = await f.Create().ExecuteAsync("get_cranes", null, User(), Tenant);

        result.RowCount.ShouldBe(1);
        await f.RowScope.Received(1).GetAllowedKeysAsync(
            Arg.Any<ProcedureDefinition>(), Arg.Any<TableMetadata>(), Arg.Any<TableAccessDecision>(),
            Arg.Is<IReadOnlyList<string>>(k => k.SequenceEqual(new[] { "serial_number" })),
            Arg.Any<IReadOnlyList<object?[]>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("md", "crane", "serial_number", true)]      // key comes from the result table column
    [InlineData("md", "crane_alias", "serial_number", false)] // other table with a column of the same name
    [InlineData("md", "crane", "other_column", false)]        // other column renamed to the key name
    [InlineData(null, null, null, false)]                     // computed / ambiguous column
    public async Task Execute_CatalogMode_KeyColumnMustComeFromTheResultTable(string? schema, string? table, string? column, bool allowed)
    {
        var f = Active();
        f.Registry.MarkActive("get_cranes", new ProcedureValidationResult(
            true, [], ["serial_number", "name"], ["md.crane"], new Dictionary<string, string>(),
            new Dictionary<string, ResultColumnSource>
            {
                ["serial_number"] = new(schema, table, column),
                ["name"] = new("md", "crane", "name")
            }));
        f.RowScope.GetAllowedKeysAsync(
                Arg.Any<ProcedureDefinition>(), Arg.Any<TableMetadata>(), Arg.Any<TableAccessDecision>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyList<object?[]>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { RowScopeKeys.Normalize(["200"])! }));

        if (allowed)
        {
            (await f.Create().ExecuteAsync("get_cranes", null, User(), Tenant)).RowCount.ShouldBe(1);
        }
        else
        {
            await Should.ThrowAsync<SecurityException>(() => f.Create().ExecuteAsync("get_cranes", null, User(), Tenant));
            await f.RowScope.DidNotReceiveWithAnyArgs().GetAllowedKeysAsync(default!, default!, default!, default!, default!, default!, default);
        }
    }

    [Theory]
    [InlineData("procedure: tem.usp_X\nrow_scope_key: serial_number\n")]                                          // no result_table
    [InlineData("procedure: tem.usp_X\nresult_table: md.crane\nrow_scope_key: [a, A]\n")]                          // duplicate
    [InlineData("procedure: tem.usp_X\nresult_table: md.crane\nrow_scope_key: 'a; DROP TABLE x'\n")]               // identifier
    [InlineData("procedure: tem.usp_X\nresult_table: md.crane\nvalidation: declared\noutputs: [other]\nrow_scope_key: serial_number\n")] // not an output
    public void ParseYaml_InvalidRowScopeKey_Throws(string yaml)
    {
        Should.Throw<FormatException>(() => ProcedureDefinitionParser.ParseYaml(yaml, "x", false, 60));
    }

    [Fact]
    public void Normalize_NullPartNeverMatches_AndKeepsPartsApart()
    {
        RowScopeKeys.Normalize(["a", null]).ShouldBeNull();
        RowScopeKeys.Normalize(["a|b", "c"]).ShouldNotBe(RowScopeKeys.Normalize(["a", "b|c"]));
        RowScopeKeys.Normalize([1, "x"]).ShouldBe(RowScopeKeys.Normalize([1L, "x"]));
    }

    // ---------- execution ----------

    private sealed class Fixture
    {
        public InMemoryProcedureRegistry Registry { get; } = new();
        public IProcedureInvoker Invoker { get; } = Substitute.For<IProcedureInvoker>();
        public IProcedureRowScopeResolver RowScope { get; } = Substitute.For<IProcedureRowScopeResolver>();
        public ITableMetadataRepository Tables { get; } = Substitute.For<ITableMetadataRepository>();
        public IConsentRepository Consents { get; } = Substitute.For<IConsentRepository>();
        public IConsentResolutionService Resolution { get; } = Substitute.For<IConsentResolutionService>();
        public IAuditLogRepository Audit { get; } = Substitute.For<IAuditLogRepository>();
        public List<AuditLogEntry> AuditEntries { get; } = [];

        public GovernedProcedureExecutionService Create(bool withResolver = true)
        {
            Audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(AuditEntries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            return new GovernedProcedureExecutionService(
                Registry, Invoker, Options.Create(new GatewayOptions()), Audit, Tables, Consents, Resolution, masking: null,
                policyEnforcement: null, rowScope: withResolver ? RowScope : null);
        }
    }

    private static TableMetadata CraneTable() => new()
    {
        Identifier = new TableIdentifier("default", "md", "crane"),
        Columns = [new TableColumn { ColumnName = "serial_number" }, new TableColumn { ColumnName = "name" }]
    };

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-LWE-DAVID")], "test"));

    private static Fixture Active(
        string header = Header,
        ColumnAccessLevel keyAccess = ColumnAccessLevel.Clear,
        string rowFilter = "[autheris_target].[is_delivered] IS NULL")
    {
        var f = new Fixture();
        var def = ProcedureDefinitionParser.Parse(header, "x", false, 60);
        f.Registry.Register(def);
        f.Registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], ["serial_number", "name"], ["md.crane"], new Dictionary<string, string>()));

        f.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(CraneTable());
        f.Consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Consent>());

        var columns = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["serial_number"] = keyAccess,
            ["name"] = ColumnAccessLevel.Clear
        };
        f.Resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), columns, rowFilter));

        f.Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(
                ["serial_number", "name"],
                [
                    new object?[] { "100", "delivered" },
                    new object?[] { "200", "active-a" },
                    new object?[] { null, "no-key" },
                    new object?[] { "300", "active-b" }
                ],
                false));

        // The database allows 200 and 300 (not delivered); 100 is delivered.
        f.RowScope.GetAllowedKeysAsync(
                Arg.Any<ProcedureDefinition>(), Arg.Any<TableMetadata>(), Arg.Any<TableAccessDecision>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyList<object?[]>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string>
            {
                RowScopeKeys.Normalize(["200"])!,
                RowScopeKeys.Normalize(["300"])!
            }));
        return f;
    }

    [Fact]
    public async Task Execute_RowFilterWithScopeKey_KeepsOnlyAllowedRows()
    {
        var f = Active();

        var result = await f.Create().ExecuteAsync("get_cranes", null, User(), Tenant);

        result.RowCount.ShouldBe(2);
        result.Rows.Select(r => r["serial_number"]).ShouldBe(["200", "300"]);
        f.AuditEntries.Single(a => a.EventType == "PROCEDURE_EXECUTE").DetailsJson.ShouldContain("\"rowsRemovedByScope\":2");
    }

    [Fact]
    public async Task Execute_ScopeLookupReceivesKeysOfTheResult_AndTheCallerContext()
    {
        var f = Active();

        await f.Create().ExecuteAsync("get_cranes", null, User(), Tenant);

        await f.RowScope.Received(1).GetAllowedKeysAsync(
            Arg.Any<ProcedureDefinition>(),
            Arg.Is<TableMetadata>(t => t.Identifier.TableName == "crane"),
            Arg.Is<TableAccessDecision>(d => d.CombinedRowFilterSql!.Contains("is_delivered")),
            Arg.Is<IReadOnlyList<string>>(k => k.SequenceEqual(new[] { "serial_number" })),
            Arg.Is<IReadOnlyList<object?[]>>(c => c.Count == 4),
            Arg.Is<ProcedureSecurityContext>(s => s.TenantId == "tenant-a" && s.UserSid == "S-1-5-21-LWE-DAVID"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_CompositeKey_IsPassedInDeclaredOrder()
    {
        var f = Active(Header.Replace("serial_number", "name, serial_number"));
        f.RowScope.GetAllowedKeysAsync(
                Arg.Any<ProcedureDefinition>(), Arg.Any<TableMetadata>(), Arg.Any<TableAccessDecision>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyList<object?[]>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { RowScopeKeys.Normalize(["active-a", "200"])! }));

        var result = await f.Create().ExecuteAsync("get_cranes", null, User(), Tenant);

        result.RowCount.ShouldBe(1);
        result.Rows[0]["name"].ShouldBe("active-a");
    }

    [Fact]
    public async Task Execute_RowFilterWithoutScopeKey_StillDenies()
    {
        var f = Active(Header.Replace("-- @row-scope-key serial_number", string.Empty));

        await Should.ThrowAsync<SecurityException>(() => f.Create().ExecuteAsync("get_cranes", null, User(), Tenant));
        await f.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Execute_ScopeKeyWithoutResolver_Denies()
    {
        var f = Active();

        await Should.ThrowAsync<SecurityException>(() => f.Create(withResolver: false).ExecuteAsync("get_cranes", null, User(), Tenant));
        await f.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData(ColumnAccessLevel.Mask)]
    [InlineData(ColumnAccessLevel.Deny)]
    public async Task Execute_KeyColumnNotClear_Denies(ColumnAccessLevel access)
    {
        var f = Active(keyAccess: access);

        await Should.ThrowAsync<SecurityException>(() => f.Create().ExecuteAsync("get_cranes", null, User(), Tenant));
        await f.RowScope.DidNotReceiveWithAnyArgs().GetAllowedKeysAsync(default!, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Execute_KeyColumnMissingInResult_Denies()
    {
        var f = Active();
        f.Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(["name"], [new object?[] { "x" }], false));

        await Should.ThrowAsync<SecurityException>(() => f.Create().ExecuteAsync("get_cranes", null, User(), Tenant));
    }

    [Fact]
    public async Task Execute_ScopeLookupFails_DeniesInsteadOfReturningUnfilteredRows()
    {
        var f = Active();
        f.RowScope.GetAllowedKeysAsync(
                Arg.Any<ProcedureDefinition>(), Arg.Any<TableMetadata>(), Arg.Any<TableAccessDecision>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyList<object?[]>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlySet<string>>>(_ => throw new InvalidOperationException("db down"));

        await Should.ThrowAsync<SecurityException>(() => f.Create().ExecuteAsync("get_cranes", null, User(), Tenant));
        f.AuditEntries.ShouldContain(a => a.EventType == "PROCEDURE_FAILED");
    }

    [Fact]
    public async Task Execute_WithoutRowFilter_DoesNotQueryTheScope()
    {
        var f = Active(rowFilter: null!);

        var result = await f.Create().ExecuteAsync("get_cranes", null, User(), Tenant);

        result.RowCount.ShouldBe(4);
        await f.RowScope.DidNotReceiveWithAnyArgs().GetAllowedKeysAsync(default!, default!, default!, default!, default!, default!, default);
    }
}
