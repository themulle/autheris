using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Autheris.Tests.Unit;

public class MemoryPackAndGarnetTests
{
    [Fact]
    public void MemoryPackCacheSerializer_SerializesAndDeserializes_CachedConsentEnvelope()
    {
        var serializer = new MemoryPackCacheSerializer();

        var table = new TableIdentifier("sales", "crm", "customers");
        var colMap = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask,
            ["ssn"] = ColumnAccessLevel.Deny
        };

        var decision = TableAccessDecision.Allowed(table, colMap, "tenant_id = 42", hasUnconstrainedColumnAllow: false);
        var envelope = CachedConsentEnvelope.FromDecision(decision, epoch: 105);

        // Serialize
        byte[] bytes = serializer.Serialize(envelope);
        bytes.ShouldNotBeNull();
        bytes.Length.ShouldBeGreaterThan(0);
        bytes[0].ShouldBe((byte)0x4D); // 'M' magic prefix for MemoryPack

        // Deserialize
        bool ok = serializer.TryDeserialize<CachedConsentEnvelope>(bytes, out var deserialized);
        ok.ShouldBeTrue();
        deserialized.ShouldNotBeNull();
        deserialized.Epoch.ShouldBe(105);
        deserialized.IsAllowed.ShouldBeTrue();
        deserialized.RowFilterSql.ShouldBe("tenant_id = 42");

        // Convert back to TableAccessDecision
        var restoredDecision = deserialized.ToDecision(Autheris.Domain.Security.RestrictedScopePolicy.Default.Fingerprint)!;
        restoredDecision.Table.Domain.ShouldBe("sales");
        restoredDecision.Table.Schema.ShouldBe("crm");
        restoredDecision.Table.TableName.ShouldBe("customers");
        restoredDecision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        restoredDecision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
        restoredDecision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public void MemoryPackCacheSerializer_FallbackToJson_ForArbitraryPoco()
    {
        var serializer = new MemoryPackCacheSerializer();
        var testObj = new NonMemoryPackPoco { Name = "Autheris", Value = 42 };

        byte[] bytes = serializer.Serialize(testObj);
        bytes.ShouldNotBeNull();
        bytes[0].ShouldBe((byte)0x4A); // 'J' magic prefix for JSON fallback

        bool ok = serializer.TryDeserialize<NonMemoryPackPoco>(bytes, out var deserialized);
        ok.ShouldBeTrue();
        deserialized.ShouldNotBeNull();
        deserialized.Name.ShouldBe("Autheris");
        deserialized.Value.ShouldBe(42);
    }

    public class NonMemoryPackPoco
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }
}
