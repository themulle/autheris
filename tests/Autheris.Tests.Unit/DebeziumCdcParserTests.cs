namespace Autheris.Tests.Unit;

using System;
using Autheris.Domain.Model;
using Autheris.Extensions.Cdc;
using Shouldly;
using Xunit;

public sealed class DebeziumCdcParserTests
{
    [Fact]
    public void Parse_StandardDebeziumUpdate_MapsPropertiesCorrectly()
    {
        var json = """
        {
            "before": { "id": 42, "name": "Old Name", "tenant_id": "tenant-corp" },
            "after": { "id": 42, "name": "New Name", "tenant_id": "tenant-corp" },
            "source": {
                "version": "2.5.0",
                "connector": "postgresql",
                "name": "salesdb",
                "ts_ms": 1727400000000,
                "schema": "crm",
                "table": "accounts"
            },
            "op": "u",
            "ts_ms": 1727400000100
        }
        """;

        var cdcEvent = DebeziumCdcParser.Parse(json);

        cdcEvent.ShouldNotBeNull();
        cdcEvent.Operation.ShouldBe(CdcOperation.Update);
        cdcEvent.Table.Domain.ShouldBe("salesdb");
        cdcEvent.Table.Schema.ShouldBe("crm");
        cdcEvent.Table.TableName.ShouldBe("accounts");
        cdcEvent.TenantId.ShouldBe("tenant-corp");
        cdcEvent.Before.ShouldNotBeNull();
        cdcEvent.Before["name"].ShouldBe("Old Name");
        cdcEvent.After.ShouldNotBeNull();
        cdcEvent.After["name"].ShouldBe("New Name");
    }

    [Fact]
    public void Parse_KafkaConnectEnvelope_MapsCorrectly()
    {
        var json = """
        {
            "schema": { "type": "struct" },
            "payload": {
                "before": null,
                "after": { "id": 101, "email": "lead@customer.com", "tenant_id": "tenant-1" },
                "source": { "schema": "public", "table": "leads", "name": "marketing" },
                "op": "c"
            }
        }
        """;

        var cdcEvent = DebeziumCdcParser.Parse(json);

        cdcEvent.ShouldNotBeNull();
        cdcEvent.Operation.ShouldBe(CdcOperation.Insert);
        cdcEvent.Table.Schema.ShouldBe("public");
        cdcEvent.Table.TableName.ShouldBe("leads");
        cdcEvent.TenantId.ShouldBe("tenant-1");
        cdcEvent.After.ShouldNotBeNull();
        cdcEvent.After["email"].ShouldBe("lead@customer.com");
    }

    [Fact]
    public void Parse_DeleteOperation_MappedToCdcOperationDelete()
    {
        var json = """
        {
            "before": { "id": 55, "deleted_by": "admin" },
            "after": null,
            "source": { "schema": "public", "table": "sessions" },
            "op": "d"
        }
        """;

        var cdcEvent = DebeziumCdcParser.Parse(json, defaultTenantId: "tenant-fallback");

        cdcEvent.Operation.ShouldBe(CdcOperation.Delete);
        cdcEvent.TenantId.ShouldBe("tenant-fallback");
        cdcEvent.Before.ShouldNotBeNull();
        cdcEvent.Before["id"].ShouldBe(55);
    }
}
