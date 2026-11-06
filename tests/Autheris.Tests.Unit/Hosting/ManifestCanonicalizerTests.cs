namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Text;
using Autheris.Infrastructure.Modules.Trust;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ManifestCanonicalizerTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Field_order_does_not_change_canonical_bytes()
    {
        const string jsonOrderA = """
        {
          "schema": 1,
          "id": "lakehouse",
          "version": "1.2.0",
          "hostVersion": "1.2.0",
          "abstractionsVersion": "1.0.0",
          "entryAssembly": "Autheris.Modules.Lakehouse.dll",
          "entryType": "Autheris.Modules.Lakehouse.LakehouseModule",
          "files": [
            { "path": "Parquet.dll", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            { "path": "Autheris.Modules.Lakehouse.dll", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
          ],
          "keyId": "0123456789abcdef",
          "signature": "c2lnbmF0dXJl"
        }
        """;

        const string jsonOrderB = """
        {
          "signature": "c2lnbmF0dXJl",
          "keyId": "0123456789abcdef",
          "files": [
            { "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "path": "Autheris.Modules.Lakehouse.dll" },
            { "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "path": "Parquet.dll" }
          ],
          "entryType": "Autheris.Modules.Lakehouse.LakehouseModule",
          "entryAssembly": "Autheris.Modules.Lakehouse.dll",
          "abstractionsVersion": "1.0.0",
          "hostVersion": "1.2.0",
          "version": "1.2.0",
          "id": "lakehouse",
          "schema": 1
        }
        """;

        var bytesA = ManifestCanonicalizer.ToCanonicalBytes(jsonOrderA);
        var bytesB = ManifestCanonicalizer.ToCanonicalBytes(jsonOrderB);

        bytesA.ShouldBe(bytesB);

        var canonicalText = Encoding.UTF8.GetString(bytesA);
        canonicalText.ShouldStartWith("{\"schema\":1,\"abstractionsVersion\":\"1.0.0\"");
        canonicalText.IndexOf("Autheris.Modules.Lakehouse.dll", StringComparison.Ordinal)
            .ShouldBeLessThan(canonicalText.IndexOf("Parquet.dll", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Signature_field_is_excluded()
    {
        var manifest1 = new ModuleManifest(
            Schema: 1,
            Id: "dbt",
            Version: "1.2.0",
            HostVersion: "1.2.0",
            AbstractionsVersion: "1.0.0",
            EntryAssembly: "Autheris.Modules.Dbt.dll",
            EntryType: "Autheris.Modules.Dbt.DbtModule",
            Files: [new ModuleManifestFile("Autheris.Modules.Dbt.dll", new string('c', 64))],
            KeyId: "fedcba9876543210",
            Signature: "first-signature-value");

        var manifest2 = manifest1 with { Signature = "completely-different-signature-value" };

        var canonical1 = ManifestCanonicalizer.ToCanonicalBytes(manifest1);
        var canonical2 = ManifestCanonicalizer.ToCanonicalBytes(manifest2);

        canonical1.ShouldBe(canonical2);
        Encoding.UTF8.GetString(canonical1).ShouldNotContain("signature");
        Encoding.UTF8.GetString(canonical1).ShouldNotContain("first-signature-value");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Non_ascii_and_whitespace_are_canonical()
    {
        const string compactJson = "{\"schema\":1,\"id\":\"openmetadata\",\"version\":\"1.2.0\",\"hostVersion\":\"1.2.0\",\"abstractionsVersion\":\"1.0.0\",\"entryAssembly\":\"Sub/Mod_ü.dll\",\"entryType\":\"Autheris.Mod\",\"files\":[{\"path\":\"Sub/Mod_ü.dll\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}],\"keyId\":\"0011223344556677\",\"signature\":\"abc\"}";
        const string formattedWithEscapesJson = """
        {
            "schema" : 1 ,
            "id" : "openmetadata" ,
            "version" : "1.2.0" ,
            "hostVersion" : "1.2.0" ,
            "abstractionsVersion" : "1.0.0" ,
            "entryAssembly" : "Sub/Mod_\u00fc.dll" ,
            "entryType" : "Autheris.Mod" ,
            "files" : [
                {
                    "path" : "Sub/Mod_\u00fc.dll" ,
                    "sha256" : "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                }
            ] ,
            "keyId" : "0011223344556677" ,
            "signature" : "xyz"
        }
        """;

        var bytesCompact = ManifestCanonicalizer.ToCanonicalBytes(compactJson);
        var bytesFormatted = ManifestCanonicalizer.ToCanonicalBytes(formattedWithEscapesJson);

        bytesCompact.ShouldBe(bytesFormatted);
        var text = Encoding.UTF8.GetString(bytesCompact);
        text.ShouldNotContain("\n");
        text.ShouldNotContain(" : ");
    }
}
