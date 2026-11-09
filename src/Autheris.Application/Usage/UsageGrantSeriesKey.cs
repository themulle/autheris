namespace Autheris.Application.Usage;

using System;
using Autheris.Domain.Common;

public readonly record struct UsageGrantSeriesKey(
    TenantId Tenant,
    DateOnly DayUtc,
    string GrantId,
    string DataSource,
    string TableId);
