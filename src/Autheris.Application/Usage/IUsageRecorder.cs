namespace Autheris.Application.Usage;

using Autheris.Domain.Common;

public interface IUsageRecorder
{
    void Record(in UsageFact fact, TenantId tenant, Sid actor);
}
