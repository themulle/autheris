global using Autheris.Domain.Common;
global using Autheris.Domain.Interfaces;
global using Autheris.Domain.Model;
global using Autheris.Domain.Options;
global using Autheris.Application.Interfaces;
global using RateLimitDecision = Autheris.Domain.Model.CostQuotaResult;
global using TokenBucketPolicy = Autheris.Domain.Model.ClientQuotaPolicy;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Autheris.Tests.Unit")]
