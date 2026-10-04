namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Diagnostics.Shadowing;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TrafficShadowingGuardTests
{
    [Theory]
    [InlineData("mutation { createCustomer(name: \"Acme\") { id } }")]
    [InlineData("mutation AddInvoice($input: InvoiceInput!) { addInvoice(input: $input) { id } }")]
    [InlineData("{\"query\":\"mutation UpdateStatus { setStatus(val: true) }\"}")]
    [InlineData("# Some comment\nmutation { deleteUser(id: 1) }")]
    public void AstShadowingFilter_RejectsGraphQLMutations(string mutationPayload)
    {
        var (isSafe, reason) = AstShadowingFilter.IsSafeForShadowing("/graphql", mutationPayload);

        isSafe.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason.ShouldContain("mutations are strictly blocked");
    }

    [Theory]
    [InlineData("query { customers { id name } }")]
    [InlineData("query GetAccounts { accounts { id balance } }")]
    [InlineData("{\"query\":\"query { products(limit: 10) { sku title } }\"}")]
    [InlineData("{ users { id email } }")]
    public void AstShadowingFilter_AllowsGraphQLQueries(string queryPayload)
    {
        var (isSafe, reason) = AstShadowingFilter.IsSafeForShadowing("/graphql", queryPayload);

        isSafe.ShouldBeTrue();
        reason.ShouldBeNull();
    }

    [Theory]
    [InlineData("INSERT INTO customers (id, name) VALUES (1, 'Alice')")]
    [InlineData("UPDATE accounts SET balance = balance + 100 WHERE id = 42")]
    [InlineData("DELETE FROM logs WHERE created_at < NOW()")]
    [InlineData("DELETE logs WHERE created_at < NOW()")]
    [InlineData("INSERT orders (id) VALUES (1)")]
    [InlineData("DROP TABLE staging_orders")]
    [InlineData("ALTER TABLE users ADD COLUMN is_admin BOOLEAN")]
    [InlineData("TRUNCATE TABLE session_cache")]
    [InlineData("MERGE INTO target_table USING source_table ON (id)")]
    [InlineData("GRANT SELECT ON accounts TO public")]
    public void AstShadowingFilter_RejectsSqlWrites(string sqlPayload)
    {
        var (isSafe, reason) = AstShadowingFilter.IsSafeForShadowing("/api/sql/execute", sqlPayload);

        isSafe.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason.ShouldContain("blocked");
    }

    [Theory]
    [InlineData("SELECT id, name, email FROM customers WHERE active = true")]
    [InlineData("SELECT COUNT(*) AS total FROM transactions GROUP BY category")]
    [InlineData("{\"sql\":\"SELECT * FROM vw_sales_summary\"}")]
    public void AstShadowingFilter_AllowsSqlSelect(string sqlPayload)
    {
        var (isSafe, reason) = AstShadowingFilter.IsSafeForShadowing("/api/sql/execute", sqlPayload);

        isSafe.ShouldBeTrue();
        reason.ShouldBeNull();
    }

    [Fact]
    public void PiiShadowingRedactor_RedactsSensitiveHeaders_AndMasksEmailsAndCards()
    {
        // Arrange
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer prod-super-secret-user-jwt-token",
            ["Cookie"] = "session_id=abcdef123456",
            ["X-Api-Key"] = "secret-client-key-999",
            ["User-Agent"] = "Mozilla/5.0",
            ["Accept"] = "application/json"
        };

        var rawBody = """
        {
          "email": "customer.vip@enterprise.com",
          "creditCard": "4111-2222-3333-4444",
          "query": "query { customerByEmail(email: \"user@example.org\") { id } }"
        }
        """;

        // Act
        var sanitizedHeaders = PiiShadowingRedactor.RedactHeaders(headers, stripPiiHeaders: true);
        var sanitizedBody = PiiShadowingRedactor.RedactBody(rawBody);

        // Assert
        sanitizedHeaders.ShouldNotContainKey("Cookie");
        sanitizedHeaders.ShouldNotContainKey("X-Api-Key");
        sanitizedHeaders["Authorization"].ShouldBe("Bearer staging-shadow-synthetic-token");
        sanitizedHeaders["User-Agent"].ShouldBe("Mozilla/5.0");

        sanitizedBody.ShouldNotBeNull();
        sanitizedBody.ShouldNotContain("customer.vip@enterprise.com");
        sanitizedBody.ShouldNotContain("user@example.org");
        sanitizedBody.ShouldNotContain("4111-2222-3333-4444");
        sanitizedBody.ShouldContain("***@redacted.local");
        sanitizedBody.ShouldContain("****-****-****-****");
    }

    [Fact]
    public void TrafficShadowingService_Enqueues_SafeRequests()
    {
        // Arrange
        var options = new GatewayOptions
        {
            TrafficShadowing = new TrafficShadowingOptions
            {
                Enabled = true,
                SampleRatePercentage = 100.0,
                ChannelCapacity = 100
            }
        };

        using var service = new TrafficShadowingService(
            Options.Create(options),
            new HttpClient(),
            NullLogger<TrafficShadowingService>.Instance);

        var request = new ShadowRequest(
            Method: "POST",
            PathAndQuery: "/graphql",
            Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer test" },
            Body: "query { users { id } }"
        );

        // Act
        var enqueued = service.EnqueueShadowRequest(request);

        // Assert
        enqueued.ShouldBeTrue();
        service.EnqueuedRequestsCount.ShouldBe(1);
        service.DroppedRequestsCount.ShouldBe(0);
    }

    [Fact]
    public async Task TrafficShadowingMiddleware_RejectsMutation_AndEnqueuesQuery()
    {
        // Arrange
        var shadowingService = Substitute.For<ITrafficShadowingService>();
        shadowingService.IsEnabled.Returns(true);
        shadowingService.ShouldSample().Returns(true);

        var middleware = new TrafficShadowingMiddleware(
            next: (ctx) => Task.CompletedTask,
            NullLogger<TrafficShadowingMiddleware>.Instance);

        // Act 1: Mutation request
        var mutationContext = new DefaultHttpContext();
        mutationContext.Request.Path = "/graphql";
        mutationContext.Request.Method = "POST";
        mutationContext.Request.ContentType = "application/json";
        var mutationBody = Encoding.UTF8.GetBytes("mutation { deleteAccount(id: 1) }");
        mutationContext.Request.Body = new MemoryStream(mutationBody);
        mutationContext.Request.ContentLength = mutationBody.Length;

        await middleware.InvokeAsync(mutationContext, shadowingService);

        // Assert 1: Mutation must NOT be enqueued
        shadowingService.DidNotReceive().EnqueueShadowRequest(Arg.Any<ShadowRequest>());

        // Act 2: Safe Query request
        var queryContext = new DefaultHttpContext();
        queryContext.Request.Path = "/graphql";
        queryContext.Request.Method = "POST";
        queryContext.Request.ContentType = "application/json";
        var queryBody = Encoding.UTF8.GetBytes("query { accounts { id balance } }");
        queryContext.Request.Body = new MemoryStream(queryBody);
        queryContext.Request.ContentLength = queryBody.Length;

        await middleware.InvokeAsync(queryContext, shadowingService);

        // Assert 2: Safe query MUST be enqueued
        shadowingService.Received(1).EnqueueShadowRequest(Arg.Is<ShadowRequest>(r => r.PathAndQuery == "/graphql"));
    }
}
