using System.Collections;
using System.Data;
using System.Data.Common;
using Autheris.Application.Services;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// O11/O12 (docs/plans/rls-subquery-in-strategy.md, OData-Härtung): the reader stops as soon as the response exceeds the
/// byte limit, and a column type the driver cannot materialize (SQL Server UDTs such as geography/hierarchyid without
/// Microsoft.SqlServer.Types) ends in a named, client-actionable error instead of an unhandled exception.
/// </summary>
public sealed class SqlDataSourceExecutorReaderTests
{
    private static readonly IReadOnlyDictionary<string, MaskingRule> NoHmac = new Dictionary<string, MaskingRule>();

    private static SqlDataSourceExecutor CreateExecutor(long maxResponseBytes = 10 * 1024 * 1024) =>
        new(options: Options.Create(new GatewayOptions { GraphQL = new GraphQLOptions { MaxResponseBytes = maxResponseBytes } }));

    private static DbDataReader CreateReader(int rows, int textLength)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("payload", typeof(string));
        for (var i = 0; i < rows; i++)
        {
            table.Rows.Add(i, new string('x', textLength));
        }
        return table.CreateDataReader();
    }

    [Fact]
    public async Task ReadRowsAsync_WithinLimit_ReturnsAllRows()
    {
        var executor = CreateExecutor();

        var rows = await executor.ReadRowsAsync(CreateReader(10, 100), NoHmac, 10, CancellationToken.None);

        rows.Count.ShouldBe(10);
        rows[3]["id"].ShouldBe(3);
    }

    [Fact]
    public async Task ReadRowsAsync_AboveByteLimit_StopsReadingEarly()
    {
        var executor = CreateExecutor(maxResponseBytes: 1024 * 1024);
        var reader = new CountingReader(CreateReader(10_000, 1_000));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            executor.ReadRowsAsync(reader, NoHmac, 1000, CancellationToken.None));

        ex.ErrorCode.ShouldBe("RESPONSE_TOO_LARGE");
        // 1 MB at ~2 KB per row: aborted after a few hundred rows, not after all 10 000.
        reader.ReadCalls.ShouldBeLessThan(2_000);
    }

    [Fact]
    public async Task ReadRowsAsync_UnsupportedProviderType_ThrowsNamedError()
    {
        var executor = CreateExecutor();
        var reader = new ThrowingValueReader("location", "geography", new FileNotFoundException("Microsoft.SqlServer.Types"));

        var ex = await Should.ThrowAsync<GatewayUnsupportedColumnTypeException>(() =>
            executor.ReadRowsAsync(reader, NoHmac, 10, CancellationToken.None));

        ex.ColumnName.ShouldBe("location");
        ex.DataTypeName.ShouldBe("geography");
        ex.Message.ShouldNotContain("Microsoft.SqlServer.Types");
    }

    [Fact]
    public async Task ReadRowsAsync_CancellationDuringRead_IsNotMappedToUnsupportedType()
    {
        var executor = CreateExecutor();
        var reader = new ThrowingValueReader("location", "geography", new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            executor.ReadRowsAsync(reader, NoHmac, 10, CancellationToken.None));
    }

    /// <summary>Delegating reader that counts Read calls.</summary>
    private sealed class CountingReader(DbDataReader inner) : DelegatingReader(inner)
    {
        public int ReadCalls { get; private set; }

        public override bool Read()
        {
            ReadCalls++;
            return base.Read();
        }

        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            ReadCalls++;
            return base.ReadAsync(cancellationToken);
        }
    }

    /// <summary>One row, one column whose GetValue throws (as SqlDataReader does for UDTs without the type assembly).</summary>
    private sealed class ThrowingValueReader(string columnName, string dataTypeName, Exception error) : DbDataReader
    {
        private bool _read;

        public override int FieldCount => 1;
        public override int Depth => 0;
        public override bool HasRows => true;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(0);
        public override string GetName(int ordinal) => columnName;
        public override string GetDataTypeName(int ordinal) => dataTypeName;
        public override Type GetFieldType(int ordinal) => typeof(object);
        public override int GetOrdinal(string name) => 0;
        public override bool IsDBNull(int ordinal) => false;
        public override object GetValue(int ordinal) => throw error;
        public override int GetValues(object[] values) => throw error;
        public override bool Read()
        {
            if (_read)
            {
                return false;
            }
            _read = true;
            return true;
        }
        public override bool NextResult() => false;
        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
        public override bool GetBoolean(int ordinal) => throw error;
        public override byte GetByte(int ordinal) => throw error;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw error;
        public override char GetChar(int ordinal) => throw error;
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw error;
        public override DateTime GetDateTime(int ordinal) => throw error;
        public override decimal GetDecimal(int ordinal) => throw error;
        public override double GetDouble(int ordinal) => throw error;
        public override float GetFloat(int ordinal) => throw error;
        public override Guid GetGuid(int ordinal) => throw error;
        public override short GetInt16(int ordinal) => throw error;
        public override int GetInt32(int ordinal) => throw error;
        public override long GetInt64(int ordinal) => throw error;
        public override string GetString(int ordinal) => throw error;
    }

    private abstract class DelegatingReader(DbDataReader inner) : DbDataReader
    {
        public override int FieldCount => inner.FieldCount;
        public override int Depth => inner.Depth;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool Read() => inner.Read();
        public override bool NextResult() => inner.NextResult();
        public override IEnumerator GetEnumerator() => inner.GetEnumerator();
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
    }
}
