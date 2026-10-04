using System;
using System.Collections.Generic;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Benchmarks;

public class ConsentResolutionBenchmark
{
    private readonly ConsentResolutionService _service = new();
    private readonly Sid _userSid = new("S-1-5-21-1001");
    private readonly HashSet<Sid> _groupSids = new()
    {
        new("S-1-5-21-GROUP-A"),
        new("S-1-5-21-GROUP-B"),
        new("S-1-5-21-GROUP-C"),
        new("S-1-5-21-GROUP-D"),
        new("S-1-5-21-GROUP-E")
    };
    private readonly HashSet<string> _roles = new() { "FinanceAnalyst", "DataConsumer" };
    private readonly TableIdentifier _table = new("finance", "dbo", "invoices");
    private readonly List<Consent> _consents = new();

    public ConsentResolutionBenchmark()
    {
        var c1 = new Consent
        {
            TableIdentifier = _table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid("S-1-5-21-GROUP-A"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            IsRevoked = false,
            ColumnRules = new List<ConsentColumnRule>
            {
                new() { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new() { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear },
                new() { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
            },
            RowFilters = new List<ConsentRowFilter>
            {
                new() { ColumnName = "amount", Operator = "GT", ValueType = "decimal", ValueJson = "100", FilterGroup = 1 }
            }
        };

        var c2 = new Consent
        {
            TableIdentifier = _table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid("S-1-5-21-GROUP-B"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            IsRevoked = false,
            ColumnRules = new List<ConsentColumnRule>
            {
                new() { ColumnName = "email", AccessLevel = ColumnAccessLevel.Clear },
                new() { ColumnName = "vendor", AccessLevel = ColumnAccessLevel.Clear }
            },
            RowFilters = new List<ConsentRowFilter>
            {
                new() { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EU\"", FilterGroup = 1 }
            }
        };

        var c3 = new Consent
        {
            TableIdentifier = _table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Role,
            RoleName = "FinanceAnalyst",
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            IsRevoked = false,
            ColumnRules = new List<ConsentColumnRule>
            {
                new() { ColumnName = "salary", AccessLevel = ColumnAccessLevel.Deny }
            }
        };

        _consents.Add(c1);
        _consents.Add(c2);
        _consents.Add(c3);
    }

    public TableAccessDecision Run()
    {
        return _service.ResolveAccess(_userSid, _groupSids, _roles, _table, _consents);
    }
}
