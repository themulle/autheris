namespace Autheris.Application.Jobs.Services;

using System;

/// <summary>Identity of this gateway process within the cluster; stored on job records so the owner node is known.</summary>
public sealed class AsyncJobNodeIdentity
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N");
}
