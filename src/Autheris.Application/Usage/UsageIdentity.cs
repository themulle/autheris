namespace Autheris.Application.Usage;

using System.Collections.Generic;
using Autheris.Domain.Common;

public sealed record UsageIdentity(TenantId Tenant, Sid UserSid, IReadOnlySet<Sid> GroupSids);
