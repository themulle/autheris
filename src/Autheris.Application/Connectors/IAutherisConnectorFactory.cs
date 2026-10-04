using System;
using Microsoft.Extensions.Configuration;

namespace Autheris.Application.Connectors;

public interface IAutherisConnectorFactory
{
    string ConnectorType { get; }
    IAutherisConnector CreateConnector(string catalogName, IConfiguration configuration, IServiceProvider serviceProvider);
}
