namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 8: a procedure result cannot be wrapped in SQL. The virtual filter of the result table is
/// enforced by the existing row scope (key match against the database); without a row scope key, for an uncovered
/// result table or on a table the procedure only reads, the call is denied. Bindings can exclude procedure results.
/// </summary>
public sealed class ProcedureVirtualFilterTests
{
    private const string Header = """
        -- @name get_cranes
        -- @procedure tem.usp_Cranes
        -- @result-table md.crane
        -- @row-scope-key serial_number
        """;

    private static readonly TenantId Tenant = new("tenant-a");

    private sealed class RecordingResolver(MandatoryFilterOutcome outcome) : IMandatoryRowFilterResolver
    {
        public List<MandatoryFilterQuery> Queries { get; } = [];

        public ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            return ValueTask.FromResult(outcome);
        }
    }

    private static readonly MandatoryFilterOutcome Applied =
        new(false, null, "EXISTS (SELECT 1 FROM [md].[delivery] AS [d] WHERE [d].[serial_number] = [autheris_target].[serial_number])", ["nicht_ausgelieferte_krane"]);

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

        public GovernedProcedureExecutionService Create(IMandatoryRowFilterResolver resolver, GatewayOptions? options = null, bool withConsents = true)
        {
            Audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(AuditEntries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            return new GovernedProcedureExecutionService(
                Registry, Invoker, Options.Create(options ?? new GatewayOptions()), Tables,
                withConsents ? Consents : null, withConsents ? Resolution : null, masking: null,
                policyEnforcement: null, audit: Audit, rowScope: RowScope, mandatoryFilters: resolver);
        }
    }

    private static Fixture Active(string header = Header)
    {
        var f = new Fixture();
        var def = ProcedureDefinitionParser.Parse(header, "x", false, 60);
        f.Registry.Register(def);
        f.Registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], ["serial_number", "name"], ["md.crane"], new Dictionary<string, string>()));
        f.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = new TableIdentifier("default", "md", "crane"),
            Columns = [new TableColumn { ColumnName = "serial_number" }, new TableColumn { ColumnName = "name" }]
        });
        f.Consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Consent>());
        f.Resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["serial_number"] = ColumnAccessLevel.Clear, ["name"] = ColumnAccessLevel.Clear }));
        f.Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(["serial_number", "name"], [new object?[] { "100", "delivered" }, new object?[] { "200", "active" }], false));
        f.RowScope.GetAllowedKeysAsync(
                Arg.Any<ProcedureDefinition>(), Arg.Any<TableMetadata>(), Arg.Any<TableAccessDecision>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyList<object?[]>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { RowScopeKeys.Normalize(["200"])! }));
        return f;
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-LWE-DAVID")], "test"));

    [Fact]
    public async Task VirtualFilterOnTheResultTable_IsEnforcedByTheRowScope_AndAudited()
    {
        var f = Active();
        var resolver = new RecordingResolver(Applied);

        var result = await f.Create(resolver).ExecuteAsync("get_cranes", null, User(), Tenant);

        result.Rows.Select(r => r["serial_number"]).ShouldBe(["200"]);
        resolver.Queries.ShouldAllBe(q => q.ObjectKind == FilterObjectKinds.ProcedureResult);
        await f.RowScope.Received(1).GetAllowedKeysAsync(
            Arg.Any<ProcedureDefinition>(), Arg.Any<TableMetadata>(),
            Arg.Is<TableAccessDecision>(d => d.MandatoryRowPredicateSql == Applied.PredicateSql),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<object?[]>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>());
        var audit = f.AuditEntries.Single(a => a.EventType == "PROCEDURE_EXECUTE").DetailsJson;
        audit.ShouldContain("\"rowsRemovedByScope\":1");
        audit.ShouldContain("nicht_ausgelieferte_krane");
    }

    [Fact]
    public async Task WithoutRowScopeKey_IsDenied()
    {
        var f = Active(Header.Replace("-- @row-scope-key serial_number", string.Empty));

        await Should.ThrowAsync<SecurityException>(() => f.Create(new RecordingResolver(Applied)).ExecuteAsync("get_cranes", null, User(), Tenant));
        await f.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task UncoveredResultTable_IsDenied()
    {
        var f = Active();

        await Should.ThrowAsync<SecurityException>(() => f.Create(new RecordingResolver(MandatoryFilterOutcome.Deny("uncovered"))).ExecuteAsync("get_cranes", null, User(), Tenant));
        await f.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task NoFilterForProcedureResults_LeavesTheResultUnscoped()
    {
        var f = Active();

        var result = await f.Create(new RecordingResolver(MandatoryFilterOutcome.None)).ExecuteAsync("get_cranes", null, User(), Tenant);

        result.RowCount.ShouldBe(2);
        await f.RowScope.DidNotReceiveWithAnyArgs().GetAllowedKeysAsync(default!, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task ConsentBypassWithoutConsentServices_StillAppliesTheVirtualFilter()
    {
        // Decision 1: the consent bypass does not switch off virtual filters, also on this path without consent services.
        var f = Active();
        var options = new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true } };

        var result = await f.Create(new RecordingResolver(Applied), options, withConsents: false).ExecuteAsync("get_cranes", null, User(), Tenant);

        result.Rows.Select(r => r["serial_number"]).ShouldBe(["200"]);
    }
}
