namespace Autheris.Application.Events.Interfaces;

using System;

/// <summary>The store could not complete the operation right now (for example a deadlock victim); the caller may retry.</summary>
public sealed class CloudEventSubscriptionStoreBusyException : Exception
{
    public CloudEventSubscriptionStoreBusyException()
        : base("The subscription store is busy; retry.")
    {
    }
}
