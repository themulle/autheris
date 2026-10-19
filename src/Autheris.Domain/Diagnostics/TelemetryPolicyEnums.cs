namespace Autheris.Domain.Diagnostics;

public enum SpanDetail
{
    None = 0,
    Basic = 1,
    Detailed = 2
}

public enum StatementTextMode
{
    None = 0,
    Summary = 1,
    Parameterized = 2
}

public enum TelemetryIdentityMode
{
    None = 0,
    Pseudonymous = 1
}

public enum OtlpProtocol
{
    Grpc = 0,
    HttpProtobuf = 1
}
