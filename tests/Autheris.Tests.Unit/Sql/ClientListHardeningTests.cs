namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Sql.Policy;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static Autheris.Tests.Unit.Sql.ClientListFixture;

/// <summary>REQ-SQL-128 Phase 3: error mapping (C-15), provider errors (SEC-128-07) and the federation guard (SEC-128-12).</summary>
public sealed class ClientListHardeningTests
{
    private sealed class FakeDbException(string message) : DbException(message);

    // ---- C-15 / M-17 ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("SQL_LIST_PARAM_POSITION")]
    [InlineData("SQL_LIST_PARAM_TYPE")]
    [InlineData("SQL_LIST_PARAM_NULL")]
    [InlineData("SQL_LIST_PARAM_TOO_LARGE")]
    [InlineData("SQL_LIST_PARAM_UNSUPPORTED")]
    [Trait("Category", "Security")]
    public void ErrorMapper_ClientListException_Maps400_WithAFixedMessage_NoCountNoName(string code)
    {
        SqlCompilerErrorMapper.TryMap(new ClientListException(code), out var mapping).ShouldBeTrue();
        mapping.Status.ShouldBe(400);
        mapping.Code.ShouldBe(code);
        mapping.Title.ShouldBe(GovernedSqlErrorCodes.FixedMessage(code));
        mapping.Title.Any(char.IsDigit).ShouldBeFalse();
        mapping.Limit.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ErrorMapper_ClientListException_PrecedesTheSecurityExceptionRow_EvenWhenWrapped()
    {
        // The wrapping exception is a SecurityException (403 row); the inner list error must win because its row comes first.
        var wrapped = new SecurityException("x", new ClientListException("SQL_LIST_PARAM_POSITION"));
        SqlCompilerErrorMapper.TryMap(wrapped, out var mapping).ShouldBeTrue();
        mapping.Status.ShouldBe(400);
        mapping.Code.ShouldBe("SQL_LIST_PARAM_POSITION");
    }

    // ---- SEC-128-07 / M-7 --------------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Security")]
    public async Task ProviderError_WithElementText_IsNotInLogsAuditOrResponse()
    {
        const string element = "super-secret-element-777";
        var db = new RecordingDbConnection { ExecuteFailure = new FakeDbException($"Conversion failed when converting the nvarchar value '{element}' to data type int.") };
        var logger = new ListCapturingLogger<GovernedSqlExecutionService>();
        await using var f = await CreateAsync(logger: logger, openConnection: () => db);

        var ex = await Should.ThrowAsync<GovernedSqlException>(() =>
            f.QueryAsync("SELECT id FROM orders WHERE status IN (@s)", new Dictionary<string, object?> { ["s"] = Json($"[\"{element}\", \"b\"]") }));

        ex.Message.ShouldNotContain(element);
        string everything = string.Join("\n", logger.Entries) + "\n" + string.Join("\n", f.Audit.Select(e => e.DetailsJson)) + "\n" + ex;
        everything.ShouldNotContain(element);
        logger.Entries.ShouldContain(e => e.Contains("provider error"));   // the event is still logged, only without the driver text
    }

    [Fact]
    public async Task ProviderError_WithoutLists_KeepsLoggingTheDriverError()
    {
        const string driverText = "driver-detail-for-operators";
        var db = new RecordingDbConnection { ExecuteFailure = new FakeDbException(driverText) };
        var logger = new ListCapturingLogger<GovernedSqlExecutionService>();
        await using var f = await CreateAsync(logger: logger, openConnection: () => db);

        await Should.ThrowAsync<GovernedSqlException>(() =>
            f.QueryAsync("SELECT id FROM orders WHERE status = @s", new Dictionary<string, object?> { ["s"] = "x" }));

        logger.Entries.ShouldContain(e => e.Contains(driverText));
    }

    // ---- SEC-128-12 / M-10 --------------------------------------------------------------------------------------------------------

    private static FederatedDuckDbExecutionService Federated(out ICrossSourceQueryRouter router) =>
        new(Options.Create(new GatewayOptions()), Substitute.For<IAuditLogRepository>(), router = Substitute.For<ICrossSourceQueryRouter>(),
            Substitute.For<ICrossSourcePlanner>(), Substitute.For<IFederatedStagingService>(), Substitute.For<IDuckDbOlapEngine>());

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("[]")]
    [Trait("Category", "Security")]
    public async Task Federation_ListParameter_IsRefused_OnEveryEntry_RawArrayDetected(string json)
    {
        var service = Federated(out _);
        var request = Request("SELECT 1", new Dictionary<string, object?> { ["x"] = Json(json) });
        var user = User();
        var tenant = new TenantId(TenantA);

        (await Should.ThrowAsync<ClientListException>(() => service.ExecuteQueryBufferedAsync(request, user, tenant))).Code.ShouldBe("SQL_LIST_PARAM_UNSUPPORTED");
        (await Should.ThrowAsync<ClientListException>(() => service.ExecuteGovernedQueryAsync(request, user, tenant, (_, _) => Task.CompletedTask))).Code.ShouldBe("SQL_LIST_PARAM_UNSUPPORTED");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Federation_ScalarParameters_AreNotRefusedByTheListGuard()
    {
        var service = Federated(out _);
        var request = Request("SELECT 1", new Dictionary<string, object?> { ["x"] = Json("1") });

        // The guard does not fire; whatever the unconfigured substitutes do next is not a ClientListException.
        var ex = await Record.ExceptionAsync(() => service.ExecuteQueryBufferedAsync(request, User(), new TenantId(TenantA)));
        ex.ShouldNotBeOfType<ClientListException>();
    }
}
