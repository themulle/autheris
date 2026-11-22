namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Unit.TestSupport;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Resolvers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CatalogOwnershipAndTierVisibilityTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TableIdentifier Customers = new("crm", "public", "customers");
    private static readonly TableIdentifier AuditLogs = new("system", "public", "audit_logs");

    private static ClaimsPrincipal CreateUser(string sid = "S-1-USER", string tenant = Tenant, string[]? roles = null)
    {
        var claims = new List<Claim>
        {
            new("sub", sid),
            new("http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid", sid),
            new("tenant_id", tenant)
        };
        if (roles != null)
        {
            foreach (var r in roles)
            {
                claims.Add(new(ClaimTypes.Role, r));
            }
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    /// <summary>Issue #111: persisted tiers apply on shared tables in Multi mode; the fixture tables are shared.</summary>
    private static IOptions<GatewayOptions> TierOptions() => Microsoft.Extensions.Options.Options.Create(new GatewayOptions
    {
        Catalog = new DataCatalogOptions { SharedTables = [Orders.ToString(), Customers.ToString(), AuditLogs.ToString()] }
    });

    private static TableMetadata CreateTableMetadata(TableIdentifier id, string description, IReadOnlyList<string> tags)
    {
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = id.TableName,
                DisplayName = $"{id.Schema}.{id.TableName}",
                Description = description,
                Tags = tags, // Backstage export (Table.Tags) and MCP/GraphQL (CatalogTags) read different sources; the fixture feeds both
            },
            CatalogTags = tags,
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ],
            PrimaryKeyColumns = ["id"]
        };
    }



    [Fact]
    public void CatalogVisibility_ComputeAccessStatus_CorrectlyClassifiesStatus()
    {
        var activeConsent = new Consent
        {
            TableIdentifier = Orders,
            TenantId = new TenantId(Tenant),
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };

        var pendingReq = new ConsentRequest
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableIdentifier = Customers,
            Status = "PENDING",
            RequesterSid = new Sid("S-1-USER")
        };

        // 1. With Allow consent -> GRANTED
        CatalogVisibility.ComputeAccessStatus(Orders, [activeConsent], null, isAdmin: false)
            .ShouldBe("GRANTED");

        // 2. With Pending request -> PENDING
        CatalogVisibility.ComputeAccessStatus(Customers, [], [pendingReq], isAdmin: false)
            .ShouldBe("PENDING");

        // 3. With neither -> NONE
        CatalogVisibility.ComputeAccessStatus(AuditLogs, [], [pendingReq], isAdmin: false)
            .ShouldBe("NONE");

        // 4. Admin always GRANTED
        CatalogVisibility.ComputeAccessStatus(AuditLogs, [], null, isAdmin: true)
            .ShouldBe("GRANTED");
    }


    public sealed class TestMaskingQuery
    {
        public string MaskedField(IResolverContext ctx)
        {
            if (ctx.ContextData.TryGetValue("MaskedFields", out var mf) && mf is ConcurrentBag<string> bag)
            {
                bag.Add(ctx.Path.Print());
            }
            return "[REDACTED]";
        }

        public string ClearField() => "cleartext";
    }

    [Fact]
    public async Task MaskedFieldsMiddleware_AddsMaskedFieldsExtensionWhenMasked()
    {
        // Arrange
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<TestMaskingQuery>()
            .UseRequest<MaskedFieldsMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ maskedField }");

        // Assert
        var opResult = result.ExpectOperationResult();
        opResult.Extensions.ShouldNotBeNull();
        opResult.Extensions.ShouldContainKey("maskedFields");

        var maskedList = opResult.Extensions["maskedFields"] as IReadOnlyList<string>;
        maskedList.ShouldNotBeNull();
        maskedList.ShouldContain("maskedField");
    }

    [Fact]
    public async Task MaskedFieldsMiddleware_OmitsExtensionWhenNoFieldsMasked()
    {
        // Arrange
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<TestMaskingQuery>()
            .UseRequest<MaskedFieldsMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ clearField }");

        // Assert
        var opResult = result.ExpectOperationResult();
        if (opResult.Extensions != null)
        {
            opResult.Extensions.ShouldNotContainKey("maskedFields");
        }
    }

}
