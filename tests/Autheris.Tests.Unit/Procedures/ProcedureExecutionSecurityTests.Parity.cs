using System.Linq;
using System.Security.Claims;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Procedures;

/// <summary>Hidden and unknown procedures answer and audit alike on REST and MCP.</summary>
public partial class ProcedureExecutionSecurityTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task Rest_HiddenAndUnknownProcedure_Indistinguishable_SEC_PROC_09()
    {
        var hidden = Active();
        DenyConsent(hidden);
        var unknown = Active();

        var hiddenResponse = await CallRoute(hidden.Create(), post: false, "get_orders", "customer_id=7", null);
        var unknownResponse = await CallRoute(unknown.Create(), post: false, "nope", "customer_id=7", null);

        hiddenResponse.Status.ShouldBe(404);
        unknownResponse.ShouldBe(hiddenResponse);
        AssertSameDenialAudit(hidden, unknown);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Mcp_HiddenAndUnknownProcedure_Indistinguishable_SEC_PROC_09()
    {
        var hidden = Active();
        DenyConsent(hidden);
        var unknown = Active();

        async Task<string> CallAsync(Env env, string name)
        {
            var service = env.Create();
            var handler = new global::Autheris.Application.Mcp.Services.McpToolExecutionHandler(
                Dummy<global::Autheris.Application.Sql.Interfaces.IGovernedSqlExecutionService>(),
                Dummy<global::Autheris.Application.DataSources.Interfaces.IGovernedDataQueryService>(),
                Dummy<global::Autheris.Application.Catalog.Interfaces.ICatalogDiscoveryService>(),
                Dummy<global::Autheris.Application.Mcp.Interfaces.IApiDispatcherService>(),
                Dummy<global::Autheris.Application.Lineage.ILineageGraphStore>(),
                Dummy<ITableMetadataRepository>(),
                procedureCatalog: new ProcedureCatalog(env.Registry, service),
                procedureExecutionService: service);
            var tool = global::Autheris.Application.Mcp.Services.McpDatasetTools.Definitions()
                .Single(t => t.Name == global::Autheris.Application.Mcp.Services.McpDatasetTools.CallProcedure);
            var session = new McpSessionContext("s", "u", Tenant.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-5-21-1001");
            return await handler.ExecuteToolAsync(tool, "{\"procedure_name\":\"" + name + "\",\"arguments\":{\"customer_id\":7}}", session, CancellationToken.None);
        }

        string hiddenResult = await CallAsync(hidden, "get_orders");
        string unknownResult = await CallAsync(unknown, "nope");

        hiddenResult.ShouldContain("\"code\":\"NOT_FOUND\"");
        unknownResult.ShouldBe(hiddenResult);
        AssertSameDenialAudit(hidden, unknown);
    }

    /// <summary>A dependency the call_procedure path never touches; any call fails loudly.</summary>
    private static T Dummy<T>() where T : class => System.Reflection.DispatchProxy.Create<T, UnusedProxy>();

    public class UnusedProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }

    private static void AssertSameDenialAudit(Env hidden, Env unknown)
    {
        hidden.AuditEntries.Count.ShouldBe(1);
        unknown.AuditEntries.Count.ShouldBe(1);
        unknown.AuditEntries[0].EventType.ShouldBe(hidden.AuditEntries[0].EventType);
        unknown.AuditEntries[0].Decision.ShouldBe(hidden.AuditEntries[0].Decision);
        unknown.AuditEntries[0].TenantId.ShouldBe(hidden.AuditEntries[0].TenantId);
        unknown.AuditEntries[0].ActorSid.ShouldBe(hidden.AuditEntries[0].ActorSid);
    }
}
