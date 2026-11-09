namespace Autheris.Application.Usage;

using System;
using Autheris.Domain.Common;
using Autheris.Domain.Options.DataSources;

public readonly record struct UsageSeriesKey(
    TenantId Tenant,
    DateOnly DayUtc,
    string UserKey,
    string GroupSetId,
    string BillingGroup,
    DataSourceTransportFlags Transport,
    string Application,
    string ClientId,
    string DataSource,
    string TableId);
