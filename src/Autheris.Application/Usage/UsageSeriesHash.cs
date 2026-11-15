namespace Autheris.Application.Usage;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

/// <summary>Computes the canonical 32-byte series hash for <see cref="UsageSeriesKey"/>.</summary>
public static class UsageSeriesHash
{
    public static byte[] Compute(in UsageSeriesKey key)
    {
        using var ms = new MemoryStream(256);
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(key.Tenant.Value);
            writer.Write(key.UserKey);
            writer.Write(key.GroupSetId);
            writer.Write(key.BillingGroup);
            writer.Write((short)key.Transport);
            writer.Write(key.Application);
            writer.Write(key.ClientId);
            writer.Write(key.DataSource);
            writer.Write(key.TableId);
        }

        return SHA256.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }
}
