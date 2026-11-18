using System.Text;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Products;
using Autheris.Application.Products.Sync;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ProductSyncWebhookTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly byte[] SecretA = Encoding.UTF8.GetBytes("secret-of-tenant-a");
    private static readonly byte[] SecretB = Encoding.UTF8.GetBytes("secret-of-tenant-b");

    private readonly ProductHarness _h = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingAuditWriter _audit = new();
    private readonly ProductSyncWebhookService _webhook;
    private readonly ProductSyncService _sync;

    public ProductSyncWebhookTests()
    {
        var secrets = new FakeSecrets(new Dictionary<string, byte[]>
        {
            [ProductSyncWebhookService.SecretRef(TenantA)] = SecretA,
            [ProductSyncWebhookService.SecretRef(TenantB)] = SecretB
        });
        _webhook = new ProductSyncWebhookService(secrets, new InMemoryClusterStateProvider(_time), _audit, _time);
        _sync = new ProductSyncService(_h.Service, _h.Store, _h.Audit);
    }

    private string Now() => _time.GetUtcNow().ToUnixTimeSeconds().ToString();

    private static ProductSyncWebhookDelivery Signed(byte[] secret, string timestamp, byte[] body, string delivery = "d-1") =>
        new(timestamp, delivery, ProductSyncWebhookService.Sign(secret, timestamp, body));

    private static byte[] Body(string json = """{"after":"0123456789abcdef"}""") => Encoding.UTF8.GetBytes(json);

    private const string ViewFile =
        "schema: sales\nname: orders_view\nkind: view\nproduct_owner: S-1-5-21-1001\ndefinition: |\n  SELECT o.id, o.total, c.segment FROM crm.public.orders o JOIN crm.public.customers c ON o.customer_id = c.id\n";

    private ProductSyncRequest Request(params ProductSyncFile[] files) =>
        new(_h.Caller(ProductHarness.SyncIdentity, viaSync: true), "0123456789abcdef", files);

    private static ProductSyncFile File(string path, string content, bool symlink = false) => new(path, content, symlink);

    [Fact]
    public async Task Bad_signature_rejected_before_parse()
    {
        var invalidJson = Body("{ this is not json");
        var wrong = new ProductSyncWebhookDelivery(Now(), "d-1", "sha256=" + new string('0', 64));

        (await _webhook.HandleAsync(TenantA, invalidJson, wrong)).ShouldBe(ProductSyncWebhookStatus.Unauthorized, "an unsigned body is never parsed (a parse error would answer 400)");
        (await _webhook.HandleAsync(TenantA, invalidJson, new ProductSyncWebhookDelivery(Now(), "d-1", null))).ShouldBe(ProductSyncWebhookStatus.Unauthorized);
        (await _webhook.HandleAsync(TenantA, invalidJson, new ProductSyncWebhookDelivery(Now(), "d-1", "sha256=zz"))).ShouldBe(ProductSyncWebhookStatus.Unauthorized);
        (await _webhook.HandleAsync(TenantA, invalidJson, Signed(SecretA, Now(), invalidJson))).ShouldBe(ProductSyncWebhookStatus.BadRequest, "only a verified body is parsed");
        _audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductSyncWebhookRejected);
        _audit.Entries.ShouldNotContain(e => e.EventType == AuditEventTypes.ProductSyncWebhookReceived);
    }

    [Fact]
    public async Task Unknown_tenant_secret_is_unauthorized()
    {
        var body = Body();

        (await _webhook.HandleAsync(new TenantId("tenant-z"), body, Signed(SecretA, Now(), body))).ShouldBe(ProductSyncWebhookStatus.Unauthorized);
    }

    [Fact]
    public async Task Stale_timestamp_rejected()
    {
        var body = Body();
        var old = _time.GetUtcNow().AddMinutes(-6).ToUnixTimeSeconds().ToString();
        var future = _time.GetUtcNow().AddMinutes(6).ToUnixTimeSeconds().ToString();

        (await _webhook.HandleAsync(TenantA, body, Signed(SecretA, old, body))).ShouldBe(ProductSyncWebhookStatus.Unauthorized);
        (await _webhook.HandleAsync(TenantA, body, Signed(SecretA, future, body))).ShouldBe(ProductSyncWebhookStatus.Unauthorized);
        (await _webhook.HandleAsync(TenantA, body, Signed(SecretA, _time.GetUtcNow().AddMinutes(-4).ToUnixTimeSeconds().ToString(), body))).ShouldBe(ProductSyncWebhookStatus.Accepted);
    }

    [Fact]
    public async Task Replayed_delivery_rejected()
    {
        var body = Body();
        var delivery = Signed(SecretA, Now(), body, "delivery-77");

        (await _webhook.HandleAsync(TenantA, body, delivery)).ShouldBe(ProductSyncWebhookStatus.Accepted);
        (await _webhook.HandleAsync(TenantA, body, delivery)).ShouldBe(ProductSyncWebhookStatus.Duplicate);
        (await _webhook.HandleAsync(TenantA, body, Signed(SecretA, Now(), body, "delivery-78"))).ShouldBe(ProductSyncWebhookStatus.Accepted);
    }

    [Fact]
    public async Task Tenant_from_binding_not_payload()
    {
        var body = Body("""{"tenant":"tenant-b","after":"0123456789abcdef"}""");

        (await _webhook.HandleAsync(TenantA, body, Signed(SecretA, Now(), body))).ShouldBe(ProductSyncWebhookStatus.Accepted);
        _audit.Entries.Single(e => e.EventType == AuditEventTypes.ProductSyncWebhookReceived).TenantId.ShouldBe(TenantA);
        (await _webhook.HandleAsync(TenantB, body, Signed(SecretA, Now(), body, "d-2"))).ShouldBe(ProductSyncWebhookStatus.Unauthorized, "the payload cannot move a delivery signed for tenant a to tenant b");
    }

    [Fact]
    public async Task Oversize_body_rejected()
    {
        var body = new byte[ProductSyncWebhookService.MaxBodyBytes + 1];

        (await _webhook.HandleAsync(TenantA, body, Signed(SecretA, Now(), body))).ShouldBe(ProductSyncWebhookStatus.TooLarge);
    }

    [Fact]
    public async Task Sync_file_cannot_name_sync_identity_as_owner()
    {
        var file = File("sales/orders.product.yaml", ViewFile.Replace("S-1-5-21-1001", ProductHarness.SyncIdentity.Value));

        var ex = await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.ApplyAsync(Request(file)));

        ex.ProductCode.ShouldBe(ProductErrorCodes.OwnerMustDiffer);
        (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view")).ShouldBeNull();
    }

    [Fact]
    public async Task Sync_file_runs_authority_checks()
    {
        var unknownObject = File("a.product.yaml", ViewFile.Replace("crm.public.orders o JOIN crm.public.customers c ON o.customer_id = c.id", "crm.public.no_such_table o").Replace("o.id, o.total, c.segment", "o.id"));
        var noOwner = File("b.product.yaml", ViewFile.Replace("product_owner: S-1-5-21-1001\n", string.Empty));
        var notSql = File("c.product.yaml", ViewFile.Replace("SELECT", "DROP TABLE x; SELECT"));

        foreach (var bad in new[] { unknownObject, noOwner, notSql })
        {
            await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.ApplyAsync(Request(bad)));
        }

        (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view")).ShouldBeNull();

        var result = await _sync.ApplyAsync(Request(File("good.product.yaml", ViewFile)));

        result.Count(ProductSyncAction.Create).ShouldBe(1);
        var product = (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!;
        product.Authority.ShouldBe(ProductCreatorAuthority.GitSync);
        product.ManagedBy.ShouldBe("Git");
        product.ProductOwner.ShouldBe(ProductHarness.Alice);
        product.BaseObjects.ShouldBe([ProductHarness.Orders, ProductHarness.Customers], ignoreOrder: true);
    }

    [Theory]
    [InlineData("schema: x\nname: y\nproduct_owner: &o S-1-5-21-1001\ndefinition: SELECT 1\n")]
    [InlineData("schema: x\nname: y\nproduct_owner: !!str S-1-5-21-1001\ndefinition: SELECT 1\n")]
    [InlineData("a: &a [1,2]\nb: *a\n")]
    [InlineData("tenant: tenant-b\nschema: x\nname: y\nproduct_owner: S-1-5-21-1001\ndefinition: SELECT 1\n")]
    [InlineData("grants: [{user: alice}]\nschema: x\nname: y\nproduct_owner: S-1-5-21-1001\ndefinition: SELECT 1\n")]
    [InlineData("schema: x\nname: y\nproduct_owner: S-1-5-21-1001\ndefinition: SELECT 1\ncolumns:\n  - name: segment\n    class: NonPii\n")]
    [InlineData("schema: x\nname: y\n---\nschema: z\n")]
    [InlineData("schema: [[[[[[[[[[1]]]]]]]]]]\n")]
    [InlineData("schema: x\nname: y\nproduct_owner: S-1-5-21-1001\nproduct_owner: S-1-5-21-1002\ndefinition: SELECT 1\n")]
    public async Task Bad_file_rejects_whole_commit(string badContent)
    {
        var request = Request(File("good.product.yaml", ViewFile), File("bad.product.yaml", badContent));

        var ex = await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.ApplyAsync(request));

        ex.FileIndex.ShouldBe(1);
        (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view")).ShouldBeNull("the valid file of a rejected commit is not applied");
        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductSyncRejected && e.Decision == "DENY");
        _h.Audit.Entries.ShouldNotContain(e => e.EventType == AuditEventTypes.ProductSyncApplied);
    }

    [Fact]
    public async Task Declassification_from_a_file_needs_a_reason_and_is_audited_as_sync_originated()
    {
        var withEmail = ViewFile.Replace("c.segment", "c.email");

        await Should.ThrowAsync<ProductSyncRejectedException>(() =>
            _sync.ApplyAsync(Request(File("p.product.yaml", withEmail + "columns:\n  - name: email\n    class: NonPii\n"))));

        await _sync.ApplyAsync(Request(File("p.product.yaml", withEmail + "columns:\n  - name: email\n    class: PiiClearEligible\n    reason: consent of the data subjects documented\n")));

        var column = (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!.Columns.Single(c => c.Name == "email");
        column.Class.ShouldBe(ProductColumnClass.PiiClearEligible);
        column.DeclassifiedBy.ShouldBe(ProductHarness.SyncIdentity.Value);
        column.DeclassificationReason.ShouldNotBeNullOrWhiteSpace();
        _h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.ProductColumnClassificationChanged).DetailsJson.ShouldContain("\"viaSync\":true");
        _h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.ProductSyncApplied).DetailsJson.ShouldContain("\"declassifiedColumns\":1");
    }

    [Theory]
    [InlineData("../x.product.yaml")]
    [InlineData("/etc/x.product.yaml")]
    [InlineData("a/../../x.product.yaml")]
    [InlineData("a/./x.product.yaml")]
    [InlineData("a\\x.product.yaml")]
    [InlineData(".hidden/x.product.yaml")]
    [InlineData("x.txt")]
    [InlineData("")]
    public async Task Path_traversal_and_symlink_rejected(string path)
    {
        await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.ApplyAsync(Request(File(path, ViewFile))));
        var link = await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.ApplyAsync(Request(File("ok.product.yaml", ViewFile, symlink: true))));

        link.Reason.ShouldBe(ProductSyncReasons.Symlink);
        (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view")).ShouldBeNull();
    }

    [Fact]
    public async Task File_count_size_and_duplicates_are_capped()
    {
        var many = Enumerable.Range(0, ProductSyncFileParser.MaxFiles + 1).Select(i => File($"f{i}.product.yaml", ViewFile)).ToArray();
        var huge = File("huge.product.yaml", ViewFile + new string('#', ProductSyncFileParser.MaxFileBytes));

        (await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.PlanAsync(Request(many)))).Reason.ShouldBe(ProductSyncReasons.TooManyFiles);
        (await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.PlanAsync(Request(huge)))).Reason.ShouldBe(ProductSyncReasons.FileTooLarge);
        (await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.PlanAsync(Request(File("a.product.yaml", ViewFile), File("b.product.yaml", ViewFile))))).Reason.ShouldBe(ProductSyncReasons.Duplicate);
    }

    [Fact]
    public async Task Git_deletion_retires_not_deletes()
    {
        await _sync.ApplyAsync(Request(File("p.product.yaml", ViewFile)));
        var created = (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!;

        var plan = await _sync.PlanAsync(Request());
        plan.Applied.ShouldBeFalse();
        plan.Items.ShouldHaveSingleItem().Action.ShouldBe(ProductSyncAction.Retire);
        (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!.Status.ShouldBe(DataProductStatus.Active, "a plan writes nothing");

        var result = await _sync.ApplyAsync(Request());

        result.Count(ProductSyncAction.Retire).ShouldBe(1);
        var retired = (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!;
        retired.Status.ShouldBe(DataProductStatus.Retired);
        retired.Id.ShouldBe(created.Id);
        (await _h.Store.GetProductVersionsAsync(ProductHarness.Tenant, created.Id)).ShouldNotBeEmpty("the history stays");
        _h.Audit.Entries.Last(e => e.EventType == AuditEventTypes.ProductSyncApplied).DetailsJson.ShouldContain("\"retired\":1");
    }

    [Fact]
    public async Task Sync_role_refused_on_rest_routes()
    {
        ProductAuthorityService.ForRestRoute(ProductHarness.SyncIdentity, ProductHarness.Tenant, isDataProductAdmin: true, isDataProductSync: true).ShouldBeNull();
        ProductAuthorityService.ForRestRoute(ProductHarness.SyncIdentity, ProductHarness.Tenant, isDataProductAdmin: false, isDataProductSync: true).ShouldBeNull();

        var rest = new ProductSyncRequest(_h.Caller(ProductHarness.Admin, admin: true), "0123456789abcdef", [File("p.product.yaml", ViewFile)]);
        var ex = await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.ApplyAsync(rest));

        ex.Reason.ShouldBe(ProductSyncReasons.NotAllowed, "the sync service only runs for the sync path, a REST caller never reaches it");
    }

    [Fact]
    public async Task Commit_id_is_validated_and_audited_without_file_content()
    {
        await Should.ThrowAsync<ProductSyncRejectedException>(() =>
            _sync.PlanAsync(new ProductSyncRequest(_h.Caller(ProductHarness.SyncIdentity, viaSync: true), "not a commit; DROP", [])));

        await _sync.ApplyAsync(Request(File("p.product.yaml", ViewFile)));

        var entry = _h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.ProductSyncApplied);
        entry.DetailsJson.ShouldContain("0123456789abcdef");
        entry.DetailsJson.ShouldNotContain("SELECT");
        _h.Audit.Entries.First(e => e.EventType == AuditEventTypes.ProductSyncRejected).DetailsJson.ShouldContain("\"commit\":\"invalid\"");
    }
}

internal sealed class FakeSecrets(Dictionary<string, byte[]> secrets) : IKeyVaultSecretProvider
{
    public byte[] GetSecretBytes(string secretRef) => secrets.TryGetValue(secretRef, out var value) ? (byte[])value.Clone() : throw new KeyNotFoundException();
}
