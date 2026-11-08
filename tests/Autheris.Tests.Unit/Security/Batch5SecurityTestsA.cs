namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Api.Security;
using Autheris.Application.Analytics;
using Autheris.Application.DataSources.Changes;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Runtime.Jobs.Interfaces;
using Autheris.Application.Runtime.Jobs.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.Common.Serialization;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security unit tests for Batch 5 advisories:
/// 1. GHSA-335j-6vwc-7p4c
/// 2. GHSA-wg8p-wfw5-qvgj
/// 3. GHSA-jcpx-6m27-2m97
/// 4. GHSA-qx32-576x-5cq2
/// 5. GHSA-cj7m-c75j-6p6h
/// 6. GHSA-78j7-3gh6-7cqx
/// 7. GHSA-mgv5-4r69-mwww
/// </summary>
[Trait("Category", "Security")]
public sealed class Batch5SecurityTestsA
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private static async Task<(int Status, string Body)> ExecuteResultAsync(IResult result, HttpContext context)
    {
        if (context.RequestServices == null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            context.RequestServices = services.BuildServiceProvider();
        }
        if (result is Microsoft.AspNetCore.Http.IStatusCodeHttpResult sc && sc.StatusCode.HasValue)
        {
            context.Response.StatusCode = sc.StatusCode.Value;
        }
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static HttpContext CreateEndpointContext(
        string tenant = "tenant-a",
        string callerSid = "S-1-5-21-ADMIN",
        string authScheme = "Bearer",
        IAccessProfileCache? profileCache = null,
        IAuditLogRepository? auditRepo = null,
        params string[] roles)
    {
        var services = new ServiceCollection();
        if (profileCache != null) services.AddSingleton(profileCache);
        services.AddLogging();
        services.AddAuditWriterOver(auditRepo ?? Substitute.For<IAuditLogRepository>());

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, callerSid),
            new("sub", callerSid),
            new("tenant_id", tenant)
        };
        foreach (var r in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, r));
            claims.Add(new Claim("roles", r));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authScheme));

        var effectiveRoles = roles.Length > 0 ? roles : ["GovernanceAdmin"];
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid(callerSid),
            TenantId = new TenantId(tenant),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(effectiveRoles.Where(r => r is not ("ClusterAdmin" or "GovernanceAdmin"))),
            ClusterRoles = new HashSet<string>(effectiveRoles.Where(r => r is "ClusterAdmin" or "GovernanceAdmin")),
            AuthenticationScheme = authScheme
        };

        return context;
    }

    // =========================================================================
    // 1. GHSA-335j-6vwc-7p4c
    // =========================================================================

    [Fact]
    public async Task Ghsa_335j_7p4c_regression()
    {
        // Calling DeleteProfileAsync when profile is already null/deleted still invokes cache invalidation/epoch bump idempotently.
        var repo = new InMemoryAccessProfileRepository();
        var profileCache = Substitute.For<IAccessProfileCache>();
        var auditRepo = Substitute.For<IAuditLogRepository>();

        var context = CreateEndpointContext(
            tenant: TenantA.Value,
            callerSid: "S-1-5-21-GOVADMIN",
            authScheme: "Bearer",
            profileCache: profileCache,
            auditRepo: auditRepo,
            roles: ["GovernanceAdmin"]);

        // Act: Delete a profile that does NOT exist in the repository (e.g. was deleted or retry scenario)
        var result = await GovernanceEndpoints.DeleteProfileAsync("profile-already-deleted", context, repo, CancellationToken.None);
        var (status, _) = await ExecuteResultAsync(result, context);

        // Assert: Cache invalidation for the tenant was still triggered idempotently
        await profileCache.Received(1).InvalidateTenantAsync(TenantA, Arg.Any<CancellationToken>());
        status.ShouldBe(StatusCodes.Status404NotFound);

        // Also verify: if cache invalidation fails during retry, endpoint returns 503 ServiceUnavailable
        profileCache.ClearReceivedCalls();
        profileCache.When(c => c.InvalidateTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("Redis unavailable"));

        var retryFailResult = await GovernanceEndpoints.DeleteProfileAsync("profile-already-deleted", context, repo, CancellationToken.None);
        var (retryStatus, _) = await ExecuteResultAsync(retryFailResult, context);
        retryStatus.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    // =========================================================================
    // 2. GHSA-wg8p-wfw5-qvgj
    // =========================================================================

    [Fact]
    public async Task Ghsa_wg8p_qvgj_regression()
    {
        // With RequireTotp2Fa=false, if the approver has an enrolled TOTP secret but supplies no code, approval is denied.
        var clusterState = new InMemoryClusterStateProvider();
        var totpService = new TotpVerificationService(clusterState);
        var secretStore = new InMemoryTotpSecretStore();

        var approverSid = "steward-with-secret";
        var enrolledSecret = "JBSWY3DPEHPK3PXP";
        await secretStore.SetSecretAsync(approverSid, enrolledSecret);

        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions
            {
                Enabled = true,
                ApprovalTimeoutSeconds = 30,
                RequireDifferentApprover = false,
                RequireTotp2Fa = false // Explicitly false!
            }
        });

        var service = new HitLStepUpApprovalService(
            options,
            NullLogger<HitLStepUpApprovalService>.Instance,
            scopeFactory: null,
            clusterState: clusterState,
            totpVerificationService: totpService,
            totpSecretStore: secretStore);

        var requestTask = service.RequestStepUpApprovalAsync(
            "query_table",
            "tenant-1",
            "user-alice",
            TableIdentifier.Parse("sales.orders"));

        await Autheris.Tests.Common.WaitHelper.WaitUntil(
            () => service.GetPendingTickets("tenant-1").Any(),
            Autheris.Tests.Common.WaitHelper.HangGuard,
            "the step-up ticket was never registered");

        var ticket = Assert.Single(service.GetPendingTickets("tenant-1"));
        var approvalId = ticket.ApprovalId;
        var approverContext = new HitLApproverContext(approverSid, [approverSid], "tenant-1", IsCrossTenantAdmin: false);

        // Act: approver supplies no TOTP code even though they have an enrolled secret
        var result = await service.ApproveStepUpRequestAsync(approvalId, approverContext, totpCode: null);

        // Assert: must be denied because 2FA code is required
        result.IsApproved.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("TOTP 2FA code is required for Step-Up approval");

        // Ticket must remain Pending
        var fetchedTicket = await service.GetTicketAsync(approvalId);
        fetchedTicket.ShouldNotBeNull();
        fetchedTicket.Status.ShouldBe(HitLApprovalStatus.Pending);
    }

    // =========================================================================
    // 3. GHSA-jcpx-6m27-2m97
    // =========================================================================

    [Fact]
    public async Task Ghsa_jcpx_2m97_regression()
    {
        await Case_jcpx_2m97_1();
        await Case_jcpx_2m97_2();
    }

    private async Task Case_jcpx_2m97_1()
    {
        // Unify table existence oracle (missing vs denied) to 403 ACCESS_DENIED on TableNotFoundException
        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var aggService = Substitute.For<IGovernedAggregationService>();
        aggService.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new TableNotFoundException(tableId));

        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user1"), new Claim("tenant_id", "tenant-a")], "TestAuth")),
            Response = { Body = new MemoryStream() }
        };
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        var req = new AnalyticsEndpoints.AggregateRequestDto("sales.dbo.orders", Aggregates: [new AnalyticsEndpoints.AggregateSelectionDto("count", null, "n")]);
        var result = await AnalyticsEndpoints.HandleAggregateAsync(req, ctx, aggService, default);

        var (status, body) = await ExecuteResultAsync(result, ctx);
        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain(ApiErrorCodes.AccessDenied);
    }

    private async Task Case_jcpx_2m97_2()
    {
        // Unify table existence oracle (missing vs denied) to 403 ACCESS_DENIED on TableNotFoundException
        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var changeService = Substitute.For<IGovernedChangeDetectionService>();
        changeService.CheckTableModifiedAsync(
            tableId,
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns<ChangeCheckResult>(_ => throw new TableNotFoundException(tableId));

        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user1"), new Claim("tenant_id", "tenant-a")], "TestAuth")),
            Response = { Body = new MemoryStream() }
        };
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        var result = await ChangeDetectionEndpoints.HandleGetChangeTokenAsync("sales", "dbo", "orders", ctx, changeService, default);

        var (status, body) = await ExecuteResultAsync(result, ctx);
        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain(ApiErrorCodes.AccessDenied);
    }

    // =========================================================================
    // 4. GHSA-qx32-576x-5cq2
    // =========================================================================

    [Fact]
    public async Task Ghsa_qx32_5cq2_regression()
    {
        await Case_qx32_5cq2_1();
        await Case_qx32_5cq2_2();
        await Case_qx32_5cq2_3();
    }

    private async Task Case_qx32_5cq2_1()
    {
        // Verify caller without table ownership / admin standing is denied access to report for another subject
        var table = new TableIdentifier("finance", "dbo", "salary");
        var callerSid = new Sid("S-1-5-21-ANALYST");
        var foreignSubjectSid = new Sid("S-1-5-21-OTHER-USER");

        var ownershipRepo = Substitute.For<IDataOwnershipRepository>();
        ownershipRepo.IsAuthorizedApproverForTableAsync(table, callerSid, Arg.Any<CancellationToken>())
            .Returns(false);

        var lineageService = new LineageImpactAnalyzerService(
            Substitute.For<IConsentRepository>(),
            ownershipRepo,
            Substitute.For<ILineageGraphStore>(),
            Substitute.For<IAuditLogRepository>(),
            Substitute.For<ITableMetadataRepository>(),
            NullLogger<LineageImpactAnalyzerService>.Instance);

        var callerContext = new CallerSecurityContext(
            callerSid,
            GroupSids: [],
            Roles: ["Reader"], // Not GovernanceAdmin, not PrivacyAdmin
            Tenant: TenantA,
            IsGovernanceAdmin: false,
            IsClusterAdmin: false);

        // Act & Assert: Denied when attempting to query report for a foreign subject
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            lineageService.GetGdprDataDisclosureReportAsync(table, foreignSubjectSid, 365, callerContext, CancellationToken.None));

        // Also denied when requesting an unscoped / all-subjects report without table authorization
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            lineageService.GetGdprDataDisclosureReportAsync(table, subjectSid: null, 365, callerContext, CancellationToken.None));
    }

    private async Task Case_qx32_5cq2_2()
    {
        // When a non-owner/non-admin caller views their own disclosure report, only their own accesses are visible.
        var table = new TableIdentifier("finance", "dbo", "salary");
        var callerSid = new Sid("S-1-5-21-CALLER");
        var otherActorSid = new Sid("S-1-5-21-OTHER-ACTOR");

        var auditLogs = new List<AuditLogEntry>
        {
            new()
            {
                TenantId = TenantA,
                ActorSid = callerSid,
                TargetTable = table.ToString(),
                Decision = "ALLOW",
                EventType = "QUERY"
            },
            new()
            {
                TenantId = TenantA,
                ActorSid = otherActorSid,
                TargetTable = table.ToString(),
                Decision = "ALLOW",
                EventType = "QUERY"
            }
        };

        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.QueryAuditLogsAsync(Arg.Any<string?>(), Arg.Any<Sid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(auditLogs);

        var ownershipRepo = Substitute.For<IDataOwnershipRepository>();
        ownershipRepo.IsAuthorizedApproverForTableAsync(table, callerSid, Arg.Any<CancellationToken>())
            .Returns(false); // Not owner

        var lineageService = new LineageImpactAnalyzerService(
            Substitute.For<IConsentRepository>(),
            ownershipRepo,
            Substitute.For<ILineageGraphStore>(),
            auditRepo,
            Substitute.For<ITableMetadataRepository>(),
            NullLogger<LineageImpactAnalyzerService>.Instance);

        var callerContext = new CallerSecurityContext(
            callerSid,
            GroupSids: [],
            Roles: ["Reader"],
            Tenant: TenantA,
            IsGovernanceAdmin: false,
            IsClusterAdmin: false);

        // Act: Caller requests report for themselves
        var report = await lineageService.GetGdprDataDisclosureReportAsync(table, callerSid, 365, callerContext, CancellationToken.None);

        // Assert: The report disclosed recipients must ONLY contain callerSid, never otherActorSid
        report.DisclosedRecipients.ShouldNotBeEmpty();
        report.DisclosedRecipients.ShouldAllBe(r => r.RecipientSid == callerSid.Value);
        report.DisclosedRecipients.Any(r => r.RecipientSid == otherActorSid.Value).ShouldBeFalse();
    }

    private async Task Case_qx32_5cq2_3()
    {
        // ForwardAuth scheme claiming ClusterAdmin role is not canonical ClusterAdmin and must be rejected with 403
        var context = CreateEndpointContext(
            tenant: "tenant-a",
            callerSid: "S-1-5-21-FWD-ADMIN",
            authScheme: ClusterAdminPolicy.ForwardAuthScheme, // "ForwardAuth"
            roles: ["ClusterAdmin"]);

        var lineageService = Substitute.For<ILineageImpactAnalyzerService>();
        var pdfExporter = Substitute.For<IGdprAuditReportExporter>();

        // Act: Invoke GDPR PDF export with a foreign subjectSid
        var result = await GovernanceEndpoints.ExportGdprPdfAsync(
            domain: "finance",
            schema: "dbo",
            table: "salary",
            subjectSid: "S-1-5-21-FOREIGN-USER",
            timeWindowDays: 365,
            lineageService: lineageService,
            pdfExporter: pdfExporter,
            context: context,
            ct: CancellationToken.None);

        var (status, _) = await ExecuteResultAsync(result, context);
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    // =========================================================================
    // 5. GHSA-cj7m-c75j-6p6h
    // =========================================================================

    [Fact]
    public async Task Ghsa_cj7m_6p6h_regression()
    {
        await Case_cj7m_6p6h_1();
        await Case_cj7m_6p6h_2();
    }

    private async Task Case_cj7m_6p6h_1()
    {
        // Scratch quota is summed strictly per tenant. Heavy usage in Tenant B does not affect Tenant A.
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris_jobs_quota_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var auditRepo = Substitute.For<IAuditLogRepository>();
            var jobOptions = Options.Create(new AsyncJobOptions
            {
                MaxScratchBytes = 1000,
                ScratchDirectory = tempDir
            });

            var manager = new AsyncQueryJobManager(auditRepo, jobOptions: jobOptions);

            // Add an existing completed job for Tenant B consuming 1500 bytes (over the 1000 limit)
            manager.UpdateJob(new AsyncJobDescriptor(
                JobId: "job-b-1",
                TenantId: TenantB.Value,
                SubmittedByUserId: "user-b",
                Query: "SELECT 1",
                Format: "json",
                State: AsyncJobState.Completed,
                CreatedAt: DateTimeOffset.UtcNow,
                BytesProduced: 1500));

            var userPrincipalA = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "user-a"),
                new Claim("tenant_id", TenantA.Value)
            ], "Bearer"));

            // Act & Assert 1: Submitting a job for Tenant A must SUCCEED because Tenant A has 0 bytes used
            var descriptorA = await manager.SubmitJobAsync(
                new AsyncQueryJobRequest("SELECT secret FROM table_a"),
                userPrincipalA,
                TenantA);
            descriptorA.ShouldNotBeNull();
            descriptorA.JobId.ShouldNotBeNull();

            // Act & Assert 2: Add an existing completed job for Tenant A consuming 1500 bytes
            manager.UpdateJob(new AsyncJobDescriptor(
                JobId: "job-a-heavy",
                TenantId: TenantA.Value,
                SubmittedByUserId: "user-a",
                Query: "SELECT 2",
                Format: "json",
                State: AsyncJobState.Completed,
                CreatedAt: DateTimeOffset.UtcNow,
                BytesProduced: 1500));

            // Now submitting for Tenant A should fail with AsyncJobQueueLimitExceededException
            var ex = await Should.ThrowAsync<AsyncJobQueueLimitExceededException>(() =>
                manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 3"), userPrincipalA, TenantA));
            ex.Message.ShouldContain("scratch disk quota");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private async Task Case_cj7m_6p6h_2()
    {
        // Cancelled jobs clear BytesProduced and do not consume scratch quota.
        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris_jobs_cancel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var auditRepo = Substitute.For<IAuditLogRepository>();
            var jobOptions = Options.Create(new AsyncJobOptions
            {
                MaxScratchBytes = 1000,
                ScratchDirectory = tempDir
            });

            var manager = new AsyncQueryJobManager(auditRepo, jobOptions: jobOptions);

            var jobId = "job-to-cancel";
            var fakeResultPath = Path.Combine(tempDir, $"{jobId}.json");
            File.WriteAllText(fakeResultPath, new string('x', 1500));

            manager.UpdateJob(new AsyncJobDescriptor(
                JobId: jobId,
                TenantId: TenantA.Value,
                SubmittedByUserId: "user-a",
                Query: "SELECT 1",
                Format: "json",
                State: AsyncJobState.Running,
                CreatedAt: DateTimeOffset.UtcNow,
                ResultFilePath: fakeResultPath,
                BytesProduced: 1500));

            var userPrincipalA = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "user-a"),
                new Claim("tenant_id", TenantA.Value)
            ], "Bearer"));

            // While job has 1500 bytes, new submit should fail
            await Should.ThrowAsync<AsyncJobQueueLimitExceededException>(() =>
                manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 2"), userPrincipalA, TenantA));

            // Act: Cancel the job
            var cancelled = await manager.CancelJobAsync(jobId, TenantA, "user-a");
            cancelled.ShouldBeTrue();

            // Assert: BytesProduced is cleared
            var jobAfterCancel = await manager.GetJobAsync(jobId, TenantA, "user-a");
            jobAfterCancel.ShouldNotBeNull();
            jobAfterCancel.BytesProduced.ShouldBeNull();
            jobAfterCancel.State.ShouldBe(AsyncJobState.Cancelled);

            // Submitting for Tenant A now succeeds because scratch bytes were released
            var newJob = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 3"), userPrincipalA, TenantA);
            newJob.ShouldNotBeNull();
            newJob.JobId.ShouldNotBeNull();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // 6. GHSA-78j7-3gh6-7cqx
    // =========================================================================

    private sealed class EndpointInvocationContext(HttpContext httpContext) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;
        public override IList<object?> Arguments { get; } = new List<object?>();
        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    [Fact]
    public async Task Ghsa_78j7_7cqx_regression()
    {
        // Verify RebacTableGate.ObjectId("table:domain.schema.table") is evaluated by RebacEndpointFilter and ArrowExportEndpoints
        var canonicalTableId = new TableIdentifier("sales", "public", "orders");
        var expectedCanonicalObjectId = RebacTableGate.ObjectId(canonicalTableId);
        expectedCanonicalObjectId.ShouldBe("table:sales.public.orders");

        // Part 1: RebacEndpointFilter evaluates canonical object ID and stores it in context.Items
        var loader = Substitute.For<IRebacBatchDataLoader>();
        loader.CheckAsync("tenant-a", "user:alice", "viewer", expectedCanonicalObjectId, Arg.Any<CancellationToken>())
            .Returns(true);

        var services = new ServiceCollection();
        services.AddSingleton(loader);
        services.AddSingleton(Options.Create(new GatewayOptions
        {
            Rebac = new RebacOptions { Enabled = true }
        }));
        var sp = services.BuildServiceProvider();

        var filterContext = new DefaultHttpContext { RequestServices = sp };
        filterContext.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "user:alice"),
            new Claim("tenant_id", "tenant-a")
        ], "Bearer"));
        filterContext.Request.QueryString = new QueryString("?table=sales.public.orders");

        var filter = new RebacEndpointFilter("viewer", "table", "table", RebacParameterSource.QueryOrJsonBody);
        var invocationContext = new EndpointInvocationContext(filterContext);

        bool nextCalled = false;
        await filter.InvokeAsync(invocationContext, _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        });

        nextCalled.ShouldBeTrue();
        await loader.Received(1).CheckAsync("tenant-a", "user:alice", "viewer", expectedCanonicalObjectId, Arg.Any<CancellationToken>());
        filterContext.Items["RebacValidated:table"].ShouldBe(expectedCanonicalObjectId);

        // Part 2: ArrowExportEndpoints validates target table matches RebacValidated:table
        var arrowService = Substitute.For<IArrowExportService>();
        var planner = new Autheris.Application.DataSources.Services.GovernedTableReadPlanner();

        // 2a. Matching table query succeeds past the ReBAC gate
        var validContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "user:alice"),
                new Claim("tenant_id", "tenant-a")
            ], "Bearer")),
            RequestServices = sp
        };
        validContext.Request.QueryString = new QueryString("?table=sales.public.orders");
        validContext.Items["RebacValidated:table"] = expectedCanonicalObjectId;

        // When ReBAC matches, HandleArrowExportAsync proceeds past the ReBAC check (fails later on planner/meta if not mocked, but NOT 403 ACCESS_DENIED from ReBAC)
        var validResult = await ArrowExportEndpoints.HandleArrowExportAsync(validContext, arrowService, null, planner, CancellationToken.None);
        if (validResult is ProblemHttpResult validProblem)
        {
            validProblem.StatusCode.ShouldNotBe(StatusCodes.Status403Forbidden);
        }

        // 2b. Mismatched table query is rejected with 403 ACCESS_DENIED
        var mismatchedContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "user:alice"),
                new Claim("tenant_id", "tenant-a")
            ], "Bearer")),
            RequestServices = sp
        };
        mismatchedContext.Request.QueryString = new QueryString("?table=sales.public.mismatched_table");
        mismatchedContext.Items["RebacValidated:table"] = expectedCanonicalObjectId;

        var mismatchedResult = await ArrowExportEndpoints.HandleArrowExportAsync(mismatchedContext, arrowService, null, planner, CancellationToken.None);
        var mismatchedProblem = mismatchedResult.ShouldBeOfType<ProblemHttpResult>();
        mismatchedProblem.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        mismatchedProblem.ProblemDetails.Extensions["code"]?.ToString().ShouldBe(ApiErrorCodes.AccessDenied);
    }

    // =========================================================================
    // 7. GHSA-mgv5-4r69-mwww
    // =========================================================================

    [Fact]
    public async Task Ghsa_mgv5_mwww_regression()
    {
        await Case_mgv5_mwww_1();
        await Case_mgv5_mwww_2();
    }

    private async Task Case_mgv5_mwww_1()
    {
        // Verify tenant-scoped GovernanceAdmin cannot perform global actions on CDC stream admin
        var claims = new List<Claim>
        {
            new("sub", "S-1-5-21-TENANTADMIN"),
            new("tenant_id", "tenant-a"),
            new(ClaimTypes.Role, "GovernanceAdmin")
        };
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-TENANTADMIN"),
            TenantId = TenantA,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(["GovernanceAdmin"]),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };

        // Tenant-scoped GovernanceAdmin is NOT a global GovernanceAdmin
        EndpointSecurity.IsGlobalGovernanceAdmin(context.User).ShouldBeFalse();
        EndpointSecurity.IsCanonicalClusterAdmin(context.User).ShouldBeFalse();

        // Act & Assert: All stream admin actions return 403 Forbidden
        var result = await CdcStreamAdminEndpoints.ListAsync(context, null!, default);
        var (status, _) = await ExecuteResultAsync(result, context);
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    private async Task Case_mgv5_mwww_2()
    {
        // GHSA-mgv5-4r69-mwww
        var relationId = Guid.NewGuid();
        var relationRepo = Substitute.For<ITableRelationRepository>();

        // Scenario 1: Tenant-scoped GovernanceAdmin (has tenant_id claim)
        var tenantScopedClaims = new List<Claim>
        {
            new("sub", "S-1-5-21-TENANTADMIN"),
            new("tenant_id", "tenant-a"),
            new(ClaimTypes.Role, "GovernanceAdmin")
        };
        var tenantScopedContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(tenantScopedClaims, "Bearer"))
        };

        var tenantResult = await CatalogApiEndpoints.SetRelationCrossDataSourceAsync(
            relationId,
            new SetRelationCrossDataSourceDto(true),
            tenantScopedContext,
            relationRepo,
            default);

        var (tenantStatus, _) = await ExecuteResultAsync(tenantResult, tenantScopedContext);
        tenantStatus.ShouldBe(StatusCodes.Status403Forbidden);
        await relationRepo.DidNotReceiveWithAnyArgs().SetRelationCrossDataSourceOptInAsync(default, default, default);

        // Scenario 2: canonical ClusterAdmin is PERMITTED (OD-9: GovernanceAdmin is never global)
        var globalClaims = new List<Claim>
        {
            new("sub", "S-1-5-21-GLOBALADMIN"),
            new(ClaimTypes.Role, "ClusterAdmin")
        };
        var globalContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(globalClaims, "Bearer"))
        };

        relationRepo.SetRelationCrossDataSourceOptInAsync(relationId, true, Arg.Any<CancellationToken>())
            .Returns(true);

        var globalResult = await CatalogApiEndpoints.SetRelationCrossDataSourceAsync(
            relationId,
            new SetRelationCrossDataSourceDto(true),
            globalContext,
            relationRepo,
            default);

        var (globalStatus, globalBody) = await ExecuteResultAsync(globalResult, globalContext);
        globalStatus.ShouldBe(StatusCodes.Status200OK);
        globalBody.ShouldContain("allowCrossDataSource");
        await relationRepo.Received(1).SetRelationCrossDataSourceOptInAsync(relationId, true, Arg.Any<CancellationToken>());
    }
}
