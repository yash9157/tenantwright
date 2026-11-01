using System;

namespace Shared.Events;

public interface IEventNameProvider
{
    string GetName(Type eventType);
}
