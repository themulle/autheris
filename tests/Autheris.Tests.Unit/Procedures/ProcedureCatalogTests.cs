namespace Autheris.Tests.Unit.Procedures;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>One visibility path and one governed table set for REST, MCP, the catalog and the AI guardrail.</summary>
public sealed class ProcedureCatalogTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly ClaimsPrincipal User = new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1001")], "test"));

    private static ProcedureDefinition Definition(string name, string? resultTable = "sales.orders") =>
        TestProcedureYaml.Definition($"name: {name}\nprocedure: api.usp_{name}\n") with { ResultTable = resultTable };

    private static ProcedureValidationResult Validation(params string[] referenced) =>
        new(true, [], [], referenced, new Dictionary<string, string>());

    [Fact]
    [Trait("Category", "Security")]
    public async Task ListVisible_OmitsInactiveInvisibleAndFailedEvaluation_SEC_PROC_09()
    {
        var registry = new InMemoryProcedureRegistry();
        foreach (string name in new[] { "ok", "hidden", "boom", "pending" })
        {
            registry.Register(Definition(name));
        }

        foreach (string name in new[] { "ok", "hidden", "boom" })
        {
            registry.MarkActive(name, Validation());
        }

        var execution = new VisibilityStub(p => p.Definition.Name switch
        {
            "ok" => Task.FromResult(true),
            "boom" => Task.FromException<bool>(new InvalidOperationException("evaluation failed")),
            _ => Task.FromResult(false)
        });

        var visible = await new ProcedureCatalog(registry, execution).ListVisibleAsync(User, Tenant, CancellationToken.None);

        visible.Select(v => v.Definition.Name).ShouldBe(["ok"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FindVisible_UnknownAndInvisible_ReturnSameNull_SEC_PROC_09()
    {
        var registry = new InMemoryProcedureRegistry();
        registry.Register(Definition("hidden"));
        registry.MarkActive("hidden", Validation());
        registry.Register(Definition("pending"));
        var execution = new VisibilityStub(_ => Task.FromResult(false));
        var catalog = new ProcedureCatalog(registry, execution);

        (await catalog.FindVisibleAsync("unknown", User, Tenant, CancellationToken.None)).ShouldBeNull();
        (await catalog.FindVisibleAsync("hidden", User, Tenant, CancellationToken.None)).ShouldBeNull();
        (await catalog.FindVisibleAsync("pending", User, Tenant, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GovernedTables_ExecutorAndGuardrail_UseSameSet_SEC_PROC_09()
    {
        var registered = new RegisteredProcedure(Definition("p"), ProcedureState.Active, Validation("sales.customers", "SALES.ORDERS"), DateTimeOffset.UtcNow, null);

        registered.GovernedTableKeys().ShouldBe(["sales.customers", "SALES.ORDERS"]);
        registered.GovernedTables().ShouldBe(
        [
            new TableIdentifier("default", "sales", "customers"),
            new TableIdentifier("default", "sales", "orders")
        ]);
    }

    [Fact]
    public void GovernedTables_NonActive_ResultTableOnly()
    {
        var pending = new RegisteredProcedure(Definition("p"), ProcedureState.Pending, Validation("sales.customers"), null, null);

        pending.GovernedTableKeys().ShouldBe(["sales.orders"]);
    }

    internal sealed class VisibilityStub(Func<RegisteredProcedure, Task<bool>> visible) : IProcedureExecutionService
    {
        public Task<bool> IsVisibleAsync(RegisteredProcedure procedure, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) => visible(procedure);

        public Task<GovernedProcedureResult> ExecuteAsync(string name, IReadOnlyDictionary<string, object?>? rawInputs, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
