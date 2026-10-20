namespace Autheris.Application.Plugins.Auth;

using Autheris.Application.Identity.Attributes;

public interface IPluginCallerAttributeProvider : ICallerAttributeProvider
{
    new string Kind { get; }
}
