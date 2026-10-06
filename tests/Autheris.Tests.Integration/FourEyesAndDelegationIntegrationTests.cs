using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

public class FourEyesAndDelegationIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public FourEyesAndDelegationIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
        });
    }

    private HttpClient CreateClient(Sid? userSid = null, string[]? roles = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        if (userSid.HasValue)
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value.Value);
        }
        if (roles != null && roles.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Test-Roles", string.Join(",", roles));
        }
        return client;
    }

    [Fact]
    public async Task FourEyesPrinciple_StepByStep_FullLifecycleFlow()
    {
        var requesterSid = new Sid("S-1-5-21-FOUR-EYES-REQ-1");
        var approver1Sid = new Sid("S-1-5-21-APPROVER-A");
        var approver2Sid = new Sid("S-1-5-21-APPROVER-B");

        var requesterClient = CreateClient(requesterSid);

        // 1. Initially, requester has no access to sensitive finance_table_5
        var queryBefore = new
        {
            query = @"query { table(domain: ""finance"", name: ""finance_table_5"") { tableName totalCount } }"
        };
        var resBefore = await requesterClient.PostAsJsonAsync("/graphql", queryBefore);
        var bodyBefore = await resBefore.Content.ReadAsStringAsync();
        bodyBefore.ShouldContain("FORBIDDEN");

        // 2. Requester submits access request for finance_table_5 (which requires 4-eyes)
        var reqMutation = new
        {
            query = @"mutation {
                requestTableAccess(
                    domain: ""finance"",
                    schema: ""dbo"",
                    tableName: ""finance_table_5"",
                    justification: ""Quarterly executive audit"",
                    durationDays: 14,
                    idempotencyKey: ""key-4eyes-req-1""
                ) {
                    requestId
                    status
                    message
                }
            }"
        };
        var reqResponse = await requesterClient.PostAsJsonAsync("/graphql", reqMutation);
        var reqBody = await reqResponse.Content.ReadAsStringAsync();
        reqBody.ShouldContain("PENDING");

        using var reqDoc = JsonDocument.Parse(reqBody);
        var requestId = reqDoc.RootElement
            .GetProperty("data")
            .GetProperty("requestTableAccess")
            .GetProperty("requestId")
            .GetGuid();

        // 3. Step 1 Approval: Approver 1 approves
        var approver1Client = CreateClient(approver1Sid, new[] { "DataOwner" });
        var step1Mutation = new
        {
            query = $@"mutation {{
                approveConsentRequest(
                    requestId: ""{requestId}"",
                    idempotencyKey: ""key-4eyes-step1""
                ) {{
                    requestId
                    status
                    message
                }}
            }}"
        };
        var step1Response = await approver1Client.PostAsJsonAsync("/graphql", step1Mutation);
        var step1Body = await step1Response.Content.ReadAsStringAsync();
        step1Body.ShouldContain("PENDING_SECOND_APPROVAL");

        // 4. Requester attempts to query after ONLY 1 approval -> Access must STILL be FORBIDDEN!
        var resAfterStep1 = await requesterClient.PostAsJsonAsync("/graphql", queryBefore);
        var bodyAfterStep1 = await resAfterStep1.Content.ReadAsStringAsync();
        bodyAfterStep1.ShouldContain("FORBIDDEN");

        // 5. Approver 1 attempts to approve again -> Must be rejected (cannot approve twice)
        var step1RetryResponse = await approver1Client.PostAsJsonAsync("/graphql", new
        {
            query = $@"mutation {{
                approveConsentRequest(
                    requestId: ""{requestId}"",
                    idempotencyKey: ""key-4eyes-step1-retry""
                ) {{
                    requestId
                    status
                }}
            }}"
        });
        var step1RetryBody = await step1RetryResponse.Content.ReadAsStringAsync();
        step1RetryBody.ShouldContain("errors");

        // 6. Step 2 Approval: Approver 2 (distinct approver) approves
        var approver2Client = CreateClient(approver2Sid, new[] { "DataOwner" });
        var step2Mutation = new
        {
            query = $@"mutation {{
                approveConsentRequest(
                    requestId: ""{requestId}"",
                    idempotencyKey: ""key-4eyes-step2""
                ) {{
                    requestId
                    status
                    message
                }}
            }}"
        };
        var step2Response = await approver2Client.PostAsJsonAsync("/graphql", step2Mutation);
        var step2Body = await step2Response.Content.ReadAsStringAsync();
        step2Body.ShouldContain("APPROVED");

        // 7. Requester queries table now -> Access ALLOWED!
        var resFinal = await requesterClient.PostAsJsonAsync("/graphql", queryBefore);
        var bodyFinal = await resFinal.Content.ReadAsStringAsync();
        bodyFinal.ShouldNotContain("FORBIDDEN");
        bodyFinal.ShouldContain("finance.dbo.finance_table_5");
    }

    [Fact]
    public async Task FourEyesPrinciple_StepTwoRejection_AccessRemainsForbidden()
    {
        var requesterSid = new Sid("S-1-5-21-FOUR-EYES-REJ-REQ");
        var approver1Sid = new Sid("S-1-5-21-APPROVER-A");
        var approver2Sid = new Sid("S-1-5-21-APPROVER-B");

        var requesterClient = CreateClient(requesterSid);

        // 1. Submit request
        var reqResponse = await requesterClient.PostAsJsonAsync("/graphql", new
        {
            query = @"mutation {
                requestTableAccess(
                    domain: ""finance"",
                    schema: ""dbo"",
                    tableName: ""finance_table_5"",
                    justification: ""Testing four eyes rejection"",
                    durationDays: 7,
                    idempotencyKey: ""key-4eyes-rej-req""
                ) {
                    requestId
                    status
                }
            }"
        });
        var reqBody = await reqResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(reqBody);
        var requestId = doc.RootElement.GetProperty("data").GetProperty("requestTableAccess").GetProperty("requestId").GetGuid();

        // 2. Approver 1 approves -> PENDING_SECOND_APPROVAL
        var approver1Client = CreateClient(approver1Sid, new[] { "DataOwner" });
        await approver1Client.PostAsJsonAsync("/graphql", new
        {
            query = $@"mutation {{
                approveConsentRequest(requestId: ""{requestId}"", idempotencyKey: ""key-rej-step1"") {{
                    status
                }}
            }}"
        });

        // 3. Approver 2 rejects with reason
        var approver2Client = CreateClient(approver2Sid, new[] { "DataOwner" });
        var rejectResponse = await approver2Client.PostAsJsonAsync("/graphql", new
        {
            query = $@"mutation {{
                rejectConsentRequest(
                    requestId: ""{requestId}"",
                    reason: ""Executive policy violation"",
                    idempotencyKey: ""key-rej-step2""
                ) {{
                    requestId
                    status
                    message
                }}
            }}"
        });
        var rejectBody = await rejectResponse.Content.ReadAsStringAsync();
        rejectBody.ShouldContain("REJECTED");
        rejectBody.ShouldContain("Executive policy violation");

        // 4. Requester queries table -> Access remains FORBIDDEN
        var resAfterRejection = await requesterClient.PostAsJsonAsync("/graphql", new
        {
            query = @"query { table(domain: ""finance"", name: ""finance_table_5"") { tableName totalCount } }"
        });
        var bodyAfterRejection = await resAfterRejection.Content.ReadAsStringAsync();
        bodyAfterRejection.ShouldContain("FORBIDDEN");
    }

    [Fact]
    public async Task Delegation_VacationWorkflow_EndToEndIntegration()
    {
        var requesterSid = new Sid("S-1-5-21-VACATION-DELEGATE-REQ");
        var delegateSid = new Sid("S-1-5-21-VACATION-DELEGATE-APP");

        // Primary owner for sales_table_1 is DATAOWNER-APPROVER
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var table = new TableIdentifier("sales", "dbo", "sales_table_1");
            var owners = await repo.GetDataOwnersForTableAsync(table);
            var primaryOwner = owners.First(o => o.IsActive);

            await repo.DelegateDataOwnershipAsync(new DataOwnerDelegation
            {
                DataOwnerId = primaryOwner.Id,
                DelegateSid = delegateSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(7),
                Reason = "Vacation coverage integration test"
            });
        }

        var requesterClient = CreateClient(requesterSid);

        // 1. Submit request
        var reqResponse = await requesterClient.PostAsJsonAsync("/graphql", new
        {
            query = @"mutation {
                requestTableAccess(
                    domain: ""sales"",
                    schema: ""dbo"",
                    tableName: ""sales_table_1"",
                    justification: ""Vacation delegation test access"",
                    durationDays: 7,
                    idempotencyKey: ""key-vacation-req""
                ) {
                    requestId
                    status
                }
            }"
        });
        var reqBody = await reqResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(reqBody);
        var requestId = doc.RootElement.GetProperty("data").GetProperty("requestTableAccess").GetProperty("requestId").GetGuid();

        // 2. Delegate approves request
        var delegateClient = CreateClient(delegateSid);
        var appResponse = await delegateClient.PostAsJsonAsync("/graphql", new
        {
            query = $@"mutation {{
                approveConsentRequest(
                    requestId: ""{requestId}"",
                    idempotencyKey: ""key-vacation-app""
                ) {{
                    requestId
                    status
                    message
                }}
            }}"
        });
        var appBody = await appResponse.Content.ReadAsStringAsync();
        appBody.ShouldContain("APPROVED");

        // 3. Requester queries table -> Access ALLOWED!
        var queryResponse = await requesterClient.PostAsJsonAsync("/graphql", new
        {
            query = @"query { table(domain: ""sales"", name: ""sales_table_1"") { tableName totalCount } }"
        });
        var queryBody = await queryResponse.Content.ReadAsStringAsync();
        queryBody.ShouldNotContain("FORBIDDEN");
        queryBody.ShouldContain("sales.dbo.sales_table_1");
    }

    [Fact]
    public async Task DataLoader_OddParentBatchEvaluation_WithSensitiveMasking_EndToEnd()
    {
        var userSid = new Sid("S-1-5-21-ODD-DATALOADER-INTEG");
        var client = CreateClient(userSid);

        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var parentTable = new TableIdentifier("finance", "dbo", "finance_table_1");
            var childTable = new TableIdentifier("finance", "dbo", "finance_items");

            var parentMeta = await repo.GetTableMetadataAsync(parentTable);
            var childMeta = await repo.GetTableMetadataAsync(childTable);
            parentMeta.ShouldNotBeNull();
            childMeta.ShouldNotBeNull();

            // Grant parent consent
            await repo.CreateConsentAsync(new Consent
            {
                TableId = parentMeta.Table.Id,
                TableIdentifier = parentTable,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(14),
                ColumnRules = new[]
                {
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "name", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
                }
            });

            // Grant child consent with masking on sensitive description column
            await repo.CreateConsentAsync(new Consent
            {
                TableId = childMeta.Table.Id,
                TableIdentifier = childTable,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(14),
                ColumnRules = new[]
                {
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "product_name", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "price", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "sensitive_note", AccessLevel = ColumnAccessLevel.Mask }
                }
            });
        }

        // Query an odd number of parent entities (e.g., 3; kept small because M-9 weights nested unpaginated lists by the list multiplier) with nested relation "items"
        var query = new
        {
            query = @"query {
                finance {
                    invoicesWithItems(first: 3) {
                        id
                        amount
                        vendor
                        items {
                            id
                            productName
                            price
                            sensitiveNote
                        }
                    }
                }
            }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("FORBIDDEN");
        body.ShouldContain("invoicesWithItems");
        body.ShouldContain("items");
        body.ShouldContain("Enterprise License Pack");
        body.ShouldContain("[CONFIDENTIAL NOTE]");
    }
}
