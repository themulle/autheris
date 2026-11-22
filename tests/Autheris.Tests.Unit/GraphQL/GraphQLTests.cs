using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.GraphQL.Subscriptions;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Streaming;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public class GraphQLTests
{
    [Theory]
    [InlineData(10_001)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public async Task SR_M_17_TableRootField_RejectsAfterBeyondMaxAllowedOffset(int after)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
        {
            GraphQL = new Autheris.Domain.Options.GraphQLOptions { MaxAllowedOffset = 10_000 }
        });

        var ex = await Should.ThrowAsync<HotChocolate.GraphQLException>(() => new Query().GetTableAsync(
            "sales", "invoices", "dbo", 50, after, null!, null!, options));

        ex.Errors[0].Code.ShouldBe("BAD_REQUEST");
    }






    [Fact]
    public async Task SchemaSnapshot_BuildsValidGraphQLSchema()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>();

        var sp = services.BuildServiceProvider();
        var executor = await sp.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();
        var schema = executor.Schema;

        var sdl = schema.ToString();
        sdl.ShouldNotBeNull();
        sdl.ShouldContain("type Query");
        sdl.ShouldContain("type Mutation");
        sdl.ShouldContain("table(");
        sdl.ShouldContain("TableRecordPayload");
        sdl.ShouldContain("requestTableAccess");
        sdl.ShouldContain("approveConsentRequest");
        sdl.ShouldContain("revokeConsent");
    }





    [Fact]
    public async Task DynamicTableType_WhenColumnIsBigint_MapsToLongType()
    {
        var table = new Table
        {
            SchemaName = "dbo",
            TableName = "transactions",
            DisplayName = "Transactions"
        };
        var columns = new[]
        {
            new TableColumn { ColumnName = "tx_id", DataType = "bigint" },
            new TableColumn { ColumnName = "quantity", DataType = "int" }
        };
        var meta = new TableMetadata
        {
            Table = table,
            Identifier = new TableIdentifier("finance", "dbo", "transactions"),
            Columns = columns
        };

        var masking = NSubstitute.Substitute.For<IColumnMaskingProvider>();

        var schema = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType(d => d.Name("Query").Field("test").Resolve(_ => "ok"))
            .AddType(new Autheris.GraphQL.DynamicTypes.DynamicTableType(meta, masking))
            .BuildSchemaAsync();

        var dynamicType = schema.Types.GetType<IObjectTypeDefinition>("finance_transactions");
        dynamicType.ShouldNotBeNull();
        ((INameProvider)dynamicType.Fields["tx_id"].Type).Name.ShouldBe("Long");
        ((INameProvider)dynamicType.Fields["quantity"].Type).Name.ShouldBe("Int");
    }

    [Fact]
    public async Task MyActiveConsents_MalformedTableFilter_ThrowsBadRequestInsteadOfAllConsents()
    {
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenants.Default,
            TableIdentifier = new TableIdentifier("finance", "dbo", "orders"),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-TEST"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        };
        var repo = new InMemoryConsentRepository(consent);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST"),
            new Claim("tenant_id", TestTenants.Default.Value)
        ], "TestAuth"));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };

        var ex = await Should.ThrowAsync<HotChocolate.GraphQLException>(() =>
            new Query().MyActiveConsentsAsync("not-a-valid-table", null, null, repo, accessor));

        ex.Errors.ShouldHaveSingleItem();
        ex.Errors[0].Code.ShouldBe("BAD_REQUEST");
        ex.Errors[0].Message.ShouldBe("The table identifier is invalid.");
        ex.Errors[0].Message.ShouldNotContain("not-a-valid-table");
    }

    [Fact]
    public async Task GetTableAsync_UsesTotalCountFromPagedExecution_InsteadOfPageSize()
    {
        var table = new TableIdentifier("finance", "dbo", "orders");
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
        [
            new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["id"] = 2 }
        ];
        var exec = StubGatewayExecutionService.WithPage(new TableQueryPage(
            rows,
            TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            TotalCount: 42));
        exec.OnExecuteQuery = (_, _, _, _, _, _, _, _) => Task.FromResult((
            rows,
            TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST"),
            new Claim("tenant_id", TestTenants.Default.Value)
        ], "TestAuth"));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };

        var payload = await new Query().GetTableAsync("finance", "orders", "dbo", first: 2, after: 0, exec, accessor);

        payload.JsonRows.Count.ShouldBe(2);
        payload.TotalCount.ShouldBe(42);
    }

    [Fact]
    public async Task MyAccess_MalformedTable_DoesNotEchoInputInErrorMessage()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST"),
            new Claim("tenant_id", TestTenants.Default.Value)
        ], "TestAuth"));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };

        var ex = await Should.ThrowAsync<HotChocolate.GraphQLException>(() =>
            new Query().MyAccessAsync("secret-probe-invalid", null!, accessor));

        ex.Errors[0].Code.ShouldBe("BAD_REQUEST");
        ex.Errors[0].Message.ShouldBe("The table identifier is invalid.");
        ex.Errors[0].Message.ShouldNotContain("secret-probe-invalid");
    }

    [Fact]
    public async Task SubscribeToTableEventsAsync_TenantMismatch_DoesNotEchoTenantIds()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST"),
            new Claim("tenant_id", "tenant-alpha")
        ], "TestAuth"));
        var channel = new InMemoryCdcEventChannel(NullLogger<InMemoryCdcEventChannel>.Instance);
        await using var enumerator = new Subscription()
            .SubscribeToTableEventsAsync("finance.dbo.orders", "evil-probe-tenant", channel, null!, new ServiceCollection().BuildServiceProvider(), principal, CancellationToken.None)
            .GetAsyncEnumerator();

        var ex = await Should.ThrowAsync<HotChocolate.GraphQLException>(async () => await enumerator.MoveNextAsync());

        ex.Errors[0].Code.ShouldBe("AUTH_TENANT_MISMATCH");
        ex.Errors[0].Message.ShouldNotContain("evil-probe-tenant");
        ex.Errors[0].Message.ShouldNotContain("tenant-alpha");
    }
}


