using System;

namespace Shared.Modularity.Context;

public class ApplicationShutdownContext
{
    public IServiceProvider ServiceProvider { get; }

    public ApplicationShutdownContext(IServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
    }
}