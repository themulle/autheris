namespace Autheris.Infrastructure.Persistence.Products;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Constant SQL of the data product store. Every statement carries the tenant predicate (the tenant is part of every key).</summary>
internal static class DataProductStoreSql
{
    private const string Columns = "id, product_schema, product_name, kind, definition, definition_hash, base_objects_json, columns_json, created_by, authority, status, managed_by, load_json, limits_json, description, product_owner, version, created_at, updated_at";

    public const string GetSql = "SELECT " + Columns + " FROM DATA_PRODUCTS WHERE tenant_id = @tenant_id AND product_schema = @product_schema AND product_name = @product_name";
    public const string ListPgSql = "SELECT " + Columns + " FROM DATA_PRODUCTS WHERE tenant_id = @tenant_id AND (product_schema > @after_schema OR (product_schema = @after_schema AND product_name > @after_name)) ORDER BY product_schema, product_name LIMIT @limit";
    public const string ListSsSql = "SELECT TOP (@limit) " + Columns + " FROM DATA_PRODUCTS WHERE tenant_id = @tenant_id AND (product_schema > @after_schema OR (product_schema = @after_schema AND product_name > @after_name)) ORDER BY product_schema, product_name";
    // The one cross-tenant read: the schedule worker enumerates every tenant's scheduled views (keyset on tenant, schema, name).
    private const string ScheduledWhere = " FROM DATA_PRODUCTS WHERE kind = 'View' AND status IN ('Active', 'Stale', 'Invalid') AND load_json LIKE '%\"Mode\":\"Schedule\"%' AND (tenant_id > @after_tenant OR (tenant_id = @after_tenant AND (product_schema > @after_schema OR (product_schema = @after_schema AND product_name > @after_name)))) ORDER BY tenant_id, product_schema, product_name";
    public const string ListScheduledPgSql = "SELECT tenant_id, " + Columns + ScheduledWhere + " LIMIT @limit";
    public const string ListScheduledSsSql = "SELECT TOP (@limit) tenant_id, " + Columns + ScheduledWhere;
    public const string ExistsSql = "SELECT 1 FROM DATA_PRODUCTS WHERE tenant_id = @tenant_id AND product_schema = @product_schema AND product_name = @product_name";
    public const string InsertSql = "INSERT INTO DATA_PRODUCTS (tenant_id, " + Columns + ") VALUES (@tenant_id, @id, @product_schema, @product_name, @kind, @definition, @definition_hash, @base_objects_json, @columns_json, @created_by, @authority, @status, @managed_by, @load_json, @limits_json, @description, @product_owner, @version, @created_at, @updated_at)";
    public const string UpdateSql = "UPDATE DATA_PRODUCTS SET definition = @definition, definition_hash = @definition_hash, base_objects_json = @base_objects_json, columns_json = @columns_json, status = @status, managed_by = @managed_by, load_json = @load_json, limits_json = @limits_json, description = @description, product_owner = @product_owner, version = @version, updated_at = @updated_at WHERE tenant_id = @tenant_id AND id = @id AND product_schema = @product_schema AND product_name = @product_name AND version = @expected_version";

    public const string InsertVersionSql = "INSERT INTO DATA_PRODUCT_VERSIONS (tenant_id, product_id, version, definition_hash, definition, author, authority, created_at) VALUES (@tenant_id, @product_id, @version, @definition_hash, @definition, @author, @authority, @created_at)";
    public const string SelectVersionsSql = "SELECT product_id, version, definition_hash, definition, author, authority, created_at FROM DATA_PRODUCT_VERSIONS WHERE tenant_id = @tenant_id AND product_id = @product_id ORDER BY version";

    public const string IsOwnerSql = "SELECT 1 FROM DATA_SOURCE_OWNERS WHERE tenant_id = @tenant_id AND data_source = @data_source AND owner_sid = @owner_sid";
    public const string SelectOwnersSql = "SELECT data_source, owner_sid, assigned_by, assigned_at FROM DATA_SOURCE_OWNERS WHERE tenant_id = @tenant_id AND data_source = @data_source ORDER BY owner_sid";
    public const string InsertOwnerSql = "INSERT INTO DATA_SOURCE_OWNERS (tenant_id, data_source, owner_sid, assigned_by, assigned_at) VALUES (@tenant_id, @data_source, @owner_sid, @assigned_by, @assigned_at)";
    public const string DeleteOwnerSql = "DELETE FROM DATA_SOURCE_OWNERS WHERE tenant_id = @tenant_id AND data_source = @data_source AND owner_sid = @owner_sid";

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private sealed record LineageDto(string Table, string Column);

    private sealed record ColumnDto(string Name, string DataType, ProductColumnClass Class, List<LineageDto> Lineage, string? DeclassifiedBy, string? DeclassificationReason);

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    public static void BindKey(DbCommand cmd, TenantId tenant, string schema, string name)
    {
        cmd.AddBound("@tenant_id", tenant.Value);
        cmd.AddBound("@product_schema", schema);
        cmd.AddBound("@product_name", name);
    }

    public static void BindProduct(DbCommand cmd, DataProduct p)
    {
        BindKey(cmd, p.Tenant, p.Schema, p.Name);
        cmd.AddBound("@id", p.Id.ToString("D"));
        cmd.AddBound("@kind", p.Kind.ToString());
        cmd.AddBound("@definition", p.Definition);
        cmd.AddBound("@definition_hash", p.DefinitionHash);
        cmd.AddBound("@base_objects_json", JsonSerializer.Serialize(p.BaseObjects.Select(static o => o.ToString()), Json));
        cmd.AddBound("@columns_json", JsonSerializer.Serialize(p.Columns.Select(ToDto), Json));
        cmd.AddBound("@created_by", p.CreatedBy.Value);
        cmd.AddBound("@authority", p.Authority.ToString());
        cmd.AddBound("@status", p.Status.ToString());
        cmd.AddBound("@managed_by", (object?)p.ManagedBy ?? DBNull.Value, DbType.String);
        cmd.AddBound("@load_json", JsonSerializer.Serialize(p.Load, Json));
        cmd.AddBound("@limits_json", JsonSerializer.Serialize(p.Limits, Json));
        cmd.AddBound("@description", (object?)p.Description ?? DBNull.Value, DbType.String);
        cmd.AddBound("@product_owner", p.ProductOwner.Value);
        cmd.AddBound("@version", p.Version, DbType.Int32);
        cmd.AddBound("@created_at", p.CreatedAt.ToUniversalTime(), DbType.DateTimeOffset);
        cmd.AddBound("@updated_at", p.UpdatedAt.ToUniversalTime(), DbType.DateTimeOffset);
    }

    public static void BindVersion(DbCommand cmd, TenantId tenant, DataProductVersion v)
    {
        cmd.AddBound("@tenant_id", tenant.Value);
        cmd.AddBound("@product_id", v.ProductId.ToString("D"));
        cmd.AddBound("@version", v.Version, DbType.Int32);
        cmd.AddBound("@definition_hash", v.DefinitionHash);
        cmd.AddBound("@definition", v.Definition);
        cmd.AddBound("@author", v.Author.Value);
        cmd.AddBound("@authority", v.Authority.ToString());
        cmd.AddBound("@created_at", v.CreatedAt.ToUniversalTime(), DbType.DateTimeOffset);
    }

    public static DataProduct ReadProduct(DbDataReader r, TenantId tenant, int o = 0) => new(
        Guid.Parse(r.GetString(0 + o)),
        tenant,
        r.GetString(1 + o),
        r.GetString(2 + o),
        Enum.Parse<DataProductKind>(r.GetString(3 + o)),
        r.GetString(4 + o),
        r.GetString(5 + o),
        JsonSerializer.Deserialize<List<string>>(r.GetString(6 + o), Json)!.Select(ParseTable).ToList(),
        JsonSerializer.Deserialize<List<ColumnDto>>(r.GetString(7 + o), Json)!.Select(FromDto).ToList(),
        new Sid(r.GetString(8 + o)),
        Enum.Parse<ProductCreatorAuthority>(r.GetString(9 + o)),
        Enum.Parse<DataProductStatus>(r.GetString(10 + o)),
        r.IsDBNull(11 + o) ? null : r.GetString(11 + o),
        JsonSerializer.Deserialize<LoadSettings>(r.GetString(12 + o), Json)!,
        JsonSerializer.Deserialize<ProductLimits>(r.GetString(13 + o), Json)!,
        r.IsDBNull(14 + o) ? null : r.GetString(14 + o),
        new Sid(r.GetString(15 + o)),
        r.GetInt32(16 + o),
        r.GetFieldValue<DateTimeOffset>(17 + o),
        r.GetFieldValue<DateTimeOffset>(18 + o));

    public static DataProductVersion ReadVersion(DbDataReader r) => new(
        Guid.Parse(r.GetString(0)), r.GetInt32(1), r.GetString(2), r.GetString(3), new Sid(r.GetString(4)),
        Enum.Parse<ProductCreatorAuthority>(r.GetString(5)), r.GetFieldValue<DateTimeOffset>(6));

    private static TableIdentifier ParseTable(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 3 && TableIdentifier.TryCreate(parts[0], parts[1], parts[2], out var id)
            ? id
            : throw new InvalidOperationException("A stored product base object is not a catalog id.");
    }

    private static ColumnDto ToDto(DataProductColumn c) =>
        new(c.Name, c.DataType, c.Class, c.Lineage.Select(static l => new LineageDto(l.Table.ToString(), l.Column)).ToList(), c.DeclassifiedBy, c.DeclassificationReason);

    private static DataProductColumn FromDto(ColumnDto c) =>
        new(c.Name, c.DataType, c.Class, c.Lineage.Select(static l => new ColumnLineage(ParseTable(l.Table), l.Column)).ToList(), c.DeclassifiedBy, c.DeclassificationReason);
}
