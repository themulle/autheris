namespace Autheris.Application.Runtime.Jobs.Services;

using System;

/// <summary>Thrown when an async query job request specifies an unsupported result format.</summary>
public sealed class AsyncJobInvalidFormatException : Exception
{
    public string ErrorCode => AsyncQueryJobFormat.InvalidFormatCode;

    public AsyncJobInvalidFormatException()
        : base("Supported async query job formats are 'json' and 'csv'.")
    {
    }
}
