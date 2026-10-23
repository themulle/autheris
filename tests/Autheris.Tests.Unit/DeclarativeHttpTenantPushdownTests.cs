namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>MISC-13: executors push down only the gateway-resolved context tenant, never a raw token claim.</summary>
public sealed class DeclarativeHttpTenantPushdownTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class CapturingLogger : ILogger<DeclarativeHttpDataSourceExecutor>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private static (DeclarativeHttpDataSourceExecutor Executor, RecordingHandler Handler, CapturingLogger Logger) Create()
    {
        var handler = new RecordingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        var logger = new CapturingLogger();
        return (new DeclarativeHttpDataSourceExecutor(factory, logger), handler, logger);
    }

    private static DataSourceExecutionContext Context(HttpEndpointDescriptor descriptor, ClaimsPrincipal principal, TenantId? tenant)
    {
        var id = new TableIdentifier("crm", "public", "customers");
        var metadata = new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = "crm", SchemaName = "public", TableName = "customers", DataSourceType = DataSourceType.HttpDeclarative, HttpEndpoint = descriptor },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        return new DataSourceExecutionContext(
            "crm",
            metadata,
            principal,
            TableAccessDecision.Allowed(id, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true),
            new Dictionary<string, object?>(),
            ["id"],
            Tenant: tenant);
    }

    private static ClaimsPrincipal Token(string rawTenantClaim, params string[] roles) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1"), new Claim("tenant_id", rawTenantClaim) }
                .Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))),
            "Test"));

    [Fact]
    [Trait("Category", "Security")]
    public async Task FixedRealmTenant_PushedInsteadOfTokenClaim()
    {
        var (executor, handler, _) = Create();
        var query = new HttpEndpointDescriptor { BaseUrl = "https://93.184.216.34", PathTemplate = "/v1/c", TenantIdQueryParam = "tenant" };
        var header = new HttpEndpointDescriptor { BaseUrl = "https://93.184.216.34", PathTemplate = "/v1/c", TenantIdHeaderName = "X-Tenant" };

        await executor.ExecuteAsync(Context(query, Token("partner-b"), new TenantId("partner-a")));
        await executor.ExecuteAsync(Context(header, Token("partner-b"), new TenantId("partner-a")));

        handler.Requests[0].RequestUri!.Query.ShouldContain("tenant=partner-a");
        handler.Requests[0].RequestUri!.Query.ShouldNotContain("partner-b");
        handler.Requests[1].Headers.GetValues("X-Tenant").Single().ShouldBe("partner-a");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ClusterAdminSelectedTenant_Pushed()
    {
        var (executor, handler, logger) = Create();
        var descriptor = new HttpEndpointDescriptor { BaseUrl = "https://93.184.216.34", PathTemplate = "/v1/c", TenantIdHeaderName = "X-Tenant" };

        await executor.ExecuteAsync(Context(descriptor, Token("home-tenant", "ClusterAdmin"), new TenantId("selected-tenant")));

        handler.Requests.Single().Headers.GetValues("X-Tenant").Single().ShouldBe("selected-tenant");
        logger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MissingContextTenant_FailsClosed()
    {
        var (executor, handler, _) = Create();
        var descriptor = new HttpEndpointDescriptor { BaseUrl = "https://93.184.216.34", PathTemplate = "/v1/c", TenantIdHeaderName = "X-Tenant" };

        await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(Context(descriptor, Token("partner-b"), tenant: null)));

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ClaimMismatch_EmitsAudit()
    {
        var (executor, _, logger) = Create();
        var descriptor = new HttpEndpointDescriptor { BaseUrl = "https://93.184.216.34", PathTemplate = "/v1/c", TenantIdQueryParam = "tenant" };

        await executor.ExecuteAsync(Context(descriptor, Token("partner-b"), new TenantId("partner-a")));

        logger.Warnings.ShouldContain(w => w.Contains("MISC-13", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Executors_DoNotReadTenantClaimsDirectly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src", "Autheris.Application", "Services"), "*Executor*.cs")
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("FindFirst(\"tenant_id\")", StringComparison.Ordinal)
                       || text.Contains("FindFirst(\"tid\")", StringComparison.Ordinal)
                       || text.Contains("FindFirst(\"tenant\")", StringComparison.Ordinal);
            })
            .ToList();
        offenders.ShouldBeEmpty();
    }
}
