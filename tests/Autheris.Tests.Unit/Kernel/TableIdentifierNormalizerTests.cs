namespace Autheris.Tests.Unit.Kernel;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Kernel;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Kernel;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TableIdentifierNormalizerTests
{
    [Fact]
    public void TableIdentifierNormalizer_SinglePart_ExpandsDefaults()
    {
        var id = TableIdentifierNormalizer.Normalize("customers", defaultDomain: "sales", defaultSchema: "public", dialect: DatabaseDialect.PostgreSql);
        id.Domain.ShouldBe("sales");
        id.Schema.ShouldBe("public");
        id.TableName.ShouldBe("customers");
    }

    [Fact]
    public void TableIdentifierNormalizer_CaseFolding_PostgresLower_OracleUpper()
    {
        var pg = TableIdentifierNormalizer.Normalize("Sales.Public.Orders", dialect: DatabaseDialect.PostgreSql);
        pg.Domain.ShouldBe("sales");
        pg.Schema.ShouldBe("public");
        pg.TableName.ShouldBe("orders");

        var ora = TableIdentifierNormalizer.Normalize("sales.public.orders", dialect: DatabaseDialect.Oracle);
        ora.Domain.ShouldBe("SALES");
        ora.Schema.ShouldBe("PUBLIC");
        ora.TableName.ShouldBe("ORDERS");
    }

}
