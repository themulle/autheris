namespace Autheris.Domain.Kernel;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

public sealed record ExecutionMetrics(
    TimeSpan Duration,
    long RowCount,
    long EstimatedBytes
);
