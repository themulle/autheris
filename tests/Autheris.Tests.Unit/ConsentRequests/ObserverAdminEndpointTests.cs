namespace Autheris.Tests.Unit.ConsentRequests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1161: the admin endpoints never make an observer the target of a route or of rerouted requests, reject an unknown role,
/// and turn a role conflict found inside the repository transaction into <c>409</c> with a fixed detail.
/// </summary>
[Trait("Category", "Security")]
public sealed class ObserverAdminEndpointTests
{
    private const string Tenant = "tenant-admin";
    private const string RawRepositoryDetail = "raw repository detail";
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SystemId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000161");

    private static ExternalWorkflowSystem System(ExternalWorkflowSystemRole role) =>
        new(SystemId, Tenant, "Cockpit", "cockpit-client", "https://cockpit.example.com/events", "protected-secret",
            true, "admin", FixedNow, null, role);

    private static IConsentWorkflowConfigRepository ConfigRepoWith(ExternalWorkflowSystem system) =>
        NullFake.Create<IConsentWorkflowConfigRepository>((method, _) =>
            method.Name == nameof(IConsentWorkflowConfigRepository.GetExternalWorkflowSystemAsync)
                ? Task.FromResult<ExternalWorkflowSystem?>(system)
                : NullFake.UseDefault);

    /// <summary>An admin repository that records the methods called and lets one method fail with a role conflict.</summary>
    private static IConsentWorkflowAdminRepository AdminRepo(List<string> calls, string? conflictOn = null) =>
        NullFake.Create<IConsentWorkflowAdminRepository>((method, _) =>
        {
            calls.Add(method.Name);
            return method.Name == conflictOn
                ? FailedTask(method, new ConsentDecisionException(ConsentDecisionErrorCodes.ExternalSystemRoleConflict, RawRepositoryDetail, 409))
                : NullFake.UseDefault;
        });

    private static object FailedTask(MethodInfo method, Exception exception)
    {
        var resultType = method.ReturnType.GetGenericArguments()[0];
        return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
            .MakeGenericMethod(resultType).Invoke(null, [exception])!;
    }

    private static DefaultHttpContext AdminContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", Tenant),
                new Claim("sub", "S-1-5-21-1001"),
                new Claim(ClaimTypes.Role, "GovernanceAdmin")
            ], "Test"))
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int Status, string? Code, string Body)> ExecuteAsync(HttpContext context, IResult result)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        if (body.Length == 0)
        {
            return (context.Response.StatusCode, null, body);
        }

        using var doc = JsonDocument.Parse(body);
        return (context.Response.StatusCode, doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null, body);
    }

    private static IHostEnvironment Production() =>
        NullFake.Create<IHostEnvironment>((method, _) =>
            method.Name == "get_" + nameof(IHostEnvironment.EnvironmentName) ? Environments.Production : NullFake.UseDefault);

    [Theory]
    [InlineData(ExternalWorkflowSystemRole.Observer)]
    [InlineData(ExternalWorkflowSystemRole.Unknown)]
    public async Task Route_upsert_refuses_a_non_workflow_target(ExternalWorkflowSystemRole role)
    {
        var calls = new List<string>();
        var context = AdminContext();

        var result = await ConsentWorkflowAdminEndpoints.UpsertRouteAsync(
            "TENANT", "*", new ConsentWorkflowAdminEndpoints.UpsertWorkflowRouteRequest("EXTERNAL", SystemId, 30, 14),
            context, AdminRepo(calls), ConfigRepoWith(System(role)), new FakeTimeProvider(FixedNow), CancellationToken.None);

        (await ExecuteAsync(context, result)).Status.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        calls.ShouldNotContain(nameof(IConsentWorkflowAdminRepository.UpsertWorkflowRouteAsync));
    }

    [Fact]
    public async Task Route_upsert_returns_409_when_the_target_changes_role_concurrently()
    {
        var calls = new List<string>();
        var context = AdminContext();

        var result = await ConsentWorkflowAdminEndpoints.UpsertRouteAsync(
            "TENANT", "*", new ConsentWorkflowAdminEndpoints.UpsertWorkflowRouteRequest("EXTERNAL", SystemId, 30, 14),
            context, AdminRepo(calls, nameof(IConsentWorkflowAdminRepository.UpsertWorkflowRouteAsync)),
            ConfigRepoWith(System(ExternalWorkflowSystemRole.Workflow)), new FakeTimeProvider(FixedNow), CancellationToken.None);

        var (status, code, body) = await ExecuteAsync(context, result);
        status.ShouldBe(StatusCodes.Status409Conflict);
        code.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemRoleConflict);
        body.ShouldNotContain(RawRepositoryDetail);
    }

    [Theory]
    [InlineData(ExternalWorkflowSystemRole.Observer)]
    [InlineData(ExternalWorkflowSystemRole.Unknown)]
    public async Task Reroute_refuses_a_non_workflow_target(ExternalWorkflowSystemRole role)
    {
        var calls = new List<string>();
        var context = AdminContext();

        var result = await ConsentWorkflowAdminEndpoints.RerouteRequestsAsync(
            new ConsentWorkflowAdminEndpoints.RerouteRequestsBody("EXTERNAL", TargetExternalSystemId: SystemId),
            context, AdminRepo(calls), ConfigRepoWith(System(role)), new FakeTimeProvider(FixedNow), CancellationToken.None);

        (await ExecuteAsync(context, result)).Status.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        calls.ShouldNotContain(nameof(IConsentWorkflowAdminRepository.RerouteOpenRequestsAsync));
    }

    [Fact]
    public async Task External_system_upsert_rejects_an_unknown_role_value()
    {
        var calls = new List<string>();
        var context = AdminContext();

        var result = await ConsentWorkflowAdminEndpoints.UpsertExternalSystemAsync(
            SystemId,
            new ConsentWorkflowAdminEndpoints.UpsertExternalSystemRequest(
                "Cockpit", "cockpit-client", "https://cockpit.example.com/events", null, Role: "Auditor"),
            context, AdminRepo(calls), ConfigRepoWith(System(ExternalWorkflowSystemRole.Workflow)), Production(),
            new FakeTimeProvider(FixedNow), CancellationToken.None);

        (await ExecuteAsync(context, result)).Status.ShouldBe(StatusCodes.Status400BadRequest);
        calls.ShouldNotContain(nameof(IConsentWorkflowAdminRepository.UpsertExternalWorkflowSystemAsync));
    }

    [Fact]
    public async Task External_system_upsert_returns_409_with_a_fixed_detail_when_the_role_is_still_referenced()
    {
        var calls = new List<string>();
        var context = AdminContext();

        var result = await ConsentWorkflowAdminEndpoints.UpsertExternalSystemAsync(
            SystemId,
            new ConsentWorkflowAdminEndpoints.UpsertExternalSystemRequest(
                "Cockpit", "cockpit-client", "https://cockpit.example.com/events", null, Role: "Observer"),
            context, AdminRepo(calls, nameof(IConsentWorkflowAdminRepository.UpsertExternalWorkflowSystemAsync)),
            ConfigRepoWith(System(ExternalWorkflowSystemRole.Workflow)), Production(), new FakeTimeProvider(FixedNow),
            CancellationToken.None);

        var (status, code, body) = await ExecuteAsync(context, result);
        status.ShouldBe(StatusCodes.Status409Conflict);
        code.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemRoleConflict);
        body.ShouldNotContain(RawRepositoryDetail);
    }

    [Theory]
    [InlineData(ExternalWorkflowSystemRole.Workflow, "Workflow")]
    [InlineData(ExternalWorkflowSystemRole.Observer, "Observer")]
    [InlineData(ExternalWorkflowSystemRole.Unknown, "Unknown")]
    public void Api_role_names_are_stable(ExternalWorkflowSystemRole role, string expected) =>
        role.ToApiString().ShouldBe(expected);
}
