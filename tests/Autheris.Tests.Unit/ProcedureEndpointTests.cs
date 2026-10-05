using System.Security;
using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>F-SQL-02: declaration parsing, registration policy and governed execution of stored procedure endpoints.</summary>
public class ProcedureEndpointTests
{
    private const string ValidHeader = """
        -- @name get_orders
        -- @procedure api.usp_GetOrders
        -- @mode read                       -- read | write
        -- @summary Lists orders
        -- @param customer_id int required Customer number
        -- @param note nvarchar(40) optional Free text
        -- @context tenant_id -> @TenantId
        -- @context user_sid  -> @ActorSid
        -- @result-table sales.orders
        -- @result-column order_id clear
        -- @timeout 120
        """;

    // ---------- parser ----------

    [Fact]
    public void Parse_ValidHeader_MapsAllFields()
    {
        var def = ProcedureDefinitionParser.Parse(ValidHeader, "fallback", allowRlsNone: false, maxTimeoutSeconds: 60);

        def.Name.ShouldBe("get_orders");
        def.ProcedureName.ShouldBe("api.usp_GetOrders");
        def.Mode.ShouldBe(ProcedureMode.Read);
        def.RlsMode.ShouldBe(ProcedureRlsMode.SessionContext);
        def.ResultTable.ShouldBe("sales.orders");
        def.ClearedResultColumns.ShouldContain("order_id");
        def.TimeoutSeconds.ShouldBe(60); // clamped to the configured maximum
        def.Parameters.Count.ShouldBe(2);
        def.Parameters[0].ClrType.ShouldBe(typeof(int));
        def.Parameters[0].IsRequired.ShouldBeTrue();
        def.Parameters[1].MaxLength.ShouldBe(40);
        def.Parameters[1].IsRequired.ShouldBeFalse();
        def.ContextBindings.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("-- @procedure db.api.usp_X")]          // three-part name (other database)
    [InlineData("-- @procedure [api].[usp_X]")]         // quoting
    [InlineData("-- @procedure api.usp_X; DROP TABLE x")]
    [InlineData("-- @procedure usp_X")]                  // schema missing
    public void Parse_InvalidProcedureName_Throws(string header)
    {
        Should.Throw<FormatException>(() => ProcedureDefinitionParser.Parse(header, "x", false, 30));
    }

    [Fact]
    public void Parse_RlsNone_IsRejectedOutsideDevelopment()
    {
        const string text = "-- @procedure api.usp_X\n-- @rls none";
        Should.Throw<FormatException>(() => ProcedureDefinitionParser.Parse(text, "x", allowRlsNone: false, 30));
        ProcedureDefinitionParser.Parse(text, "x", allowRlsNone: true, 30).RlsMode.ShouldBe(ProcedureRlsMode.None);
    }

    [Theory]
    [InlineData("-- @procedure api.usp_X\n-- @bogus 1")]                                        // unknown header
    [InlineData("-- @procedure api.usp_X\n-- @param a geography required")]                     // unsupported type
    [InlineData("-- @procedure api.usp_X\n-- @param a int required\n-- @param a int required")]  // duplicate
    [InlineData("-- @procedure api.usp_X\n-- @param TenantId int required\n-- @context tenant_id -> @TenantId")] // client may not set a context param
    [InlineData("-- @procedure api.usp_X\n-- @approval four-eyes")]                              // phase 2
    [InlineData("-- @procedure api.usp_X\n-- @mode destroy")]
    public void Parse_FailClosedOnUnsupportedInput(string header)
    {
        Should.Throw<FormatException>(() => ProcedureDefinitionParser.Parse(header, "x", false, 30));
    }

    // ---------- loader / registration policy ----------

    private static (ProcedureDefinitionLoader Loader, InMemoryProcedureRegistry Registry, string Dir) CreateLoader(params string[] allowedSchemas)
    {
        string dir = Path.Combine(Path.GetTempPath(), "autheris-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var options = Options.Create(new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions { Enabled = true, Directory = dir, AllowedSchemas = [.. allowedSchemas], EnableHotReload = false }
            }
        });
        var registry = new InMemoryProcedureRegistry();
        return (new ProcedureDefinitionLoader(registry, options), registry, dir);
    }

    [Fact]
    public void Loader_RegistersAllowedSchemaAsPending()
    {
        var (loader, registry, dir) = CreateLoader("api");
        try
        {
            File.WriteAllText(Path.Combine(dir, "get_orders.proc.sql"), ValidHeader);
            loader.LoadFromDirectory().ShouldBe(1);

            registry.TryGet("get_orders", out var reg).ShouldBeTrue();
            reg!.State.ShouldBe(ProcedureState.Pending); // never active before the catalog validation
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("-- @procedure hr.usp_Salaries")]                        // schema not allowed
    [InlineData("-- @procedure sys.sp_who")]                              // system schema
    [InlineData("-- @procedure api.sp_helptext")]                         // system procedure prefix
    [InlineData("-- @procedure api.xp_cmdshell")]                         // extended procedure
    [InlineData("-- @procedure api.usp_Write\n-- @mode write")]           // phase 2
    public void Loader_RejectsPolicyViolations(string content)
    {
        var (loader, registry, dir) = CreateLoader("api", "sys");
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.proc.sql"), content);
            loader.LoadFromDirectory().ShouldBe(0);
            registry.GetAll().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Loader_EmptyAllowedSchemas_ExposesNothing()
    {
        var (loader, registry, dir) = CreateLoader();
        try
        {
            File.WriteAllText(Path.Combine(dir, "get_orders.proc.sql"), ValidHeader);
            loader.LoadFromDirectory().ShouldBe(0);
            registry.GetAll().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Loader_InvalidChangeRemovesPreviouslyRegisteredEndpoint()
    {
        var (loader, registry, dir) = CreateLoader("api");
        try
        {
            string file = Path.Combine(dir, "get_orders.proc.sql");
            File.WriteAllText(file, ValidHeader);
            loader.TryLoadFile(file, dir).ShouldBeTrue();
            registry.TryGet("get_orders", out _).ShouldBeTrue();

            File.WriteAllText(file, "-- @procedure api.usp_GetOrders\n-- @bogus 1");
            loader.TryLoadFile(file, dir).ShouldBeFalse();
            registry.TryGet("get_orders", out _).ShouldBeFalse(); // fail-closed
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------- governed execution ----------

    private sealed class Fixture
    {
        public InMemoryProcedureRegistry Registry { get; } = new();
        public IProcedureInvoker Invoker { get; } = Substitute.For<IProcedureInvoker>();
        public ITableMetadataRepository Tables { get; } = Substitute.For<ITableMetadataRepository>();
        public IConsentRepository Consents { get; } = Substitute.For<IConsentRepository>();
        public IConsentResolutionService Resolution { get; } = Substitute.For<IConsentResolutionService>();
        public IColumnMaskingProvider Masking { get; } = Substitute.For<IColumnMaskingProvider>();
        public IAuditLogRepository Audit { get; } = Substitute.For<IAuditLogRepository>();
        public List<AuditLogEntry> AuditEntries { get; } = [];

        public GovernedProcedureExecutionService Create()
        {
            Audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(AuditEntries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            return new GovernedProcedureExecutionService(
                Registry, Invoker, Options.Create(new GatewayOptions()), Tables, Consents, Resolution, Masking,
                policyEnforcement: null, audit: Audit);
        }
    }

    private static ClaimsPrincipal User(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-1001") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static TableMetadata OrdersTable() => new()
    {
        Identifier = new TableIdentifier("default", "sales", "orders"),
        Columns =
        [
            new TableColumn { ColumnName = "order_id" },
            new TableColumn { ColumnName = "secret" },
            new TableColumn { ColumnName = "salary" }
        ]
    };

    private static Fixture Active(Func<ProcedureDefinition, ProcedureDefinition>? tweak = null, string? rowFilter = null)
    {
        var f = new Fixture();
        var def = ProcedureDefinitionParser.Parse(ValidHeader, "x", false, 60);
        if (tweak != null)
        {
            def = tweak(def);
        }

        f.Registry.Register(def);
        f.Registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], ["order_id", "secret", "salary", "extra"], ["sales.orders"], new Dictionary<string, string>()));

        f.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(OrdersTable());
        f.Consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Consent>());

        var columns = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["order_id"] = ColumnAccessLevel.Clear,
            ["secret"] = ColumnAccessLevel.Deny,
            ["salary"] = ColumnAccessLevel.Mask
        };
        f.Resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), columns, rowFilter));

        f.Masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("***");
        f.Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(
                ["order_id", "secret", "salary", "extra"],
                [new object?[] { 1, "s", 1000m, "x" }, new object?[] { 2, "t", 2000m, "y" }],
                false));
        return f;
    }

    private static readonly TenantId Tenant = new("tenant-a");

    [Fact]
    public async Task Execute_UnknownEndpoint_Throws()
    {
        var svc = new Fixture().Create();
        await Should.ThrowAsync<KeyNotFoundException>(() => svc.ExecuteAsync("nope", null, User(), Tenant));
    }

    [Fact]
    public async Task Execute_PendingEndpoint_IsUnavailable()
    {
        var f = new Fixture();
        f.Registry.Register(ProcedureDefinitionParser.Parse(ValidHeader, "x", false, 60));
        await Should.ThrowAsync<ProcedureUnavailableException>(() => f.Create().ExecuteAsync("get_orders", null, User(), Tenant));
    }

    [Fact]
    public async Task Execute_AppliesColumnDenyMaskAndDropsUnknownColumns()
    {
        var f = Active();
        var result = await f.Create().ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = "7" }, User(), Tenant);

        result.Columns.ShouldBe(["order_id", "salary"]); // 'secret' denied, 'extra' unknown -> removed
        result.RowCount.ShouldBe(2);
        result.Rows[0]["order_id"].ShouldBe(1);
        result.Rows[0]["salary"].ShouldBe("***");
        result.Rows[0].ContainsKey("secret").ShouldBeFalse();
        result.Rows[0].ContainsKey("extra").ShouldBeFalse();
    }

    [Fact]
    public async Task Execute_PassesTenantAndUserOnlyThroughSecurityContext()
    {
        var f = Active();
        await f.Create().ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = 7 }, User(), Tenant);

        await f.Invoker.Received(1).ExecuteReadAsync(
            Arg.Any<ProcedureDefinition>(),
            Arg.Is<IReadOnlyDictionary<string, object?>>(v => (int)v["customer_id"]! == 7 && v.Count == 1),
            Arg.Is<ProcedureSecurityContext>(c => c.TenantId == "tenant-a" && c.UserSid == "S-1-5-21-1001"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("TenantId")]    // context-bound parameter must not be client controlled
    [InlineData("tenant_id")]
    [InlineData("whatever")]
    public async Task Execute_UnknownOrContextParameterFromClient_IsRejected(string name)
    {
        var f = Active();
        var inputs = new Dictionary<string, object?> { ["customer_id"] = 7, [name] = "other-tenant" };
        await Should.ThrowAsync<ArgumentException>(() => f.Create().ExecuteAsync("get_orders", inputs, User(), Tenant));
        await f.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Execute_MissingRequiredParameterAndOverlongText_AreRejected()
    {
        var f = Active();
        var svc = f.Create();
        await Should.ThrowAsync<ArgumentException>(() => svc.ExecuteAsync("get_orders", null, User(), Tenant));
        await Should.ThrowAsync<ArgumentException>(() => svc.ExecuteAsync(
            "get_orders", new Dictionary<string, object?> { ["customer_id"] = 7, ["note"] = new string('x', 41) }, User(), Tenant));
        await Should.ThrowAsync<ArgumentException>(() => svc.ExecuteAsync(
            "get_orders", new Dictionary<string, object?> { ["customer_id"] = "abc" }, User(), Tenant));
    }

    [Fact]
    public async Task Execute_ConsentWithRowFilter_DeniesBecauseItCannotBeEnforced()
    {
        var f = Active(rowFilter: "region = 'EMEA'");
        await Should.ThrowAsync<SecurityException>(() =>
            f.Create().ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = 7 }, User(), Tenant));
        await f.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Execute_MissingTableMetadata_Denies()
    {
        var f = Active();
        f.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns((TableMetadata?)null);
        await Should.ThrowAsync<SecurityException>(() =>
            f.Create().ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = 7 }, User(), Tenant));
    }

    [Fact]
    public async Task Execute_RequiredRoleMissing_DeniesAndAudits()
    {
        var f = Active(d => d with { RequiredRoles = ["order-reader"] });
        var svc = f.Create();

        await Should.ThrowAsync<SecurityException>(() =>
            svc.ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = 7 }, User("other"), Tenant));
        f.AuditEntries.ShouldContain(e => e.EventType == "PROCEDURE_DENIED" && e.Decision == "DENY");

        var ok = await svc.ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = 7 }, User("order-reader"), Tenant);
        ok.RowCount.ShouldBe(2);
    }

    [Fact]
    public async Task Execute_AuditsWithoutRawParameterValues()
    {
        var f = Active();
        await f.Create().ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = 424242, ["note"] = "top-secret-note" }, User(), Tenant);

        var entry = f.AuditEntries.Single(e => e.EventType == "PROCEDURE_EXECUTE");
        entry.TargetTable.ShouldBe("api.usp_GetOrders");
        entry.DetailsJson.ShouldNotContain("424242");
        entry.DetailsJson.ShouldNotContain("top-secret-note");
    }

    [Fact]
    public async Task Execute_AuditFailure_AbortsTheResponse()
    {
        var f = Active();
        f.Audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("audit down")));
        var svc = new GovernedProcedureExecutionService(
            f.Registry, f.Invoker, Options.Create(new GatewayOptions()), f.Tables, f.Consents, f.Resolution, f.Masking, audit: f.Audit);

        await Should.ThrowAsync<IOException>(() =>
            svc.ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = 7 }, User(), Tenant));
    }

    [Fact]
    public async Task Execute_BusinessError_IsRethrownAndAudited()
    {
        var f = Active();
        f.Invoker.ExecuteReadAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(new ProcedureBusinessException("Order locked"));
        var svc = f.Create();

        var ex = await Should.ThrowAsync<ProcedureBusinessException>(() =>
            svc.ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = 7 }, User(), Tenant));
        ex.Message.ShouldBe("Order locked");
        f.AuditEntries.ShouldContain(e => e.EventType == "PROCEDURE_REJECTED");
    }

    [Fact]
    public void Registry_ReRegistrationResetsToPending()
    {
        var f = Active();
        f.Registry.Register(ProcedureDefinitionParser.Parse(ValidHeader, "x", false, 60));
        f.Registry.TryGet("get_orders", out var reg).ShouldBeTrue();
        reg!.State.ShouldBe(ProcedureState.Pending);
    }

    [Fact]
    public async Task RegistrationService_AlreadyDisabled_DoesNotLogWarningOnRevalidation()
    {
        var options = Options.Create(new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions
                {
                    Enabled = true,
                    AllowedSchemas = ["api"]
                }
            }
        });

        var registry = new InMemoryProcedureRegistry();
        var def = ProcedureDefinitionParser.Parse(ValidHeader, "x", false, 60);
        registry.Register(def);

        var connFactory = Substitute.For<ISqlConnectionFactory>();
        var connProvider = new ProcedureConnectionProvider(connFactory, options);
        var validator = new StoredProcedureCatalogValidator(connProvider, options);

        var scopeFactory = Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var scope = Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(StoredProcedureCatalogValidator)).Returns(validator);

        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ProcedureRegistrationService>>();
        logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);

        var loader = new ProcedureDefinitionLoader(registry, options);
        var svc = new ProcedureRegistrationService(loader, registry, scopeFactory, options, logger);

        // Round 1: Pending -> Disabled (should log Warning once on transition)
        await svc.ValidateDueAsync(CancellationToken.None);

        registry.TryGet("get_orders", out var reg1).ShouldBeTrue();
        reg1!.State.ShouldBe(ProcedureState.Disabled);

        logger.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "Log" && (Microsoft.Extensions.Logging.LogLevel)c.GetArguments()[0]! == Microsoft.Extensions.Logging.LogLevel.Warning)
            .ShouldBe(1);

        // Round 2: Still Disabled (simulate retry interval passed)
        var internalItems = typeof(InMemoryProcedureRegistry)
            .GetField("_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(registry) as System.Collections.Concurrent.ConcurrentDictionary<string, RegisteredProcedure>;
        internalItems!["get_orders"] = reg1 with { ValidatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };

        await svc.ValidateDueAsync(CancellationToken.None);

        // Warning count must NOT increase on re-validation of already disabled procedure
        logger.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "Log" && (Microsoft.Extensions.Logging.LogLevel)c.GetArguments()[0]! == Microsoft.Extensions.Logging.LogLevel.Warning)
            .ShouldBe(1);
    }

    // ---------- YAML parsing & declared mode tests ----------

    private const string ValidYamlDefinition = """
        name: get_telemetry
        procedure: api.usp_GetTelemetry
        data_source: telemetry_db
        mode: read
        validation: declared
        timeout: 45
        summary: Retrieves device telemetry
        required_roles:
          - telemetry-reader
        parameters:
          - name: device_id
            type: int
            required: true
            description: Unique device identifier
          - name: filter_type
            type: nvarchar(50)
            required: false
            description: Optional filter
        context:
          tenant_id: tenant_id
          user_sid: actor_sid
        outputs:
          - timestamp
          - device_id
          - temperature
          - battery_level
        """;

    [Fact]
    public void ParseYaml_ValidYaml_MapsAllFields()
    {
        var def = ProcedureDefinitionParser.ParseYaml(ValidYamlDefinition, "fallback", allowRlsNone: false, maxTimeoutSeconds: 60);

        def.Name.ShouldBe("get_telemetry");
        def.ProcedureName.ShouldBe("api.usp_GetTelemetry");
        def.DataSource.ShouldBe("telemetry_db");
        def.Mode.ShouldBe(ProcedureMode.Read);
        def.ValidationMode.ShouldBe(ProcedureValidationMode.Declared);
        def.TimeoutSeconds.ShouldBe(45);
        def.Summary.ShouldBe("Retrieves device telemetry");
        def.RequiredRoles.ShouldContain("telemetry-reader");
        def.Parameters.Count.ShouldBe(2);
        def.Parameters[0].Name.ShouldBe("device_id");
        def.Parameters[0].ClrType.ShouldBe(typeof(int));
        def.Parameters[0].IsRequired.ShouldBeTrue();
        def.Parameters[1].Name.ShouldBe("filter_type");
        def.Parameters[1].ClrType.ShouldBe(typeof(string));
        def.Parameters[1].MaxLength.ShouldBe(50);
        def.Parameters[1].IsRequired.ShouldBeFalse();
        def.ContextBindings.Count.ShouldBe(2);
        def.ContextBindings.ShouldContain(b => b.Key == ProcedureContextKey.TenantId && b.ParameterName == "tenant_id");
        def.ContextBindings.ShouldContain(b => b.Key == ProcedureContextKey.UserSid && b.ParameterName == "actor_sid");
        def.DeclaredOutputs.ShouldBe(["timestamp", "device_id", "temperature", "battery_level"]);
    }

    [Theory]
    [InlineData("procedure: ''")]                                                                        // empty procedure
    [InlineData("procedure: db.api.usp_X")]                                                              // 3-part name
    [InlineData("procedure: api.usp_X\nparameters:\n  - name: p\n    type: unknown_type")]                // unsupported type
    [InlineData("procedure: api.usp_X\nparameters:\n  - name: tenant_id\n    type: int\ncontext:\n  tenant_id: tenant_id")] // collision
    public void ParseYaml_InvalidYaml_Throws(string yaml)
    {
        Should.Throw<FormatException>(() => ProcedureDefinitionParser.ParseYaml(yaml, "x", false, 30));
    }

    [Fact]
    public void Parse_SqlWithDeclaredValidationAndOutputs_MapsCorrectly()
    {
        const string sql = """
            -- @procedure api.usp_Custom
            -- @validation declared
            -- @output col_a, col_b
            """;
        var def = ProcedureDefinitionParser.Parse(sql, "custom", false, 30);
        def.ValidationMode.ShouldBe(ProcedureValidationMode.Declared);
        def.DeclaredOutputs.ShouldBe(["col_a", "col_b"]);
    }

    [Fact]
    public void Loader_LoadsProcYamlFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "autheris-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var options = Options.Create(new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions
                {
                    Enabled = true,
                    Directory = dir,
                    AllowedSchemas = ["api"],
                    AllowedDataSources = ["telemetry_db"],
                    EnableHotReload = false
                }
            }
        });
        var registry = new InMemoryProcedureRegistry();
        var loader = new ProcedureDefinitionLoader(registry, options);
        try
        {
            string file = Path.Combine(dir, "get_telemetry.proc.yaml");
            File.WriteAllText(file, ValidYamlDefinition);
            loader.LoadFromDirectory().ShouldBe(1);

            registry.TryGet("get_telemetry", out var reg).ShouldBeTrue();
            reg!.Definition.ValidationMode.ShouldBe(ProcedureValidationMode.Declared);
            reg.Definition.DeclaredOutputs.ShouldContain("temperature");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RegistrationService_DeclaredProcedure_ActivatesDirectlyWithoutCatalogValidation()
    {
        var options = Options.Create(new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions
                {
                    Enabled = true,
                    AllowedSchemas = ["api"]
                }
            }
        });

        var registry = new InMemoryProcedureRegistry();
        var def = ProcedureDefinitionParser.ParseYaml(ValidYamlDefinition, "telemetry", false, 60);
        registry.Register(def);

        var connFactory = Substitute.For<ISqlConnectionFactory>();
        var scopeFactory = Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ProcedureRegistrationService>>();

        var loader = new ProcedureDefinitionLoader(registry, options);
        var svc = new ProcedureRegistrationService(loader, registry, scopeFactory, options, logger);

        await svc.ValidateDueAsync(CancellationToken.None);

        registry.TryGet("get_telemetry", out var reg).ShouldBeTrue();
        reg!.State.ShouldBe(ProcedureState.Active);
        reg.Validation!.IsValid.ShouldBeTrue();
        reg.Validation.ResultColumns.ShouldBe(["timestamp", "device_id", "temperature", "battery_level"]);
        await connFactory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    public async Task Execute_DeclaredProcedure_ProjectsOnlyDeclaredOutputsAndStripsUndeclaredColumns()
    {
        var f = new Fixture();
        var def = ProcedureDefinitionParser.ParseYaml(ValidYamlDefinition, "telemetry", false, 60);
        f.Registry.Register(def);
        f.Registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], def.DeclaredOutputs, [], new Dictionary<string, string>()));

        f.Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(
                ["timestamp", "device_id", "temperature", "battery_level", "secret_token", "internal_status"],
                [new object?[] { "2026-10-05T12:00:00Z", 42, 23.5, 95, "shh-secret", "leak" }],
                false));

        var svc = f.Create();
        var result = await svc.ExecuteAsync(
            "get_telemetry",
            new Dictionary<string, object?> { ["device_id"] = 42 },
            User("telemetry-reader"),
            Tenant);

        result.Columns.ShouldBe(["timestamp", "device_id", "temperature", "battery_level"]);
        result.RowCount.ShouldBe(1);
        result.Rows[0]["device_id"].ShouldBe(42);
        result.Rows[0]["temperature"].ShouldBe(23.5);
        result.Rows[0].ContainsKey("secret_token").ShouldBeFalse();
        result.Rows[0].ContainsKey("internal_status").ShouldBeFalse();
    }
}
