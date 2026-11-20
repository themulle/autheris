namespace Autheris.Tests.Unit.Extensibility;

using System;
using System.IO;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class LocalStorageProviderPathTests
{
    [Fact]
    public void CRIT01_LocalStorageProvider_PrefixCollisionTraversal_ThrowsSecurityException()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "lakehouse_" + Guid.NewGuid().ToString("N"));
        var siblingDir = tempBase + "_secrets";
        Directory.CreateDirectory(tempBase);
        Directory.CreateDirectory(siblingDir);

        try
        {
            var options = Options.Create(new GatewayOptions
            {
                Lakehouse = new LakehouseOptions
                {
                    Storage = new LakehouseStorageOptions
                    {
                        LocalBasePath = tempBase
                    }
                }
            });

            var provider = new Autheris.Extensions.Lakehouse.Services.LocalStorageProvider(options);
            var attackLocation = Path.Combine(siblingDir, "secret.key");

            Should.Throw<System.Security.SecurityException>(() =>
                provider.ExistsAsync(attackLocation).AsTask().GetAwaiter().GetResult());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
            if (Directory.Exists(siblingDir)) Directory.Delete(siblingDir, true);
        }
    }
}
