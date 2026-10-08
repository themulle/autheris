namespace Autheris.Tests.Unit.Security;

using System;
using System.Data;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Exceptions;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

/// <summary>
/// R-SQL-8 / SQL2-8: procedure results read LOB values in chunks against the remaining budget (sequential access),
/// and binary values count with the Base64 factor of their JSON representation.
/// </summary>
public sealed class ProcedureLobBudgetRSql8Tests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ProcedureLobBudgetRSql8Tests()
    {
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE lob(id INTEGER, txt TEXT, bin BLOB); INSERT INTO lob VALUES (1, $txt, $bin);";
        cmd.Parameters.AddWithValue("$txt", new string('a', 100_000));
        cmd.Parameters.AddWithValue("$bin", new byte[3000]);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private SqliteDataReader OpenReader()
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, txt, bin FROM lob";
        var reader = cmd.ExecuteReader(CommandBehavior.SequentialAccess);
        reader.Read().ShouldBeTrue();
        return reader;
    }

    [Fact]
    public void LargeText_BeyondRemainingBudget_IsRejectedWhileReading()
    {
        using var reader = OpenReader();
        BoundedValueReader.Read(reader, 0, 1_000_000, out _);

        var ex = Should.Throw<GatewaySecurityException>(() => BoundedValueReader.Read(reader, 1, 50_000, out _));
        ex.ErrorCode.ShouldBe("RESPONSE_TOO_LARGE");
    }

    [Fact]
    public void Values_WithinBudget_AreReadCompletely_BinaryCountsAsBase64()
    {
        using var reader = OpenReader();
        BoundedValueReader.Read(reader, 0, 1_000_000, out _).ShouldBe(1L);

        var text = BoundedValueReader.Read(reader, 1, 1_000_000, out var textBytes);
        ((string)text!).Length.ShouldBe(100_000);
        textBytes.ShouldBe(200_000);

        var bin = BoundedValueReader.Read(reader, 2, 1_000_000, out var binBytes);
        ((byte[])bin!).Length.ShouldBe(3000);
        binBytes.ShouldBe(4000);
    }

    [Fact]
    public void Binary_JustAboveBudget_ByBase64Factor_IsRejected()
    {
        using var reader = OpenReader();
        BoundedValueReader.Read(reader, 0, 1_000_000, out _);
        BoundedValueReader.Read(reader, 1, 1_000_000, out _);

        // 3000 raw bytes fit 3500, but their Base64 form (4000) does not.
        Should.Throw<GatewaySecurityException>(() => BoundedValueReader.Read(reader, 2, 3500, out _));
    }
}
