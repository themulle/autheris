using System.Net;
using System.Text.Json;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Autheris.Extensions.Tests;

public sealed class OpenMetadataClientTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handlerFunc(request));
        }
    }

    [Fact]
    public async Task GetTablesAsync_SendsCorrectHeadersAndParsesEntities()
    {
        HttpRequestMessage? interceptedRequest = null;
        var tables = new List<OpenMetadataTable>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "customers",
                FullyQualifiedName = "postgres.sales.public.customers",
                Service = new OpenMetadataEntityReference { Name = "sales" },
                DatabaseSchema = new OpenMetadataEntityReference { Name = "public" },
                Columns =
                [
                    new OpenMetadataColumn
                    {
                        Name = "email",
                        DataType = "VARCHAR",
                        Tags = [new OpenMetadataTag { TagFQN = "PII.Email" }]
                    }
                ]
            }
        };

        var handler = new MockHttpMessageHandler(req =>
        {
            interceptedRequest = req;
            var json = JsonSerializer.Serialize(new { data = tables });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                ServerUrl = "http://openmetadata-test:8585/api/v1",
                AuthToken = "secret-token-123"
            }
        });

        var httpClient = new HttpClient(handler);
        var client = new OpenMetadataClient(httpClient, options, NullLogger<OpenMetadataClient>.Instance);

        var result = await client.GetTablesAsync();

        Assert.NotNull(interceptedRequest);
        Assert.Equal("Bearer", interceptedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token-123", interceptedRequest.Headers.Authorization?.Parameter);
        Assert.Single(result);
        Assert.Equal("customers", result[0].Name);
        Assert.Single(result[0].Columns);
        Assert.Equal("PII.Email", result[0].Columns[0].Tags[0].TagFQN);
    }

    [Fact]
    public async Task GetTableByFqnAsync_ReturnsNull_WhenNotFound()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions { ServerUrl = "http://openmetadata-test:8585/api/v1" }
        });

        var httpClient = new HttpClient(handler);
        var client = new OpenMetadataClient(httpClient, options, NullLogger<OpenMetadataClient>.Instance);

        var result = await client.GetTableByFqnAsync("non_existent_table");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetTableByFqnAsync_ReturnsTable_WhenFound()
    {
        var table = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "orders",
            FullyQualifiedName = "postgres.sales.public.orders"
        };

        var handler = new MockHttpMessageHandler(_ =>
        {
            var json = JsonSerializer.Serialize(table);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions { ServerUrl = "http://openmetadata-test:8585/api/v1" }
        });

        var httpClient = new HttpClient(handler);
        var client = new OpenMetadataClient(httpClient, options, NullLogger<OpenMetadataClient>.Instance);

        var result = await client.GetTableByFqnAsync("postgres.sales.public.orders");
        Assert.NotNull(result);
        Assert.Equal("orders", result.Name);
    }

    [Fact]
    public async Task GetPoliciesRolesTeamsUsers_ParseSuccessfully()
    {
        var policies = new List<OpenMetadataPolicy>
        {
            new()
            {
                Name = "FinancePolicy",
                Rules = [new OpenMetadataRule { Name = "AllAccess", Effect = "allow", Resources = ["table"] }]
            }
        };

        var roles = new List<OpenMetadataRole>
        {
            new()
            {
                Name = "FinanceAuditor",
                Policies = [new OpenMetadataEntityReference { Name = "FinancePolicy" }]
            }
        };

        var teams = new List<OpenMetadataTeam>
        {
            new()
            {
                Name = "FinanceTeam",
                Policies = [new OpenMetadataEntityReference { Name = "FinancePolicy" }]
            }
        };

        var users = new List<OpenMetadataUser>
        {
            new()
            {
                Name = "alice",
                Email = "alice@corp.local",
                Roles = [new OpenMetadataEntityReference { Name = "FinanceAuditor" }]
            }
        };

        var handler = new MockHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            string json;
            if (path.EndsWith("/policies", StringComparison.OrdinalIgnoreCase))
            {
                json = JsonSerializer.Serialize(new { data = policies });
            }
            else if (path.EndsWith("/roles", StringComparison.OrdinalIgnoreCase))
            {
                json = JsonSerializer.Serialize(new { data = roles });
            }
            else if (path.EndsWith("/teams", StringComparison.OrdinalIgnoreCase))
            {
                json = JsonSerializer.Serialize(new { data = teams });
            }
            else
            {
                json = JsonSerializer.Serialize(new { data = users });
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions { ServerUrl = "http://openmetadata-test:8585/api/v1" }
        });

        var httpClient = new HttpClient(handler);
        var client = new OpenMetadataClient(httpClient, options, NullLogger<OpenMetadataClient>.Instance);

        var resPolicies = await client.GetPoliciesAsync();
        var resRoles = await client.GetRolesAsync();
        var resTeams = await client.GetTeamsAsync();
        var resUsers = await client.GetUsersAsync();

        Assert.Single(resPolicies);
        Assert.Equal("FinancePolicy", resPolicies[0].Name);
        Assert.Single(resRoles);
        Assert.Equal("FinanceAuditor", resRoles[0].Name);
        Assert.Single(resTeams);
        Assert.Equal("FinanceTeam", resTeams[0].Name);
        Assert.Single(resUsers);
        Assert.Equal("alice", resUsers[0].Name);
    }
}
